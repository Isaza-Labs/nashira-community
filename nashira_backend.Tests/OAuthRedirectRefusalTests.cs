using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;
using nashira_backend.Services.Worker.Handlers;
using IntegrationEntity = nashira_backend.Data.Models.Integration;
using Mapping = nashira_backend.Tests.RedirectRefusalMappingTests;

namespace nashira_backend.Tests;

// With a cold token cache the OAuth grant runs before the API request, so a redirect from the
// token endpoint surfaces from the auth step, not from the send. It must get the same final,
// non-retryable `redirect_refused` mapping, with one grant attempt and no call to the API.
public class OAuthRedirectRefusalTests
{
    private const string Code = "redirect_refused";

    [Fact]
    public async Task An_integration_action_with_a_cold_cache_reports_the_token_redirect_and_does_not_retry()
    {
        using var harness = await Harness.CreateAsync();
        using var scope = (ScopeHolder)harness.Scope();
        var handler = new IntegrationActionSnippetHandler(
            scope.GetRequiredService<AppDbContext>(), scope.GetRequiredService<IIntegrationAuthApplier>(),
            scope.GetRequiredService<ISecretResolver>(), new Mapping.AllowAllGuard(), harness.Factory,
            NullLogger<IntegrationActionSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(
            Mapping.Request(Snippet.TypeIntegrationAction, new { integration = "example-api", action = "get_status" }),
            default);

        Assert.False(result.Success);
        Assert.Equal(Code, result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Contains("token endpoint", result.Error);
        Assert.Contains("HTTP 302", result.Error);
        Assert.DoesNotContain(Mapping.LocationToken, result.Error);
        Assert.Equal(1, harness.Factory.Requests);
    }

    [Fact]
    public async Task A_catalogued_rest_operation_with_a_cold_cache_reports_the_token_redirect()
    {
        using var harness = await Harness.CreateAsync();
        using var scope = (ScopeHolder)harness.Scope();
        var logger = new RedirectTargetRefusalTests.CapturingLogger<RestOperationExecutor>();
        var executor = harness.Executor(scope, logger);

        var result = await executor.ExecuteAsync(
            "get_status", Empty(), Empty(), Empty(), default);

        Assert.Equal(Code, result.ErrorCode);
        Assert.False(result.Success);
        Assert.Contains("token endpoint", result.Error);
        Assert.DoesNotContain(Mapping.LocationToken, result.Error);
        Assert.DoesNotContain(Mapping.LocationToken, logger.Text);
        Assert.Equal(1, harness.Factory.Requests);
    }

    [Fact]
    public async Task The_agent_tool_with_a_cold_cache_carries_the_code_to_the_model()
    {
        using var harness = await Harness.CreateAsync();
        using var scope = (ScopeHolder)harness.Scope();
        var tool = new ExecuteOperationHandler(harness.Executor(scope, NullLogger<RestOperationExecutor>.Instance));

        var output = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation_id = "get_status" }), default);

        Assert.Equal(Code, output.GetProperty("error_code").GetString());
        Assert.DoesNotContain(Mapping.LocationToken, output.GetRawText());
        Assert.Equal(1, harness.Factory.Requests);
    }

    [Fact]
    public async Task An_integration_check_with_a_cold_cache_is_degraded_and_names_the_token_endpoint()
    {
        using var harness = await Harness.CreateAsync();
        using var scope = (ScopeHolder)harness.Scope();
        var checker = new IntegrationHealthChecker(
            scope.GetRequiredService<IIntegrationAuthApplier>(), scope.GetRequiredService<ISecretResolver>(),
            new Mapping.AllowAllGuard(), harness.Factory, NullLogger<IntegrationHealthChecker>.Instance);
        var row = await scope.GetRequiredService<AppDbContext>().Integrations.AsNoTracking().SingleAsync();

        var health = await checker.CheckAsync(row, default);

        Assert.Equal(IntegrationEntity.StatusDegraded, health.Status);
        // No probe was sent, so there is no probe status to report.
        Assert.Null(health.StatusCode);
        Assert.Contains("token endpoint", health.Error);
        Assert.DoesNotContain("probe was redirected", health.Error);
        Assert.Equal(1, harness.Factory.Requests);
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;
        public Mapping.RedirectingFactory Factory { get; } = new();

        private Harness()
        {
            var name = $"oauth-redirect-{Guid.NewGuid()}";
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<ISecretProtector>(new PlainProtector());
            services.AddSingleton<ITraceLogger>(new NullTrace());
            services.AddSingleton<IUrlGuard>(new Mapping.AllowAllGuard());
            services.AddSingleton<IHttpClientFactory>(Factory);
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = null });
            services.AddSingleton<IOptions<JwtOptions>>(Options.Create(new JwtOptions
            {
                Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                Key = "test-signing-key-that-is-long-enough-for-hmac-256",
            }));
            services.AddScoped<IJwtTokenService, JwtTokenService>();
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

        // Literal OAuth settings, so the grant itself is permitted for any caller: what is under
        // test is what happens when the token endpoint answers with a redirect.
        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            using var scope = harness._root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var integrationId = Guid.NewGuid();
            db.Integrations.Add(new IntegrationEntity
            {
                IntegrationId = integrationId, Name = "example-api", Slug = "example-api", Type = "generic",
                BaseUrl = "https://203.0.113.10", HealthCheckPath = "/status",
                AuthConfig = JsonSerializer.Serialize(new
                {
                    method = "oauth2_client_credentials", token_url = "https://203.0.113.10/oauth/token",
                    client_id = "example-client", client_secret = "example-client-secret",
                }),
            });
            db.IntegrationActions.Add(new IntegrationAction
            {
                IntegrationActionId = Guid.NewGuid(), IntegrationId = integrationId, OperationId = "get_status",
                Name = "get-status", Method = "GET", Path = "/status", ReadOnly = true,
            });
            db.AiApiSpecs.Add(new AiApiSpec
            {
                AiApiSpecId = Guid.NewGuid(), Api = "example-api", Content = "openapi: 3.0.0", OperationCount = 1,
                IntegrationId = integrationId, AuthType = "none", IsActive = true,
            });
            await db.SaveChangesAsync();
            return harness;
        }

        public IServiceProvider Scope() => new ScopeHolder(_root.CreateScope());

        public RestOperationExecutor Executor(IServiceProvider scope, ILogger<RestOperationExecutor> logger) => new(
            scope.GetRequiredService<AppDbContext>(), new Mapping.OneOperationIndex(),
            scope.GetRequiredService<ISecretResolver>(), new Mapping.AllowAllGuard(), new HardeningTestUser(),
            Factory, scope.GetRequiredService<IIntegrationAuthApplier>(), new ConfigurationBuilder().Build(), logger);

        public void Dispose() => _root.Dispose();
    }

    // Lets the tests `using` a scope and use it as its own service provider.
    internal sealed class ScopeHolder(IServiceScope scope) : IServiceProvider, IDisposable
    {
        public object? GetService(Type serviceType) => scope.ServiceProvider.GetService(serviceType);
        public void Dispose() => scope.Dispose();
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
