using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Data.Models;
using nashira_backend.Services.Jobs;
using nashira_backend.Services.Security;
using HookEntity = nashira_backend.Data.Models.GitWebhook;

namespace nashira_backend.Services.Git;

// Handles one inbound delivery: verify, decide, pull, dispatch, record.
//
// The caller is GitHub, not a user, so the shared secret IS the authentication and
// every property here follows from that:
//
//   - The signature is checked against the raw body before anything else is read.
//   - An unknown route and a bad signature return the same 401, so the endpoint
//     cannot be used to enumerate which webhooks exist.
//   - A delivery id the provider already sent is answered from the recorded outcome
//     instead of pulling and dispatching a second time.
//   - The run is enqueued, never executed inline: a run that outlives the sender's
//     timeout gets retried, and a retry that runs again is a duplicate deployment.
//
// The one thing that is NOT enqueued is the auto-pull, because the run wants to see
// the commit that triggered it. It is bounded so a first-time clone of a large
// repository cannot hold the request past the sender's patience — see PullTimeout.
public sealed class GitWebhookReceiver
{
    public sealed record Outcome(int StatusCode, string Message, Guid? JobId, bool Deduplicated);

    // GitHub gives a webhook 10 seconds before it records a timeout. A pull is
    // usually well under a second; a first-time clone of a big repository is not, and
    // that is the case this exists for. On timeout the delivery still dispatches: the
    // git node pulls on its own, so a slow clone delays the run rather than losing it.
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(8);

    // A rejection is worth recording — an operator debugging "GitHub reports 401" needs
    // to see the attempts arrive. But the caller of a rejected delivery is by
    // definition unauthenticated, and recording every one would let anyone who learns a
    // route write to this table as fast as they can POST. One rejection per window is
    // enough to answer the question and turns the flood into a single row.
    private static readonly TimeSpan RejectionRecordWindow = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly IGitService _git;
    private readonly IJobQueue _queue;
    private readonly ILogger<GitWebhookReceiver> _logger;

    public GitWebhookReceiver(
        AppDbContext db, ISecretProtector protector, IGitService git,
        IJobQueue queue, ILogger<GitWebhookReceiver> logger)
    {
        _db = db;
        _protector = protector;
        _git = git;
        _queue = queue;
        _logger = logger;
    }

    public async Task<Outcome> ReceiveAsync(
        string route, string? eventHeader, string? signatureHeader, string? deliveryKey,
        byte[] body, CancellationToken ct)
    {
        var hook = await _db.GitWebhooks.FirstOrDefaultAsync(
            w => w.IsActive && w.Route == route, ct);

        // Same answer for "no such route" and "bad signature". Telling them apart
        // would let a caller without a secret discover which routes are live.
        if (hook is null || !IsAuthorized(hook, body, signatureHeader))
        {
            _logger.LogWarning("git.webhook.rejected route_len={RouteLength} known={Known}",
                route.Length, hook is not null);
            if (hook is not null && await ShouldRecordRejectionAsync(hook.GitWebhookId, ct))
                await RecordAsync(hook, GitWebhookDelivery.StatusRejected, null, null, null,
                    deliveryKey, null, "signature rejected", ct);
            return new Outcome(401, "unauthorized", null, false);
        }

        // A retry of a delivery already handled returns what happened the first time.
        // Without this, GitHub retrying on a 5xx pulls and runs the workflow twice.
        if (!string.IsNullOrWhiteSpace(deliveryKey))
        {
            var seen = await _db.GitWebhookDeliveries.AsNoTracking()
                .Where(d => d.GitWebhookId == hook.GitWebhookId && d.DeliveryKey == deliveryKey)
                .OrderByDescending(d => d.At)
                .FirstOrDefaultAsync(ct);
            if (seen is not null)
            {
                _logger.LogInformation(
                    "git.webhook.deduplicated webhook_id={WebhookId} delivery={Delivery} status={Status}",
                    hook.GitWebhookId, deliveryKey, seen.Status);
                return new Outcome(200, $"delivery already handled ({seen.Status})", seen.JobId, true);
            }
        }

        var parsed = GitWebhookPayload.Parse(hook.Provider, eventHeader, body);

        // GitHub sends `ping` when the hook is created and the operator is watching
        // that response to decide whether the setup worked. It has to be a 200 with a
        // recorded row, not an error about an event we do not act on.
        if (!parsed.IsPush)
        {
            await RecordAsync(hook, GitWebhookDelivery.StatusVerified, parsed.Event, parsed.Branch,
                parsed.CommitSha, deliveryKey, null, null, ct);
            return new Outcome(200, $"event '{parsed.Event ?? "unknown"}' acknowledged; no action taken", null, false);
        }

        if (GitWebhookDispatchRules.Refusal(hook, parsed.Branch) is { } refusal)
        {
            await RecordAsync(hook, GitWebhookDelivery.StatusVerified, parsed.Event, parsed.Branch,
                parsed.CommitSha, deliveryKey, null, refusal, ct);
            return new Outcome(200, refusal, null, false);
        }

        var pullNote = hook.AutoPull ? await TryPullAsync(hook, parsed.Branch, ct) : null;

        if (hook.OnPushWorkflowId is not { } workflowId)
        {
            await RecordAsync(hook, GitWebhookDelivery.StatusVerified, parsed.Event, parsed.Branch,
                parsed.CommitSha, deliveryKey, null, pullNote, ct);
            return new Outcome(200, "verified; no workflow bound to this webhook", null, false);
        }

        try
        {
            var input = GitWebhookDispatchRules.RunInput(hook, parsed.Branch, parsed.CommitSha);
            // No trigger id and no dedup key: dedup for this path is the delivery row
            // above, which also covers the pull. Passing the webhook id as a trigger id
            // would send the worker looking for a WorkflowTrigger that does not exist.
            var job = await _queue.EnqueueWorkflowRunAsync(
                workflowId, input, [], triggerId: null, deliveryKey: null, ct, RunTrigger.GitWebhook);

            await RecordAsync(hook, GitWebhookDelivery.StatusDispatched, parsed.Event, parsed.Branch,
                parsed.CommitSha, deliveryKey, job.JobId, pullNote, ct);

            _logger.LogInformation(
                "git.webhook.dispatched webhook_id={WebhookId} workflow_id={WorkflowId} job={Job} branch={Branch}",
                hook.GitWebhookId, workflowId, job.JobId, parsed.Branch);

            return new Outcome(202, "workflow run enqueued", job.JobId, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordAsync(hook, GitWebhookDelivery.StatusFailed, parsed.Event, parsed.Branch,
                parsed.CommitSha, deliveryKey, null, ex.Message, ct);
            _logger.LogError(ex,
                "git.webhook.enqueue_failed webhook_id={WebhookId} workflow_id={WorkflowId}",
                hook.GitWebhookId, workflowId);
            // A 500 is the honest answer and it is also the useful one: the provider
            // retries, and the retry carries the same delivery id, so it will not
            // double-dispatch if the second attempt succeeds.
            return new Outcome(500, "could not enqueue the run", null, false);
        }
    }

    private async Task<bool> ShouldRecordRejectionAsync(Guid webhookId, CancellationToken ct)
    {
        var since = DateTime.UtcNow - RejectionRecordWindow;
        return !await _db.GitWebhookDeliveries.AsNoTracking().AnyAsync(
            d => d.GitWebhookId == webhookId
                 && d.Status == GitWebhookDelivery.StatusRejected
                 && d.At >= since, ct);
    }

    // Secret on file → verify it. No secret and allow_unsigned unset → refuse, because
    // an unsigned route is an unauthenticated way to run a workflow.
    private bool IsAuthorized(HookEntity hook, byte[] body, string? signatureHeader)
    {
        var secret = _protector.Decrypt(hook.EncryptedSecret);
        if (string.IsNullOrEmpty(secret)) return hook.AllowUnsigned;
        return GitWebhookSignature.Verify(hook.Provider, body, signatureHeader, secret);
    }

    // Failure is logged onto the delivery, never thrown: the operator would rather
    // have the run fire against a stale checkout and see why than have the push
    // silently do nothing.
    private async Task<string?> TryPullAsync(HookEntity hook, string? branch, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(PullTimeout);
        try
        {
            var result = await _git.PullAsync(hook.GitRepositoryId, branch, bounded.Token);
            return result.Ok ? null : $"auto-pull failed: {result.Message}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("git.webhook.pull_timeout webhook_id={WebhookId} repo_id={RepoId}",
                hook.GitWebhookId, hook.GitRepositoryId);
            return $"auto-pull exceeded {PullTimeout.TotalSeconds:0}s and was abandoned; "
                   + "the run will pull on its own";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git.webhook.pull_failed webhook_id={WebhookId} repo_id={RepoId}",
                hook.GitWebhookId, hook.GitRepositoryId);
            return $"auto-pull failed: {ex.Message}";
        }
    }

    private async Task RecordAsync(
        HookEntity hook, string status, string? evt, string? branch, string? commitSha,
        string? deliveryKey, Guid? jobId, string? error, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.GitWebhookDeliveries.Add(new GitWebhookDelivery
        {
            GitWebhookDeliveryId = Guid.NewGuid(),
            GitWebhookId = hook.GitWebhookId,
            At = now,
            Status = status,
            Event = evt,
            Branch = branch,
            CommitSha = commitSha,
            DeliveryKey = string.IsNullOrWhiteSpace(deliveryKey) ? null : deliveryKey.Trim(),
            JobId = jobId,
            Error = error,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Denormalised onto the parent so the list screen can show "last delivery"
        // without joining a table that is pruned on a different schedule.
        var tracked = await _db.GitWebhooks.FirstOrDefaultAsync(w => w.GitWebhookId == hook.GitWebhookId, ct);
        if (tracked is not null)
        {
            tracked.LastDeliveryAt = now;
            tracked.LastDeliveryStatus = status;
            tracked.DeliveryCount += 1;
            tracked.UpdatedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two retries of the same delivery raced past the dedup check above and the
            // unique index decided. Nothing to repair: the row that won records the
            // same delivery.
            _logger.LogWarning(ex, "git.webhook.delivery_race webhook_id={WebhookId} delivery={Delivery}",
                hook.GitWebhookId, deliveryKey);
        }
    }
}
