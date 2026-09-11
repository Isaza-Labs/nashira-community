using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Fleet;

// A run, seen from outside the workflow that owns it.
//
// Carries the workflow's name because the whole point of the fleet view is that you do
// not know which workflow you are looking for yet. Resolving names client-side means a
// second round of requests for the page that is opened when something is already wrong.
public class FleetRunResponse
{
    [JsonPropertyName("workflow_run_id")] public Guid WorkflowRunId { get; set; }
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }

    /// <summary>"(deleted)" when the workflow is gone — the run still happened.</summary>
    [JsonPropertyName("workflow_name")] public string WorkflowName { get; set; } = string.Empty;

    [JsonPropertyName("environment")] public string Environment { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    // completed | rolled_back | failed. The distinction that decides whether a failure
    // was an incident: only `failed` left the world half-changed.
    [JsonPropertyName("final_state")] public string FinalState { get; set; } = string.Empty;

    [JsonPropertyName("node_count")] public int NodeCount { get; set; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; set; }
    [JsonPropertyName("failed_count")] public int FailedCount { get; set; }
    [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public DateTime? FinishedAt { get; set; }

    /// <summary>Null while the run is still going — not zero.</summary>
    [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; set; }

    // manual | agent | schedule | webhook | git_webhook | test. This list is where
    // "what ran at 3am and why" gets asked, and it is the one column that answers it
    // without opening every row.
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = string.Empty;

    // Set only when the run never reached a step. The list shows `failed` with 0 of 0
    // nodes otherwise, which reads like a bug in the platform rather than a refusal.
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>
    /// The run whose <c>subflow</c> step started this one (execution/SPEC.md §5), null
    /// for a run started on its own account. It travels on the list row as well as the
    /// detail because it is a scalar the query already read, and because a child run
    /// otherwise reads as an unexplained extra row beside the parent that caused it.
    /// </summary>
    [JsonPropertyName("parent_run_id")] public Guid? ParentRunId { get; set; }
}

// A trigger, seen from outside its workflow.
public class FleetTriggerResponse
{
    [JsonPropertyName("workflow_trigger_id")] public Guid WorkflowTriggerId { get; set; }
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("workflow_name")] public string WorkflowName { get; set; } = string.Empty;
    [JsonPropertyName("workflow_environment")] public string WorkflowEnvironment { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    // cron only
    [JsonPropertyName("cron_expression")] public string? CronExpression { get; set; }
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "UTC";

    // webhook only
    [JsonPropertyName("route")] public string? Route { get; set; }

    [JsonPropertyName("next_run_at")] public DateTime? NextRunAt { get; set; }
    [JsonPropertyName("last_run_at")] public DateTime? LastRunAt { get; set; }
    [JsonPropertyName("last_run_status")] public string? LastRunStatus { get; set; }
    [JsonPropertyName("last_run_id")] public Guid? LastRunId { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("fire_count")] public int FireCount { get; set; }

    // Decided server-side against the server's clock. A client comparing to its own
    // would call every trigger overdue the moment a laptop's time drifted.
    [JsonPropertyName("overdue")] public bool Overdue { get; set; }
}

// One run, opened from the fleet view.
//
// Deliberately NOT the same shape as /api/workflows/runs/{id}. That endpoint returns
// every step with its full output, input snapshot and logs inline, and a per-device
// node returns a payload per device — so opening a twelve-device run meant moving
// megabytes before the first card could paint. Here the steps carry only what the
// list of cards needs (result, timing, error, and how big each payload is) and the
// payload itself is fetched per step, when a card is expanded.
public class FleetRunDetailResponse : FleetRunResponse
{
    /// <summary>The payload the run was triggered with. Null when it had none.</summary>
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }

    /// <summary>Device ids the run targeted. Empty means no device context.</summary>
    [JsonPropertyName("target_devices")] public JsonElement TargetDevices { get; set; }

    [JsonPropertyName("steps")] public List<FleetStepResponse> Steps { get; set; } = [];
}

// A step's header: everything except the payloads.
public class FleetStepResponse
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = string.Empty;
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = string.Empty; // changed | no_change | failed | skipped
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("started_at")] public DateTime? StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public DateTime? FinishedAt { get; set; }
    [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }

    // Sizes, in characters, of the payloads that were left behind. Zero means there is
    // nothing to fetch; the client shows the size beside the tab so a reader knows
    // whether the click that follows will bring back a line or a megabyte.
    [JsonPropertyName("output_chars")] public int OutputChars { get; set; }
    [JsonPropertyName("input_chars")] public int InputChars { get; set; }
    [JsonPropertyName("logs_chars")] public int LogsChars { get; set; }

    /// <summary>
    /// The run a <c>subflow</c> step started (execution/SPEC.md §5); null on every other
    /// step. This endpoint deliberately carries no payloads, and a subflow step is the
    /// one step whose detail is not a payload at all — it is another run. Without the id
    /// the step is a dead end: the reader can see that a child workflow ran and has no
    /// way to reach what it did.
    /// </summary>
    [JsonPropertyName("child_run_id")] public Guid? ChildRunId { get; set; }
}

// One step's payloads, fetched on demand.
public class FleetStepPayloadResponse
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = string.Empty;
    [JsonPropertyName("sequence")] public int Sequence { get; set; }

    /// <summary>What the node produced. Null when it produced nothing, or when the stored text is not JSON.</summary>
    [JsonPropertyName("output")] public JsonElement? Output { get; set; }

    /// <summary>The node's configuration as the handler received it — templates resolved, secrets redacted.</summary>
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }

    /// <summary>The handler's own account of what it did.</summary>
    [JsonPropertyName("logs")] public string? Logs { get; set; }
}
