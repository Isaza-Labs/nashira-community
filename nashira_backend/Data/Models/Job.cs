using System.Text.Json;

namespace nashira_backend.Data.Models;

// A unit of background work, claimed atomically by a worker.
//
// This is the queue that was deliberately deferred at the start of the engine
// work — and the three problems that deferral left open all pointed back here:
// the webhook ran its workflow inside the HTTP request (sender timeout → retry →
// duplicate run), the scheduler executed triggers sequentially (one long run
// delayed every cron), and neither was safe with two replicas. A claimed-row
// queue with SKIP LOCKED closes all three at once.
//
// Claiming is a single UPDATE … WHERE JobId = (SELECT … FOR UPDATE SKIP LOCKED),
// so two workers can never take the same job. A claim carries a lease; a worker
// that dies mid-job leaves a lease that expires, and the reclaim sweep marks the
// job FAILED rather than requeueing it — a workflow run is not idempotent, and
// re-running one that half-happened is worse than reporting it dead.
public class Job : BaseModel
{
    public const string StatusQueued = "queued";
    public const string StatusClaimed = "claimed";
    public const string StatusSucceeded = "succeeded";
    public const string StatusFailed = "failed";

    public const string TypeWorkflowRun = "workflow_run";

    public Guid JobId { get; set; }
    public string Type { get; set; } = TypeWorkflowRun;
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = StatusQueued;

    public int Attempts { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }

    public string? Error { get; set; }
    public DateTime? CompletedAt { get; set; }

    // The run this job produced, once it did.
    public Guid? WorkflowRunId { get; set; }

    // Set when a trigger enqueued this job, so the worker can write the trigger's
    // last-run bookkeeping after the run finishes.
    public Guid? WorkflowTriggerId { get; set; }

    // Webhook delivery dedup. Senders retry on timeouts and 5xx; when a caller
    // supplies a delivery id, a retry of the same delivery finds this row and gets
    // the SAME job back instead of enqueueing a second run. Unique per trigger
    // (filtered), so the same key on different triggers cannot collide.
    public string? DeliveryKey { get; set; }
}

// The payload shapes, kept next to the entity so enqueue and worker cannot drift.
public static class JobPayloads
{
    public sealed record WorkflowRunPayload(
        Guid WorkflowId, JsonElement? Input, List<Guid> TargetDevices, string Trigger);

    // `trigger` travels on the payload rather than in a column: a cron sweep, an
    // inbound webhook and a git push all enqueue through the same queue, and the row's
    // WorkflowTriggerId can only tell the first apart from the rest.
    public static string WorkflowRun(
        Guid workflowId, JsonElement? input, IReadOnlyList<Guid> targets, string trigger) =>
        JsonSerializer.Serialize(new
        {
            workflow_id = workflowId,
            input,
            target_devices = targets,
            trigger,
        });

    public static WorkflowRunPayload? TryParseWorkflowRun(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("workflow_id", out var idEl)
                || !Guid.TryParse(idEl.GetString(), out var workflowId))
                return null;

            JsonElement? input = null;
            if (root.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
                input = inputEl.Clone();

            var targets = new List<Guid>();
            if (root.TryGetProperty("target_devices", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var el in t.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                        targets.Add(g);

            // Jobs enqueued before this field existed replay as what they almost
            // always were: something automated that is not a person pressing run.
            var trigger = root.TryGetProperty("trigger", out var trg) && trg.ValueKind == JsonValueKind.String
                ? trg.GetString() ?? RunTrigger.Schedule
                : RunTrigger.Schedule;

            return new WorkflowRunPayload(workflowId, input, targets, trigger);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
