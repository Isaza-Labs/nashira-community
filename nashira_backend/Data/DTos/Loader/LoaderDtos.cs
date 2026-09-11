using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Loader;

public class ValidateTemplateRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty; // skill | spec
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

public class TemplateIssueDto
{
    [JsonPropertyName("severity")] public string Severity { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public class TemplateValidationResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("issues")] public List<TemplateIssueDto> Issues { get; set; } = [];
}

public class ValidationRecordResponse
{
    [JsonPropertyName("validation_record_id")] public Guid ValidationRecordId { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("target_name")] public string TargetName { get; set; } = string.Empty;
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("issues")] public List<TemplateIssueDto> Issues { get; set; } = [];
    [JsonPropertyName("user_id")] public Guid? UserId { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
}
