using nashira_backend.Data.Models;

namespace nashira_backend.Services.Messaging;

// The named HttpClient every provider sends through, so timeouts and handler
// lifetime are configured in one place rather than per provider.
public static class MessagingHttpClients
{
    public const string Name = "messaging";
}

// A normalized view of an inbound HTTP webhook request, decoupled from ASP.NET so
// providers stay testable. Header/query lookups are case-insensitive. Body is the
// raw bytes, which HMAC verification needs verbatim — re-serializing the parsed
// JSON would change the signature.
public sealed class MessagingHttpRequest
{
    public required string Method { get; init; }
    public required byte[] Body { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Query { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string? QueryValue(string name) => Query.TryGetValue(name, out var v) ? v : null;
}

public enum WebhookVerifyOutcome
{
    Verified,

    // The provider's verification handshake wants a specific body echoed back
    // (Slack url_verification challenge, WhatsApp hub.challenge GET).
    Challenge,

    Rejected,
}

public sealed class WebhookVerifyResult
{
    public WebhookVerifyOutcome Outcome { get; init; }
    public string? ChallengeBody { get; init; }
    public string ChallengeContentType { get; init; } = "text/plain";
    public int RejectStatusCode { get; init; }
    public string? RejectReason { get; init; }

    public static WebhookVerifyResult Verified() =>
        new() { Outcome = WebhookVerifyOutcome.Verified };

    public static WebhookVerifyResult Challenge(string body, string contentType = "text/plain") =>
        new() { Outcome = WebhookVerifyOutcome.Challenge, ChallengeBody = body, ChallengeContentType = contentType };

    public static WebhookVerifyResult Rejected(int statusCode, string reason) =>
        new() { Outcome = WebhookVerifyOutcome.Rejected, RejectStatusCode = statusCode, RejectReason = reason };
}

// A user message extracted from an inbound payload. ParseInbound returns null when
// the payload is not a routable user message (the bot's own echo, a delivery
// receipt, a non-message event).
public sealed class InboundMessage
{
    public required string ProviderEventId { get; init; }

    // "" when the provider has no workspace concept (Telegram). The ingest
    // coalesces null → "" to match the identity unique index.
    public string ExternalWorkspaceId { get; init; } = string.Empty;

    public required string ExternalUserId { get; init; }
    public required string ExternalThreadId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? SenderDisplayName { get; init; }

    // message | mention | other — best-effort, stored on the inbound audit row.
    public string EventKind { get; init; } = "message";
}

// A message to push back to the external channel.
public sealed class OutboundMessage
{
    public required string ExternalThreadId { get; init; }
    public required string Text { get; init; }

    /// <summary>
    /// Provider-native rich content, as raw JSON, for providers that accept it. Null for an
    /// ordinary text reply, which is every message the ingest/reply flow sends.
    /// </summary>
    /// <remarks>
    /// Added for the `slack_message` snippet, where the interchange contract makes rich
    /// content a canonical key: a handler given one either delivers it or refuses the step,
    /// and delivering the plain text instead would be the silent reduction the contract
    /// exists to prevent.
    /// </remarks>
    public string? BlocksJson { get; init; }
}

// Strategy for one messaging platform: how to verify an inbound webhook, how to
// parse it into a user message, and how to send a reply.
//
// Decrypted secrets are passed in by the caller (ingest / worker) so providers
// never touch the keyring, which also makes every provider trivially unit-testable
// against plain strings.
//
// Implementations must be stateless and thread-safe — one instance is shared as a
// singleton and resolved by Provider name.
public interface IMessagingProvider
{
    // telegram | slack | whatsapp | teams — matches MessagingChannel.Provider.
    string Provider { get; }

    // Verify the request's authenticity (signature / token / JWT) and handle any
    // provider handshake. decryptedSigningSecret is null when the channel has none.
    Task<WebhookVerifyResult> VerifyAsync(
        MessagingChannel channel, MessagingHttpRequest request,
        string? decryptedSigningSecret, CancellationToken ct);

    // Extract a routable user message, or null if the payload is not one.
    InboundMessage? ParseInbound(MessagingChannel channel, MessagingHttpRequest request);

    // Push a reply to the channel. decryptedBotToken is null when the channel has no
    // outbound token configured; the provider should then fail with a clear message
    // rather than a null-reference.
    Task SendAsync(
        MessagingChannel channel, string? decryptedBotToken,
        OutboundMessage message, CancellationToken ct);
}

// Resolves the IMessagingProvider for a channel's Provider string. Every concrete
// provider is registered as IMessagingProvider; this indexes them by name. Returns
// null for an unknown provider so callers can reject cleanly — a channel row
// written by a newer build (or by hand) fails as a validation error rather than
// throwing out of the dispatcher.
public interface IMessagingProviderResolver
{
    IMessagingProvider? Resolve(string? provider);
}

public sealed class MessagingProviderResolver : IMessagingProviderResolver
{
    private readonly Dictionary<string, IMessagingProvider> _byName;

    public MessagingProviderResolver(IEnumerable<IMessagingProvider> providers) =>
        _byName = providers.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);

    public IMessagingProvider? Resolve(string? provider) =>
        provider is not null && _byName.TryGetValue(provider, out var p) ? p : null;
}

// Raised by a provider when an outbound send fails. `Transient` decides whether the
// job processor retries: a 500 or a socket reset is worth another attempt, while a
// 403 on a revoked bot token will fail identically three times and only makes the
// failure slower to see.
public sealed class MessagingSendException : Exception
{
    public MessagingSendException(string message, bool transient = false, Exception? inner = null)
        : base(message, inner) => Transient = transient;

    public bool Transient { get; }
}
