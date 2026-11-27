namespace nashira_backend.Data.Models;

// A named, versioned Python environment an admin defines and the backend builds with
// uv at runtime — no image rebuild.
//
// Why this exists: the image installs one set of packages for every Python execution,
// and one set cannot satisfy every device. paramiko 5 removed `ssh-rsa`, and a switch
// that offers only that host key algorithm (Extreme EXOS on OpenSSH 8.1) then fails
// with `Incompatible ssh peer (no acceptable host key)`. Downgrading the image for the
// whole fleet would trade one broken device for everyone else's security fixes, so an
// environment pins packages for the executions that need them and nothing else.
//
// Who uses one: the SSH runner (via Device.PythonEnvironmentId, or the SSH fallback
// below) and python_snippet / ansible_playbook (via Snippet.PythonEnvironmentId).
// A null reference anywhere means the image's own python3 — the behaviour that
// existed before environments did, so no existing row changes meaning.
//
// State machine: pending → building → ready | failed. Editing Requirements or
// PythonVersion, asking for a rebuild, or an allowlist change that alters what the
// environment includes (a target, a pip spec, a revoked row) puts the row back to
// pending. Only `ready`
// is ever handed to an execution (see IPythonEnvironmentResolver).
public class PythonEnvironment : BaseModel
{
    public const string StatusPending = "pending";
    public const string StatusBuilding = "building";
    public const string StatusReady = "ready";
    public const string StatusFailed = "failed";

    // The name the image's own interpreter is reported as when no environment is used.
    public const string SystemName = "system";

    public Guid PythonEnvironmentId { get; set; }

    // Slug, ^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$, unique among active rows. It is also the
    // directory name under <PackagesDir>/envs, which is why it is a slug and immutable.
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Passed to `uv venv --python`. Null means the image's python3.
    public string? PythonVersion { get; set; }

    // requirements.txt format. May carry index URLs with credentials; every response and
    // audit entry masks the `user:pass@` part (PythonEnvironmentRules.MaskCredentials).
    public string Requirements { get; set; } = string.Empty;

    // The environment the SSH runner retries with when paramiko refuses the peer's host
    // key algorithm. At most one active row may carry it; the service enforces that.
    public bool IsSshFallback { get; set; }

    public string Status { get; set; } = StatusPending;

    // Why the last build failed, tail-trimmed. Masked on the way out like Requirements.
    public string? LastError { get; set; }

    // `uv pip freeze` after the last successful build: what is actually installed, as
    // opposed to what was asked for.
    public string? ResolvedPackages { get; set; }

    // SHA-256 over (PythonVersion, effective requirements) of the build that produced
    // ResolvedPackages, so a stale ready row is detectable. The effective requirements
    // are Requirements plus the pip specs of the allowlist rows installed in this
    // environment (PythonModuleEnvironment); with none they are Requirements exactly.
    public string? RequirementsHash { get; set; }

    public DateTime? BuiltAt { get; set; }

    // Claim, same pattern as the pip provisioner: a conditional UPDATE flips the row to
    // `building` and stamps who and when. A claim older than the stale window belonged
    // to a process that died mid-build and may be taken over.
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
