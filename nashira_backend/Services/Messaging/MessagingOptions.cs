namespace nashira_backend.Services.Messaging;

// Bound to the "Messaging" section of appsettings. Every value has a sane default
// so the section can be omitted entirely.
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    // The user-facing base URL of the frontend, used to build the account-linking
    // link the bot sends and the webhook URL shown in the admin UI
    // (e.g. https://nashira.example.com). No trailing slash.
    //
    // Without it the bot's linking prompt is a path with no host, which is not
    // something a user can click — so the link service says so explicitly rather
    // than sending half a URL.
    public string PublicBaseUrl { get; set; } = string.Empty;

    // Account-linking token lifetime.
    public int LinkTokenTtlMinutes { get; set; } = 15;

    // Refuse to enqueue more inbound work once the job queue is this deep.
    // Backpressure: a provider that gets a 503 retries later, which is a better
    // outcome than an unbounded queue of turns nobody will read.
    public int MaxQueueDepth { get; set; } = 500;

    // Outbound send retries before a delivery is marked failed.
    public int MaxSendAttempts { get; set; } = 3;

    // Reject inbound deliveries whose provider timestamp is older than this
    // (anti-replay; Slack and WhatsApp carry a timestamp). 0 disables the check.
    public int MaxRequestAgeSeconds { get; set; } = 300;

    // Retention sweeper: delete inbound/delivery audit rows older than this, and
    // link tokens past expiry. 0 disables the sweeper.
    public int RetentionDays { get; set; } = 90;
}
