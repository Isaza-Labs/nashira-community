using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker;

// Everything a handler needs to run one step. Built by SnippetNodeExecutor from
// the node and its snippet row.
public sealed class SnippetRequest
{
    public required string NodeId { get; init; }
    public required Guid WorkflowId { get; init; }
    public required Guid? SnippetId { get; init; }
    public required string SnippetType { get; init; }

    // The node's config_overrides after variable resolution: every
    // {{ steps.x.output.y }} / {{ device.x }} / {{ input.x }} reference has
    // already been replaced, so a handler never sees a template.
    public required JsonElement Input { get; init; }

    // Snapshot of the snippet fields a handler may need. Carried here rather than
    // re-read from the DB so a handler cannot observe a snippet edited mid-run.
    public string? Code { get; init; }
    public string? ScriptLanguage { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    // python_snippet: run with network-capable modules available. Mirrors
    // Snippet.NetworkEnabled.
    public bool NetworkEnabled { get; init; }

    // The workflow environment this run belongs to (draft/qa/production), so a
    // handler that reaches a device can enforce the device's allow trio.
    public string? Environment { get; init; }

    // The run this step executes under. Pre-allocated by WorkflowRunService before
    // the walk starts, so artifact-producing handlers (report, slack_message) can
    // stamp the run they came from even though the run row is persisted after.
    public Guid? WorkflowRunId { get; init; }

    // The user who triggered the run, for artifact attribution. Null on
    // scheduled/system runs.
    public Guid? TriggeredBy { get; init; }
}

/// <summary>
/// Whether a step changed anything, as the handler reports it.
/// </summary>
/// <remarks>
/// Three values, and the third is not "unknown". A run cannot act on "unknown" without picking
/// something, and every reason to pick would be a guess — which is exactly the inference this
/// replaces. <see cref="AuthorDecides"/> says the handler genuinely cannot classify the action
/// because the author supplied it, and names who can: the node's <c>config_overrides.changes</c>
/// where the node carries the action (ssh commands, an mcp tool), or the snippet's own
/// declaration where the snippet carries it (a python script, a playbook).
///
/// A step of such a type with no declaration at either level FAILS, naming what to set. That is
/// louder than the old default, and it is the point: the previous design let nobody answer and
/// answered on their behalf.
/// </remarks>
public enum StepChange
{
    /// <summary>Ran and found nothing to do.</summary>
    Unchanged = 0,

    /// <summary>Ran and mutated something.</summary>
    Changed = 1,

    /// <summary>The handler cannot know; the author declared it, or the step fails.</summary>
    AuthorDecides = 2,
}

// What a handler gives back. The executor maps Success onto the node result and
// stores Output for downstream {{ steps.<node>.output.* }} references.
public sealed class SnippetResult
{
    public required bool Success { get; init; }

    // Structured payload. Handlers should return an object rather than a scalar:
    // a downstream template addresses fields, and a bare value has none.
    public JsonElement Output { get; init; }

    // Human-readable failure reason. Empty on success.
    public string Error { get; init; } = string.Empty;

    // Short machine code for the step row (timeout, http_error, unreachable…).
    public string? ErrorCode { get; init; }

    // What the handler did, in one or two lines, for a person reading the run
    // afterwards. Not a substitute for Output — output is the data a downstream node
    // addresses, this is the narrative that explains a result the data alone does not:
    // which command ran, which file was read, how many rows came back. A step that
    // succeeded and changed nothing is the case this exists for.
    public string? Logs { get; init; }

    // Whether re-running the step could plausibly succeed. Drives retry decisions;
    // a 404 is not retryable, a connect timeout is.
    public bool Retryable { get; init; }

    /// <summary>
    /// Whether this step changed anything. REQUIRED — there is no silence.
    /// </summary>
    /// <remarks>
    /// This used to be a <c>bool?</c> whose null the executor resolved from the snippet's
    /// idempotency tier: absent meant "changed" unless the handler was idempotent. Because
    /// most handlers are not, most steps were recorded as having changed something because
    /// nobody said otherwise — and the audit trail, the rollback plan and the run's final
    /// state were all computed on top of that. Three of fifteen handlers were breaking the
    /// silence.
    ///
    /// A tier says whether an action COULD be undone. This says whether anything WAS done.
    /// Answering the second with the first is what made it a label rather than a measurement.
    /// </remarks>
    public required StepChange Change { get; init; }

    public static SnippetResult Ok(object payload, StepChange change, string? logs = null) => new()
    {
        Success = true,
        Output = JsonSerializer.SerializeToElement(payload),
        Change = change,
        Logs = logs,
    };

    public static SnippetResult Fail(string error, string? code = null, bool retryable = false,
        string? logs = null) => new()
    {
        Success = false,
        // A step that failed before its action did anything changed nothing. A handler that
        // failed AFTER mutating must not use this factory — it says so explicitly instead.
        Change = StepChange.Unchanged,
        Error = error,
        ErrorCode = code,
        Retryable = retryable,
        Logs = logs,
        // Also in the output because a downstream node may branch on it. The step row
        // carries it in its own column too — digging a failure message out of a result
        // payload is not something a reader should have to know to do.
        Output = JsonSerializer.SerializeToElement(new { error }),
    };
}

// One snippet type's execution. Registered in DI as ISnippetHandler; the executor
// resolves the handler whose Type matches the snippet's, case-insensitively.
//
// Scoped, because most handlers need a request-scoped DbContext or one of
// Nashira's scoped services (the REST executor, the MCP service, the SSH runner).
public interface ISnippetHandler
{
    // Must equal Snippet.Type exactly (matched case-insensitively).
    string Type { get; }

    // This handler's default idempotency tier. A snippet may declare its own and
    // override this in EITHER direction — the author is the only one who can know that
    // their `integration_action` wraps a GET. The one exception is NonReversible, which
    // is absolute and cannot be declared away. See Idempotency.Effective.
    IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct);
}

// Resolves a snippet type to its handler. Registered as a singleton over the
// scoped handlers' types so a lookup does not build every handler.
public interface ISnippetHandlerRegistry
{
    ISnippetHandler? Resolve(string type, IServiceProvider scope);
    IReadOnlyCollection<string> KnownTypes { get; }
}
