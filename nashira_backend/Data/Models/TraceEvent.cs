namespace nashira_backend.Data.Models;

// The operational trail: what the platform did, how long it took, and whether it worked.
//
// The third of three, and each answers a question the others cannot:
//   audit_events — what CHANGED, with before/after, hash-chained and tamper-evident.
//   auth_events  — who signed in, failed, or got locked out.
//   trace_events — what HAPPENED, including everything that changed nothing.
//
// The gap this fills is work with no HTTP request behind it. A job the worker claimed
// and never finished, a cron firing that resolved to no devices, a pip install that
// timed out, a retention sweep that deleted more than expected: none of that mutates an
// entity, so none of it reaches the audit trail, and until now the only record was a
// container log that scrolls away.
//
// Deliberately NOT hash-chained. The chain serialises writes behind one process-wide
// gate, which is right for evidence and wrong for a table that takes a row on every
// request; and a debugging trail nobody is trying to forge does not need the ceremony.
public class TraceEvent : BaseModel
{
    // Statuses. `Started` rows are the point of the whole design: an operation that
    // never completed leaves one behind, which is the only way to see work that is
    // stuck rather than merely absent.
    public const string StatusStarted = "started";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    // Categories — the coarse filter the screen groups on.
    public const string CategoryHttp = "http";
    public const string CategoryAi = "ai";
    public const string CategoryTool = "tool";
    public const string CategoryWorkflow = "workflow";
    public const string CategoryWorker = "worker";
    public const string CategoryScheduler = "scheduler";
    public const string CategorySystem = "system";

    public Guid TraceEventId { get; set; }

    // Null for work nobody asked for: the scheduler, retention, the boot sequence.
    public Guid? UserId { get; set; }

    // Null when there is no signed-in user — the ambient identity of the automation
    // that ran it, the same convention the audit trail uses.
    public string? Actor { get; set; }

    // HttpContext.TraceIdentifier, matching what audit_events stores, so one request can
    // be reconstructed across both tables by the same value.
    public string? RequestId { get; set; }

    // Dot-separated, noun-first: worker.job.claim, ai.chat.stream, scheduler.trigger.fire.
    public string Action { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = StatusStarted;

    // Null while a row is `started`. Measured by the scope that opened it, not by
    // differencing timestamps — two rows written by a queue drained in batches do not
    // carry a reliable interval between them.
    public int? DurationMs { get; set; }

    public string? ErrorMessage { get; set; }

    // Free-form context: which job, which device count, which model. Stored as text
    // rather than jsonb because nothing queries inside it — the filters are all on the
    // indexed columns, and a GIN index on a table this size costs more than it returns.
    public string? MetadataJson { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}
