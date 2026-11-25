using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// A refused redirect is a configuration fault, so the agent dispatcher must not look for a learned
// correction and run the call again: that would repeat the original request, with whatever side
// effect it has, and cannot change where the endpoint lives. The store here would supply a retry
// for any ordinary failure, which is what makes the "final" cases meaningful.
public class ToolDispatcherRedirectFinalityTests
{
    private static async Task<(ToolCallOutput Output, Handler Handler, Store Store)> RunAsync(object toolResult)
    {
        var handler = new Handler(JsonSerializer.SerializeToElement(toolResult));
        var store = new Store();
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(handler);
        var services = new ServiceCollection().AddSingleton(handler).BuildServiceProvider();
        var dispatcher = new ToolDispatcher(
            registry, services, new Admin(),
            new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
            new OpenGuard(), new NoAutonomy(), new NoAudit(),
            new SelfCorrectionEngine(store, NullLogger<SelfCorrectionEngine>.Instance),
            NullLogger<ToolDispatcher>.Instance, new NoTrace());
        var output = await dispatcher.DispatchAsync(
            "execute_operation", JsonDocument.Parse("""{"operation_id":"example_op"}""").RootElement, default);
        return (output, handler, store);
    }

    [Fact]
    public async Task A_refused_redirect_is_final_and_never_consults_corrections_or_calls_the_tool_again()
    {
        var (output, handler, store) = await RunAsync(new
        {
            status_code = 0, success = false, error = "redirects are disabled; configure the final endpoint",
            error_code = "redirect_refused",
        });

        Assert.False(output.Success);
        Assert.Equal("redirect_refused", output.Result.GetProperty("error_code").GetString());
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, store.Lookups);
    }

    [Fact]
    public async Task A_failure_that_could_not_be_inspected_is_final_too()
    {
        var (output, handler, store) = await RunAsync(new
        {
            error = "MCP call failed; the error details were withheld because the failure could not be fully inspected",
            error_code = "failure_unclassified",
        });

        Assert.False(output.Success);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, store.Lookups);
    }

    [Fact]
    public async Task An_ordinary_failure_with_a_learned_correction_is_still_retried()
    {
        var (output, handler, store) = await RunAsync(new { error = "example failure: HTTP 500" });

        Assert.Equal(1, store.Lookups);
        Assert.Equal(2, handler.Calls);
        Assert.False(output.Success);
    }

    [Fact]
    public async Task Another_error_code_does_not_get_the_final_treatment()
    {
        var (_, handler, store) = await RunAsync(new { error = "example failure", error_code = "example_other_code" });

        Assert.Equal(1, store.Lookups);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class Handler(JsonElement result) : IToolHandler
    {
        public int Calls;
        public string Name => "execute_operation";
        public string Description => "example";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    // Offers a parameter adjustment for any error, so a retry is always available.
    private sealed class Store : ILearningsStore
    {
        public int Lookups;
        public Task<AgentLearning?> FindBestFixAsync(string toolName, string serviceType, string errorMessage, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult<AgentLearning?>(new AgentLearning
            {
                AgentLearningId = Guid.NewGuid(), FixStrategy = AgentLearning.StrategyParameterAdjust,
                FixParamsJson = """{"query_params":{"example":"1"}}""", Confidence = 0.9,
            });
        }
        public Task RecordOutcomeAsync(Guid learningId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task RecordDiscoveredAsync(string errorPattern, string errorCategory, string serviceType,
            string toolName, string fixStrategy, string? fixParamsJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OpenGuard : IToolResourceGuard
    {
        public Task<string?> DenyReasonAsync(string toolName, JsonElement args, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoAutonomy : IToolAutonomyResolver
    {
        public Task<bool> IsReadOnlyCallAsync(string toolName, JsonElement args, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class NoAudit : IAuditLogger
    {
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "admin-user";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }

    private sealed class NoTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null) { }
        public ITraceScope Begin(string category, string action, object? metadata = null) => new Scope();
        private sealed class Scope : ITraceScope
        {
            public void Complete(object? metadata = null) { }
            public void Fail(string error, object? metadata = null) { }
            public void Dispose() { }
        }
    }
}
