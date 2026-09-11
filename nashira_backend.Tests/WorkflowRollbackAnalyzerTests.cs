using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

public class WorkflowRollbackAnalyzerTests
{
    private static Dag Build(string nodes)
    {
        using var n = JsonDocument.Parse(nodes);
        using var e = JsonDocument.Parse("[]");
        return Dag.Parse(n.RootElement.Clone(), e.RootElement.Clone());
    }

    // The tier is no longer read off the node. It is the effective tier the executor
    // will actually apply, which only a caller that can see the snippet rows can
    // compute — so these tests supply it the way NodeTierResolver would.
    private static Dictionary<string, IdempotencyKind> Tiers(
        params (string Node, IdempotencyKind Tier)[] pairs) =>
        pairs.ToDictionary(p => p.Node, p => p.Tier);

    [Fact]
    public void All_reversible_nodes_are_rollback_safe()
    {
        var dag = Build("""[{"id":"a","snippet_id":"s"},{"id":"b","snippet_id":"s"}]""");

        var r = WorkflowRollbackAnalyzer.Analyze(
            dag,
            Tiers(("a", IdempotencyKind.Idempotent), ("b", IdempotencyKind.RequiresCompensation)));

        Assert.True(r.Reversible);
        Assert.Empty(r.NonReversibleNodes);
        Assert.Equal(2, r.ReversibleNodes.Count);
    }

    [Fact]
    public void Non_reversible_node_blocks_rollback()
    {
        var dag = Build("""[{"id":"a","snippet_id":"s"},{"id":"push","snippet_id":"s"}]""");

        var r = WorkflowRollbackAnalyzer.Analyze(
            dag,
            Tiers(("a", IdempotencyKind.Idempotent), ("push", IdempotencyKind.NonReversible)));

        Assert.False(r.Reversible);
        Assert.Contains("push", r.NonReversibleNodes);
    }

    [Fact]
    public void Nodes_the_map_does_not_mention_default_to_reversible_compensation()
    {
        var dag = Build("""[{"id":"a","snippet_id":"s"}]""");

        Assert.True(WorkflowRollbackAnalyzer.Analyze(dag, Tiers()).Reversible);
    }

    // The point of taking a tier map rather than reading the node: a node whose config
    // claims `idempotent` is still non-reversible if that is what the executor will
    // apply. The plan used to disagree with the run on exactly this.
    [Fact]
    public void The_resolved_tier_wins_over_what_the_node_config_claims()
    {
        var dag = Build(
            """[{"id":"send","snippet_id":"s","config_overrides":{"idempotency":"idempotent"}}]""");

        var r = WorkflowRollbackAnalyzer.Analyze(
            dag, Tiers(("send", IdempotencyKind.NonReversible)));

        Assert.False(r.Reversible);
        Assert.Contains("send", r.NonReversibleNodes);
    }
}
