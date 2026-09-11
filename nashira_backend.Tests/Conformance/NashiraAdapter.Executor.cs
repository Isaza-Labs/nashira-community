using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests.Conformance;

// Family `executor` (workflow-v1-conformance/execution/SPEC.md).
//
//   { workflow: { nodes, edges }, context } -> { status, final_state, steps,
//                                                rollback_plan, errors, tiers,
//                                                outputs, audit_events }
//
// `context` scripts the run the way the kit's `runs?` member is meant to:
//
//   nodes.<id>       { result, tier, error_code, output } — what that node's action did
//   subflows.<id>    the outcome of the child run a `subflow` node started
//   stop_on_failure  default true (always on for a run started through the API)
//   input, run       the run payload and run metadata a condition may read
//   workflow_id, schema_hash, actor   the audit event's fixed context
//
// What is REAL here, and it is the point of the family: the DAG walk, edge firing,
// stop-on-failure, the audit events, the rollback plan and the final state all come
// from WorkflowExecutor; conditions from ConditionEvaluator; and every `subflow` node
// is executed by the actual SnippetNodeExecutor against an in-memory instance, so
// `subflow_missing`, the cycle guard, the output shape and the strictest-child-tier
// rule are the engine's answers and not the harness's.
//
// The only thing scripted is what a handler DID — which is exactly what the vectors
// are parameterised over, and is the boundary INodeExecutor exists to make swappable.
public sealed partial class NashiraAdapter
{
    private static readonly Guid DefaultWorkflowId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime FixedNow = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement? Executor(JsonElement input)
    {
        var workflow = Prop(input, "workflow") ?? throw new InvalidOperationException("executor vector needs `workflow`");
        var ctx = Prop(input, "context") ?? default;

        Dag dag;
        try
        {
            dag = Dag.Parse(
                Prop(workflow, "nodes") ?? throw new InvalidOperationException("workflow needs `nodes`"),
                Prop(workflow, "edges") ?? JsonDocument.Parse("[]").RootElement);
            // The adapter drives the RUN path, so it applies the run path's gate.
            dag.RequireRunnable();
        }
        catch (nashira_backend.Exceptions.ValidationException)
        {
            // A graph the parser refuses. Nothing ran, so the answer is a failed run with no
            // steps — which is the same answer FlowWeaver's harness gives when its own
            // orchestrator refuses, and is the point of `graph-validation`: what a workflow
            // must state before it is allowed to run AT ALL is a separate question from what
            // happens while it runs, and the two products have to agree on it.
            return JsonSerializer.SerializeToElement(new
            {
                status = "failed",
                steps = Array.Empty<object>(),
            });
        }

        var workflowId = Str(ctx, "workflow_id") is { } wid && Guid.TryParse(wid, out var parsed) ? parsed : DefaultWorkflowId;
        var schemaHash = Str(ctx, "schema_hash") ?? "H1";
        var actor = Str(ctx, "actor") ?? "conformance";
        var stopOnFailure = Prop(ctx, "stop_on_failure") is not { ValueKind: JsonValueKind.False };

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"conformance-executor-{Guid.NewGuid()}").Options);

        var scope = new WorkflowExecutionScope();
        using var entered = scope.Enter("draft", Guid.NewGuid(), workflowId);

        var subflowNode = new SnippetNodeExecutor(
            db, new NoHandlers(), new NoServices(),
            new VariableResolver(NullLogger<VariableResolver>.Instance),
            scope, NullLogger<SnippetNodeExecutor>.Instance,
            new ScriptedSubflowRunner(dag, Prop(ctx, "subflows")))
        {
            RunInput = Prop(ctx, "input"),
        };

        var nodes = new ScriptedNodeExecutor(Prop(ctx, "nodes"), subflowNode);

        var executor = new WorkflowExecutor(
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            () => nodes.Completed,
            Prop(ctx, "input"),
            runContext: null);

        var result = executor
            .RunAsync(dag, nodes, new WorkflowExecutionContext(workflowId, schemaHash, actor, FixedNow), stopOnFailure, default)
            .GetAwaiter().GetResult();

        return JsonSerializer.SerializeToElement(new
        {
            status = result.Status,
            final_state = result.FinalState,
            steps = result.Steps.Select(s => new { node_id = s.NodeId, result = s.Result }).ToList(),
            // The same outcomes keyed by node, for the cases where the contract fixes
            // WHAT ran and not the total order. Topological order is contract; the
            // relative order of two independent nodes is not, and a vector that
            // asserted it would be pinning an implementation detail.
            results = result.Steps.ToDictionary(s => s.NodeId, s => s.Result),
            rollback_plan = result.RollbackPlan,
            errors = result.Steps.Where(s => s.ErrorCode is { Length: > 0 })
                .ToDictionary(s => s.NodeId, s => s.ErrorCode!),
            tiers = result.Steps.Where(s => s.Result != NodeResult.Skipped)
                .ToDictionary(s => s.NodeId, s => Idempotency.ToWire(s.Tier)),
            outputs = result.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Output))
                .ToDictionary(s => s.NodeId, s => JsonDocument.Parse(s.Output!).RootElement.Clone()),
            audit_events = result.AuditEvents.Select(e => new
            {
                schema = e.Schema,
                event_id = e.EventId,
                workflow_id = e.WorkflowId,
                schema_hash = e.SchemaHash,
                node_id = e.NodeId,
                op = e.Op,
                idempotency = e.Idempotency,
                actor = e.Actor,
                tenant_id = e.TenantId,
                timestamp = e.Timestamp,
                result = e.Result,
            }).ToList(),
        });
    }

    // Plays back what each node's action did. A `subflow` node is not scripted: it is
    // handed to the real SnippetNodeExecutor, because everything §5 fixes about a
    // subflow node lives there and a scripted answer would assert nothing.
    private sealed class ScriptedNodeExecutor : INodeExecutor
    {
        private readonly JsonElement? _script;
        private readonly SnippetNodeExecutor _subflows;
        private readonly Dictionary<string, StepResult> _completed = new(StringComparer.Ordinal);

        public ScriptedNodeExecutor(JsonElement? script, SnippetNodeExecutor subflows)
        {
            _script = script;
            _subflows = subflows;
        }

        // Only what has actually run, so a condition cannot read a node that has not.
        public IReadOnlyDictionary<string, StepResult> Completed => _completed;

        public async Task<NodeOutcome> ExecuteAsync(WorkflowNode node, CancellationToken ct)
        {
            var marker = JsonSerializer.SerializeToElement(new
            {
                id = node.Id,
                type = node.NodeType,
                snippet_id = node.SnippetId,
            });

            if (NodeReferences.IsSubflow(marker))
            {
                var real = await _subflows.ExecuteAsync(node, ct);
                if (real.Output is { Length: > 0 } raw)
                    _completed[node.Id] = new StepResult(JsonDocument.Parse(raw).RootElement.Clone());
                return real;
            }

            var spec = Prop(_script, node.Id);
            var output = Prop(spec, "output");
            if (output is { } o) _completed[node.Id] = new StepResult(o.Clone());

            return new NodeOutcome(
                Result: spec is { } s ? Str(s, "result") ?? NodeResult.NoChange : NodeResult.NoChange,
                ErrorCode: spec is { } e ? Str(e, "error_code") : null,
                Retryable: false,
                Output: output?.GetRawText())
            {
                Tier = Idempotency.Parse(spec is { } t ? Str(t, "tier") : null),
            };
        }
    }

    // The child run a `subflow` node starts, as the vector describes it. The interface
    // exists precisely so the child can be swapped for a scripted one; everything the
    // parent step derives from the outcome — changed vs no_change, the strictest tier,
    // subflow_failed vs subflow_missing, the output envelope — stays the engine's.
    private sealed class ScriptedSubflowRunner : ISubflowRunner
    {
        private readonly Dictionary<Guid, JsonElement> _byChild = [];

        public ScriptedSubflowRunner(Dag dag, JsonElement? script)
        {
            if (script is not { ValueKind: JsonValueKind.Object } s) return;
            foreach (var p in s.EnumerateObject())
            {
                if (!dag.Nodes.TryGetValue(p.Name, out var node)) continue;
                if (node.ConfigOverrides.ValueKind != JsonValueKind.Object) continue;
                if (Str(node.ConfigOverrides, "subflow_workflow_id") is { } raw && Guid.TryParse(raw, out var child))
                    _byChild[child] = p.Value.Clone();
            }
        }

        public Task<SubflowOutcome> RunAsync(
            Guid childWorkflowId, JsonElement input, IReadOnlyList<Guid> targetDeviceIds,
            SubflowInvocation childChain, CancellationToken ct)
        {
            if (!_byChild.TryGetValue(childWorkflowId, out var spec))
                return Task.FromResult(SubflowOutcome.NotFound($"no child run scripted for {childWorkflowId}"));

            if (Prop(spec, "unresolved") is { ValueKind: JsonValueKind.True })
                return Task.FromResult(SubflowOutcome.NotFound(Str(spec, "error") ?? "unresolved"));

            var steps = Prop(spec, "steps") is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Select(Step).ToList()
                : [];

            return Task.FromResult(new SubflowOutcome(
                Str(spec, "run_id") is { } r && Guid.TryParse(r, out var runId) ? runId : null,
                Str(spec, "status") ?? "completed",
                Str(spec, "final_state") ?? "completed",
                Str(spec, "error"),
                steps));
        }

        private static NodeExecution Step(JsonElement s) => new(
            Str(s, "node_id") ?? "?", Str(s, "result") ?? NodeResult.NoChange,
            Str(s, "error_code"), false, Prop(s, "output")?.GetRawText())
        {
            Tier = Idempotency.Parse(Str(s, "tier")),
        };
    }

    // A subflow node never reaches the handler registry, so these exist only to satisfy
    // SnippetNodeExecutor's constructor. Anything that DID reach them would be a node
    // this family is not meant to execute, so they throw rather than answer quietly.
    private sealed class NoHandlers : ISnippetHandlerRegistry
    {
        public ISnippetHandler? Resolve(string type, IServiceProvider scope) =>
            throw new InvalidOperationException($"the executor family scripts handlers; '{type}' must not be resolved");

        public IReadOnlyCollection<string> KnownTypes => [];
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
