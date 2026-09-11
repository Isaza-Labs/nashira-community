using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// What makes a workflow run without a person pressing a button: a cron schedule or
// an inbound webhook.
//
// Until this existed the only way to run a workflow was an authenticated POST from
// the UI, so nothing could be scheduled and nothing external could trigger
// remediation.
public class WorkflowTrigger : BaseModel
{
    public const string TypeCron = "cron";
    public const string TypeWebhook = "webhook";

    public static readonly string[] Types = [TypeCron, TypeWebhook];

    public Guid WorkflowTriggerId { get; set; }
    public Guid WorkflowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = TypeCron;
    public string? Description { get; set; }

    // ── cron ──────────────────────────────────────────────────────────
    public string? CronExpression { get; set; }

    // IANA zone id. Stored because "every night at 02:00" means a wall-clock time
    // in someone's timezone, and evaluating it in UTC silently shifts maintenance
    // windows by an hour twice a year.
    public string Timezone { get; set; } = "UTC";

    // ── webhook ───────────────────────────────────────────────────────

    // Public path segment: /api/hooks/{Route}. Random at creation, not derived from
    // the name — a guessable route plus AllowUnsigned would be an open trigger.
    public string? Route { get; set; }

    // HMAC-SHA256 shared secret, encrypted at rest. Never returned by the API; the
    // plaintext is shown exactly once, at creation or rotation.
    [JsonIgnore]
    public byte[]? EncryptedSecret { get; set; }

    // Escape hatch for testing: accept unsigned deliveries when no secret is set.
    // Default false, so a secret-less webhook rejects everything rather than
    // becoming an unauthenticated way to run a workflow.
    public bool AllowUnsigned { get; set; }

    // May the inbound body choose the devices the run targets?
    //
    // Default false, and that default is the security property. A webhook caller
    // authenticates with a shared secret, not a user session, so the run never
    // passes the RBAC check a manual run does. Honouring body-supplied targets
    // unconditionally would turn a leaked secret into "run this against any device
    // in the inventory". When enabled, body targets can only NARROW the configured
    // list — see WorkflowWebhookController.
    public bool AllowTargetOverride { get; set; }

    // ── common ────────────────────────────────────────────────────────

    // Devices the triggered run fires against. JSON array of ids.
    public string TargetDevicesJson { get; set; } = "[]";

    // Static payload merged under the run's `input`. JSON object.
    public string? InputDefaultsJson { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastError { get; set; }
    public Guid? LastRunId { get; set; }
    public int FireCount { get; set; }

    public Guid? CreatedBy { get; set; }
}
