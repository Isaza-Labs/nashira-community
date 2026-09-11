using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Traces;

public class TraceEventDto
{
    [JsonPropertyName("trace_event_id")] public Guid TraceEventId { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    // Null means the operation is still open — it started and has not reported an
    // outcome. That is a state, not missing data.
    [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }

    [JsonPropertyName("actor")] public string? Actor { get; set; }
    [JsonPropertyName("user_id")] public Guid? UserId { get; set; }

    // Shared with audit_events, so one request can be reconstructed across both.
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }

    [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }
    [JsonPropertyName("metadata_json")] public string? MetadataJson { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
}

public class TraceListResponse
{
    // The count before paging, so the screen can say "showing 100 of 4,312" rather than
    // implying the page is the whole answer.
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }

    // Which order the rows came back in — "at" or "duration". The slower-than filter
    // switches it, and a screen that says "newest first" while showing the slowest is
    // lying about what the reader is looking at.
    [JsonPropertyName("sorted_by")] public string SortedBy { get; set; } = "at";
    [JsonPropertyName("items")] public List<TraceEventDto> Items { get; set; } = [];
}

public class TraceSummaryResponse
{
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
    [JsonPropertyName("from")] public DateTime From { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("by_category")] public Dictionary<string, int> ByCategory { get; set; } = [];
    [JsonPropertyName("by_status")] public Dictionary<string, int> ByStatus { get; set; } = [];

    // Started and never closed.
    [JsonPropertyName("in_flight")] public int InFlight { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("slowest")] public List<TraceEventDto> Slowest { get; set; } = [];
}
