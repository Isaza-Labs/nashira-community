using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Git;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Tests;

// The guarded HTTP clients refuse to follow a redirect. That refusal is a configuration fault
// (the endpoint moved), so every caller that can say so reports `redirect_refused`, does not
// retry it, and never echoes the Location header, which belongs to the remote server. Before
// this, the refusal surfaced as an unmapped exception: `exception` in a workflow step, a retried
// `rest_error` for a catalogued call, "unreachable" for an integration check and an unhandled
// error from the GitHub and NetBox services.
public class RedirectRefusalMappingTests
{
    private const string Code = "redirect_refused";
    internal const string LocationToken = "example-location-token";

    internal static readonly string Target = $"https://203.0.113.99/next?t={LocationToken}";

    // A transport that always answers with a redirect, behind the real refusing handler.
    // `location` and `guard` let a test point the redirect at a target the real validator rejects.
    internal sealed class RedirectingFactory(
        HttpStatusCode status = HttpStatusCode.Found, string? location = null, IUrlGuard? guard = null)
        : IHttpClientFactory
    {
        public int Requests;

        public HttpClient CreateClient(string name) => new(new RejectRedirectHandler(guard ?? new AllowAllGuard())
        {
            InnerHandler = new Transport(this, status, location ?? Target),
        });

        private sealed class Transport(RedirectingFactory owner, HttpStatusCode status, string location)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.Requests);
                var response = new HttpResponseMessage(status);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return Task.FromResult(response);
            }
        }
    }

    internal sealed class AllowAllGuard : IUrlGuard
    {
        public void EnsureSafe(string url, bool allowPrivate = false) { }
    }

    internal sealed class PassthroughSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string> SubstituteAsync(string template, CancellationToken ct) => Task.FromResult(template);
        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
            Task.FromResult(template);
    }

    internal sealed class NoAuth : IIntegrationAuthApplier
    {
        public Task ApplyAsync(HttpRequestMessage request, IntegrationEntity integration, CancellationToken ct) =>
            Task.CompletedTask;
    }

    internal sealed class OneOperationIndex : IApiSpecIndex
    {
        private readonly ApiOperation _op = new()
        {
            OperationId = "get_status", Api = "example-api", Method = "GET", Path = "/status",
        };
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [_op];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [_op];
        public ApiOperation? GetByOperationId(string operationId) => operationId == _op.OperationId ? _op : null;
    }

    internal sealed class UnusedExecutor : IRestOperationExecutor
    {
        public Task<RestExecutionResult> ExecuteAsync(
            string operationId, JsonElement pathParams, JsonElement queryParams, JsonElement body,
            CancellationToken ct, string? source = null) => throw new InvalidOperationException("not used");
    }

    internal static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"redirect-refusal-{Guid.NewGuid()}").Options);

    internal static async Task<RestOperationExecutor> NewExecutorAsync(
        AppDbContext db, IHttpClientFactory factory, ILogger<RestOperationExecutor>? logger = null)
    {
        db.AiApiSpecs.Add(new AiApiSpec
        {
            AiApiSpecId = Guid.NewGuid(), Api = "example-api", Content = "openapi: 3.0.0", OperationCount = 1,
            BaseUrl = "https://203.0.113.20", AuthType = "none", IsActive = true,
        });
        await db.SaveChangesAsync();
        return new RestOperationExecutor(
            db, new OneOperationIndex(), new PassthroughSecrets(), new AllowAllGuard(), new HardeningTestUser(),
            factory, new NoAuth(), new ConfigurationBuilder().Build(), logger ?? NullLogger<RestOperationExecutor>.Instance);
    }

    internal static SnippetRequest Request(string type, object input) => new()
    {
        NodeId = "step", WorkflowId = Guid.NewGuid(), SnippetId = Guid.NewGuid(), SnippetType = type,
        Input = JsonSerializer.SerializeToElement(input), TimeoutSeconds = 10,
    };

    internal static void AssertRefusal(SnippetResult result, int status)
    {
        Assert.False(result.Success);
        Assert.Equal(Code, result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Contains($"HTTP {status}", result.Error);
        Assert.Contains("configure the final endpoint", result.Error);
        Assert.DoesNotContain(LocationToken, result.Error);
        Assert.DoesNotContain(LocationToken, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.RedirectMethod)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Rest_call_raw_form_reports_redirect_refused_and_does_not_retry(HttpStatusCode status)
    {
        var factory = new RedirectingFactory(status);
        var handler = new RestCallSnippetHandler(new UnusedExecutor(), new AllowAllGuard(), new PassthroughSecrets(), factory);

        var result = await handler.ExecuteAsync(
            Request(Snippet.TypeRestCall, new { url = "https://203.0.113.20/status", method = "GET" }), default);

        AssertRefusal(result, (int)status);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task Rest_call_catalogued_form_reports_redirect_refused_and_does_not_retry()
    {
        using var db = NewDb();
        var factory = new RedirectingFactory();
        var executor = await NewExecutorAsync(db, factory);
        var handler = new RestCallSnippetHandler(executor, new AllowAllGuard(), new PassthroughSecrets(), factory);

        var result = await handler.ExecuteAsync(
            Request(Snippet.TypeRestCall, new { operation_id = "get_status" }), default);

        AssertRefusal(result, 302);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task A_transport_failure_in_the_catalogued_form_is_still_the_retryable_rest_error()
    {
        using var db = NewDb();
        var factory = new FailingFactory();
        var executor = await NewExecutorAsync(db, factory);
        var handler = new RestCallSnippetHandler(executor, new AllowAllGuard(), new PassthroughSecrets(), factory);

        var result = await handler.ExecuteAsync(
            Request(Snippet.TypeRestCall, new { operation_id = "get_status" }), default);

        Assert.False(result.Success);
        Assert.Equal("rest_error", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    private sealed class FailingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Throwing());

        private sealed class Throwing : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("example connection refused");
        }
    }

    [Fact]
    public async Task The_chat_tool_reports_the_refusal_with_an_error_code_and_no_location()
    {
        using var db = NewDb();
        var factory = new RedirectingFactory();
        var tool = new ExecuteOperationHandler(await NewExecutorAsync(db, factory));

        var output = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation_id = "get_status" }), default);

        Assert.Equal(Code, output.GetProperty("error_code").GetString());
        Assert.False(output.GetProperty("success").GetBoolean());
        Assert.Contains("redirects are disabled", output.GetProperty("error").GetString());
        Assert.DoesNotContain(LocationToken, output.GetRawText());
    }

    [Fact]
    public async Task Ordinary_tool_results_do_not_gain_an_error_code_field()
    {
        using var db = NewDb();
        var tool = new ExecuteOperationHandler(await NewExecutorAsync(db, new RedirectingFactory()));

        var output = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { operation_id = "no_such_operation" }), default);

        Assert.Contains("not found", output.GetProperty("error").GetString());
        Assert.False(output.TryGetProperty("error_code", out _));
    }

    [Fact]
    public async Task An_integration_action_reports_redirect_refused_and_does_not_retry()
    {
        using var db = NewDb();
        var integrationId = Guid.NewGuid();
        db.Integrations.Add(new IntegrationEntity
        {
            IntegrationId = integrationId, Name = "example-api", Slug = "example-api", Type = "generic",
            BaseUrl = "https://203.0.113.10",
        });
        db.IntegrationActions.Add(new IntegrationAction
        {
            IntegrationActionId = Guid.NewGuid(), IntegrationId = integrationId, OperationId = "get_status",
            Name = "get-status", Method = "GET", Path = "/status", ReadOnly = true,
        });
        await db.SaveChangesAsync();
        var factory = new RedirectingFactory();
        var handler = new IntegrationActionSnippetHandler(
            db, new NoAuth(), new PassthroughSecrets(), new AllowAllGuard(), factory,
            NullLogger<IntegrationActionSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(
            Request(Snippet.TypeIntegrationAction, new { integration = "example-api", action = "get_status" }), default);

        AssertRefusal(result, 302);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task An_integration_check_that_is_redirected_is_degraded_not_unreachable()
    {
        var checker = new IntegrationHealthChecker(
            new NoAuth(), new PassthroughSecrets(), new AllowAllGuard(), new RedirectingFactory(),
            NullLogger<IntegrationHealthChecker>.Instance);

        var health = await checker.CheckAsync(new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(), Name = "example-api", Slug = "example-api", Type = "generic",
            BaseUrl = "https://203.0.113.10", HealthCheckPath = "/status",
        }, default);

        Assert.Equal(IntegrationEntity.StatusDegraded, health.Status);
        Assert.Equal(302, health.StatusCode);
        Assert.Contains("redirected (HTTP 302)", health.Error);
        Assert.Contains("configure the final endpoint", health.Error);
        Assert.DoesNotContain(LocationToken, health.Error);
    }

    private sealed class TokenProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) => plaintext is null ? null : [1];
        public string? Decrypt(byte[]? ciphertext) => ciphertext is null ? null : "example-token";
    }

    [Fact]
    public async Task The_github_service_reports_a_redirect_as_a_validation_error_with_the_code()
    {
        using var db = NewDb();
        var credentialId = Guid.NewGuid();
        db.Credentials.Add(new Credential
        {
            CredentialId = credentialId, Name = "example-git", Type = "git_token",
            AuthMethod = Credential.AuthMethodToken, EncryptedToken = [1], IsActive = true,
        });
        await db.SaveChangesAsync();
        var service = new GitHubService(
            db, new TokenProtector(), new AllowAllGuard(), new RedirectingFactory(),
            new ConfigurationBuilder().Build(), NullLogger<GitHubService>.Instance);

        var error = await Assert.ThrowsAsync<ValidationException>(() => service.CreateRepoAsync(
            new GitHubCreateRepoRequest { CredentialId = credentialId, Name = "example-repo" }, default));

        Assert.Equal(Code, error.Code);
        Assert.Equal(400, error.Status);
        Assert.Contains("HTTP 302", error.Message);
        Assert.DoesNotContain(LocationToken, error.Message);
    }

    [Fact]
    public async Task The_netbox_sync_reports_a_redirect_as_a_validation_error_with_the_code()
    {
        using var db = NewDb();
        var service = new NetBoxSyncService(
            db, new HardeningTestUser(), new PassthroughSecrets(), new AllowAllGuard(), new RedirectingFactory(),
            NullLogger<NetBoxSyncService>.Instance);

        var error = await Assert.ThrowsAsync<ValidationException>(() => service.SyncAsync(
            new InventorySource { Name = "example-netbox", BaseUrl = "https://203.0.113.30" }, dryRun: true, default));

        Assert.Equal(Code, error.Code);
        Assert.Contains("HTTP 302", error.Message);
        Assert.DoesNotContain(LocationToken, error.Message);
    }
}
