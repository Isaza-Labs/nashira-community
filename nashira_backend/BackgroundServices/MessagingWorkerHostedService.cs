using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Messaging;

namespace nashira_backend.BackgroundServices;

// Claims and runs the messaging jobs (agent_message, messaging_send).
//
// Separate from JobWorkerHostedService because the two have opposite shapes: a
// workflow run is long, non-idempotent and must never be retried automatically,
// while a messaging turn is short and its idempotency is anchored on the inbound
// event rather than the job row. They share the `jobs` table but each claim
// filters on its own types, so neither can take the other's work.
//
// Registered only in the API process. The tool registry is populated there, and a
// turn picked up by a worker-only container would face an empty registry and reply
// "no tools enabled" — the shared queue still means an ingest from anywhere is
// served here.
public sealed class MessagingWorkerHostedService : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    // An agent turn is an LLM round trip plus tool calls. Generous, but still a
    // liveness bound rather than a timeout.
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MessagingWorkerHostedService> _logger;
    private readonly string _workerId;

    public MessagingWorkerHostedService(
        IServiceScopeFactory scopes, ILogger<MessagingWorkerHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("messaging.worker.started id={Worker}", _workerId);

        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Poll);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReclaimExpiredAsync(ct);

                // Drain what is queued, one at a time. A turn is short; the extra
                // round trip per job is cheaper than holding a batch under lease.
                while (!ct.IsCancellationRequested)
                {
                    var job = await TryClaimAsync(ct);
                    if (job is null) break;
                    await ProcessAsync(job, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not kill the worker for the life of the process.
                _logger.LogError(ex, "messaging.worker.sweep_failed");
            }

            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("messaging.worker.stopped id={Worker}", _workerId);
    }

    // One statement, so two workers never see the same queued row. The type filter
    // is what keeps this off workflow_run jobs.
    private async Task<Job?> TryClaimAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var lease = now.Add(Lease);
        var types = MessagingJobTypes.All;

        var claimed = await db.Jobs.FromSqlInterpolated($@"
            UPDATE jobs SET
                ""Status"" = {Job.StatusClaimed},
                ""ClaimedBy"" = {_workerId},
                ""ClaimedAt"" = {now},
                ""LeaseExpiresAt"" = {lease},
                ""Attempts"" = ""Attempts"" + 1,
                ""UpdatedAt"" = {now}
            WHERE ""JobId"" = (
                SELECT ""JobId"" FROM jobs
                WHERE ""Status"" = {Job.StatusQueued} AND ""IsActive"" AND ""Type"" = ANY({types})
                ORDER BY ""CreatedAt""
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *").AsNoTracking().ToListAsync(ct);

        return claimed.Count == 0 ? null : claimed[0];
    }

    // Unlike a workflow run, a messaging job IS safe to requeue: agent_message is
    // gated by the inbound event's queued → processing claim, so a re-run that
    // already produced a turn skips instead of billing a second LLM call, and a
    // messaging_send that never reached the provider should be retried.
    private async Task ReclaimExpiredAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var types = MessagingJobTypes.All;
        var reclaimed = await db.Jobs
            .Where(j => j.Status == Job.StatusClaimed
                        && types.Contains(j.Type)
                        && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, Job.StatusQueued)
                .SetProperty(j => j.ClaimedBy, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);

        if (reclaimed > 0)
            _logger.LogWarning("messaging.worker.reclaimed count={Count}", reclaimed);
    }

    private async Task ProcessAsync(Job claimed, CancellationToken ct)
    {
        // Fresh scope per job: the processor binds the linked user onto the scope's
        // MutableCurrentUser, and one turn's identity must not leak into the next.
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<IMessagingJobProcessor>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == claimed.JobId, ct);
        if (job is null) return;

        using var actor = AuditActor.Use(AuditActor.WorkflowRunner);

        try
        {
            await processor.ProcessAsync(claimed, ct);
            job.Status = Job.StatusSucceeded;
            job.Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown mid-job. Leave the claim; the lease expires and the
            // reclaim sweep requeues it.
            return;
        }
        catch (Exception ex)
        {
            job.Status = Job.StatusFailed;
            job.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            _logger.LogError(ex, "messaging.worker.failed job={Job} type={Type}", job.JobId, job.Type);
        }

        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = job.CompletedAt.Value;
        await db.SaveChangesAsync(ct);
    }
}
