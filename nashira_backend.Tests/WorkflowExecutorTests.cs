using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

public class WorkflowExecutorTests
{
    private sealed class MapExecutor(Dictionary<string, NodeOutcome> map) : INodeExecutor
    {
        public Task<NodeOutcome> ExecuteAsync(WorkflowNode node, CancellationToken ct) =>
            Task.FromResult(map.TryGetValue(node.Id, out var o) ? o : new NodeOutcome(NodeResult.Changed));
    }

    private static Dag Build(string nodes, string edges)
    {
        using var n = JsonDocument.Parse(nodes);
        using var e = JsonDocument.Parse(edges);
        return Dag.Parse(n.RootElement.Clone(), e.RootElement.Clone());
    }

    private static readonly WorkflowExecutionContext Ctx =
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "H1",
            "tester", new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc));

    private static Task<WorkflowRunResult> Run(Dag dag, Dictionary<string, NodeOutcome> map, bool stopOnFailure = true) =>
        new WorkflowExecutor().RunAsync(dag, new MapExecutor(map), Ctx, stopOnFailure, CancellationToken.None);

    [Fact]
    public async Task Runs_in_topological_order_and_audits_changes()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"},{"id":"c","snippet_id":"s"}]""",
            """[{"source":"a","target":"c","type":"success"},{"source":"b","target":"c","type":"success"}]""");

        var run = await Run(dag, []);

        Assert.Equal("completed", run.Status);
        Assert.Equal("completed", run.FinalState);
        var order = run.Steps.Select(s => s.NodeId).ToList();
        Assert.True(order.IndexOf("c") > order.IndexOf("a"));
        Assert.True(order.IndexOf("c") > order.IndexOf("b"));
        Assert.Equal(3, run.AuditEvents.Count);
        Assert.All(run.AuditEvents, e => Assert.Equal("audit.v1", e.Schema));
    }

    [Fact]
    public async Task No_change_node_emits_no_audit_event()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");

        var run = await Run(dag, new()
        {
            ["a"] = new NodeOutcome(NodeResult.Changed),
            ["b"] = new NodeOutcome(NodeResult.NoChange),
        });

        Assert.Equal("completed", run.Status);
        Assert.Single(run.AuditEvents);
        Assert.Equal("a", run.AuditEvents[0].NodeId);
    }

    [Fact]
    public async Task Failure_of_reversible_work_rolls_back()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"s","config_overrides":{"idempotency":"idempotent"}},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");

        var run = await Run(dag, new()
        {
            ["a"] = new NodeOutcome(NodeResult.Changed),
            ["b"] = new NodeOutcome(NodeResult.Failed, "DEVICE_UNREACHABLE", Retryable: true),
        });

        Assert.Equal("failed", run.Status);
        Assert.Equal("rolled_back", run.FinalState);
        Assert.Equal(new[] { "a" }, run.RollbackPlan);
        var b = run.Steps.Single(s => s.NodeId == "b");
        Assert.Equal("DEVICE_UNREACHABLE", b.ErrorCode);
        Assert.True(b.Retryable);
    }

    [Fact]
    public async Task Failure_after_non_reversible_change_cannot_roll_back()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"s","config_overrides":{"idempotency":"non_reversible"}},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");

        var run = await Run(dag, new()
        {
            // The tier travels ON THE OUTCOME now (execution/SPEC.md §2). Only whoever
            // dispatched the node can see the snippet row and the handler's floor, so
            // the DAG walker no longer reads `config_overrides.idempotency` — reading it
            // there is precisely what let a node call itself reversible when its handler
            // knew it was not. The override stays in the graph above because it is what
            // the author wrote; a real executor resolves it into this.
            ["a"] = new NodeOutcome(NodeResult.Changed) { Tier = IdempotencyKind.NonReversible },
            ["b"] = new NodeOutcome(NodeResult.Failed, "BOOM"),
        });

        Assert.Equal("failed", run.Status);
        Assert.Equal("failed", run.FinalState);
        Assert.Empty(run.RollbackPlan);
    }

    [Fact]
    public async Task Failure_edge_routes_to_compensation_node()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"s"},{"id":"ok","snippet_id":"s"},{"id":"fix","snippet_id":"s"}]""",
            """[{"source":"a","target":"ok","type":"success"},{"source":"a","target":"fix","type":"failure"}]""");

        var run = await Run(dag, new() { ["a"] = new NodeOutcome(NodeResult.Failed, "E") }, stopOnFailure: false);

        Assert.Equal("failed", run.Status);
        Assert.Equal(NodeResult.Skipped, run.Steps.Single(s => s.NodeId == "ok").Result);
        Assert.NotEqual(NodeResult.Skipped, run.Steps.Single(s => s.NodeId == "fix").Result);
    }

    // ── what the tier actually buys, at run level ────────────────────────

    // The consequence of the old NonReversible floor on python_snippet / mcp_call, stated
    // where it was visible to an operator: the run's FINAL STATE. A compensable step that
    // changed something, followed by a failure, is a run that rolled back. The same run
    // with the step marked irreversible is a run that left changes behind — and while the
    // floor was absolute, every script step produced the second answer no matter what its
    // author had declared.
    [Fact]
    public async Task A_failed_run_over_compensable_changes_reports_rolled_back()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"s"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");

        var run = await Run(dag, new()
        {
            ["a"] = new NodeOutcome(NodeResult.Changed) { Tier = IdempotencyKind.RequiresCompensation },
            ["b"] = new NodeOutcome(NodeResult.Failed),
        });

        Assert.Equal("failed", run.Status);
        Assert.Equal("rolled_back", run.FinalState);
        Assert.Equal(["a"], run.RollbackPlan);
    }

    [Fact]
    public async Task A_failed_run_over_an_irreversible_change_reports_failed()
    {
        var dag = Build(
            """[{"id":"a","snippet_id":"s"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");

        var run = await Run(dag, new()
        {
            ["a"] = new NodeOutcome(NodeResult.Changed) { Tier = IdempotencyKind.NonReversible },
            ["b"] = new NodeOutcome(NodeResult.Failed),
        });

        Assert.Equal("failed", run.Status);
        // Not "rolled_back": nothing can undo it, and saying otherwise would be a
        // promise of a reversal that never happened.
        Assert.Equal("failed", run.FinalState);
        Assert.Empty(run.RollbackPlan);
    }
}
