using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Audit;

public class AuditEventSummary
{
    [JsonPropertyName("audit_event_id")] public Guid AuditEventId { get; set; }
    [JsonPropertyName("sequence")] public long Sequence { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("user_id")] public Guid? UserId { get; set; }

    // Resolved for display. A trail that identifies its actor by GUID is technically
    // complete and practically unreadable — the reviewer ends up looking up every id by
    // hand, which is how an audit log stops being consulted.
    [JsonPropertyName("username")] public string? Username { get; set; }

    // Who acted, as text. The username for a signed-in request; the automation identity
    // ("workflow-runner", "scheduler", …) when there is no user at all. On those rows it
    // is the only answer to "who did this", which is why it is bound into the hash.
    [JsonPropertyName("actor")] public string? Actor { get; set; }

    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = string.Empty;
    [JsonPropertyName("entity_id")] public Guid? EntityId { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;

    // Which canonical form the hash was taken over. Surfaced so a reviewer can tell a
    // row that predates a chain change from one that was written after it.
    [JsonPropertyName("hash_version")] public int HashVersion { get; set; }
}

public class AuditEventDetail : AuditEventSummary
{
    [JsonPropertyName("prev_hash")] public string? PrevHash { get; set; }
    [JsonPropertyName("user_agent")] public string? UserAgent { get; set; }
    [JsonPropertyName("before")] public JsonElement? Before { get; set; }
    [JsonPropertyName("after")] public JsonElement? After { get; set; }

    // True when this is a delete event whose record the restore endpoint knows how
    // to bring back. Computed server-side so the UI never offers a button that the
    // API would refuse on principle (it can still fail on state: already active,
    // hard-removed, or a name now taken by a live record).
    [JsonPropertyName("restorable")] public bool Restorable { get; set; }
}

public class AuditRestoreResponse
{
    [JsonPropertyName("entity_type")] public string EntityType { get; set; } = string.Empty;
    [JsonPropertyName("entity_id")] public Guid EntityId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("restored")] public bool Restored { get; set; } = true;
}

public class AuditVerifyResponse
{
    [JsonPropertyName("valid")] public bool Valid { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("broken_at_sequence")] public long? BrokenAtSequence { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
