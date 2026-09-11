namespace nashira_backend.Data.Models;

// A learned or curated fix for a tool error. "knowledge" = curated (seeded/admin);
// "learning" = discovered by the self-correction engine. FixParamsJson is the param
// delta to merge (parameter_adjust) or {"message": "..."} (escalate hint). Confidence
// tracks the success rate.
//
// IsSystem marks the rows shipped by LearningsSeedService: read-only, not editable
// or deletable through the API. It is an explicit column because it used to be
// inferred from CompanyId == Guid.Empty, a sentinel that died with multi-tenancy.
public class AgentLearning : BaseModel
{
    public const string CategoryKnowledge = "knowledge";
    public const string CategoryLearning = "learning";
    public const string StrategyParameterAdjust = "parameter_adjust";
    public const string StrategyEscalate = "escalate";

    public Guid AgentLearningId { get; set; }
    public string ErrorPattern { get; set; } = string.Empty;   // regex or substring
    public string ErrorCategory { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;    // "" = any
    public string ToolName { get; set; } = string.Empty;       // "" = any
    public string FixStrategy { get; set; } = StrategyParameterAdjust;
    public string? FixParamsJson { get; set; }
    public string Category { get; set; } = CategoryLearning;
    public bool IsSystem { get; set; }
    public double Confidence { get; set; } = 0.5;
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
}
