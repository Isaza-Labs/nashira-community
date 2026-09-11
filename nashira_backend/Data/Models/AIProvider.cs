using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// A configured LLM provider for a tenant (OpenAI / Anthropic / Ollama). The API
// key is stored encrypted (Data Protection); `Enabled` is the admin on/off toggle
// (distinct from `IsActive`, the soft-delete flag).
public class AIProvider : BaseModel
{
    public Guid AIProviderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "openai" | "anthropic" | "ollama"
    public string? BaseURL { get; set; }

    [JsonIgnore]
    public byte[]? EncryptedApiKey { get; set; }

    public string DefaultModel { get; set; } = string.Empty;

    // Backed by a jsonb column. A default(JsonElement) is Undefined and can't be
    // serialized (Npgsql throws "Operation is not valid…"), so seed a valid empty
    // object. The static document is kept alive by this reference, so the element
    // stays valid; it is read-only, so sharing it across providers is safe.
    private static readonly JsonElement EmptyConfig = JsonDocument.Parse("{}").RootElement;
    public JsonElement Config { get; set; } = EmptyConfig;

    public bool Enabled { get; set; } = true;
}
