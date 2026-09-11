namespace nashira_backend.Data.Models;

// A module a `python_snippet` may import.
//
// The allowlist is a database table rather than a constant so an operator can
// widen it for their environment without a redeploy — but it is an ALLOWlist, not
// a denylist, because enumerating what is dangerous in the Python standard library
// is a losing game. `os`, `subprocess`, `ctypes`, `importlib` and `socket` all
// reach outside the sandbox, and that list grows with every release.
//
// Matching is on the ROOT module: an entry for `json` permits `json.decoder`, and
// an entry for `xml` permits every `xml.*`. That is coarse on purpose — a snippet
// author reasons about "may I use requests?", not about submodule trees.
// A row can also be a PACKAGE this deployment installs. The standard library is
// already on disk, so allowing `json` is the whole job; allowing `netmiko` is not,
// and an allowlist entry for something nobody installed is an ImportError with a
// misleading message. A `pip` row therefore carries an install to perform and a
// state machine to perform it in: pending → installing → ready | failed. Only
// `ready` rows are offered to a snippet.
public class AllowedPythonModule : BaseModel
{
    public const string SourceStdlib = "stdlib";
    public const string SourcePip = "pip";

    public const string StatusPending = "pending";
    public const string StatusInstalling = "installing";
    public const string StatusReady = "ready";
    public const string StatusFailed = "failed";

    public Guid AllowedPythonModuleId { get; set; }

    // Root module name, lowercased. e.g. "json", "re", "ipaddress".
    public string Module { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Whether this module is only available to snippets that opted into the
    // relaxed sandbox. `socket` and `requests` are useless without network access
    // and dangerous with it, so they are gated behind the same opt-in.
    public bool RequiresNetwork { get; set; }

    // `stdlib` (nothing to install) or `pip` (installed by the provisioner).
    public string Source { get; set; } = SourceStdlib;

    // The pip requirement to install — `netmiko`, or `netmiko==4.3.0`. Distinct from
    // Module because the two routinely differ: `pip install pyyaml` imports as `yaml`.
    // Null for stdlib rows.
    public string? PipSpec { get; set; }

    // stdlib rows are born ready; pip rows are born pending and wait for the
    // provisioner. The import guard honours `ready` and nothing else, so a half-
    // installed package can never be reachable from a snippet.
    public string Status { get; set; } = StatusReady;

    // What pip actually installed, read back from the module after the install so it
    // reflects reality rather than the requested spec.
    public string? InstalledVersion { get; set; }

    // Why the install failed, for the admin screen. pip's own output, tail-trimmed.
    public string? Error { get; set; }

    public Guid? CreatedBy { get; set; }
}
