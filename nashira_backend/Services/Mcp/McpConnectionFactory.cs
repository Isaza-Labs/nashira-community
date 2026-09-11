using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;

namespace nashira_backend.Services.Mcp;

public interface IMcpConnectionFactory
{
    Task<McpConnection> CreateAsync(McpServer server, CancellationToken ct);
}

// Turns a stored McpServer row into a ready-to-use connection: validates the URL,
// runs it past the SSRF guard, and materialises the headers that carry the server's
// credentials.
public sealed class McpConnectionFactory : IMcpConnectionFactory
{
    private readonly ISecretProtector _protector;
    private readonly IUrlGuard _urlGuard;
    private readonly IMcpTokenService _tokens;
    private readonly AppDbContext _db;
    private readonly ILogger<McpConnectionFactory> _logger;

    public McpConnectionFactory(
        ISecretProtector protector, IUrlGuard urlGuard, IMcpTokenService tokens,
        AppDbContext db, ILogger<McpConnectionFactory> logger)
    {
        _protector = protector;
        _urlGuard = urlGuard;
        _tokens = tokens;
        _db = db;
        _logger = logger;
    }

    public async Task<McpConnection> CreateAsync(McpServer server, CancellationToken ct)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"MCP server '{server.Name}' has no valid absolute http(s) URL");

        // Throws on a blocked target. Runs on every connection, not just on save:
        // a hostname that resolved publicly at configuration time can be re-pointed
        // at an internal address afterwards.
        _urlGuard.EnsureSafe(server.Url, allowPrivate: server.AllowPrivateNetwork);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ApplyStaticHeaders(headers, server.HeadersJson);

        var auth = McpAuthConfigCodec.Decrypt(server.AuthConfigEncrypted, _protector);

        if (server.AuthCredentialId is { } credentialId)
        {
            var credential = await _db.Credentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId && c.IsActive, ct);

            // A deleted credential degrades to whatever is stored rather than throwing,
            // so the failure shows up as an upstream 401 in the health probe instead of
            // taking the server out of the catalogue.
            if (credential is null)
                _logger.LogWarning(
                    "mcp.auth.credential_missing server={Server} credential={Credential}",
                    server.Name, credentialId);
            else
                auth = McpCredentialAuth.Merge(auth, credential, _protector);
        }

        switch ((server.AuthType ?? McpServer.AuthNone).ToLowerInvariant())
        {
            case McpServer.AuthApiKey:
                if (!string.IsNullOrEmpty(auth.ApiKey))
                {
                    var header = string.IsNullOrWhiteSpace(auth.ApiKeyHeader) ? "X-API-Key" : auth.ApiKeyHeader!;
                    headers[header] = auth.ApiKey!;
                }
                break;

            case McpServer.AuthBearer:
                if (!string.IsNullOrEmpty(auth.Token))
                    headers["Authorization"] = "Bearer " + auth.Token;
                break;

            case McpServer.AuthBasic:
                if (!string.IsNullOrEmpty(auth.Username) || !string.IsNullOrEmpty(auth.Password))
                    headers["Authorization"] = "Basic " + Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"));
                break;

            case McpServer.AuthHeaders:
                if (auth.SecretHeaders is not null)
                    foreach (var kv in auth.SecretHeaders)
                        headers[kv.Key] = kv.Value;
                break;

            case McpServer.AuthOAuthClientCredentials:
            case McpServer.AuthOAuthAuthorizationCode:
                // Both grants resolve through the token service: client-credentials
                // re-grants on expiry, authorization-code refreshes (or flips the
                // server to needs_authorization when only a browser can fix it).
                var token = await _tokens.GetValidAccessTokenAsync(server, ct);
                if (!string.IsNullOrEmpty(token))
                    headers["Authorization"] = "Bearer " + token;
                break;
        }

        return new McpConnection(endpoint, headers, server.TlsSkipVerify);
    }

    private static void ApplyStaticHeaders(Dictionary<string, string> headers, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                // Authorization comes from the auth material alone.
                if (p.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                headers[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Cosmetic headers; a broken blob must not block the connection.
        }
    }
}
