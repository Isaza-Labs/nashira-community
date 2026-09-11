using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

public class WorkflowSimulationAnalyzerTests
{
    private static SimulationAnalysis Analyze(string nodes, string edges)
    {
        using var n = JsonDocument.Parse(nodes);
        using var e = JsonDocument.Parse(edges);
        return WorkflowSimulationAnalyzer.Analyze(n.RootElement, e.RootElement);
    }

    [Fact]
    public void Clean_workflow_is_ok()
    {
        var a = Analyze(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");
        Assert.True(a.Ok);
        Assert.Equal(2, a.NodeCount);
        Assert.Empty(a.Issues);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Conditional_edge_without_condition_is_an_issue()
    {
        var a = Analyze(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"conditional"}]""");
        Assert.False(a.Ok);
        Assert.Contains(a.Issues, i => i.Code == "conditional_missing_condition");
    }

    [Fact]
    public void Cycle_is_an_issue()
    {
        var a = Analyze(
            """[{"id":"a","snippet_id":"s"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"},{"source":"b","target":"a","type":"success"}]""");
        Assert.False(a.Ok);
        Assert.Contains(a.Issues, i => i.Code == "cycle");
    }

    [Fact]
    public void Island_disconnected_from_start_sentinel_is_warned()
    {
        // a(__start__)->b is the entry island; c->d is disconnected from it.
        var a = Analyze(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"},{"id":"c","snippet_id":"s"},{"id":"d","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"},{"source":"c","target":"d","type":"success"}]""");
        Assert.True(a.Ok); // warnings don't block
        Assert.Equal(2, a.Warnings.Count);
        Assert.Contains(a.Warnings, w => w.Code == "unreachable_node" && w.NodeId == "c");
        Assert.Contains(a.Warnings, w => w.Code == "unreachable_node" && w.NodeId == "d");
    }

    [Fact]
    public void Empty_workflow_is_an_issue()
    {
        var a = Analyze("[]", "[]");
        Assert.False(a.Ok);
        Assert.Contains(a.Issues, i => i.Code == "empty_workflow");
    }
}
