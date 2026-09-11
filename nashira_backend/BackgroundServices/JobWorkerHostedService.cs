using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Workflow;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Trace;

namespace nashira_backend.BackgroundServices;

// Claims queued jobs and executes them, several at a time.
//
// The claim is one atomic UPDATE with FOR UPDATE SKIP LOCKED, which is what makes
// this safe to run on every replica: two workers asking at once get two different
// jobs or one job and nothing, never the same job twice. That single property is
// also what fixed the scheduler's multi-replica double-fire — the sweep only
// enqueues, and enqueueing twice is prevented upstream, while executing twice is
// prevented here.
public sealed class JobWorkerHostedService : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    // A workflow run can legitimately take many minutes of SSH. The lease is a
    // liveness bound, not a timeout: it only matters if the worker process died.
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JobWorkerHostedService> _logger;
    private readonly ITraceLogger _trace;
    private readonly int _maxConcurrent;
    private readonly string _workerId;

    public JobWorkerHostedService(
        IServiceScopeFactory scopes, IConfiguration config,
        ILogger<JobWorkerHostedService> logger, ITraceLogger trace)
    {
        _scopes = scopes;
        _logger = logger;
        _trace = trace;
        _maxConcurrent = Math.Clamp(config.GetValue("Jobs:MaxConcurrent", 3), 1, 16);
        _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("jobs.worker.started id={Worker} slots={Slots}", _workerId, _maxConcurrent);

        try { await Task.Delay(TimeSpan.FromSeconds(12), ct); }
        catch (OperationCanceledException) { return; }

        var running = new List<Task>();

        using var timer = new PeriodicTimer(Poll);
        while (!ct.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);

            try
            {
                await ReclaimExpiredAsync(ct);

                // Fill free slots. Each claim is its own round trip, so an empty
                // queue costs one query per tick, not one per slot.
                while (running.Count < _maxConcurrent)
                {
                    var job = await TryClaimAsync(ct);
                    if (job is null) break;
                    running.Add(ProcessAsync(job, ct));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not kill the worker for the process's life.
                _logger.LogError(ex, "jobs.worker.sweep_failed");
            }

            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }

        // Let in-flight jobs finish writing their outcome before the host exits.
        try { await Task.WhenAll(running); } catch (Exception) { /* logged per job */ }
        _logger.LogInformation("jobs.worker.stopped id={Worker}", _workerId);
    }

    // UPDATE … WHERE JobId = (SELECT … FOR UPDATE SKIP LOCKED) RETURNING *.
    // One statement, so there is no window in which two workers see the same
    // queued row. Oldest first: a queue that isn't FIFO surprises operators.
    private async Task<Job?> TryClaimAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var lease = now.Add(Lease);
        // Messaging jobs ride the same table but belong to MessagingWorkerHostedService.
        // Without this filter they would be claimed here and immediately failed by the
        // job-type check below.
        var messagingTypes = Services.Messaging.MessagingJobTypes.All;

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
                WHERE ""Status"" = {Job.StatusQueued} AND ""IsActive""
                      AND NOT (""Type"" = ANY({messagingTypes}))
                ORDER BY ""CreatedAt""
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *").AsNoTracking().ToListAsync(ct);

        return claimed.Count == 0 ? null : claimed[0];
    }

    // A dead worker's jobs are marked FAILED, not requeued. A workflow run is not
    // idempotent: it may have half-executed before the crash, and silently running
    // it again is worse than an operator seeing "worker died mid-run" and deciding.
    private async Task ReclaimExpiredAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var reclaimed = await db.Jobs
            .Where(j => j.Status == Job.StatusClaimed
                        && !Services.Messaging.MessagingJobTypes.All.Contains(j.Type)
                        && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, Job.StatusFailed)
                .SetProperty(j => j.Error, j => "the worker holding this job stopped responding (lease expired)")
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.UpdatedAt, now), ct);

        if (reclaimed > 0)
        {
            _logger.LogWarning("jobs.worker.reclaimed count={Count}", reclaimed);
            _trace.Event(TraceEvent.CategoryWorker, "worker.lease.expired",
                new { reclaimed, worker = _workerId },
                error: $"{reclaimed} job(s) were held by a worker that stopped responding");
        }
    }

    private async Task ProcessAsync(Job claimed, CancellationToken ct)
    {
        // Fresh scope per job: the run service and its node executor are scoped,
        // and one run's state must not leak into the next.
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.JobId == claimed.JobId, ct);
        if (job is null) return;

        // Bound around the whole job, not just the run: everything this method attributes
        // — audit rows, trace rows — is the runner acting, and nobody is signed in.
        using var actor = AuditActor.Use(AuditActor.WorkflowRunner);
        // A worker that dies mid-job leaves the row saying `claimed` and nothing saying
        // why; this leaves a `started` trace that never closed, which is the only
        // durable difference between work that is stuck and work that never began.
        using var trace = _trace.Begin(TraceEvent.CategoryWorker, "worker.job.process",
            new { job_id = job.JobId, type = job.Type, worker = _workerId });

        try
        {
            if (job.Type != Job.TypeWorkflowRun)
                throw new InvalidOperationException($"this build cannot execute job type '{job.Type}'");

            var payload = JobPayloads.TryParseWorkflowRun(job.PayloadJson)
                ?? throw new InvalidOperationException("the job payload is not a valid workflow_run payload");

            var workflow = await db.Workflows.FirstOrDefaultAsync(
                w => w.WorkflowId == payload.WorkflowId && w.IsActive, ct);
            if (workflow is null)
            {
                await DisableOrphanTriggerAsync(db, job, ct);
                throw new InvalidOperationException("the workflow this job points at no longer exists");
            }

            // A triggered run executes as the user who created the trigger. Without
            // this, a step that calls back into our own API (rest_call against an
            // na_* spec, authenticated by ${secret:session:current:jwt}) has no
            // identity to mint a token from and goes out with the marker literal —
            // the manual-vs-scheduled 401. Binding CreatedBy also caps the run at
            // that user's own permissions, same as running it by hand.
            await BindTriggerCreatorAsync(scope.ServiceProvider, db, job, ct);

            var runs = scope.ServiceProvider.GetRequiredService<WorkflowRunService>();
            var run = await runs.RunAsync(workflow, payload.Input, payload.TargetDevices, ct, payload.Trigger);

            job.Status = Job.StatusSucceeded;
            job.WorkflowRunId = run.WorkflowRunId;
            job.Error = null;
            await RecordTriggerOutcomeAsync(db, job, run.Status, run.WorkflowRunId, null, ct);

            _logger.LogInformation(
                "jobs.worker.completed job={Job} run={Run} status={Status}",
                job.JobId, run.WorkflowRunId, run.Status);
            trace.Complete(new
            {
                job_id = job.JobId, run_id = run.WorkflowRunId,
                run_status = run.Status, final_state = run.FinalState,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown mid-run. Leave the claim; the lease will expire and
            // the reclaim sweep will record the honest outcome. The trace says the same:
            // interrupted, not finished.
            trace.Fail("the host shut down while this job was running");
            return;
        }
        catch (Exception ex)
        {
            job.Status = Job.StatusFailed;
            job.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await RecordTriggerOutcomeAsync(db, job, "error", null, job.Error, ct);
            _logger.LogError(ex, "jobs.worker.failed job={Job}", job.JobId);
            trace.Fail(job.Error, new { job_id = job.JobId });
        }

        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = job.CompletedAt.Value;
        await db.SaveChangesAsync(ct);
    }

    // Binds the trigger creator's identity onto the scope's MutableCurrentUser, so
    // user-scoped services (the session-token mint in SecretResolver above all) see
    // the run as that user. Best effort by design: a job with no trigger (nothing
    // says who to run as), a trigger with no CreatedBy, or a creator since deleted
    // or disabled all leave the scope anonymous, and any self-call in the workflow
    // fails with a 401 exactly as it would for a signed-out user — rather than the
    // run silently escalating to an invented identity.
    private async Task BindTriggerCreatorAsync(
        IServiceProvider services, AppDbContext db, Job job, CancellationToken ct)
    {
        if (job.WorkflowTriggerId is not { } triggerId) return;

        var creator = await db.WorkflowTriggers.AsNoTracking()
            .Where(t => t.WorkflowTriggerId == triggerId && t.CreatedBy != null)
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive),
                t => t.CreatedBy, u => u.UserId,
                (t, u) => new { u.UserId, u.Username, u.Role })
            .FirstOrDefaultAsync(ct);
        if (creator is null)
        {
            _logger.LogWarning(
                "jobs.worker.run_as_anonymous job={Job} trigger={Trigger} — the trigger has no active " +
                "creator to run as; self-API steps in this run will be unauthenticated", job.JobId, triggerId);
            return;
        }

        // In a background scope ICurrentUser resolves to this same instance.
        services.GetRequiredService<MutableCurrentUser>()
            .Bind(creator.UserId, creator.Username, [creator.Role]);
        _logger.LogInformation(
            "jobs.worker.run_as job={Job} user={User}", job.JobId, creator.Username);
    }

    // The trigger's last-run bookkeeping used to be written by whoever fired it;
    // now that firing and running are decoupled, the worker is the only place that
    // knows the outcome.
    private static async Task RecordTriggerOutcomeAsync(
        AppDbContext db, Job job, string status, Guid? runId, string? error, CancellationToken ct)
    {
        if (job.WorkflowTriggerId is not { } triggerId) return;
        var trigger = await db.WorkflowTriggers.FirstOrDefaultAsync(
            t => t.WorkflowTriggerId == triggerId, ct);
        if (trigger is null) return;

        trigger.LastRunAt = DateTime.UtcNow;
        trigger.LastRunStatus = status;
        trigger.LastRunId = runId;
        trigger.FireCount++;
        trigger.UpdatedAt = trigger.LastRunAt.Value;

        // LastError means two things depending on the row's state: on an enabled
        // trigger it is why the last run failed, and a success clears it. On a
        // disabled one it is why the scheduler switched it off — a last firing that
        // has no occurrence after it, or an expression that stopped being
        // schedulable — and that reason has to survive the very run that disabling
        // let through. Clearing it there would leave a trigger that is off for no
        // stated reason, which is the hardest kind of "it just stopped" to diagnose.
        if (trigger.Enabled || error is not null) trigger.LastError = error;
    }

    private static async Task DisableOrphanTriggerAsync(AppDbContext db, Job job, CancellationToken ct)
    {
        if (job.WorkflowTriggerId is not { } triggerId) return;
        var trigger = await db.WorkflowTriggers.FirstOrDefaultAsync(
            t => t.WorkflowTriggerId == triggerId, ct);
        if (trigger is null) return;
        trigger.Enabled = false;
        trigger.LastError = "the workflow this trigger points at no longer exists";
        trigger.UpdatedAt = DateTime.UtcNow;
    }
}
