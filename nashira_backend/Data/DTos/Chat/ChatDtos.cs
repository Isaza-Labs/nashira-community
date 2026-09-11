using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Chat;

public class ChatRequest
{
    [JsonPropertyName("conversation_id")] public Guid? ConversationId { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    // Files attached to this turn (inlined into the user message for the agent).
    [JsonPropertyName("attachments")] public List<ChatAttachment>? Attachments { get; set; }
    // Tool names the user has approved for this turn (governance confirmation).
    [JsonPropertyName("approvals")] public List<string>? Approvals { get; set; }

    // Which configured provider should answer, and optionally which of its models.
    // Both optional: omitted, the turn keeps whatever the conversation already used,
    // and a brand-new conversation falls back to the tenant's default provider.
    // The ids come from GET /api/ai/models, so a client can only name a provider
    // that is active and enabled.
    [JsonPropertyName("provider_id")] public Guid? ProviderId { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
}

public class ChatAttachment
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
    [JsonPropertyName("content_base64")] public string ContentBase64 { get; set; } = string.Empty;
}

public class ConversationResponse
{
    [JsonPropertyName("conversation_id")] public Guid ConversationId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("tokens_in")] public int TokensIn { get; set; }
    [JsonPropertyName("tokens_out")] public int TokensOut { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    // What last answered this thread, so reopening it shows the right selection
    // instead of the default. Null on conversations that predate the picker.
    [JsonPropertyName("ai_provider_id")] public Guid? AIProviderId { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
}

public class ConversationDetailResponse : ConversationResponse
{
    [JsonPropertyName("messages")] public JsonElement Messages { get; set; }
}
