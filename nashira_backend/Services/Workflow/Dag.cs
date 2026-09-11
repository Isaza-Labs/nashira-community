using System.Text.Json;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Workflow;

public sealed record WorkflowNode(string Id, string SnippetId, string NodeType, JsonElement ConfigOverrides);

public sealed record WorkflowEdge(string Source, string Target, string EdgeType, string? Condition);

// Parsed workflow.v1 DAG: nodes by id, edges, adjacency (outgoing edges by node), and
// in-degree. Parse enforces structural integrity (unique node ids, edges referencing
// existing nodes); TopologicalOrder additionally rejects cycles. Adapted from flow-weaver.
public sealed class Dag
{
    public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; }
    public IReadOnlyList<WorkflowEdge> Edges { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<WorkflowEdge>> Adjacency { get; }
    public IReadOnlyDictionary<string, int> InDegree { get; }

    private Dag(
        Dictionary<string, WorkflowNode> nodes, List<WorkflowEdge> edges,
        Dictionary<string, IReadOnlyList<WorkflowEdge>> adjacency, Dictionary<string, int> inDegree)
    {
        Nodes = nodes;
        Edges = edges;
        Adjacency = adjacency;
        InDegree = inDegree;
    }

    public static Dag Parse(JsonElement nodes, JsonElement edges)
    {
        if (nodes.ValueKind != JsonValueKind.Array) throw new ValidationException("nodes must be an array");
        if (edges.ValueKind != JsonValueKind.Array) throw new ValidationException("edges must be an array");

        var nodeMap = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var n in nodes.EnumerateArray())
        {
            var id = Str(n, "id") ?? throw new ValidationException("a node is missing 'id'");
            if (!nodeMap.TryAdd(id, new WorkflowNode(
                    id,
                    Str(n, "snippet_id") ?? string.Empty,
                    Str(n, "type") ?? "task",
                    n.TryGetProperty("config_overrides", out var cfg) ? cfg.Clone() : default)))
                throw new ValidationException($"duplicate node id '{id}'");
        }

        var edgeList = new List<WorkflowEdge>();
        var adjacency = nodeMap.Keys.ToDictionary(k => k, _ => (List<WorkflowEdge>)[], StringComparer.Ordinal);
        var inDegree = nodeMap.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);

        foreach (var e in edges.EnumerateArray())
        {
            var source = Str(e, "source") ?? throw new ValidationException("an edge is missing 'source'");
            var target = Str(e, "target") ?? throw new ValidationException("an edge is missing 'target'");
            if (!nodeMap.ContainsKey(source)) throw new ValidationException($"edge source '{source}' is not a node");
            if (!nodeMap.ContainsKey(target)) throw new ValidationException($"edge target '{target}' is not a node");

            var edge = new WorkflowEdge(source, target, Str(e, "type") ?? "success", Str(e, "condition"));
            edgeList.Add(edge);
            adjacency[source].Add(edge);
            inDegree[target]++;
        }

        return new Dag(
            nodeMap, edgeList,
            adjacency.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<WorkflowEdge>)kv.Value, StringComparer.Ordinal),
            inDegree);
    }

    /// <summary>
    /// Refuses a graph that must not be RUN, as distinct from one that cannot be parsed.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <see cref="Parse"/>. Six callers parse a graph and only one
    /// of them is about to execute it: the simulation analyzer parses precisely to report
    /// everything wrong with a graph, and a parser that threw on the first fault would blind
    /// it — the author would get one issue at a time instead of the list they asked for. The
    /// first draft of this put the check in Parse and did exactly that.
    ///
    /// So: parsing stays permissive, analysis sees the whole graph, and the refusal happens
    /// where a run begins.
    ///
    /// <para>A `conditional` edge with no expression is the one rule here today.
    /// `WorkflowSimulationAnalyzer` reports it as `conditional_missing_condition` at
    /// promotion, which is the earlier and friendlier warning; this is the guarantee behind
    /// it, for a graph that was never analysed. The reason it is a refusal rather than a
    /// silently non-firing edge is that the silent version is the hardest kind of fault to
    /// notice: the run SUCCEEDS, the branch is simply absent, and nothing in the record says
    /// the author forgot to write the expression. An edge type whose entire purpose is to
    /// gate cannot default to a silent no.</para>
    ///
    /// <para>The message is FlowWeaver's, verbatim, so both products answer an invalid graph
    /// identically (`executor.conditional.blank_condition_is_refused`).</para>
    /// </remarks>
    public void RequireRunnable()
    {
        foreach (var edge in Edges)
            if (edge.EdgeType == "conditional" && string.IsNullOrWhiteSpace(edge.Condition))
                throw new ValidationException(
                    $"conditional edge `{edge.Source}->{edge.Target}` requires a non-empty condition");
    }

    public IReadOnlyList<WorkflowNode> StartNodes() =>
        Nodes.Values.Where(n => InDegree[n.Id] == 0).ToList();

    // Kahn's algorithm. Throws if the DAG has a cycle.
    public IReadOnlyList<string> TopologicalOrder()
    {
        var inDeg = new Dictionary<string, int>(InDegree, StringComparer.Ordinal);
        var queue = new Queue<string>(inDeg.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
        var order = new List<string>(Nodes.Count);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var e in Adjacency[id])
                if (--inDeg[e.Target] == 0)
                    queue.Enqueue(e.Target);
        }

        if (order.Count != Nodes.Count)
            throw new ValidationException("workflow has a cycle (not a DAG)");
        return order;
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
