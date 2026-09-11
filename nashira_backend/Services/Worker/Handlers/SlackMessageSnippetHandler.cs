using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Notifications;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// FlowWeaver's `slack_message`, on Nashira's channel model.
//
// Two transports, because they are not equally capable and the contract cares which.
//
//   A Slack MessagingChannel (bot token, chat.postMessage) is used when one exists. It
//   is the only path here that can post INTO A THREAD, which is why it is preferred: an
//   incoming webhook has no thread parameter, so a threaded reply sent through one lands
//   at the root of the channel and reports success. That silent flattening is exactly
//   what the interchange contract forbids, and it is what the oracle — which posts with a
//   bot token — does not do.
//
//   A NotificationChannel (encrypted webhook, delivery records, retry policy) otherwise.
//   It carries `blocks` but not `thread_ts`, so a declared thread is REFUSED with
//   `not_supported` rather than dropped.
//
// The type keeps FlowWeaver's name because that is what shared workflows reference; a
// Teams or generic-webhook record still works for a plain text post.
//
// Input (the node's config_overrides, resolved before dispatch), per workflow.v1
// snippets/SPEC.md:
//   { "channel": "#net-ops" | "C0123",   // the Slack destination
//     "text":    "…",
//     "via":     "ops-slack" }            // optional: the NotificationChannel record
//                                        // (name, slug or id) to post through
//
// A webhook is bound to one Slack channel when it is created, so `channel` cannot
// redirect a post; it decides WHICH record carries it when `via` is absent: the
// only Slack-kind record if there is exactly one, else the record whose name, slug
// or id equals `channel` (with or without the `#`). Nothing matching is
// `not_found`, and the message says what to configure.
//
// The delivery is recorded against the run (NotificationDelivery.WorkflowRunId), so
// "did the on-call channel actually get the alert?" stays answerable after the fact.
public sealed class SlackMessageSnippetHandler : ISnippetHandler
{
    private readonly AppDbContext _db;
    private readonly INotificationDispatcher _dispatcher;
    private readonly Services.Messaging.IMessagingProviderResolver _providers;
    private readonly Services.Security.ISecretProtector _protector;
    private readonly ILogger<SlackMessageSnippetHandler> _logger;

    public SlackMessageSnippetHandler(
        AppDbContext db, INotificationDispatcher dispatcher,
        Services.Messaging.IMessagingProviderResolver providers,
        Services.Security.ISecretProtector protector,
        ILogger<SlackMessageSnippetHandler> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _providers = providers;
        _protector = protector;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeSlackMessage;

    // A sent chat message has no automatic undo — same class as "send an email".
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.NonReversible;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var src = request.Input;

        var channelRef = Str(src, "channel");
        if (string.IsNullOrWhiteSpace(channelRef))
            return SnippetResult.Fail(
                "slack_message needs a `channel` (the Slack destination, e.g. #net-ops)", "bad_input");

        var text = Str(src, "text");
        if (string.IsNullOrWhiteSpace(text))
            return SnippetResult.Fail("slack_message needs `text`", "bad_input");

        var threadTs = Str(src, "thread_ts");
        var blocksJson = Raw(src, "blocks");
        var via = Str(src, "via");

        // A bot-token channel first, because it is the only transport here that can thread.
        // An incoming webhook is bound to one Slack channel and has no thread parameter at
        // all, so a threaded reply sent through one lands at the root of the channel and
        // reports success — the silent drop this contract exists to forbid. Where a Slack
        // MessagingChannel is configured, the snippet posts through chat.postMessage with its
        // bot token, which is what the oracle does.
        var bot = await ResolveBotChannelAsync(via, channelRef!.Trim(), ct);
        if (bot is not null)
            return await SendViaBotAsync(bot, channelRef!.Trim(), text!, threadTs, blocksJson, request, ct);

        // Webhook path. It carries rich content but not a thread, so a declared thread is
        // REFUSED rather than quietly flattened.
        if (!string.IsNullOrWhiteSpace(threadTs))
            return SnippetResult.Fail(
                "`thread_ts` needs a Slack messaging channel with a bot token — an incoming webhook "
                + "cannot post into a thread, and posting at the root of the channel instead would "
                + "report success for a reply nobody will see in its thread",
                "not_supported");

        NotificationChannel? channel;
        if (!string.IsNullOrWhiteSpace(via))
        {
            channel = await FindAsync(via!.Trim(), ct);
            if (channel is null)
                return SnippetResult.Fail(
                    $"notification channel '{via}' (from `via`) not found — create it under /admin/notifications", "not_found");
        }
        else
        {
            channel = await ResolveByDestinationAsync(channelRef!.Trim(), ct);
            if (channel is null)
                return SnippetResult.Fail(
                    $"no notification channel to post to '{channelRef}' through — configure exactly one Slack channel "
                    + $"under /admin/notifications, name one '{channelRef.TrimStart('#')}', or set `via` to the channel "
                    + "record to use", "not_found");
        }

        // Rich content is Slack-shaped. A Teams or generic-webhook record would drop it, so
        // it is refused there rather than delivered as bare text.
        if (!string.IsNullOrWhiteSpace(blocksJson) && channel.Kind != NotificationChannel.KindSlack)
            return SnippetResult.Fail(
                $"`blocks` is Slack-specific and channel '{channel.Name}' is {channel.Kind} — "
                + "sending the plain text instead would drop content the node asked for",
                "not_supported");

        NotificationResult result;
        try
        {
            result = await _dispatcher.SendAsync(
                channel.NotificationChannelId, text!, request.WorkflowRunId, ct, blocksJson);
        }
        catch (DomainException ex)
        {
            // Disabled channel, unreadable webhook — configuration, not transport.
            return SnippetResult.Fail(ex.Message, "bad_channel");
        }

        var output = new
        {
            ok = result.Success,
            // A webhook post returns no message timestamp; the contract's field is
            // present and null so a template reading it resolves.
            ts = (string?)null,
            channel = channelRef,
            error = result.Error,
            via = channel.Name,
            channel_id = channel.NotificationChannelId,
            channel_name = channel.Name,
            kind = channel.Kind,
            status_code = result.StatusCode,
            attempts = result.Attempts,
            elapsed_ms = result.ElapsedMs,
        };

        if (!result.Success)
        {
            _logger.LogWarning("workflow.slack_message.failed channel={Channel} error={Error}",
                channel.Name, result.Error);
            return new SnippetResult
            {
                Success = false,
                // The dispatcher refused or the transport failed: nothing was posted.
                Change = StepChange.Unchanged,
                Output = JsonSerializer.SerializeToElement(output),
                Error = $"delivery to '{channel.Name}' failed: {result.Error ?? "unknown error"}",
                ErrorCode = "delivery_failed",
                // The dispatcher already retried what was worth retrying (5xx, 429,
                // transport); what reaches here as a failure will fail again.
                Retryable = false,
                Logs = $"{channel.Kind} '{channel.Name}' → failed after {result.Attempts} attempt(s), "
                    + $"{result.ElapsedMs}ms",
            };
        }

        return SnippetResult.Ok(output, StepChange.Changed,
            logs: $"{channel.Kind} '{channel.Name}' → HTTP {result.StatusCode}, "
                + $"{result.Attempts} attempt(s), {result.ElapsedMs}ms");
    }

    // The Slack messaging channel to post through, or null when none is configured and the
    // webhook path should run. `via` names one explicitly; otherwise the only enabled Slack
    // channel is used, and ambiguity resolves to nothing rather than to a guess — posting an
    // operations alert into whichever workspace sorted first is not a recoverable mistake.
    private async Task<MessagingChannel?> ResolveBotChannelAsync(
        string? via, string destination, CancellationToken ct)
    {
        var slack = _db.MessagingChannels.AsNoTracking()
            .Where(c => c.IsActive && c.Enabled && c.Provider == MessagingChannel.ProviderSlack);

        if (!string.IsNullOrWhiteSpace(via))
        {
            var reference = via!.Trim();
            return Guid.TryParse(reference, out var id)
                ? await slack.FirstOrDefaultAsync(c => c.MessagingChannelId == id, ct)
                : await slack.FirstOrDefaultAsync(c => c.Slug == reference || c.Name == reference, ct);
        }

        var candidates = await slack.Take(2).ToListAsync(ct);
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0) return null;

        var bare = destination.TrimStart('#');
        return await slack.FirstOrDefaultAsync(c => c.Slug == bare || c.Name == bare, ct);
    }

    private async Task<SnippetResult> SendViaBotAsync(
        MessagingChannel channel, string destination, string text,
        string? threadTs, string? blocksJson, SnippetRequest request, CancellationToken ct)
    {
        var provider = _providers.Resolve(channel.Provider);
        if (provider is null)
            return SnippetResult.Fail(
                $"messaging provider '{channel.Provider}' is not available", "bad_channel");

        var token = _protector.Decrypt(channel.BotTokenEncrypted);
        if (string.IsNullOrWhiteSpace(token))
            return SnippetResult.Fail(
                $"messaging channel '{channel.Name}' has no readable bot token; re-enter it", "bad_channel");

        // The provider addresses a destination as "channel" or "channel:thread_ts".
        var threadId = string.IsNullOrWhiteSpace(threadTs) ? destination : $"{destination}:{threadTs}";

        var started = DateTime.UtcNow;
        try
        {
            await provider.SendAsync(channel, token,
                new Services.Messaging.OutboundMessage { ExternalThreadId = threadId, Text = text, BlocksJson = blocksJson }, ct);
        }
        catch (Services.Messaging.MessagingSendException ex)
        {
            _logger.LogWarning("workflow.slack_message.failed channel={Channel} error={Error}",
                channel.Name, ex.Message);
            return SnippetResult.Fail(
                $"delivery to '{channel.Name}' failed: {ex.Message}", "delivery_failed", retryable: ex.Transient);
        }

        var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        return SnippetResult.Ok(new
        {
            ok = true,
            // chat.postMessage returns the posted timestamp, but the provider's send is
            // fire-and-forget by design (it throws or it succeeded) and reshaping it to
            // return one would change a path the reply flow depends on. The field stays
            // present and null, as it was on the webhook path, and carrying it is a
            // follow-up rather than a silent half-answer.
            ts = (string?)null,
            channel = destination,
            error = (string?)null,
            via = channel.Name,
            channel_id = channel.MessagingChannelId,
            channel_name = channel.Name,
            kind = MessagingChannel.ProviderSlack,
            status_code = 200,
            attempts = 1,
            elapsed_ms = elapsed,
        }, StepChange.Changed, logs: $"chat.postMessage '{channel.Name}' → {destination}"
            + (string.IsNullOrWhiteSpace(threadTs) ? "" : $" thread {threadTs}")
            + (string.IsNullOrWhiteSpace(blocksJson) ? "" : " with blocks")
            + $", {elapsed}ms");
    }

    // The raw JSON text of a property, for values passed through verbatim.
    private static string? Raw(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
            && v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? v.GetRawText() : null;

    // A record by id, slug or name.
    private async Task<NotificationChannel?> FindAsync(string reference, CancellationToken ct)
    {
        if (Guid.TryParse(reference, out var id))
            return await _db.NotificationChannels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.IsActive && c.NotificationChannelId == id, ct);
        return await _db.NotificationChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsActive && (c.Slug == reference || c.Name == reference), ct);
    }

    private async Task<NotificationChannel?> ResolveByDestinationAsync(string destination, CancellationToken ct)
    {
        var slack = await _db.NotificationChannels.AsNoTracking()
            .Where(c => c.IsActive && c.Kind == NotificationChannel.KindSlack)
            .Take(2)
            .ToListAsync(ct);
        if (slack.Count == 1) return slack[0];

        return await FindAsync(destination, ct) ?? await FindAsync(destination.TrimStart('#'), ct);
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
