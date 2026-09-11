using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.AiApiSpec;

public class CreateAiApiSpec
{
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("auth_type")] public string? AuthType { get; set; }
    [JsonPropertyName("auth_config")] public string? AuthConfig { get; set; }
    [JsonPropertyName("verify_ssl")] public bool? VerifySsl { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
}

public class UpdateAiApiSpec
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("auth_type")] public string? AuthType { get; set; }
    [JsonPropertyName("auth_config")] public string? AuthConfig { get; set; }
    [JsonPropertyName("verify_ssl")] public bool? VerifySsl { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
    // Explicit unlink; see UpdateAiPromptSkill.ClearIntegration.
    [JsonPropertyName("clear_integration")] public bool? ClearIntegration { get; set; }
}

public class AiApiSpecResponse
{
    [JsonPropertyName("ai_api_spec_id")] public Guid AiApiSpecId { get; set; }
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("operation_count")] public int OperationCount { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("auth_type")] public string AuthType { get; set; } = "none";
    [JsonPropertyName("verify_ssl")] public bool VerifySsl { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("integration_id")] public Guid? IntegrationId { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class AiApiSpecDetailResponse : AiApiSpecResponse
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}
