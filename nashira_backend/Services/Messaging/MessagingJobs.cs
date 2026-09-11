using System.Text.Json;

namespace nashira_backend.Services.Messaging;

// Job types + payloads for the messaging queue. Both ride the shared `jobs` table
// and are consumed by MessagingWorkerHostedService.
//
// JobWorkerHostedService deliberately skips these types (and this service skips
// workflow_run), so the two workers share a table without ever claiming each
// other's work.
public static class MessagingJobTypes
{
    // Run the agent for an inbound user message, then enqueue a Send.
    public const string AgentMessage = "agent_message";

    // Push a single outbound message to the channel.
    public const string Send = "messaging_send";

    public static readonly string[] All = [AgentMessage, Send];
}

public sealed class AgentMessagePayload
{
    public Guid ChannelId { get; init; }
    public Guid ConversationId { get; init; }
    public string ExternalThreadId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;

    // The internal user whose CURRENT role the worker reads at process time — the
    // role is resolved fresh on every turn, never baked into the payload, so a
    // demotion takes effect on the next message rather than whenever the queue
    // happens to drain.
    public Guid LinkedUserId { get; init; }

    // The inbound audit row that triggered this turn. The worker uses it as the
    // idempotency anchor: it atomically claims it (queued → processing), so a
    // reclaimed job will not re-run the agent and bill a second LLM call.
    public Guid MessagingInboundEventId { get; init; }
}

public sealed class SendPayload
{
    public Guid ChannelId { get; init; }
    public string ExternalThreadId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public Guid? ConversationId { get; init; }

    // 1-based attempt counter. On a failed send the worker re-enqueues with
    // Attempt + 1 until MessagingOptions.MaxSendAttempts is reached.
    public int Attempt { get; init; } = 1;
}

internal static class MessagingJobJson
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static T? Deserialize<T>(string payload)
    {
        try { return JsonSerializer.Deserialize<T>(payload, Options); }
        catch (JsonException) { return default; }
    }
}
