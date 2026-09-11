using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Workflow;

// Optional body for POST /workflows/{id}/run. Absent = no input, no targets, which
// is exactly how every run behaved before this existed.
public class RunWorkflowRequest
{
    // Feeds `{{ input.* }}` in every node's payload.
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }

    // Devices a `per_device` step fans out over. Each one is still checked against
    // the run's environment (allow_draft/allow_qa/allow_production) before it is
    // targeted.
    [JsonPropertyName("target_devices")] public List<Guid>? TargetDevices { get; set; }
}

public class WorkflowRunResponse
{
    [JsonPropertyName("workflow_run_id")] public Guid WorkflowRunId { get; set; }
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("environment")] public string Environment { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("final_state")] public string FinalState { get; set; } = string.Empty;
    [JsonPropertyName("node_count")] public int NodeCount { get; set; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; set; }
    [JsonPropertyName("failed_count")] public int FailedCount { get; set; }
    [JsonPropertyName("rollback_plan")] public JsonElement RollbackPlan { get; set; }
    // What the run was asked to do. Without these the record says a definition ran
    // but not against what, and the same definition does different work per input.
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }
    [JsonPropertyName("target_devices")] public JsonElement TargetDevices { get; set; }
    [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public DateTime? FinishedAt { get; set; }

    // What started it: manual | agent | schedule | webhook | git_webhook | test.
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = string.Empty;

    // Why the run failed when no step can say — a policy denied it, the DAG would not
    // parse, the targets resolved to nothing. Null on success and on ordinary step
    // failures, where the failing step's own error is the better answer.
    [JsonPropertyName("error")] public string? Error { get; set; }

    // The run whose `subflow` step started this one (execution/SPEC.md §5); null for a
    // run started on its own account. `trigger: "subflow"` already says that a run is a
    // child; this says whose.
    [JsonPropertyName("parent_run_id")] public Guid? ParentRunId { get; set; }
}

public class StepRunResponse
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = string.Empty;
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = string.Empty;
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }

    // What the node actually produced. The engine has always stored this and the
    // API has never returned it, so a finished run could say a node ended
    // `no_change` and nothing about what it saw — on a read-only node like a ping,
    // the output IS the entire result. A failed node carries its message here too,
    // which is why `error_code` alone was never enough to explain a failure.
    [JsonPropertyName("output")] public JsonElement? Output { get; set; }

    // The failure message. Used to be readable only by digging `error` out of the
    // output payload, which worked by convention rather than contract.
    [JsonPropertyName("error")] public string? Error { get; set; }

    // The node's configuration as the handler received it: templates resolved,
    // secret-looking values redacted, capped. A `{{ … }}` reference that resolved to
    // something other than what the author assumed is the most common cause of a
    // surprising step, and it cannot be reconstructed after the run.
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }

    // The handler's own account of what it did — what explains a `no_change`.
    [JsonPropertyName("logs")] public string? Logs { get; set; }

    // 0 when the step never reached a handler, 1 normally, more when it was retried.
    [JsonPropertyName("attempts")] public int Attempts { get; set; }

    [JsonPropertyName("started_at")] public DateTime? StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public DateTime? FinishedAt { get; set; }
    // Precomputed rather than left to every reader to subtract two timestamps and get
    // the timezone handling wrong.
    [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }

    // The run a `subflow` step started (execution/SPEC.md §5); null on every other step.
    // This response inlines every payload, and a subflow step's `output` already carries
    // the child's `run_id` inside its JSON — but a caller should not have to parse a
    // payload to follow a link the engine recorded in a column of its own.
    [JsonPropertyName("child_run_id")] public Guid? ChildRunId { get; set; }
}

public class WorkflowRunDetailResponse : WorkflowRunResponse
{
    [JsonPropertyName("steps")] public List<StepRunResponse> Steps { get; set; } = [];
}
