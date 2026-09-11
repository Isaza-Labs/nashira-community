namespace nashira_backend.Data.Models;

// One execution of a workflow. Immutable after it finishes; the per-node detail lives in
// StepRun rows and the per-mutation trail in the hash-chained audit log. Tenant-scoped.
public class WorkflowRun : BaseModel
{
    public const string StatusRunning = "running";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    public Guid WorkflowRunId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string SchemaHash { get; set; } = string.Empty;
    public string Status { get; set; } = StatusRunning;
    public string FinalState { get; set; } = string.Empty; // completed | rolled_back | failed
    public int NodeCount { get; set; }
    public int ChangedCount { get; set; }
    public int FailedCount { get; set; }
    public string RollbackPlanJson { get; set; } = "[]";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public Guid? TriggeredByUserId { get; set; }

    // The run whose `subflow` node started this one (execution/SPEC.md §5), null for a
    // top-level run. A child subflow run is a real run — it has its own row, its own
    // steps and its own audit events — and this is the only thing that says it was not
    // started on its own account. Without it a runs list shows a burst of runs nobody
    // asked for and no way to tell which parent they belong to.
    public Guid? ParentRunId { get; set; }

    // What started this run: manual | agent | schedule | webhook | git_webhook | test.
    // TriggeredByUserId already separates a person from automation, but not which
    // automation — and once a cron, an inbound webhook and a git push can all start
    // the same workflow, "it ran at 3am and nobody knows why" is a question the run
    // record has to be able to answer on its own.
    public string Trigger { get; set; } = RunTrigger.Manual;

    // Why the run failed when NO step can say. Orchestration fails before or between
    // steps — a policy that denied it, a DAG that would not parse, a target set that
    // resolves to nothing — and those failures left no run row at all: the caller got
    // an exception and the runs list showed nothing, so "I pressed run and nothing
    // happened" was literally true and completely undiagnosable.
    //
    // Null on success and on ordinary step failures, where the failing step's own
    // error is the better answer.
    public string? Error { get; set; }

    // The payload the run was triggered with, feeding `{{ input.* }}`. Stored so a
    // finished run can be read back and understood — the same definition produces
    // different work for different inputs, and without this the run record does not
    // say which one it did.
    public string? InputJson { get; set; }

    // Device ids this run targets, as a JSON array. Per-device steps fan out over
    // them; `once` steps ignore them. Empty means no device context, and a
    // per-device step then has nothing to run against — reported rather than
    // silently skipped.
    public string TargetDevicesJson { get; set; } = "[]";
}

// What started a run. Free-form on the column so a new source does not need a
// migration; these are the ones that exist.
public static class RunTrigger
{
    public const string Manual = "manual";
    public const string Agent = "agent";
    public const string Schedule = "schedule";
    public const string Webhook = "webhook";
    public const string GitWebhook = "git_webhook";
    public const string Test = "test";

    // A run started by a `subflow` node of another run. It is what separates the
    // children of one parent run from the runs a person or a schedule asked for, and
    // execution/SPEC.md §5 fixes the wire word.
    public const string Subflow = "subflow";
}

// One node's execution within a WorkflowRun.
//
// Result, ErrorCode and OutputJson say what happened. The rest says enough to act on
// it without re-running anything: a result word and a machine code do not tell you
// what the node was handed after templates resolved, what it said when it failed, how
// long it took, or whether it got there on the third try.
public class StepRun : BaseModel
{
    public Guid StepRunId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string Result { get; set; } = string.Empty; // changed | no_change | failed | skipped
    public string? ErrorCode { get; set; }
    public bool Retryable { get; set; }
    public string? OutputJson { get; set; }

    // The failure message, in its own column. It was only ever readable by digging
    // `error` out of the output payload, which worked because SnippetResult.Fail put
    // it there — a convention, not a contract, and invisible to anyone reading the
    // row who did not already know it.
    public string? Error { get; set; }

    // The node's configuration as the handler received it: templates resolved,
    // secret-looking values redacted, capped. The single most useful field when a
    // step did something unexpected, because `{{ steps.x.output.y }}` resolving to
    // something other than what the author assumed is the most common cause and is
    // otherwise unreconstructable after the fact.
    public string? InputJson { get; set; }

    // The handler's own account of what it did. Explains a `no_change` that the
    // output cannot.
    public string? Logs { get; set; }

    // 0 when the step never reached a handler; 1 normally; more when the retry policy
    // re-attempted. Used to live spliced into the output payload, and only when it was
    // greater than one.
    public int Attempts { get; set; }

    // The run this step started, when the step is a `subflow` node (execution/SPEC.md
    // §5). The back-link is what turns "this step ran another workflow" into something
    // a reader can follow: the child's rows carry the detail, and matching them by
    // NodeId alone could not tell two subflow nodes invoking the same child apart.
    public Guid? ChildRunId { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
