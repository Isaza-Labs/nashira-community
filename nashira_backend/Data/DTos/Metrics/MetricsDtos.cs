using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Metrics;

// One day of a windowed series. `counts` is keyed by whatever the series is grouped on
// — run status, auth event kind — and is empty on a day with no activity rather than
// missing, so a chart's x-axis stays continuous.
public class DailyBucket
{
    [JsonPropertyName("date")] public DateTime Date { get; set; }
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts { get; set; } = [];
}

public class TopFailingWorkflow
{
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("workflow_name")] public string WorkflowName { get; set; } = string.Empty;
    [JsonPropertyName("failed_count")] public int FailedCount { get; set; }
}

public class RunMetricsResponse
{
    [JsonPropertyName("days")] public int Days { get; set; }
    [JsonPropertyName("from")] public DateTime From { get; set; }
    [JsonPropertyName("series")] public List<DailyBucket> Series { get; set; } = [];

    // rolled_back vs failed. A failed run whose changes were all reversed is a different
    // operational fact from one that left the world half-changed, and only the second
    // needs somebody to go and look.
    [JsonPropertyName("final_states")] public Dictionary<string, int> FinalStates { get; set; } = [];

    [JsonPropertyName("by_environment")] public Dictionary<string, int> ByEnvironment { get; set; } = [];
    [JsonPropertyName("top_failing")] public List<TopFailingWorkflow> TopFailing { get; set; } = [];
}

public class AuthMetricsResponse
{
    [JsonPropertyName("days")] public int Days { get; set; }
    [JsonPropertyName("from")] public DateTime From { get; set; }
    [JsonPropertyName("series")] public List<DailyBucket> Series { get; set; } = [];
    [JsonPropertyName("successes")] public int Successes { get; set; }
    [JsonPropertyName("failures")] public int Failures { get; set; }
    [JsonPropertyName("lockouts")] public int Lockouts { get; set; }
}

public class QueueMetricsResponse
{
    [JsonPropertyName("by_status")] public Dictionary<string, int> ByStatus { get; set; } = [];
    [JsonPropertyName("queued")] public int Queued { get; set; }
    [JsonPropertyName("claimed")] public int Claimed { get; set; }

    // Depth alone cannot tell a busy queue from a stuck one: ten jobs queued for a
    // second is healthy, one queued for an hour is not.
    [JsonPropertyName("oldest_queued_at")] public DateTime? OldestQueuedAt { get; set; }
    [JsonPropertyName("oldest_queued_seconds")] public int? OldestQueuedSeconds { get; set; }
}

public class DeviceMetricsResponse
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("by_status")] public Dictionary<string, int> ByStatus { get; set; } = [];
    [JsonPropertyName("by_vendor")] public Dictionary<string, int> ByVendor { get; set; } = [];
    [JsonPropertyName("by_site")] public Dictionary<string, int> BySite { get; set; } = [];
    [JsonPropertyName("from_inventory")] public int FromInventory { get; set; }
    [JsonPropertyName("manual")] public int Manual { get; set; }
    [JsonPropertyName("allow_draft")] public int AllowDraft { get; set; }
    [JsonPropertyName("allow_qa")] public int AllowQa { get; set; }
    [JsonPropertyName("allow_production")] public int AllowProduction { get; set; }
}
