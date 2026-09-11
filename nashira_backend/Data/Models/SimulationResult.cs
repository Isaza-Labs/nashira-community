namespace nashira_backend.Data.Models;

// Result of a workflow structural simulation. SchemaHash is the canonical hash of the
// nodes/edges that were simulated; the draft->qa promotion gate compares it against the
// workflow's current hash to detect staleness. Ok = no error-severity issues. Tenant-scoped.
public class SimulationResult : BaseModel
{
    public Guid SimulationResultId { get; set; }
    public Guid WorkflowId { get; set; }
    public int NodeCount { get; set; }
    public int IssueCount { get; set; }
    public int WarningCount { get; set; }
    public bool Ok { get; set; }
    public string SchemaHash { get; set; } = string.Empty;
    public string IssuesJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public Guid? SimulatedByUserId { get; set; }
}
