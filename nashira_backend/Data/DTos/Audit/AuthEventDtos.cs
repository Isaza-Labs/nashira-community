using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Audit;

public class AuthEventSummary
{
    [JsonPropertyName("auth_event_id")] public Guid AuthEventId { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("user_id")] public Guid? UserId { get; set; }

    // Resolved for display, like the audit trail. Null on an unattributed row — a
    // sign-in for a username that does not exist has no user to name.
    [JsonPropertyName("username")] public string? Username { get; set; }

    [JsonPropertyName("event")] public string Event { get; set; } = string.Empty;
    [JsonPropertyName("ip")] public string Ip { get; set; } = string.Empty;
    [JsonPropertyName("user_agent")] public string UserAgent { get; set; } = string.Empty;

    // Why it failed, which username was attempted, how long a lockout lasts. Parsed
    // rather than passed through as a string so the client renders it as JSON.
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; set; }
}
