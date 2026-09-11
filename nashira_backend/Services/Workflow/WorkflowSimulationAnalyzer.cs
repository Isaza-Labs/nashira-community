using System.Text.Json;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Workflow;

public sealed record SimulationFinding(string Code, string Message, string? NodeId);

public sealed record SimulationAnalysis(
    bool Ok, int NodeCount, IReadOnlyList<SimulationFinding> Issues, IReadOnlyList<SimulationFinding> Warnings);

// Pure structural analysis of a workflow.v1 DAG. Issues block (Ok=false): unparseable
// DAG, cycles, empty workflow, conditional edges without a condition. Warnings inform:
// nodes unreachable from any start node. No I/O — the service persists the outcome.
public static class WorkflowSimulationAnalyzer
{
    public static SimulationAnalysis Analyze(JsonElement nodes, JsonElement edges)
    {
        var issues = new List<SimulationFinding>();
        var warnings = new List<SimulationFinding>();

        Dag dag;
        try
        {
            dag = Dag.Parse(nodes, edges);
        }
        catch (ValidationException ex)
        {
            issues.Add(new SimulationFinding("invalid_dag", ex.Message, null));
            return new SimulationAnalysis(false, 0, issues, warnings);
        }

        if (dag.Nodes.Count == 0)
            issues.Add(new SimulationFinding("empty_workflow", "workflow has no nodes", null));

        try
        {
            dag.TopologicalOrder();
        }
        catch (ValidationException)
        {
            issues.Add(new SimulationFinding("cycle", "workflow has a cycle (not a DAG)", null));
        }

        foreach (var edge in dag.Edges)
        {
            if (edge.EdgeType == "conditional" && string.IsNullOrWhiteSpace(edge.Condition))
                issues.Add(new SimulationFinding(
                    "conditional_missing_condition",
                    $"conditional edge {edge.Source} -> {edge.Target} has no condition",
                    edge.Source));
        }

        // Reachability from the __start__ sentinel(s) when present (in an acyclic graph
        // every node reaches back to some in-degree-0 node, so those are only the
        // fallback). Flags islands not connected to the declared entry point.
        if (issues.Count == 0 && dag.Nodes.Count > 0)
        {
            var starts = dag.Nodes.Values.Where(n => n.SnippetId == "__start__").Select(n => n.Id).ToList();
            if (starts.Count == 0) starts = dag.StartNodes().Select(n => n.Id).ToList();

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(starts);
            foreach (var id in queue) visited.Add(id);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var e in dag.Adjacency[id])
                    if (visited.Add(e.Target))
                        queue.Enqueue(e.Target);
            }
            foreach (var id in dag.Nodes.Keys.Where(k => !visited.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
                warnings.Add(new SimulationFinding("unreachable_node", $"node '{id}' is unreachable from any start node", id));
        }

        return new SimulationAnalysis(issues.Count == 0, dag.Nodes.Count, issues, warnings);
    }
}
