using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Learning;

public class CreateAgentLearning
{
    [JsonPropertyName("error_pattern")] public string ErrorPattern { get; set; } = string.Empty;
    [JsonPropertyName("error_category")] public string? ErrorCategory { get; set; }
    [JsonPropertyName("service_type")] public string? ServiceType { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("fix_strategy")] public string FixStrategy { get; set; } = "parameter_adjust";
    [JsonPropertyName("fix_params")] public JsonElement? FixParams { get; set; }
}

public class UpdateAgentLearning
{
    [JsonPropertyName("error_pattern")] public string? ErrorPattern { get; set; }
    [JsonPropertyName("error_category")] public string? ErrorCategory { get; set; }
    [JsonPropertyName("service_type")] public string? ServiceType { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("fix_strategy")] public string? FixStrategy { get; set; }
    [JsonPropertyName("fix_params")] public JsonElement? FixParams { get; set; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
}

public class AgentLearningResponse
{
    [JsonPropertyName("agent_learning_id")] public Guid AgentLearningId { get; set; }
    [JsonPropertyName("error_pattern")] public string ErrorPattern { get; set; } = string.Empty;
    [JsonPropertyName("error_category")] public string ErrorCategory { get; set; } = string.Empty;
    [JsonPropertyName("service_type")] public string ServiceType { get; set; } = string.Empty;
    [JsonPropertyName("tool_name")] public string ToolName { get; set; } = string.Empty;
    [JsonPropertyName("fix_strategy")] public string FixStrategy { get; set; } = string.Empty;
    [JsonPropertyName("fix_params")] public JsonElement? FixParams { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("success_count")] public long SuccessCount { get; set; }
    [JsonPropertyName("failure_count")] public long FailureCount { get; set; }
    [JsonPropertyName("is_system")] public bool IsSystem { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}
