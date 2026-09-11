using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Security;
using JobEntity = nashira_backend.Data.Models.Job;

namespace nashira_backend.Services.Messaging;

// Raw = true means Body is a verbatim response to echo back (a provider
// verification challenge), not a human-facing status message.
public sealed record MessagingIngestOutcome(
    int StatusCode, string? Body = null, string ContentType = "text/plain", bool Raw = false);

// Handles one inbound webhook delivery for a messaging channel. Modelled on
// GitWebhookReceiver: the public controller has no auth context, so this opens a
// fresh DI scope and does all its work there.
//
// Verifies the signature, dedupes, resolves the external identity (prompting
// account linking when unknown), resolves or creates the conversation, and
// enqueues an agent_message job. An inbound audit row is persisted on every
// outcome, including the rejections — "the bot ignored me" is otherwise
// undiagnosable.
public interface IMessagingIngestService
{
    // HTTP webhook path: verifies the provider signature before processing.
    Task<MessagingIngestOutcome> ReceiveAsync(
        string provider, Guid channelId, MessagingHttpRequest request, CancellationToken ct);

    // Socket Mode / Relay path: the event arrived over an already-authenticated
    // outbound connection, so signature verification is skipped. `body` is the raw
    // event payload, the same shape the HTTP path would have carried.
    Task<MessagingIngestOutcome> ReceiveVerifiedAsync(
        string provider, Guid channelId, byte[] body, CancellationToken ct);
}

public sealed class MessagingIngestService : IMessagingIngestService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessagingIngestService> _logger;

    public MessagingIngestService(
        IServiceScopeFactory scopeFactory, ILogger<MessagingIngestService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<MessagingIngestOutcome> ReceiveAsync(
        string provider, Guid channelId, MessagingHttpRequest request, CancellationToken ct)
        => ReceiveCoreAsync(provider, channelId, request, verifySignature: true, ct);

    public Task<MessagingIngestOutcome> ReceiveVerifiedAsync(
        string provider, Guid channelId, byte[] body, CancellationToken ct)
        => ReceiveCoreAsync(
            provider, channelId,
            new MessagingHttpRequest { Method = "POST", Body = body },
            verifySignature: false, ct);

    private async Task<MessagingIngestOutcome> ReceiveCoreAsync(
        string provider, Guid channelId, MessagingHttpRequest request,
        bool verifySignature, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var protector = sp.GetRequiredService<ISecretProtector>();
        var resolver = sp.GetRequiredService<IMessagingProviderResolver>();
        var linkService = sp.GetRequiredService<IMessagingLinkService>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;

        var channel = await db.MessagingChannels
            .FirstOrDefaultAsync(c => c.MessagingChannelId == channelId && c.IsActive, ct);
        if (channel is null)
            return new MessagingIngestOutcome(404, "channel not found");

        // The provider is in the URL as well as on the row. A mismatch means the
        // caller is pointed at the wrong channel; treating it as "not found" avoids
        // confirming that the id exists.
        if (!string.Equals(channel.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return new MessagingIngestOutcome(404, "channel/provider mismatch");

        var prov = resolver.Resolve(channel.Provider);
        if (prov is null)
            return new MessagingIngestOutcome(501, $"provider '{channel.Provider}' not supported");

        if (!channel.Enabled)
            return new MessagingIngestOutcome(403, "channel disabled");

        if (verifySignature)
        {
            var secret = protector.Decrypt(channel.SigningSecretEncrypted);
            var verify = await prov.VerifyAsync(channel, request, secret, ct);
            switch (verify.Outcome)
            {
                case WebhookVerifyOutcome.Challenge:
                    return new MessagingIngestOutcome(
                        200, verify.ChallengeBody, verify.ChallengeContentType, Raw: true);
                case WebhookVerifyOutcome.Rejected:
                    _logger.LogWarning(
                        "messaging.webhook.rejected channel={Channel} reason={Reason}",
                        channelId, verify.RejectReason);
                    return new MessagingIngestOutcome(verify.RejectStatusCode, verify.RejectReason);
            }
        }

        var inbound = prov.ParseInbound(channel, request);
        if (inbound is null)
            return new MessagingIngestOutcome(200, "no actionable message");

        // Idempotency: a provider re-delivery carrying the same event id is a no-op.
        // This catches the serial case; the unique index below catches the parallel one.
        var duplicate = await db.MessagingInboundEvents.AnyAsync(
            e => e.MessagingChannelId == channelId && e.ProviderEventId == inbound.ProviderEventId, ct);
        if (duplicate)
            return new MessagingIngestOutcome(200, "duplicate");

        var workspace = inbound.ExternalWorkspaceId ?? string.Empty;

        var allowed = MessagingChannelConfig.AllowedExternalIds(channel);
        if (allowed.Count > 0 && !allowed.Contains(inbound.ExternalUserId, StringComparer.Ordinal))
        {
            await PersistInboundAsync(db, channel, inbound, MessagingInboundEvent.StatusRejected,
                null, "external user not in allowlist", ct);
            return new MessagingIngestOutcome(202, "not allowed");
        }

        // Identity resolution. Permissions come from the linked user's real role,
        // read fresh at process time — never from anything the channel asserts.
        var link = await db.MessagingIdentityLinks.FirstOrDefaultAsync(
            l => l.MessagingChannelId == channelId
                 && l.ExternalWorkspaceId == workspace
                 && l.ExternalUserId == inbound.ExternalUserId
                 && l.IsActive, ct);

        if (link is null)
        {
            // No linked account. Under the no-escalation rule the agent does not run
            // for an unverified identity; instead the bot offers the linking link.
            if (channel.RequireLinkedUser)
            {
                var invite = await linkService.CreateLinkAsync(
                    channel, workspace, inbound.ExternalUserId, ct);

                if (string.IsNullOrEmpty(invite.Url))
                    _logger.LogWarning(
                        "messaging.link.no_public_base_url channel={Channel} provider={Provider} — "
                        + "set Messaging:PublicBaseUrl (env MESSAGING__PUBLICBASEURL) to the frontend URL "
                        + "so the bot can send a complete /link?token=… URL.",
                        channelId, channel.Provider);

                var text = string.IsNullOrEmpty(invite.Url)
                    ? "To use this assistant, link your Nashira account from the web app first."
                    : $"To use this assistant, link your Nashira account: {invite.Url}";

                await EnqueueSendAsync(db, channel, inbound.ExternalThreadId, text, null, ct);
                await PersistInboundAsync(db, channel, inbound, MessagingInboundEvent.StatusVerified,
                    null, "unlinked — account-linking prompt sent", ct);
                return new MessagingIngestOutcome(202, "link prompt sent");
            }

            await PersistInboundAsync(db, channel, inbound, MessagingInboundEvent.StatusRejected,
                null, "no linked user", ct);
            return new MessagingIngestOutcome(202, "no linked user");
        }

        // One conversation per external thread, so a Slack thread keeps its history
        // across turns exactly as a web chat does.
        var existing = await db.AIConversations.FirstOrDefaultAsync(
            c => c.MessagingChannelId == channelId
                 && c.ExternalThreadId == inbound.ExternalThreadId
                 && c.IsActive, ct);

        Guid conversationId;
        if (existing is null)
        {
            conversationId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.AIConversations.Add(new AIConversation
            {
                AIConversationId = conversationId,
                UserId = link.LinkedUserId,
                MessagesJson = "[]",
                Status = "active",
                Source = channel.Provider,
                MessagingChannelId = channelId,
                ExternalThreadId = inbound.ExternalThreadId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            conversationId = existing.AIConversationId;
        }

        // Backpressure. A provider that gets a 503 retries later, which beats an
        // unbounded queue of turns nobody will read.
        var depth = await db.Jobs.CountAsync(
            j => j.IsActive && (j.Status == JobEntity.StatusQueued || j.Status == JobEntity.StatusClaimed), ct);
        if (depth >= options.MaxQueueDepth)
        {
            await PersistInboundAsync(db, channel, inbound, MessagingInboundEvent.StatusRejected,
                conversationId, $"queue full ({depth})", ct);
            return new MessagingIngestOutcome(503, "queue full, retry later");
        }

        // Dedupe gate: the inbound "queued" row is written BEFORE the job is
        // enqueued. The unique (channel, provider_event_id) index makes a concurrent
        // re-delivery throw right here, so the agent job is enqueued at most once
        // per event even when a provider double-delivers in parallel.
        Guid inboundEventId;
        try
        {
            inboundEventId = await PersistInboundAsync(db, channel, inbound,
                MessagingInboundEvent.StatusQueued, conversationId, null, ct);
        }
        catch (DbUpdateException)
        {
            return new MessagingIngestOutcome(200, "duplicate");
        }

        var payload = MessagingJobJson.Serialize(new AgentMessagePayload
        {
            ChannelId = channelId,
            ConversationId = conversationId,
            ExternalThreadId = inbound.ExternalThreadId,
            Text = inbound.Text,
            LinkedUserId = link.LinkedUserId,
            MessagingInboundEventId = inboundEventId,
        });
        await EnqueueAsync(db, MessagingJobTypes.AgentMessage, payload, ct);

        return new MessagingIngestOutcome(202, "queued");
    }

    private static Task EnqueueSendAsync(
        AppDbContext db, MessagingChannel channel, string thread, string text,
        Guid? conversationId, CancellationToken ct)
    {
        var payload = MessagingJobJson.Serialize(new SendPayload
        {
            ChannelId = channel.MessagingChannelId,
            ExternalThreadId = thread,
            Text = text,
            ConversationId = conversationId,
        });
        return EnqueueAsync(db, MessagingJobTypes.Send, payload, ct);
    }

    private static async Task EnqueueAsync(
        AppDbContext db, string type, string payloadJson, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        db.Jobs.Add(new JobEntity
        {
            JobId = Guid.NewGuid(),
            Type = type,
            PayloadJson = payloadJson,
            Status = JobEntity.StatusQueued,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    // Returns the created inbound event id, so the queued path can thread it into
    // the agent_message payload as the idempotency anchor.
    private static async Task<Guid> PersistInboundAsync(
        AppDbContext db, MessagingChannel channel, InboundMessage inbound,
        string status, Guid? conversationId, string? error, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var inboundEventId = Guid.NewGuid();
        db.MessagingInboundEvents.Add(new MessagingInboundEvent
        {
            MessagingInboundEventId = inboundEventId,
            MessagingChannelId = channel.MessagingChannelId,
            ProviderEventId = inbound.ProviderEventId,
            ExternalThreadId = inbound.ExternalThreadId,
            ConversationId = conversationId,
            Status = status,
            Event = inbound.EventKind,
            Error = error,
            At = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return inboundEventId;
    }
}
