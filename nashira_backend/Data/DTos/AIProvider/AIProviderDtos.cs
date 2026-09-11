using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.AIProvider;

public class CreateAIProvider
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("default_model")] public string DefaultModel { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    // Free-form provider settings, stored as jsonb. Two keys are read today —
    // `models` (extra model ids this installation licenses, offered in the chat's
    // model picker alongside default_model) and `model_limits`
    // (context_window / max_output_tokens) — and anything else is kept untouched
    // for whatever reads it next. Omitted on create means an empty object.
    [JsonPropertyName("config")] public JsonElement? Config { get; set; }
}

public class UpdateAIProvider
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("default_model")] public string? DefaultModel { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    // Replaces the whole object when present; omitted leaves it as it was. Sending
    // `{}` is how you clear it — the same rule as every other field here, where
    // null means "not part of this update".
    [JsonPropertyName("config")] public JsonElement? Config { get; set; }
}

// One selectable model, with the provider it belongs to. `id` is what a chat request
// would carry; `provider` is there because the same model name can be served by more
// than one provider row (a cloud one and a self-hosted one, say).
public class AIModelResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;
    [JsonPropertyName("ai_provider_id")] public Guid AIProviderId { get; set; }
    [JsonPropertyName("provider_type")] public string ProviderType { get; set; } = string.Empty;
    [JsonPropertyName("is_default")] public bool IsDefault { get; set; }
}

// The API key is never returned — only a has_api_key flag.
public class AIProviderResponse
{
    [JsonPropertyName("ai_provider_id")] public Guid AIProviderId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("default_model")] public string DefaultModel { get; set; } = string.Empty;
    [JsonPropertyName("has_api_key")] public bool HasApiKey { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("config")] public JsonElement Config { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}
