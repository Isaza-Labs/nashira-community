using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Workflow;

public class SimulationResultResponse
{
    [JsonPropertyName("simulation_result_id")] public Guid SimulationResultId { get; set; }
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("node_count")] public int NodeCount { get; set; }
    [JsonPropertyName("issue_count")] public int IssueCount { get; set; }
    [JsonPropertyName("warning_count")] public int WarningCount { get; set; }
    [JsonPropertyName("schema_hash")] public string SchemaHash { get; set; } = string.Empty;
    [JsonPropertyName("issues")] public JsonElement Issues { get; set; }
    [JsonPropertyName("warnings")] public JsonElement Warnings { get; set; }
    [JsonPropertyName("simulated_at")] public DateTime SimulatedAt { get; set; }
}

public class PromoteWorkflowRequest
{
    [JsonPropertyName("target")] public string Target { get; set; } = string.Empty; // qa | production
    [JsonPropertyName("approved_by")] public string? ApprovedBy { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
}

// Execution plan: topological node order + rollback safety (whether the workflow can be
// automatically rolled back, and which nodes block it).
public class WorkflowPlanResponse
{
    [JsonPropertyName("order")] public List<string> Order { get; set; } = [];
    [JsonPropertyName("rollback_reversible")] public bool RollbackReversible { get; set; }
    [JsonPropertyName("non_reversible_nodes")] public List<string> NonReversibleNodes { get; set; } = [];

    /// <summary>
    /// Nodes whose tier could not be predicted at all, each with the reason. Always a
    /// subset of <see cref="NonReversibleNodes"/>: an unscorable node is scored at the
    /// ceiling, so the plan never promises a rollback over work it could not read.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point. "This step cannot be undone" and "this step
    /// names a subflow that does not exist" both block an automatic rollback, and they
    /// need opposite responses: the first is a property of the workflow its author chose,
    /// the second is a broken reference nobody has noticed yet. Folding them together
    /// would make the second look like a design decision.
    /// </remarks>
    [JsonPropertyName("unresolvable_nodes")]
    public List<PlanUnresolvableNode> UnresolvableNodes { get; set; } = [];
}

// One node the plan could not score, and why.
public class PlanUnresolvableNode
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// The code the step executor would fail this node with — `subflow_missing`,
    /// `subflow_cycle`, `subflow_depth_exceeded`, `subflow_unreadable` — so the plan and
    /// the run that follows it name the same problem the same way.
    /// </summary>
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;

    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}
