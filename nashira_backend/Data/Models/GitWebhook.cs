using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// Inbound webhook attached to a registered Git repository. The remote (GitHub,
// GitLab, or anything that can sign a body) POSTs to /api/git/hooks/{Route}; a
// verified push optionally pulls the working copy and optionally enqueues a run of
// OnPushWorkflowId.
//
// Route is random and not derived from the name, for the same reason
// WorkflowTrigger.Route is: a guessable path plus AllowUnsigned would be an open
// trigger for anyone who can spell the repository.
public class GitWebhook : BaseModel
{
    public const string ProviderGithub = "github";
    public const string ProviderGitlab = "gitlab";
    public const string ProviderGeneric = "generic";

    public static readonly string[] Providers = [ProviderGithub, ProviderGitlab, ProviderGeneric];

    public Guid GitWebhookId { get; set; }
    public Guid GitRepositoryId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Decides which header carries the signature and how it is verified.
    public string Provider { get; set; } = ProviderGithub;

    // Public path segment: /api/git/hooks/{Route}.
    public string Route { get; set; } = string.Empty;

    // GitHub: HMAC-SHA256 of the body. GitLab: the literal token. Generic: HMAC like
    // GitHub. Encrypted at rest; the plaintext is shown once, at create or rotate.
    [JsonIgnore]
    public byte[]? EncryptedSecret { get; set; }

    public Guid? OnPushWorkflowId { get; set; }

    // Branch filter over the short ref name ("main"). Empty matches every branch.
    // Literal names only — wildcards would need a matcher the receiver has no way to
    // test against a real push before the push happens.
    public List<string> OnPushBranches { get; set; } = [];

    // Pull the working copy before dispatching, so the run sees the commit that
    // triggered it rather than whatever was on disk.
    public bool AutoPull { get; set; } = true;

    public bool Enabled { get; set; } = true;

    // Without a secret the receiver refuses the delivery: accepting unsigned POSTs
    // would make the route an unauthenticated way to run a workflow. Defaults to
    // false so the safe path is the one you get by not thinking about it.
    public bool AllowUnsigned { get; set; }

    public DateTime? LastDeliveryAt { get; set; }
    public string? LastDeliveryStatus { get; set; }
    public int DeliveryCount { get; set; }
}

// One row per webhook hit, verified or not, so an operator can answer "did GitHub
// call us, and what did we do about it".
//
// The body and headers are deliberately not persisted: a push payload carries commit
// messages and author emails, and this table would otherwise become the largest
// unreviewed store of them in the system. The metadata that explains the outcome is
// enough to debug one.
public class GitWebhookDelivery : BaseModel
{
    public const string StatusVerified = "verified";
    public const string StatusRejected = "rejected";
    public const string StatusDispatched = "dispatched";
    public const string StatusFailed = "failed";

    public Guid GitWebhookDeliveryId { get; set; }
    public Guid GitWebhookId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;

    public string Status { get; set; } = string.Empty;

    // push | ping | tag_push … best-effort, from the provider's event header or the
    // body's object_kind.
    public string? Event { get; set; }
    public string? Branch { get; set; }
    public string? CommitSha { get; set; }

    // The provider's own delivery id (GitHub's X-GitHub-Delivery). A retry reuses it,
    // which is what makes deduplication possible: the second POST finds this row and
    // returns its outcome instead of pulling and dispatching again.
    public string? DeliveryKey { get; set; }

    public Guid? JobId { get; set; }
    public string? Error { get; set; }
}
