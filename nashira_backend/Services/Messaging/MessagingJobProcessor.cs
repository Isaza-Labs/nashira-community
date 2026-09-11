using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using JobEntity = nashira_backend.Data.Models.Job;

namespace nashira_backend.Services.Messaging;

// Processes one claimed messaging job inside a fresh DI scope.
//
//   agent_message  → runs the agent as the linked user (their real, current role,
//                    capped by the channel) and enqueues the reply.
//   messaging_send → pushes one message to the provider and records a delivery row.
public interface IMessagingJobProcessor
{
    Task ProcessAsync(JobEntity job, CancellationToken ct);
}

public sealed class MessagingJobProcessor : IMessagingJobProcessor
{
    private readonly AppDbContext _db;
    private readonly IMessagingProviderResolver _resolver;
    private readonly ISecretProtector _protector;
    private readonly AgentConversationRunner _runner;
    private readonly MutableCurrentUser _caller;
    private readonly MessagingOptions _options;
    private readonly ILogger<MessagingJobProcessor> _logger;

    public MessagingJobProcessor(
        AppDbContext db,
        IMessagingProviderResolver resolver,
        ISecretProtector protector,
        AgentConversationRunner runner,
        MutableCurrentUser caller,
        IOptions<MessagingOptions> options,
        ILogger<MessagingJobProcessor> logger)
    {
        _db = db;
        _resolver = resolver;
        _protector = protector;
        _runner = runner;
        _caller = caller;
        _options = options.Value;
        _logger = logger;
    }

    public Task ProcessAsync(JobEntity job, CancellationToken ct) => job.Type switch
    {
        MessagingJobTypes.AgentMessage => ProcessAgentMessageAsync(job, ct),
        MessagingJobTypes.Send => ProcessSendAsync(job, ct),
        _ => LogUnknown(job),
    };

    private Task LogUnknown(JobEntity job)
    {
        _logger.LogWarning("messaging.job.unknown_type type={Type} job={Job}", job.Type, job.JobId);
        return Task.CompletedTask;
    }

    private async Task ProcessAgentMessageAsync(JobEntity job, CancellationToken ct)
    {
        var p = MessagingJobJson.Deserialize<AgentMessagePayload>(job.PayloadJson);
        if (p is null)
        {
            _logger.LogWarning("messaging.job.bad_payload job={Job}", job.JobId);
            return;
        }

        // At-most-once gate. Atomically claiming the inbound row (queued →
        // processing) is what stops a reclaimed job from running the agent a second
        // time — which would mean a second LLM bill and a duplicate reply in the
        // user's thread.
        if (p.MessagingInboundEventId != Guid.Empty
            && !await TryClaimInboundAsync(p.MessagingInboundEventId, ct))
        {
            _logger.LogInformation(
                "messaging.agent.already_processed inbound={Inbound} job={Job}",
                p.MessagingInboundEventId, job.JobId);
            return;
        }

        var channel = await _db.MessagingChannels.AsNoTracking().FirstOrDefaultAsync(
            c => c.MessagingChannelId == p.ChannelId && c.IsActive, ct);
        if (channel is null || !channel.Enabled)
        {
            _logger.LogInformation("messaging.agent.channel_gone channel={Channel}", p.ChannelId);
            await MarkInboundAsync(p, MessagingInboundEvent.StatusFailed, ct);
            return;
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(
            u => u.UserId == p.LinkedUserId && u.IsActive, ct);
        if (user is null)
        {
            _logger.LogWarning(
                "messaging.agent.user_gone user={User} channel={Channel}", p.LinkedUserId, p.ChannelId);
            await MarkInboundAsync(p, MessagingInboundEvent.StatusFailed, ct);
            return;
        }

        // The role is resolved HERE, from the user row, on every turn — never taken
        // from the payload or cached on the link. A demotion in Nashira therefore
        // takes effect on the user's next message. The channel's MaxRole can only
        // narrow it further.
        var role = MessagingRoles.Effective(user.Role, channel.MaxRole);
        _caller.Bind(user.UserId, user.Username, [role]);

        var sink = new CollectingAgentEventSink();
        try
        {
            await _runner.RunAsync(new AgentTurnRequest(p.ConversationId, p.Text), sink, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The runner reports most failures through the sink; anything that
            // escapes it still has to produce a reply, or the user is left with
            // silence and no way to tell whether the bot heard them.
            _logger.LogError(ex, "messaging.agent.run_failed channel={Channel}", p.ChannelId);
            await EnqueueSendAsync(p.ChannelId, p.ExternalThreadId,
                "Sorry — something went wrong handling your request. Please try again.",
                p.ConversationId, 1, ct);
            await MarkInboundAsync(p, MessagingInboundEvent.StatusFailed, ct);
            return;
        }

        await EnqueueSendAsync(p.ChannelId, p.ExternalThreadId, sink.ReplyText(), p.ConversationId, 1, ct);

        // Terminal status only AFTER the reply is enqueued: a crash in the
        // run-to-enqueue gap leaves the row 'processing', and the at-most-once
        // claim above still prevents a second agent run on reclaim.
        await MarkInboundAsync(p,
            sink.Error is null ? MessagingInboundEvent.StatusCompleted : MessagingInboundEvent.StatusFailed, ct);
    }

    // queued → processing in one statement, so two workers cannot both win it.
    private async Task<bool> TryClaimInboundAsync(Guid inboundEventId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimed = await _db.MessagingInboundEvents
            .Where(e => e.MessagingInboundEventId == inboundEventId
                        && e.Status == MessagingInboundEvent.StatusQueued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, MessagingInboundEvent.StatusProcessing)
                .SetProperty(e => e.UpdatedAt, now), ct);
        return claimed > 0;
    }

    // Best-effort terminal status on the inbound audit row. Never throws into the
    // caller: failing to write an audit row must not cost the user their reply.
    private async Task MarkInboundAsync(AgentMessagePayload p, string status, CancellationToken ct)
    {
        if (p.MessagingInboundEventId == Guid.Empty) return;
        try
        {
            var now = DateTime.UtcNow;
            await _db.MessagingInboundEvents
                .Where(e => e.MessagingInboundEventId == p.MessagingInboundEventId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, status)
                    .SetProperty(e => e.UpdatedAt, now), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "messaging.agent.mark_inbound_failed inbound={Inbound}", p.MessagingInboundEventId);
        }
    }

    private async Task ProcessSendAsync(JobEntity job, CancellationToken ct)
    {
        var p = MessagingJobJson.Deserialize<SendPayload>(job.PayloadJson);
        if (p is null)
        {
            _logger.LogWarning("messaging.send.bad_payload job={Job}", job.JobId);
            return;
        }

        var channel = await _db.MessagingChannels.FirstOrDefaultAsync(
            c => c.MessagingChannelId == p.ChannelId && c.IsActive, ct);
        if (channel is null)
        {
            _logger.LogInformation("messaging.send.channel_gone channel={Channel}", p.ChannelId);
            return;
        }

        var prov = _resolver.Resolve(channel.Provider);
        string status;
        string? error = null;
        var transient = false;

        if (prov is null)
        {
            status = MessagingDelivery.StatusFailed;
            error = $"provider '{channel.Provider}' not supported";
        }
        else
        {
            try
            {
                var token = _protector.Decrypt(channel.BotTokenEncrypted);
                await prov.SendAsync(channel, token,
                    new OutboundMessage { ExternalThreadId = p.ExternalThreadId, Text = p.Text }, ct);
                status = MessagingDelivery.StatusSent;
            }
            catch (MessagingSendException ex)
            {
                status = MessagingDelivery.StatusFailed;
                error = ex.Message;
                transient = ex.Transient;
                _logger.LogWarning(
                    "messaging.send.failed channel={Channel} thread={Thread} transient={Transient} error={Error}",
                    p.ChannelId, p.ExternalThreadId, transient, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = MessagingDelivery.StatusFailed;
                error = ex.Message;
                _logger.LogWarning(ex,
                    "messaging.send.failed channel={Channel} thread={Thread}", p.ChannelId, p.ExternalThreadId);
            }
        }

        await RecordDeliveryAsync(channel, p, status, error, p.Attempt, ct);

        // Only transient failures are retried. A revoked bot token fails
        // identically three times and retrying it just makes the failure slower to
        // see; each attempt leaves its own delivery row either way.
        if (status != MessagingDelivery.StatusFailed || !transient) return;

        var maxAttempts = _options.MaxSendAttempts <= 0 ? 3 : _options.MaxSendAttempts;
        if (p.Attempt < maxAttempts)
        {
            await EnqueueSendAsync(
                p.ChannelId, p.ExternalThreadId, p.Text, p.ConversationId, p.Attempt + 1, ct);
            _logger.LogInformation(
                "messaging.send.retry channel={Channel} next_attempt={Attempt}/{Max}",
                p.ChannelId, p.Attempt + 1, maxAttempts);
        }
        else
        {
            _logger.LogWarning(
                "messaging.send.exhausted channel={Channel} attempts={Attempts}", p.ChannelId, p.Attempt);
        }
    }

    private async Task EnqueueSendAsync(
        Guid channelId, string thread, string text, Guid? conversationId, int attempt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.Jobs.Add(new JobEntity
        {
            JobId = Guid.NewGuid(),
            Type = MessagingJobTypes.Send,
            PayloadJson = MessagingJobJson.Serialize(new SendPayload
            {
                ChannelId = channelId,
                ExternalThreadId = thread,
                Text = text,
                ConversationId = conversationId,
                Attempt = attempt,
            }),
            Status = JobEntity.StatusQueued,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task RecordDeliveryAsync(
        MessagingChannel channel, SendPayload p, string status, string? error, int attempt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.MessagingDeliveries.Add(new MessagingDelivery
        {
            MessagingDeliveryId = Guid.NewGuid(),
            MessagingChannelId = channel.MessagingChannelId,
            ConversationId = p.ConversationId,
            ExternalThreadId = p.ExternalThreadId,
            Status = status,
            Attempt = attempt,
            Error = error is { Length: > 500 } e ? e[..500] : error,
            At = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        channel.LastDeliveryAt = now;
        channel.LastDeliveryStatus = status;
        channel.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
    }
}
