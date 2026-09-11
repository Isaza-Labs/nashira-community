using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// The MCP OAuth authorization-code flow, ported from FlowWeaver together with its
// tests (McpOAuthFlowServiceTests / McpTokenServiceTests there). Start mints a
// signed state + PKCE and persists the verifier; the callback validates
// state/nonce, exchanges the code, and stores tokens; the token service refreshes
// with the refresh token or flips the server to needs_authorization.
public class McpOAuthFlowTests
{
    private static readonly FakeProtector Crypto = new();

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"mcp-oauth-{Guid.NewGuid()}")
        .Options);

    private static Guid AddServer(AppDbContext db, string authType, McpAuthConfig auth)
    {
        var id = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = id, Name = "srv", Url = "https://mcp.example.com",
            AuthType = authType, Enabled = true, IsActive = true,
            Status = Integration.StatusUnknown,
            AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(auth, Crypto),
        });
        db.SaveChanges();
        return id;
    }

    private static (McpOAuthFlowService Svc, StubMcpOAuthService OAuth, AppDbContext Db, Guid Id)
        FlowSetup(string authType, McpAuthConfig auth)
    {
        var db = NewDb();
        var id = AddServer(db, authType, auth);
        var oauth = new StubMcpOAuthService();
        var svc = new McpOAuthFlowService(
            db, oauth, Crypto, new EphemeralDataProtectionProvider(),
            new ConfigurationBuilder().Build(), NullLogger<McpOAuthFlowService>.Instance);
        return (svc, oauth, db, id);
    }

    private static string Extract(string url, string key)
        => Uri.UnescapeDataString(Regex.Match(url, $"[?&]{key}=([^&]+)").Groups[1].Value);

    // ── the redirect flow ───────────────────────────────────────────────

    [Fact]
    public async Task Start_rejects_non_authorization_code_servers()
    {
        var (svc, _, _, id) = FlowSetup(McpServer.AuthOAuthClientCredentials, new McpAuthConfig());
        var res = await svc.StartAsync(id, "https://backend/cb");
        Assert.NotNull(res.Error);
        Assert.Null(res.AuthorizationUrl);
    }

    [Fact]
    public async Task Start_builds_authorization_url_and_persists_verifier()
    {
        var (svc, oauth, db, id) = FlowSetup(
            McpServer.AuthOAuthAuthorizationCode, new McpAuthConfig { ClientId = "cid" });

        var res = await svc.StartAsync(id, "https://backend/cb");

        Assert.Null(res.Error);
        Assert.NotNull(res.AuthorizationUrl);
        Assert.StartsWith("https://as/authorize?", res.AuthorizationUrl);
        Assert.Contains("client_id=cid", res.AuthorizationUrl);
        Assert.Contains("code_challenge=CHALLENGE", res.AuthorizationUrl);
        Assert.Contains("code_challenge_method=S256", res.AuthorizationUrl);
        Assert.Contains("state=", res.AuthorizationUrl);

        var stored = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, Crypto);
        Assert.Equal(oauth.Pkce.verifier, stored.CodeVerifier);
        Assert.Equal(oauth.Nonce, stored.StateNonce);
        Assert.Equal(McpServer.StatusNeedsAuthorization, db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Callback_exchanges_code_and_stores_tokens()
    {
        var (svc, oauth, db, id) = FlowSetup(
            McpServer.AuthOAuthAuthorizationCode, new McpAuthConfig { ClientId = "cid" });
        var start = await svc.StartAsync(id, "https://backend/cb");
        var state = Extract(start.AuthorizationUrl!, "state");
        oauth.Tokens = new McpOAuthTokens("ACCESS", "REFRESH", DateTime.UtcNow.AddHours(1));

        var redirect = await svc.HandleCallbackAsync("the-code", state, null, "https://backend/cb");

        Assert.Contains($"mcp_authorized={id}", redirect);
        Assert.Contains("/admin/mcp", redirect);
        Assert.Equal(1, oauth.ExchangeCalls);
        var stored = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, Crypto);
        Assert.Equal("ACCESS", stored.AccessToken);
        Assert.Equal("REFRESH", stored.RefreshToken);
        Assert.Null(stored.CodeVerifier);   // one-time, cleared
        Assert.Null(stored.StateNonce);
        Assert.Equal(Integration.StatusHealthy, db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Callback_rejects_tampered_state()
    {
        var (svc, oauth, _, _) = FlowSetup(
            McpServer.AuthOAuthAuthorizationCode, new McpAuthConfig { ClientId = "cid" });

        var redirect = await svc.HandleCallbackAsync("code", "not-a-valid-state", null, "https://backend/cb");

        Assert.Contains("mcp_error=invalid_state", redirect);
        Assert.Equal(0, oauth.ExchangeCalls);
    }

    [Fact]
    public async Task Callback_with_error_marks_needs_authorization()
    {
        var (svc, oauth, db, id) = FlowSetup(
            McpServer.AuthOAuthAuthorizationCode, new McpAuthConfig { ClientId = "cid" });
        var start = await svc.StartAsync(id, "https://backend/cb");
        var state = Extract(start.AuthorizationUrl!, "state");

        var redirect = await svc.HandleCallbackAsync(null, state, "access_denied", "https://backend/cb");

        Assert.Contains("mcp_error=access_denied", redirect);
        Assert.Equal(0, oauth.ExchangeCalls);
        Assert.Equal(McpServer.StatusNeedsAuthorization, db.McpServers.Single().Status);
    }

    // A replay of a validly-signed state AFTER a successful authorize (verifier
    // already cleared) must not downgrade a healthy server.
    [Fact]
    public async Task Callback_replay_after_success_cannot_downgrade_the_server()
    {
        var (svc, oauth, db, id) = FlowSetup(
            McpServer.AuthOAuthAuthorizationCode, new McpAuthConfig { ClientId = "cid" });
        var start = await svc.StartAsync(id, "https://backend/cb");
        var state = Extract(start.AuthorizationUrl!, "state");
        oauth.Tokens = new McpOAuthTokens("ACCESS", "REFRESH", DateTime.UtcNow.AddHours(1));
        await svc.HandleCallbackAsync("the-code", state, null, "https://backend/cb");

        var replay = await svc.HandleCallbackAsync("the-code", state, null, "https://backend/cb");

        Assert.Contains("mcp_error=invalid_callback", replay);
        Assert.Equal(Integration.StatusHealthy, db.McpServers.Single().Status);
    }

    // ── the token service's authorization-code path ─────────────────────

    private static (McpTokenService Svc, StubMcpOAuthService OAuth, AppDbContext Db, Guid Id)
        TokenSetup(string authType, McpAuthConfig auth)
    {
        var db = NewDb();
        var id = AddServer(db, authType, auth);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var oauth = new StubMcpOAuthService();
        var svc = new McpTokenService(
            scopes, Crypto, new PermissiveGuard(), new PlainHttpFactory(), oauth,
            NullLogger<McpTokenService>.Instance);
        return (svc, oauth, db, id);
    }

    private static Task<McpServer> Load(AppDbContext db, Guid id)
        => db.McpServers.AsNoTracking().SingleAsync(s => s.McpServerId == id);

    [Fact]
    public async Task Returns_cached_token_when_valid()
    {
        var (svc, oauth, db, id) = TokenSetup(McpServer.AuthOAuthAuthorizationCode,
            new McpAuthConfig { AccessToken = "cached", ExpiresAt = DateTime.UtcNow.AddHours(1) });

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id), default);

        Assert.Equal("cached", token);
        Assert.Equal(0, oauth.RefreshCalls);
    }

    [Fact]
    public async Task Authorization_code_refreshes_with_refresh_token()
    {
        var (svc, oauth, db, id) = TokenSetup(McpServer.AuthOAuthAuthorizationCode,
            new McpAuthConfig
            {
                AccessToken = "old", ExpiresAt = DateTime.UtcNow.AddMinutes(-1), RefreshToken = "rt",
            });
        oauth.Tokens = new McpOAuthTokens("REFRESHED", "rt2", DateTime.UtcNow.AddHours(1));

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id), default);

        Assert.Equal("REFRESHED", token);
        Assert.Equal(1, oauth.RefreshCalls);
        var stored = McpAuthConfigCodec.Decrypt(db.McpServers.Single().AuthConfigEncrypted, Crypto);
        Assert.Equal("REFRESHED", stored.AccessToken);
        Assert.Equal("rt2", stored.RefreshToken);
        Assert.Equal(Integration.StatusHealthy, db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Authorization_code_without_refresh_needs_authorization()
    {
        var (svc, _, db, id) = TokenSetup(McpServer.AuthOAuthAuthorizationCode,
            new McpAuthConfig { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id), default);

        Assert.Null(token);
        Assert.Equal(McpServer.StatusNeedsAuthorization, db.McpServers.Single().Status);
    }

    [Fact]
    public async Task Refresh_failure_flips_to_needs_authorization()
    {
        var (svc, oauth, db, id) = TokenSetup(McpServer.AuthOAuthAuthorizationCode,
            new McpAuthConfig
            {
                AccessToken = "old", ExpiresAt = DateTime.UtcNow.AddMinutes(-1), RefreshToken = "rt",
            });
        oauth.ThrowOnGrant = new InvalidOperationException("token endpoint returned 400");

        var token = await svc.GetValidAccessTokenAsync(await Load(db, id), default);

        Assert.Null(token);
        Assert.Equal(McpServer.StatusNeedsAuthorization, db.McpServers.Single().Status);
    }

    // ── doubles ─────────────────────────────────────────────────────────

    private sealed class FakeProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext)
            => plaintext is null ? null : Encoding.UTF8.GetBytes(plaintext);
        public string? Decrypt(byte[]? ciphertext)
            => ciphertext is null ? null : Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class PermissiveGuard : IUrlGuard
    {
        public void EnsureSafe(string url, bool allowPrivate = false) { }
    }

    private sealed class PlainHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

// Configurable IMcpOAuthService double — the same shape FlowWeaver's tests use.
internal sealed class StubMcpOAuthService : IMcpOAuthService
{
    public McpOAuthTokens Tokens = new("AT", "RT", null);
    public McpOAuthMetadata Metadata = new("https://as/authorize", "https://as/token", null, null);
    public Exception? ThrowOnGrant;
    public int ClientCredentialsCalls;
    public int RefreshCalls;
    public int ExchangeCalls;
    public (string verifier, string challenge) Pkce = ("VERIFIER-0000000000000000000000000000000", "CHALLENGE");
    public string Nonce = "NONCE-123";

    public Task<McpOAuthMetadata> ResolveMetadataAsync(
        Data.Models.McpServer server, McpAuthConfig auth, CancellationToken ct = default)
        => Task.FromResult(Metadata);

    public Task<(string clientId, string? clientSecret)> RegisterClientAsync(
        Data.Models.McpServer server, string registrationEndpoint, string redirectUri, CancellationToken ct = default)
        => Task.FromResult<(string, string?)>(("dcr-client", null));

    public Task<McpOAuthTokens> ClientCredentialsAsync(
        Data.Models.McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, CancellationToken ct = default)
    {
        ClientCredentialsCalls++;
        if (ThrowOnGrant is not null) throw ThrowOnGrant;
        return Task.FromResult(Tokens);
    }

    public Task<McpOAuthTokens> ExchangeCodeAsync(
        Data.Models.McpServer server, McpOAuthMetadata meta, McpAuthConfig auth,
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        ExchangeCalls++;
        if (ThrowOnGrant is not null) throw ThrowOnGrant;
        return Task.FromResult(Tokens);
    }

    public Task<McpOAuthTokens> RefreshAsync(
        Data.Models.McpServer server, McpOAuthMetadata meta, McpAuthConfig auth, string refreshToken,
        CancellationToken ct = default)
    {
        RefreshCalls++;
        if (ThrowOnGrant is not null) throw ThrowOnGrant;
        return Task.FromResult(Tokens);
    }

    public (string verifier, string challenge) GeneratePkce() => Pkce;
    public string GenerateNonce() => Nonce;
}
