namespace nashira_backend.Data.Models;

// An immutable snapshot of a workflow definition at the moment it was promoted.
//
// The live Workflow row carries a `Version` integer but only ever holds the
// current definition, so "what exactly ran in production last month" and "put back
// what we had before this promotion" were both unanswerable. A version row answers
// both: it stores the nodes and edges verbatim plus the schema hash they produced,
// so a rollback restores a definition rather than reconstructing one.
public class WorkflowVersion : BaseModel
{
    public Guid WorkflowVersionId { get; set; }
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }

    public string NodesJson { get; set; } = "[]";
    public string EdgesJson { get; set; } = "[]";
    public string? InputSchemaJson { get; set; }
    public string? MetadataJson { get; set; }

    // The canonical hash of (nodes, edges) as of this snapshot. Kept alongside the
    // definition rather than recomputed on read: if the canonicalizer ever changes,
    // recomputing would silently rewrite history.
    public string SchemaHash { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;
    public string ChangeSummary { get; set; } = string.Empty;
    public DateTime PromotedAt { get; set; }
    public string PromotedBy { get; set; } = string.Empty;
    public Guid? ConversationId { get; set; }

    // The simulation that cleared this definition through the gate, so an auditor
    // can see the evidence the promotion was granted on.
    public Guid? SimulationId { get; set; }
}
