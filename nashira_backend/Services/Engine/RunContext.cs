using System.Text.Json;

namespace nashira_backend.Services.Engine;

// What `{{ run.* }}` resolves against (workflow.v1 templates/SPEC.md §7).
//
// Ten fields, fixed by the contract: id, workflow_id, workflow_name, environment,
// trigger, started_at, owner_email, url, failed_step_id, failed_step_error. A field
// this build cannot provide is emitted as null rather than omitted, so a template
// that reads it resolves (to null) instead of being left literal and failing the
// step with `unresolved_template` — the contract's rule, and the difference between
// a portable workflow degrading gracefully and refusing to run.
//
// Built once per run by WorkflowRunService; the two failed_step_* fields are set by
// SnippetNodeExecutor as steps fail, so a node reached through a `failure` edge can
// name the step that sent it there — which is what a notify-on-failure node is for.
public sealed class RunContext
{
    public required Guid Id { get; init; }
    public required Guid WorkflowId { get; init; }
    public required string WorkflowName { get; init; }
    public required string Environment { get; init; }
    public required string Trigger { get; init; }
    public required DateTime StartedAt { get; init; }

    // Nashira's run records the triggering user by id; the email is not on the
    // execution path. Null until a caller has it.
    public string? OwnerEmail { get; init; }

    // No public base URL is configured for the UI, so there is no run page to link.
    public string? Url { get; init; }

    // Empty until a step fails, NOT null, and the difference is not cosmetic. A notify node
    // reached by an `always` edge reads these on the SUCCESS path, often as the whole value of
    // a message body: as a JSON null the body becomes null and a handler that requires one
    // refuses a step that used to work; as "" it sends an empty body, which is what the author
    // asked for. This matches the contract's oracle, which seeds them the same way and for the
    // same reason — a conformance vector briefly said otherwise and was corrected.
    public string FailedStepId { get; private set; } = string.Empty;
    public string FailedStepError { get; private set; } = string.Empty;

    // The most recent failure. Under stop_on_failure the only steps that run after a
    // failure are the ones on its `failure` edges, so "the last step that failed" is
    // the step whose failure they consume.
    public void MarkFailed(string nodeId, string? error)
    {
        FailedStepId = nodeId;
        FailedStepError = error ?? string.Empty;
    }

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(new
    {
        id = Id,
        workflow_id = WorkflowId,
        workflow_name = WorkflowName,
        environment = Environment,
        trigger = Trigger,
        started_at = StartedAt.ToUniversalTime().ToString("o"),
        owner_email = OwnerEmail,
        url = Url,
        failed_step_id = FailedStepId,
        failed_step_error = FailedStepError,
    });
}
