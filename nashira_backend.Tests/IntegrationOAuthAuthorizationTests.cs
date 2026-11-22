using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// An OAuth access token is secret-derived: whoever may not resolve the credentials it was
// granted from may not use it either, and a step that used it must not publish its output.
// These run the real applier, token service and resolver (registered the way Program.cs
// registers them) against a stub token endpoint.
public class IntegrationOAuthAuthorizationTests
{
    private const string Token = "example-api-token-0001";
    private const string ClientSecretRef = "${secret:secret:oauth-client:value}";

    [Fact]
    public async Task A_token_an_admin_cached_is_not_served_to_an_operator()
    {
        using var h = new Harness();
        await h.SeedAsync(literalConfig: true);

        await h.ApplyAsync("admin");
        Assert.Equal(1, h.TokenRequests);

        var error = await Assert.ThrowsAsync<ForbiddenException>(() => h.ApplyAsync("operator"));

        Assert.Equal(SecretResolver.AccessDeniedCode, error.Code);
        Assert.DoesNotContain(Token, error.Message);
        Assert.Equal(1, h.TokenRequests);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    [InlineData(null)]
    public async Task A_non_admin_is_denied_before_any_grant_is_attempted(string? role)
    {
        using var h = new Harness();
        await h.SeedAsync(literalConfig: true);

        await Assert.ThrowsAsync<ForbiddenException>(() => h.ApplyAsync(role));

        Assert.Equal(0, h.TokenRequests);
    }

    [Fact]
    public async Task Every_use_of_the_token_marks_the_execution_as_holding_resolved_material()
    {
        using var h = new Harness();
        await h.SeedAsync(literalConfig: true);

        var (_, firstUse) = await h.ApplyAsync("admin");
        var (header, cachedUse) = await h.ApplyAsync("admin");

        Assert.True(firstUse);
        Assert.Equal(1, h.TokenRequests);
        Assert.Equal($"Bearer {Token}", header);
        Assert.True(cachedUse, "a cache hit resolves nothing, but the bearer is still secret-derived");
    }

    [Fact]
    public async Task A_background_caller_keeps_its_identity_while_the_grant_resolves_stored_secrets()
    {
        using var h = new Harness();
        await h.SeedAsync(literalConfig: false);

        // No HTTP context, the caller bound on the outer scope only: the grant opens a scope of its own.
        var (header, _) = await h.ApplyAsync("admin");

        Assert.Equal($"Bearer {Token}", header);
        Assert.Equal(1, h.TokenRequests);
        // The reference was resolved (as the bound admin) into the token request.
        Assert.Equal("example-client:example-client-secret", h.LastTokenRequestCredentials);
    }

    [Fact]
    public async Task A_background_scope_with_no_identity_still_fails_closed_without_a_token_request()
    {
        using var h = new Harness();
        await h.SeedAsync(literalConfig: false);

        await Assert.ThrowsAsync<ForbiddenException>(() => h.ApplyAsync(role: null));

        Assert.Equal(0, h.TokenRequests);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly Guid _integrationId = Guid.NewGuid();
        private readonly StubTokenEndpoint _endpoint = new();

        public int TokenRequests => _endpoint.Requests;
        public string? LastTokenRequestCredentials => _endpoint.LastCredentials;

        public Harness()
        {
            var name = $"oauth-authorization-{Guid.NewGuid()}";
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<ISecretProtector>(new PlainProtector());
            services.AddSingleton<ITraceLogger>(new NullTrace());
            services.AddSingleton<IUrlGuard>(new AllowAllGuard());
            services.AddSingleton<IHttpClientFactory>(new StubFactory(_endpoint));
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = null });
            services.AddSingleton<IOptions<JwtOptions>>(Options.Create(new JwtOptions
            {
                Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                Key = "test-signing-key-that-is-long-enough-for-hmac-256",
            }));
            services.AddScoped<IJwtTokenService, JwtTokenService>();
            // The same context-dependent registration Program.cs uses.
            services.AddScoped<CurrentUser>();
            services.AddScoped<MutableCurrentUser>();
            services.AddScoped<ICurrentUser>(sp =>
                sp.GetRequiredService<IHttpContextAccessor>().HttpContext is null
                    ? sp.GetRequiredService<MutableCurrentUser>()
                    : sp.GetRequiredService<CurrentUser>());
            services.AddScoped<ISecretResolver, SecretResolver>();
            services.AddSingleton<IIntegrationOAuthTokenService, IntegrationOAuthTokenService>();
            services.AddScoped<IIntegrationAuthApplier, IntegrationAuthApplier>();
            _root = services.BuildServiceProvider();
        }

        public async Task SeedAsync(bool literalConfig)
        {
            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Secrets.Add(new Secret
            {
                SecretId = Guid.NewGuid(), Name = "oauth-client", EncryptedValue = Encoding.UTF8.GetBytes("example-client-secret"),
            });
            db.Integrations.Add(new Integration
            {
                IntegrationId = _integrationId, Name = "example-api", Slug = "example-api", Type = "generic",
                BaseUrl = "https://203.0.113.10",
                AuthConfig = System.Text.Json.JsonSerializer.Serialize(new
                {
                    method = "oauth2_client_credentials",
                    token_url = "https://203.0.113.10/oauth/token",
                    client_id = "example-client",
                    client_secret = literalConfig ? "example-client-secret" : ClientSecretRef,
                }),
            });
            await db.SaveChangesAsync();
        }

        // Applies the integration's auth as a caller with the given role on a scope of its
        // own. Returns the Authorization header and whether the execution was marked.
        public async Task<(string? Header, bool Marked)> ApplyAsync(string? role)
        {
            using var scope = _root.CreateScope();
            if (role is not null)
                scope.ServiceProvider.GetRequiredService<MutableCurrentUser>().Bind(Guid.NewGuid(), "test", [role]);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var integration = await db.Integrations.AsNoTracking().SingleAsync(i => i.IntegrationId == _integrationId);
            using var marker = new SecretResolutionScope();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://203.0.113.10/status");
            await scope.ServiceProvider.GetRequiredService<IIntegrationAuthApplier>()
                .ApplyAsync(request, integration, default);
            return (request.Headers.Authorization?.ToString(), marker.HasResolvedMaterial);
        }

        public void Dispose() => _root.Dispose();
    }

    private sealed class StubTokenEndpoint : HttpMessageHandler
    {
        public int Requests;
        public string? LastCredentials;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            LastCredentials = request.Headers.Authorization is { Scheme: "Basic", Parameter: { } p }
                ? Encoding.UTF8.GetString(Convert.FromBase64String(p)) : null;
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{Token}}","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class AllowAllGuard : IUrlGuard
    {
        public void EnsureSafe(string url, bool allowPrivate = false) { }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
    }

    private sealed class NullTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null) { }
        public ITraceScope Begin(string category, string action, object? metadata = null) => throw new NotSupportedException();
    }
}
