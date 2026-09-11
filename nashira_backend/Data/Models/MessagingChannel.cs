using System.Text.Json.Serialization;

namespace nashira_backend.Data.Models;

// A BIDIRECTIONAL messaging channel: a connection to a single provider workspace
// (a Telegram bot, a Slack app install, a WhatsApp number, a Teams bot). A user
// writes from the chat platform, the message enters through a webhook (or an
// outbound socket), the agent processes it, and the reply returns to the same
// thread.
//
// Inbound webhooks are verified against EncryptedSigningSecret; outbound replies
// authenticate with EncryptedBotToken.
//
// Identity & permissions: a turn from this channel runs as the *linked* internal
// user (see MessagingIdentityLink) with that user's real, current role, capped —
// never raised — by MaxRole. RequireLinkedUser gates whether an unlinked external
// user may do anything at all. A channel can only narrow privilege; there is no
// escalation by transport.
//
// Not to be confused with NotificationChannel, which is the outbound-only
// notification sink a workflow's `slack_message` step posts to.
public class MessagingChannel : BaseModel
{
    public const string ProviderTelegram = "telegram";
    public const string ProviderSlack = "slack";
    public const string ProviderWhatsApp = "whatsapp";
    public const string ProviderTeams = "teams";

    public static readonly string[] Providers =
        [ProviderTelegram, ProviderSlack, ProviderWhatsApp, ProviderTeams];

    public const string RoleViewer = "viewer";
    public const string RoleOperator = "operator";
    public const string RoleAdmin = "admin";

    public static readonly string[] MaxRoles = [RoleViewer, RoleOperator, RoleAdmin];

    public Guid MessagingChannelId { get; set; }

    // telegram | slack | whatsapp | teams. Selects the IMessagingProvider.
    public string Provider { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    // Outbound auth (Slack xoxb-, Telegram bot token, WhatsApp access token,
    // Teams Entra client secret). Encrypted at rest by ISecretProtector.
    [JsonIgnore]
    public byte[]? BotTokenEncrypted { get; set; }

    // Inbound signature/secret material (Slack signing secret, Telegram
    // secret_token, WhatsApp app secret). Encrypted at rest.
    // Unused for Teams (Bot Framework JWT) and for Slack Socket Mode (no HTTP
    // webhook to verify).
    [JsonIgnore]
    public byte[]? SigningSecretEncrypted { get; set; }

    // Opt-in credential for the provider's NO-PUBLIC-INGRESS mode. Setting it on
    // an enabled channel makes the backend dial OUT and receive events over that
    // connection instead of waiting on a public webhook.
    //
    //   slack → App-Level Token (xapp-…, scope connections:write) for Socket
    //           Mode; SlackSocketModeHostedService opens a WebSocket to Slack.
    //   teams → Azure Relay Hybrid Connection connection string (with
    //           EntityPath=…); TeamsRelayHostedService opens the Relay control
    //           channel and the Bot Framework POSTs arrive over it. Teams has no
    //           Socket Mode of its own, so the Relay stands in for one.
    //
    // Unused by telegram and whatsapp, which are webhook-only.
    [JsonIgnore]
    public byte[]? AppTokenEncrypted { get; set; }

    // Non-secret provider-specific config as a JSON object string
    // (phone_number_id, verify_token, graph_version, app_id, tenant_id, …).
    // Stored as text rather than jsonb to match how Nashira persists every other
    // free-form config blob (Integration.HeadersJson, Snippet config).
    public string? ExternalConfigJson { get; set; }

    // Restrictive ceiling on the effective role for turns from this channel
    // (e.g. "operator"): effective = min(linkedUser.Role, MaxRole). NEVER raises
    // privileges. Null = no extra ceiling (the user's real role applies).
    public string? MaxRole { get; set; }

    // When true (default), an external user with no MessagingIdentityLink cannot
    // run the agent — the bot replies with an account-linking link instead.
    public bool RequireLinkedUser { get; set; } = true;

    // Allowlist of external user ids permitted to use the channel. Empty means
    // "no allowlist" — anyone who passes the linking rules may talk to the bot.
    public string? AllowedExternalIdsJson { get; set; }

    // Escape hatch mirroring GitWebhook's: accept unverified inbound deliveries
    // (test emitters). Defaults false so the safe path is the default.
    public bool AllowUnsigned { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime? LastDeliveryAt { get; set; }
    public string? LastDeliveryStatus { get; set; }

    public Guid? CreatedBy { get; set; }
}

// Maps an external messaging identity (provider user, optionally scoped to a
// workspace/team) to a real internal user. Created by the self-service linking
// flow: the bot sends a one-time link, the user opens it authenticated, and
// confirms.
//
// The linked user's *current* role governs every turn — the link never caches the
// role, so a role change in Nashira takes effect on the next message.
public class MessagingIdentityLink : BaseModel
{
    public Guid MessagingIdentityLinkId { get; set; }
    public Guid MessagingChannelId { get; set; }

    // Slack team_id / WhatsApp business id / Telegram (empty) — disambiguates the
    // same external user id across workspaces. Empty when the provider has no
    // workspace concept; empty rather than null so the unique index behaves.
    public string ExternalWorkspaceId { get; set; } = string.Empty;

    // The external user id (Slack U…, Telegram from.id, WhatsApp wa_id, Teams aad id).
    public string ExternalUserId { get; set; } = string.Empty;

    // The internal user whose role governs turns from this external identity.
    public Guid LinkedUserId { get; set; }

    public string? DisplayName { get; set; }
}

// Append-only audit row for every inbound webhook delivery (verified or not),
// and the idempotency key for the channel. Providers re-deliver when a webhook is
// slow to ack, so (MessagingChannelId, ProviderEventId) is unique and a repeat is
// dropped.
//
// The raw body is deliberately NOT persisted (PII + size) — only the metadata
// that explains what happened, mirroring GitWebhookDelivery.
public class MessagingInboundEvent : BaseModel
{
    // queued → processing is the atomic idempotency claim the worker makes, so a
    // reclaimed job cannot run the agent twice (at-most-once turn processing).
    public const string StatusReceived = "received";
    public const string StatusVerified = "verified";
    public const string StatusRejected = "rejected";
    public const string StatusQueued = "queued";
    public const string StatusProcessing = "processing";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    public Guid MessagingInboundEventId { get; set; }
    public Guid MessagingChannelId { get; set; }

    // Provider-native event id: Slack event_id, Telegram update_id, WhatsApp
    // message id, Teams activity id. Unique per channel for dedupe.
    public string ProviderEventId { get; set; } = string.Empty;

    // thread_ts / chat_id / conversation id — the external thread this belongs to.
    public string? ExternalThreadId { get; set; }

    // The AIConversation this event resolved to (null if rejected/unlinked).
    public Guid? ConversationId { get; set; }

    public string Status { get; set; } = StatusReceived;

    // message | mention | other — best-effort classification.
    public string? Event { get; set; }

    public string? Error { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}

// Append-only audit row for every outbound message attempt (the agent's reply, or
// the account-linking prompt). Retried sends bump Attempt; a terminal failure
// lands as Status=failed with the last Error.
//
// Distinct from NotificationDelivery, which records posts to an outbound-only
// notification channel.
public class MessagingDelivery : BaseModel
{
    public const string StatusPending = "pending";
    public const string StatusSent = "sent";
    public const string StatusFailed = "failed";

    public Guid MessagingDeliveryId { get; set; }
    public Guid MessagingChannelId { get; set; }
    public Guid? ConversationId { get; set; }
    public string? ExternalThreadId { get; set; }
    public string Status { get; set; } = StatusPending;
    public int Attempt { get; set; }
    public string? Error { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

// One-time account-linking token. Minted when an unlinked external user messages
// the bot; the cleartext travels only in the link the bot sends them. On confirm,
// the authenticated user's id is written to ConsumedByUserId and a
// MessagingIdentityLink is created.
//
// Short-lived and single-use, so the link cannot be replayed or shared. Only the
// hash is stored — a leaked database gives nobody a usable link.
public class MessagingLinkToken : BaseModel
{
    public Guid MessagingLinkTokenId { get; set; }
    public Guid MessagingChannelId { get; set; }

    // The external identity that will be linked when this token is confirmed.
    public string ExternalWorkspaceId { get; set; } = string.Empty;
    public string ExternalUserId { get; set; } = string.Empty;

    // SHA-256 of the cleartext token. Lookups hash the presented value and compare.
    public byte[] TokenHash { get; set; } = [];

    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public Guid? ConsumedByUserId { get; set; }
}
