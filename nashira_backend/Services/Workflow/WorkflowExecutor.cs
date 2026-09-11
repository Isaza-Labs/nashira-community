using System.Text.Json;

namespace nashira_backend.Services.Workflow;

public static class NodeResult
{
    public const string Changed = "changed";
    public const string NoChange = "no_change";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

// What a single node's action produced. Error fields are set only on Failed; Output is
// the node's result payload (JSON) when it ran.
//
// The diagnostics below are init-only rather than positional so every existing
// construction site — the conformance harness included — keeps compiling while a real
// executor fills them in. They answer the questions a bare result word cannot: what
// the node was actually handed after templates resolved, what it said when it failed,
// how long it took, and whether it got there on the first attempt.
public sealed record NodeOutcome(string Result, string? ErrorCode = null, bool Retryable = false, string? Output = null)
{
    public string? Error { get; init; }
    public string? Logs { get; init; }
    public string? Input { get; init; }
    public int Attempts { get; init; } = 1;
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }

    // The effective idempotency tier of what actually ran (execution/SPEC.md §2): the
    // handler's floor, the snippet's own declaration, and a stricter-only
    // `config_overrides.idempotency` override, resolved once by whoever dispatched the
    // node. It travels on the outcome because only the node executor can see the
    // snippet row — the DAG walker holds a graph, not a snippet registry, and reading
    // the tier off `config_overrides` here is exactly what let a node claim to be
    // reversible when its handler knew it was not. The default is the contract's
    // default for an unannotated node.
    public IdempotencyKind Tier { get; init; } = IdempotencyKind.RequiresCompensation;

    // The run a `subflow` node started (execution/SPEC.md §5). Null on every other
    // node. It travels on the outcome for the same reason the tier does: only the
    // node executor knows it, and the row that has to record it is written by the
    // run service two layers up.
    public Guid? ChildRunId { get; init; }
}

public sealed record NodeExecution(string NodeId, string Result, string? ErrorCode, bool Retryable, string? Output)
{
    public string? Error { get; init; }
    public string? Logs { get; init; }
    public string? Input { get; init; }
    public int Attempts { get; init; } = 1;
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }

    // Carried through from the outcome so a caller holding a finished run — a parent
    // run asking what its subflow child did — can read the tier that actually applied
    // without re-deriving it from snippet rows it may no longer be able to see. A
    // `subflow` step's tier is the strictest of its child's, which no static resolver
    // over the parent's graph can compute.
    public IdempotencyKind Tier { get; init; } = IdempotencyKind.RequiresCompensation;

    public Guid? ChildRunId { get; init; }
}

// audit.v1 event emitted per state-changing node (the executed artifact, not the plan).
//
// TenantId survives the removal of multi-tenancy on purpose: `tenant_id` is a
// REQUIRED field of the audit.v1 schema in the workflow.v1 conformance kit, which
// is shared with flow-weaver. Dropping it here would silently fork an interop
// contract, so the field stays and is emitted as Guid.Empty (the single scope).
public sealed record AuditV1Event(
    string Schema, Guid EventId, Guid WorkflowId, string SchemaHash, string NodeId, string Op,
    string Idempotency, string Actor, Guid TenantId, DateTime Timestamp, string Result);

public sealed record WorkflowExecutionContext(Guid WorkflowId, string SchemaHash, string Actor, DateTime Now);

public sealed record WorkflowRunResult(
    string Status,                          // completed | failed
    IReadOnlyList<NodeExecution> Steps,
    IReadOnlyList<string> RollbackPlan,     // reversible changed nodes, reverse execution order
    string FinalState,                      // completed | rolled_back | failed
    IReadOnlyList<AuditV1Event> AuditEvents);

// Executes one node's action. The executor stays free of I/O; a real implementation maps
// the node to a tool/snippet, a simulated one drives from a mocked context (conformance).
public interface INodeExecutor
{
    Task<NodeOutcome> ExecuteAsync(WorkflowNode node, CancellationToken ct);
}

// Deterministic DAG executor. Runs nodes in topological order, firing edges by predecessor
// result (success/failure/always; conditional treated as success until a condition evaluator
// exists), aggregates status, emits an audit.v1 event per state change, and on failure
// computes a rollback plan (reversible changed nodes, reversed) + a final state.
public sealed class WorkflowExecutor
{
    // Evaluates `conditional` edge expressions. Optional: a caller that passes
    // none keeps the previous behaviour (conditional fires like success), which is
    // what the conformance harness does when it drives the executor with mocked
    // nodes and no run context.
    private readonly Services.Engine.IConditionEvaluator? _conditions;
    private readonly Func<IReadOnlyDictionary<string, Services.Engine.StepResult>>? _stepOutputs;

    // The run's input payload and run context, for `{{ input.* }}` and `{{ run.* }}`
    // inside a condition (templates/SPEC.md §9). Both optional for the same reason as
    // above; the run context is the live object the step executor writes to, not a
    // snapshot, so a condition and a step payload can never disagree about the run.
    private readonly JsonElement? _runInput;
    private readonly Services.Engine.RunContext? _runContext;

    public WorkflowExecutor(
        Services.Engine.IConditionEvaluator? conditions = null,
        Func<IReadOnlyDictionary<string, Services.Engine.StepResult>>? stepOutputs = null,
        JsonElement? runInput = null,
        Services.Engine.RunContext? runContext = null)
    {
        _conditions = conditions;
        _stepOutputs = stepOutputs;
        _runInput = runInput;
        _runContext = runContext;
    }

    public async Task<WorkflowRunResult> RunAsync(
        Dag dag, INodeExecutor nodeExecutor, WorkflowExecutionContext ctx, bool stopOnFailure, CancellationToken ct)
    {
        var order = dag.TopologicalOrder();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<NodeExecution>();
        var audits = new List<AuditV1Event>();
        var changedReversible = new List<string>();
        var hasNonReversibleChange = false;
        var failed = false;
        var stopped = false;

        foreach (var nodeId in order)
        {
            var node = dag.Nodes[nodeId];

            if (stopped || !ShouldRun(dag, nodeId, results, EvaluateCondition))
            {
                results[nodeId] = NodeResult.Skipped;
                steps.Add(new NodeExecution(nodeId, NodeResult.Skipped, null, false, null));
                continue;
            }

            var outcome = await nodeExecutor.ExecuteAsync(node, ct);
            results[nodeId] = outcome.Result;
            steps.Add(new NodeExecution(nodeId, outcome.Result, outcome.ErrorCode, outcome.Retryable, outcome.Output)
            {
                Error = outcome.Error,
                Logs = outcome.Logs,
                Input = outcome.Input,
                Attempts = outcome.Attempts,
                StartedAt = outcome.StartedAt,
                FinishedAt = outcome.FinishedAt,
                Tier = outcome.Tier,
                ChildRunId = outcome.ChildRunId,
            });

            if (outcome.Result == NodeResult.Changed)
            {
                var kind = outcome.Tier;
                audits.Add(new AuditV1Event(
                    "audit.v1", Guid.NewGuid(), ctx.WorkflowId, ctx.SchemaHash, nodeId, node.SnippetId,
                    Idempotency.ToWire(kind), ctx.Actor, Guid.Empty, ctx.Now, outcome.Result));
                if (Idempotency.IsReversible(kind)) changedReversible.Add(nodeId);
                else hasNonReversibleChange = true;
            }
            else if (outcome.Result == NodeResult.Failed)
            {
                failed = true;

                // execution/SPEC.md §1: with stop_on_failure the walk stops at the
                // first failed node *whose failure is not consumed by a `failure`
                // edge*. The guard used to be a plain `failed && stopOnFailure` tested
                // before ShouldRun, so it short-circuited the very rule that makes a
                // `failure` edge mean anything — and since stop_on_failure is always
                // on for an API run, no notify-on-failure or compensation node has
                // ever run and `{{ run.failed_step_* }}` was unreachable by design.
                if (stopOnFailure && !ConsumesFailure(dag, nodeId)) stopped = true;
            }
        }

        var status = failed ? "failed" : "completed";
        var rollbackPlan = failed ? changedReversible.AsEnumerable().Reverse().ToList() : [];
        var finalState = failed
            ? (hasNonReversibleChange ? "failed" : "rolled_back")
            : "completed";

        return new WorkflowRunResult(status, steps, rollbackPlan, finalState, audits);
    }

    // Whether the author wired a `failure` edge out of this node — i.e. whether the
    // graph says what to do when it fails. That, and only that, is what lets the walk
    // continue past a failure under stop_on_failure.
    private static bool ConsumesFailure(Dag dag, string nodeId) =>
        dag.Edges.Any(e => e.EdgeType == "failure" && string.Equals(e.Source, nodeId, StringComparison.Ordinal));

    // A node runs if it has no incoming edges (an entry point) or at least one incoming
    // edge fired given the predecessor's result. Predecessors precede it in topo order.
    private static bool ShouldRun(
        Dag dag, string nodeId, IReadOnlyDictionary<string, string> results, Func<WorkflowEdge, bool> conditional)
    {
        if (dag.InDegree[nodeId] == 0) return true;
        foreach (var e in dag.Edges)
        {
            if (e.Target != nodeId) continue;
            if (!results.TryGetValue(e.Source, out var src)) continue;
            var srcOk = src is NodeResult.Changed or NodeResult.NoChange;
            var fired = e.EdgeType switch
            {
                "always" => true,
                "success" => srcOk,
                "failure" => src == NodeResult.Failed,
                // A conditional edge additionally requires its expression to hold.
                // The predecessor still has to have succeeded: a condition over the
                // output of a step that failed is reading a partial result.
                "conditional" => srcOk && conditional(e),
                _ => false,
            };
            if (fired) return true;
        }
        return false;
    }

    // No evaluator wired keeps the pre-condition behaviour: the edge fires on success.
    // That is what the conformance harness relies on, and it means adding an evaluator
    // cannot change how an existing unconditional workflow runs.
    //
    // A blank condition is a different thing and fails CLOSED, matching
    // ConditionEvaluator.Evaluate, which reads an empty expression as false
    // (templates/SPEC.md §9). The two used to disagree — the executor fired the edge,
    // the evaluator refused it — and the executor's reading was fail-open on the one
    // edge type whose entire purpose is to gate.
    private bool EvaluateCondition(WorkflowEdge edge)
    {
        if (string.IsNullOrWhiteSpace(edge.Condition)) return false;
        if (_conditions is null) return true;
        var outputs = _stepOutputs?.Invoke()
                      ?? new Dictionary<string, Services.Engine.StepResult>(StringComparer.Ordinal);

        // deviceContext stays null on purpose: an edge fires once at DAG level after
        // its source node completes, so there is no single current device even when
        // the source fanned out. `{{ device.* }}` in a condition therefore reads
        // unresolved and the condition is false — SPEC.md §9.
        return _conditions.Evaluate(
            edge.Condition!, outputs, deviceContext: null, runInput: _runInput,
            runContext: _runContext?.ToJson());
    }
}
