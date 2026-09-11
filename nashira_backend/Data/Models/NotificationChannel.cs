using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// An outbound notification destination: a Slack incoming webhook, a Teams
// connector, or a generic JSON webhook.
//
// Nashira already sends email through EmailService; this is the other half —
// where a workflow's "tell the network team" step lands.
//
// Not to be confused with MessagingChannel, which is the *bidirectional* chat
// surface (a user talks to the agent from Slack/Telegram/WhatsApp/Teams and the
// reply returns to the same thread). This type is fire-and-forget: one URL, one
// direction, no identity, no conversation.
public class NotificationChannel : BaseModel
{
    public const string KindSlack = "slack";
    public const string KindTeams = "teams";
    public const string KindWebhook = "webhook";

    public static readonly string[] Kinds = [KindSlack, KindTeams, KindWebhook];

    public Guid NotificationChannelId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Kind { get; set; } = KindSlack;
    public string? Description { get; set; }

    // The destination URL, encrypted.
    //
    // For Slack and Teams the URL *is* the credential — anyone holding it can post
    // to the channel — so it is treated as a secret rather than as configuration,
    // and never returned by the API. That is why this is `byte[]` and not the
    // ${secret:...} reference Integration uses: there is no separate token an admin
    // could store and point at.
    [JsonIgnore]
    public byte[]? WebhookUrlEncrypted { get; set; }

    // Non-secret host, kept in the clear purely so the UI can show which service a
    // channel points at without decrypting anything.
    public string? TargetHost { get; set; }

    // Static headers for `webhook` kind (JSON object). Secrets do not belong here —
    // it is returned by the API.
    public string? HeadersJson { get; set; }

    // Same semantics as Integration.AllowPrivateNetwork.
    public bool AllowPrivateNetwork { get; set; }

    public string Status { get; set; } = Integration.StatusUnknown;
    public string? LastCheckError { get; set; }
    public DateTime? LastCheckedAt { get; set; }

    public bool Enabled { get; set; } = true;
    public Guid? CreatedBy { get; set; }
}

// One send attempt. Kept as a row rather than only a log line because "did the
// on-call channel actually get the alert?" is a question asked after the fact,
// when the log has rotated.
public class NotificationDelivery : BaseModel
{
    public Guid NotificationDeliveryId { get; set; }
    public Guid NotificationChannelId { get; set; }

    // Truncated before storing: a delivery record is an audit trail, not a copy of
    // every report the system has ever sent.
    public string Preview { get; set; } = string.Empty;

    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public int ElapsedMs { get; set; }
    public DateTime SentAt { get; set; }

    // The run this delivery came from, when a workflow sent it.
    public Guid? WorkflowRunId { get; set; }
}
