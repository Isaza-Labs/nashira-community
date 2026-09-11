using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// DAG parsing + validation: unique node ids, edges must reference nodes, topological
// order respects dependencies, and cycles are rejected.
public class DagTests
{
    private static (JsonElement Nodes, JsonElement Edges) Parse(string nodes, string edges)
    {
        using var n = JsonDocument.Parse(nodes);
        using var e = JsonDocument.Parse(edges);
        return (n.RootElement.Clone(), e.RootElement.Clone());
    }

    [Fact]
    public void TopologicalOrder_respects_dependencies()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"},{"id":"c","snippet_id":"__end__"}]""",
            """[{"source":"a","target":"c","type":"success"},{"source":"b","target":"c","type":"success"}]""");

        var order = Dag.Parse(nodes, edges).TopologicalOrder().ToList();

        Assert.Equal(3, order.Count);
        Assert.True(order.IndexOf("c") > order.IndexOf("a"));
        Assert.True(order.IndexOf("c") > order.IndexOf("b"));
    }

    [Fact]
    public void Parse_rejects_edge_to_unknown_node()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"__start__"}]""",
            """[{"source":"a","target":"ghost","type":"success"}]""");
        Assert.Throws<ValidationException>(() => Dag.Parse(nodes, edges));
    }

    [Fact]
    public void Parse_rejects_duplicate_node_id()
    {
        var (nodes, edges) = Parse("""[{"id":"a","snippet_id":"s"},{"id":"a","snippet_id":"s"}]""", "[]");
        Assert.Throws<ValidationException>(() => Dag.Parse(nodes, edges));
    }

    [Fact]
    public void TopologicalOrder_detects_cycle()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"s"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"},{"source":"b","target":"a","type":"success"}]""");

        var dag = Dag.Parse(nodes, edges);
        Assert.Throws<ValidationException>(() => dag.TopologicalOrder());
    }

    [Fact]
    public void StartNodes_are_indegree_zero()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"success"}]""");

        var starts = Dag.Parse(nodes, edges).StartNodes();

        Assert.Single(starts);
        Assert.Equal("a", starts[0].Id);
    }

    // ─── runnable, which is not the same as parseable ───────────────────

    // A `conditional` edge exists to gate. An empty expression used to make it silently
    // never fire — the run SUCCEEDED with the branch simply absent and nothing in the
    // record saying the author forgot to write it, which is the hardest kind of fault to
    // notice. FlowWeaver has always refused such a graph; this is the match.
    [Fact]
    public void A_blank_conditional_condition_is_not_runnable()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"conditional","condition":"  "}]""");

        var ex = Assert.Throws<ValidationException>(() => Dag.Parse(nodes, edges).RequireRunnable());

        // Names the edge by source and target: the author has to find it without reading
        // the whole graph.
        Assert.Contains("a->b", ex.Message);
        Assert.Contains("non-empty condition", ex.Message);
    }

    // The half most at risk of being caught by a too-broad refusal. A condition that is
    // PRESENT and false is the feature working, not an authoring mistake.
    [Fact]
    public void A_condition_that_is_present_is_runnable_whatever_it_evaluates_to()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"conditional","condition":"1 == 2"}]""");

        Dag.Parse(nodes, edges).RequireRunnable();
    }

    // Parsing stays permissive on purpose. Six callers parse a graph and only one is about
    // to run it — the simulation analyzer parses precisely to report EVERYTHING wrong with
    // a graph, and a parser that threw on the first fault would hand the author one issue
    // at a time instead of the list they asked for. The first draft of this change put the
    // check in Parse and broke exactly that.
    [Fact]
    public void Parsing_a_blank_conditional_condition_still_succeeds()
    {
        var (nodes, edges) = Parse(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
            """[{"source":"a","target":"b","type":"conditional","condition":""}]""");

        var dag = Dag.Parse(nodes, edges);

        Assert.Single(dag.Edges);
    }
}
