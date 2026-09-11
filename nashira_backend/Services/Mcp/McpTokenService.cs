using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;

namespace nashira_backend.Services.Mcp;

public interface IMcpTokenService
{
    Task<string?> GetValidAccessTokenAsync(McpServer server, CancellationToken ct);
}

// OAuth client-credentials tokens for MCP servers.
//
// Unlike the integration equivalent these are persisted (encrypted, inside
// McpAuthConfig) rather than cached in memory. An MCP session is opened per call
// and the token is part of the connection handshake, so losing it on restart would
// re-grant on the very next tool call; and a provider that also issues a refresh
// token gives us something that genuinely cannot be re-derived.
public sealed class McpTokenService : IMcpTokenService
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // One grant in flight per server; a burst of tool calls on an expired token
    // would otherwise fire N identical grants.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecretProtector _protector;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMcpOAuthService _oauth;
    private readonly ILogger<McpTokenService> _logger;

    public McpTokenService(
        IServiceScopeFactory scopes, ISecretProtector protector, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, IMcpOAuthService oauth, ILogger<McpTokenService> logger)
    {
        _scopes = scopes;
        _protector = protector;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _oauth = oauth;
        _logger = logger;
    }

    public async Task<string?> GetValidAccessTokenAsync(McpServer server, CancellationToken ct)
    {
        var auth = McpAuthConfigCodec.Decrypt(server.AuthConfigEncrypted, _protector);
        if (IsUsable(auth, server.AuthType)) return auth.AccessToken;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-read: another caller may have refreshed and persisted while we waited.
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.McpServers.FirstOrDefaultAsync(s => s.McpServerId == server.McpServerId, ct);
            if (row is null) return null;

            auth = McpAuthConfigCodec.Decrypt(row.AuthConfigEncrypted, _protector);
            if (IsUsable(auth, server.AuthType))
            {
                server.AuthConfigEncrypted = row.AuthConfigEncrypted;
                return auth.AccessToken;
            }

            if (string.Equals(server.AuthType, McpServer.AuthOAuthAuthorizationCode,
                    StringComparison.OrdinalIgnoreCase))
                return await RefreshAuthorizationCodeAsync(server, row, auth, db, ct);

            var (token, expiresAt) = await GrantAsync(server, auth, ct);
            auth.AccessToken = token;
            auth.ExpiresAt = expiresAt;

            row.AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, _protector);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // Keep the caller's instance in step so it does not re-grant immediately.
            server.AuthConfigEncrypted = row.AuthConfigEncrypted;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    // FlowWeaver's refresh path (McpTokenService.cs there), on Nashira's storage:
    // a valid cached token was already preferred above; here either a refresh
    // token renews the grant, or the admin has to re-run Authorize — which is
    // what flipping Status → needs_authorization surfaces in /admin/mcp.
    private async Task<string?> RefreshAuthorizationCodeAsync(
        McpServer server, McpServer row, McpAuthConfig auth, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.RefreshToken))
        {
            await MarkNeedsAuthorizationAsync(server, row, db, ct);
            return null;
        }

        try
        {
            var meta = await _oauth.ResolveMetadataAsync(server, auth, ct);
            var tokens = await _oauth.RefreshAsync(server, meta, auth, auth.RefreshToken!, ct);

            auth.AccessToken = tokens.AccessToken;
            // Keep the existing refresh token when the AS didn't rotate it.
            if (!string.IsNullOrEmpty(tokens.RefreshToken)) auth.RefreshToken = tokens.RefreshToken;
            auth.ExpiresAt = tokens.ExpiresAt;

            row.AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, _protector);
            row.Status = Data.Models.Integration.StatusHealthy;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            server.AuthConfigEncrypted = row.AuthConfigEncrypted;
            server.Status = row.Status;
            return tokens.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Transient timeout — don't downgrade the server's status; the next
            // call retries.
            _logger.LogWarning("mcp.oauth.token_refresh_timeout server={Server}", server.McpServerId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.oauth.token_resolve_failed server={Server}", server.McpServerId);
            await MarkNeedsAuthorizationAsync(server, row, db, ct);
            return null;
        }
    }

    private static async Task MarkNeedsAuthorizationAsync(
        McpServer server, McpServer row, AppDbContext db, CancellationToken ct)
    {
        row.Status = McpServer.StatusNeedsAuthorization;
        row.LastCheckedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        server.Status = row.Status;
    }

    // Client-credentials trusts a cached token only while known-unexpired (an AS
    // that omitted expires_in means "re-grant" — cheap and stateless). For the
    // authorization-code type an unknown expiry is trusted: the grant is NOT
    // re-derivable without the admin at a browser. Same split FlowWeaver makes.
    private static bool IsUsable(McpAuthConfig auth, string? authType)
    {
        if (string.IsNullOrEmpty(auth.AccessToken)) return false;
        var isAuthCode = string.Equals(
            authType, McpServer.AuthOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase);
        return isAuthCode
            ? auth.ExpiresAt is null || auth.ExpiresAt > DateTime.UtcNow
            : auth.ExpiresAt is { } exp && exp > DateTime.UtcNow;
    }

    private async Task<(string Token, DateTime ExpiresAt)> GrantAsync(
        McpServer server, McpAuthConfig auth, CancellationToken ct)
    {
        // FlowWeaver capability, ported: with no token_url typed, discover the
        // authorization server's metadata (RFC 9728 / RFC 8414) instead of failing.
        if (string.IsNullOrWhiteSpace(auth.TokenUrl))
        {
            var meta = await _oauth.ResolveMetadataAsync(server, auth, ct);
            auth.TokenUrl = meta.TokenEndpoint;
        }
        if (string.IsNullOrWhiteSpace(auth.TokenUrl))
            throw new InvalidOperationException(
                $"MCP server '{server.Name}' uses oauth_client_credentials but has no token_url");

        _urlGuard.EnsureSafe(auth.TokenUrl!, allowPrivate: server.AllowPrivateNetwork);

        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (!string.IsNullOrEmpty(auth.Scope)) form["scope"] = auth.Scope!;

        using var request = new HttpRequestMessage(HttpMethod.Post, auth.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.ClientId}:{auth.ClientSecret}")));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        var client = _httpFactory.CreateClient(
            server.TlsSkipVerify ? McpHttpClients.Insecure : McpHttpClients.Secure);

        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("mcp.oauth.failed server={Server} status={Status}",
                server.Name, (int)response.StatusCode);
            // The body can echo the client secret back; it stays out of the message.
            throw new InvalidOperationException(
                $"OAuth token request for MCP server '{server.Name}' failed with HTTP {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                $"OAuth token response for MCP server '{server.Name}' has no access_token");

        var seconds = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var s) ? s : 3600;
        return (at.GetString()!, DateTime.UtcNow.AddSeconds(Math.Max(30, seconds)) - ExpiryMargin);
    }
}
