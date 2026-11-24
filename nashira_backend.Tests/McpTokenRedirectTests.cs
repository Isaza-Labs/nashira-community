using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Worker.Handlers;

namespace nashira_backend.Tests;

// An MCP server that authenticates with OAuth gets its bearer token from the token service. When the
// token endpoint (or the metadata that points at it) answers with a redirect, the request is refused.
// That has to reach the MCP caller as the final `redirect_refused`: not as a swallowed failure that
// leaves the connection without a bearer, and not as a status that tells an admin to re-authorize
// against the same redirect. The cases run the real connection factory, token service, OAuth service,
// server service and client, with a transport that counts what it is sent.
public class McpTokenRedirectTests
{
    private const string Code = "redirect_refused";
    private static readonly PlainProtector Crypto = new();
    private static readonly object Input = new { server = "example-mcp", tool = "example_tool" };

    public static TheoryData<string> Grants => new()
    {
        "auth_code_refresh", "auth_code_metadata", "client_credentials_grant", "client_credentials_metadata",
    };

    private sealed class Rig : IDisposable
    {
        public required AppDbContext Db { get; init; }
        public required Guid ServerId { get; init; }
        public required RedirectRefusalMappingTests.RedirectingFactory TokenHttp { get; init; }
        public required RedirectRefusalMappingTests.RedirectingFactory McpHttp { get; init; }
        public required CapturingLoggerFactory Logs { get; init; }
        public required McpServerService Service { get; init; }
        public void Dispose() => Db.Dispose();
    }

    private static Rig Build(string kind, IHttpClientFactory? tokenHttpOverride = null)
    {
        var isAuthCode = kind.StartsWith("auth_code", StringComparison.Ordinal);
        var viaMetadata = kind.EndsWith("metadata", StringComparison.Ordinal);

        var db = RedirectRefusalMappingTests.NewDb();
        var serverId = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = serverId, Name = "example-mcp", Url = "https://203.0.113.30/mcp",
            AuthType = isAuthCode ? McpServer.AuthOAuthAuthorizationCode : McpServer.AuthOAuthClientCredentials,
            IsActive = true, Enabled = true, Status = Integration.StatusHealthy,
            AuthConfigEncrypted = McpAuthConfigCodec.Encrypt(new McpAuthConfig
            {
                AccessToken = "example-expired-access-token",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
                RefreshToken = isAuthCode ? "example-refresh-token" : null,
                ClientId = "example-client", ClientSecret = "example-client-secret",
                // With no token URL the endpoint is discovered, and the discovery request is what redirects.
                TokenUrl = viaMetadata ? null : "https://203.0.113.40/token",
            }, Crypto),
        });
        db.McpTools.Add(new McpTool
        {
            McpToolId = Guid.NewGuid(), McpServerId = serverId, Name = "example_tool", IsActive = true, Enabled = true,
        });
        db.SaveChanges();

        var logs = new CapturingLoggerFactory();
        var guard = new RedirectRefusalMappingTests.AllowAllGuard();
        var tokenHttp = new RedirectRefusalMappingTests.RedirectingFactory();
        var mcpHttp = new RedirectRefusalMappingTests.RedirectingFactory();
        var scopes = new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        IHttpClientFactory http = tokenHttpOverride ?? tokenHttp;

        var tokens = new McpTokenService(
            scopes, Crypto, guard, http, new McpOAuthService(http, guard, logs.For<McpOAuthService>()),
            logs.For<McpTokenService>());
        var connections = new McpConnectionFactory(Crypto, guard, tokens, db, logs.For<McpConnectionFactory>());
        var service = new McpServerService(
            db, new McpClient(connections, mcpHttp, logs), logs.For<McpServerService>());

        return new Rig
        {
            Db = db, ServerId = serverId, TokenHttp = tokenHttp, McpHttp = mcpHttp, Logs = logs, Service = service,
        };
    }

    [Theory]
    [MemberData(nameof(Grants))]
    public async Task A_redirecting_token_or_metadata_endpoint_is_final_for_both_callers_and_sends_nothing_anonymously(string kind)
    {
        using var rig = Build(kind);

        var step = await new McpCallSnippetHandler(rig.Db, rig.Service)
            .ExecuteAsync(RedirectRefusalMappingTests.Request("mcp_call", Input), default);
        var tool = await new McpCallHandler(
                rig.Db, new HardeningTestUser(), rig.Service, rig.Logs.For<McpCallHandler>())
            .ExecuteAsync(JsonSerializer.SerializeToElement(Input), default);

        RedirectRefusalMappingTests.AssertRefusal(step, 302);
        Assert.Equal(Code, tool.GetProperty("error_code").GetString());
        Assert.Contains("HTTP 302", tool.GetProperty("error").GetString());

        // The redirected request was made and refused (the endpoint was asked once per call); the MCP
        // server itself was never contacted, with the stale token or without one.
        Assert.True(rig.TokenHttp.Requests >= 1);
        Assert.Equal(0, rig.McpHttp.Requests);

        // Nothing from the redirect is in a result or a log line.
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, JsonSerializer.Serialize(step));
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, tool.GetRawText());
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, rig.Logs.Text);
        Assert.DoesNotContain("example-client-secret", rig.Logs.Text);
        Assert.DoesNotContain("example-refresh-token", rig.Logs.Text);
    }

    [Theory]
    [MemberData(nameof(Grants))]
    public async Task A_redirecting_endpoint_does_not_flip_the_server_to_needs_authorization(string kind)
    {
        using var rig = Build(kind);

        await new McpCallSnippetHandler(rig.Db, rig.Service)
            .ExecuteAsync(RedirectRefusalMappingTests.Request("mcp_call", Input), default);

        var row = await rig.Db.McpServers.AsNoTracking().SingleAsync(s => s.McpServerId == rig.ServerId);
        Assert.Equal(Integration.StatusHealthy, row.Status);
    }

    [Theory]
    [MemberData(nameof(Grants))]
    public async Task The_connectivity_check_records_only_the_fixed_refusal_text(string kind)
    {
        using var rig = Build(kind);

        var health = await rig.Service.CheckAsync(rig.ServerId, default);

        var row = await rig.Db.McpServers.AsNoTracking().SingleAsync(s => s.McpServerId == rig.ServerId);
        Assert.Equal(Integration.StatusUnreachable, row.Status);
        Assert.Contains("configure the final endpoint", row.LastCheckError);
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, row.LastCheckError);
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, health.Error ?? "");
        Assert.Equal(0, rig.McpHttp.Requests);
    }

    // The refusal is the only failure that leaves the status alone and is final. Any other refresh
    // failure still means the grant is gone: the server needs authorizing again.
    [Fact]
    public async Task An_ordinary_refresh_failure_still_marks_the_server_as_needing_authorization()
    {
        using var rig = Build("auth_code_refresh", tokenHttpOverride: new StatusFactory(HttpStatusCode.BadRequest));
        var server = await rig.Db.McpServers.SingleAsync(s => s.McpServerId == rig.ServerId);
        var tokens = new McpTokenService(
            new ServiceCollection().AddSingleton(rig.Db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Crypto, new RedirectRefusalMappingTests.AllowAllGuard(), new StatusFactory(HttpStatusCode.BadRequest),
            new McpOAuthService(new StatusFactory(HttpStatusCode.BadRequest),
                new RedirectRefusalMappingTests.AllowAllGuard(), rig.Logs.For<McpOAuthService>()),
            rig.Logs.For<McpTokenService>());

        var token = await tokens.GetValidAccessTokenAsync(server, default);

        Assert.Null(token);
        Assert.Equal(McpServer.StatusNeedsAuthorization, server.Status);
    }

    private sealed class StatusFactory(HttpStatusCode status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Stub(status));

        private sealed class Stub(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
    }

    // Everything any component logs, message and exception text together.
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _lines = [];
        public string Text { get { lock (_lines) return string.Join('\n', _lines); } }
        public ILogger<T> For<T>() => new Typed<T>(this);
        public ILogger CreateLogger(string categoryName) => new Typed<object>(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private void Add(string line) { lock (_lines) _lines.Add(line); }

        private sealed class Typed<T>(CapturingLoggerFactory owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
        }
    }
}
