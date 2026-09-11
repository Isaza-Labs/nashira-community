namespace nashira_backend.Data.Models;

// A canonical workflow definition (workflow.v1 DAG). nodes/edges are stored as JSON text,
// validated on write against workflow.v1 and required to be acyclic. SchemaHash is the
// canonical fingerprint the simulation-staleness gate compares. Environment is the
// promotion state (draft -> qa -> production). Tenant-scoped.
public class Workflow : BaseModel
{
    public const string EnvDraft = "draft";
    public const string EnvQa = "qa";
    public const string EnvProduction = "production";

    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
    public string SchemaVersion { get; set; } = "v1";
    public string NodesJson { get; set; } = "[]";
    public string EdgesJson { get; set; } = "[]";
    public string? InputSchemaJson { get; set; }
    public string? MetadataJson { get; set; }
    public string Environment { get; set; } = EnvDraft;
    public string SchemaHash { get; set; } = string.Empty;
    public Guid? LastSimulationId { get; set; }
    public Guid? PromotedFrom { get; set; }
    public string ChangeSummary { get; set; } = string.Empty;
    public DateTime? PromotedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }

    // The AIConversation this workflow was authored in, when the agent built it
    // (create_workflow reads it off the ambient turn context). Null for workflows
    // created through the API/UI. Carried across promotion so a production workflow
    // still points at the conversation that produced its draft.
    public Guid? ConversationId { get; set; }
}
