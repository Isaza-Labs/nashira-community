namespace nashira_backend.Data.Models;

// Canonical-form versions for the audit hash chain.
public static class AuditHashVersion
{
    // No Actor in the canonical string.
    public const int WithoutActor = 1;
    // Actor bound into the canonical string.
    public const int WithActor = 2;

    public const int Current = WithActor;
}

// Append-only, immutable audit record. Each mutation the agent executes (and any
// audited domain mutation) is one row. Rows are hash-chained (PrevHash -> Hash)
// so the trail is tamper-evident and signable from T0.
//
// Deliberately NOT a BaseModel: an audit row is never soft-deleted or updated,
// so it carries no IsActive/UpdatedAt. At is declared directly.
public class AuditEvent
{
    public Guid AuditEventId { get; set; }
    public long Sequence { get; set; }            // monotonic, 1-based
    public Guid? UserId { get; set; }

    // Human-readable actor. The username for a signed-in request; for automation it is
    // the synthetic identity the work bound on its scope — "workflow-runner",
    // "scheduler", "webhook".
    //
    // UserId is null on every automation path, because nobody is signed in. Without
    // this, an unattended run and a human change were the same indistinguishable null
    // row, which is the one question an audit trail exists to answer.
    //
    // Today the only bound identity is "workflow-runner" (the job worker). A webhook
    // that enqueues a run is attributed to the runner too, because the run happens
    // later in the worker — telling those apart needs the job to carry its origin, and
    // it does not yet.
    public string? Actor { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string? RequestId { get; set; }
    public DateTime At { get; set; }
    public string? PrevHash { get; set; }         // previous row's Hash (null at genesis)
    public string Hash { get; set; } = string.Empty;

    // Which canonical form Hash was computed over. v1 predates Actor; v2 binds it.
    //
    // Versioned rather than re-signed: re-hashing the existing rows under the new
    // formula would make them verify by destroying the evidence they exist to provide.
    // A row states how it was signed, and Verify checks it that way.
    public int HashVersion { get; set; } = AuditHashVersion.Current;
}
