using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Net;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// A denied secret lookup is an authorization decision. It has to reach the step as
// secret_access_denied, must not be retried as if the credentials were merely broken, and
// must still honour an author's `redact_output:true`.
public class WorkflowDenialHandlingTests
{
    private const string Denied = "secret_access_denied";
    private const string Message = "Admin role is required to resolve stored secrets";

    [Fact]
    public async Task A_denied_lookup_in_integration_auth_stops_the_step_without_retrying()
    {
        using var db = NewDb();
        var applier = new DenyingApplier();
        var handler = new IntegrationActionSnippetHandler(
            db, applier, new PassthroughSecrets(), new AllowAllGuard(), new NoClients(),
            NullLogger<IntegrationActionSnippetHandler>.Instance);
        var snippet = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = "call", Type = Snippet.TypeIntegrationAction,
            Idempotency = "idempotent", ChangesState = false,
            RetryPolicyJson = """{"max_retries":2,"initial_delay_seconds":0}""",
        };
        db.Snippets.Add(snippet);
        var integrationId = Guid.NewGuid();
        db.Integrations.Add(new Integration
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

        var step = await RunStepAsync(db, handler, snippet,
            new { integration = "example-api", action = "get_status" });

        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal(Denied, step.ErrorCode);
        Assert.False(step.Retryable);
        Assert.Equal(1, step.Attempts);
        Assert.Equal(1, applier.Calls);
        Assert.Contains("Admin role", step.Error);
    }

    [Fact]
    public async Task A_denial_honours_redact_output_true()
    {
        using var db = NewDb();
        var snippet = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = "deny", Type = "denies", Idempotency = "idempotent", ChangesState = false,
        };
        db.Snippets.Add(snippet);
        await db.SaveChangesAsync();

        var step = await RunStepAsync(db, new DenyingHandler(), snippet, new { redact_output = true });

        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.False(step.Retryable);
        // The decision is reported as documented; only the payload, logs and text are withheld.
        Assert.Equal(Denied, step.ErrorCode);
        Assert.Equal("[redacted]", step.Error);
        Assert.Equal("[redacted]", step.Logs);
        Assert.Equal("\"[redacted]\"", step.Output);
        Assert.DoesNotContain(Message, JsonSerializer.Serialize(step));
    }

    [Fact]
    public async Task A_denial_after_secret_material_was_resolved_keeps_its_code_and_withholds_the_rest()
    {
        using var db = NewDb();
        var snippet = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = "deny", Type = "denies", Idempotency = "idempotent", ChangesState = false,
            RetryPolicyJson = """{"max_retries":2,"initial_delay_seconds":0}""",
        };
        db.Snippets.Add(snippet);
        await db.SaveChangesAsync();

        // No redact_output flag: the step already received resolved material before the denial.
        var step = await RunStepAsync(db, new DenyingHandler(markResolvedFirst: true), snippet, new { });

        Assert.Equal(Denied, step.ErrorCode);
        Assert.False(step.Retryable);
        Assert.Equal(1, step.Attempts);
        Assert.Equal("[redacted]", step.Error);
        Assert.Equal("[redacted]", step.Logs);
        Assert.DoesNotContain(Message, JsonSerializer.Serialize(step));
    }

    [Theory]
    [InlineData("script_error")]
    [InlineData("http_500")]
    [InlineData("exception")]
    [InlineData("example-token-value")]
    [InlineData("SECRET_ACCESS_DENIED")]
    [InlineData("secret_access_denied ")]
    [InlineData("secret_access_denied:example-token-value")]
    [InlineData("admin_required")]
    public async Task A_code_supplied_by_a_handler_is_still_replaced_when_output_is_withheld(string code)
    {
        using var db = NewDb();
        var snippet = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = "fail", Type = "fails", Idempotency = "idempotent", ChangesState = false,
        };
        db.Snippets.Add(snippet);
        await db.SaveChangesAsync();

        var step = await RunStepAsync(
            db, new ReturningHandler(SnippetResult.Fail("example-token-value leaked", code)), snippet,
            new { redact_output = true });

        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("output_redacted", step.ErrorCode);
        Assert.Equal("[redacted]", step.Error);
        Assert.DoesNotContain("example-token-value", JsonSerializer.Serialize(step));
    }

    [Fact]
    public async Task Without_the_flag_a_denial_keeps_its_code_and_message()
    {
        using var db = NewDb();
        var snippet = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = "deny", Type = "denies", Idempotency = "idempotent", ChangesState = false,
        };
        db.Snippets.Add(snippet);
        await db.SaveChangesAsync();

        var step = await RunStepAsync(db, new DenyingHandler(), snippet, new { redact_output = false });

        Assert.Equal(Denied, step.ErrorCode);
        Assert.Equal(Message, step.Error);
        Assert.False(step.Retryable);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"denial-handling-{Guid.NewGuid()}").Options);

    private static async Task<NodeExecution> RunStepAsync(
        AppDbContext db, ISnippetHandler handler, Snippet snippet, object overrides)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var nodes = new SnippetNodeExecutor(db, new Registry(handler), services,
            new VariableResolver(NullLogger<VariableResolver>.Instance), new WorkflowExecutionScope(),
            NullLogger<SnippetNodeExecutor>.Instance);
        var config = JsonSerializer.SerializeToElement(new[]
        {
            new { id = "step", snippet_id = snippet.SnippetId, config_overrides = overrides },
        });
        using var edges = JsonDocument.Parse("[]");
        var result = await new WorkflowExecutor().RunAsync(Dag.Parse(config, edges.RootElement), nodes,
            new WorkflowExecutionContext(Guid.NewGuid(), "test", "operator", DateTime.UtcNow),
            stopOnFailure: true, CancellationToken.None);
        return Assert.Single(result.Steps);
    }

    private sealed class DenyingApplier : IIntegrationAuthApplier
    {
        public int Calls;

        public Task ApplyAsync(HttpRequestMessage request, Integration integration, CancellationToken ct)
        {
            Calls++;
            throw new ForbiddenException(Message, Denied);
        }
    }

    private sealed class DenyingHandler(bool markResolvedFirst = false) : ISnippetHandler
    {
        public string Type => "denies";

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            if (markResolvedFirst) SecretResolutionScope.MarkResolved();
            throw new ForbiddenException(Message, Denied);
        }
    }

    private sealed class ReturningHandler(SnippetResult result) : ISnippetHandler
    {
        public string Type => "fails";

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class Registry(ISnippetHandler handler) : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => [handler.Type];
        public ISnippetHandler? Resolve(string type, IServiceProvider services) => handler;
    }

    private sealed class PassthroughSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string> SubstituteAsync(string template, CancellationToken ct) => Task.FromResult(template);
        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
            Task.FromResult(template);
    }

    private sealed class AllowAllGuard : IUrlGuard
    {
        public void EnsureSafe(string url, bool allowPrivate = false) { }
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no request expected");
    }
}
