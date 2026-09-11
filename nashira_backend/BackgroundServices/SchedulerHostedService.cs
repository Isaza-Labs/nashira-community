using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Jobs;
using nashira_backend.Services.Scheduler;
using nashira_backend.Services.Trace;

namespace nashira_backend.BackgroundServices;

// Fires cron triggers whose time has come — by ENQUEUEING a job, never by running
// the workflow inline.
//
// That split is what fixed the two problems the first version shipped with: a
// long run no longer delays every other cron (the sweep only writes rows), and
// multiple replicas are safe, because the firing itself is claimed atomically:
// the re-base is an UPDATE … WHERE NextRunAt = <the due time we read>, so of N
// replicas sweeping the same due trigger, exactly one wins the update and only
// the winner enqueues. The job queue's own SKIP LOCKED claim then guarantees the
// enqueued run executes once, wherever.
//
// Polls rather than sleeping until the next occurrence: a trigger can be created,
// edited or disabled at any moment, and a long sleep would keep firing the old
// schedule until it woke.
public sealed class SchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    // A trigger whose due time is far in the past (the process was down for a
    // weekend) fires once and re-bases, rather than replaying every occurrence it
    // missed. Replaying a nightly job forty times would be worse than skipping it.
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITraceLogger _trace;
    private readonly ILogger<SchedulerHostedService> _logger;

    public SchedulerHostedService(
        IServiceScopeFactory scopes, ILogger<SchedulerHostedService> logger, ITraceLogger trace)
    {
        _scopes = scopes;
        _logger = logger;
        _trace = trace;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("scheduler.started tick={Seconds}s", Tick.TotalSeconds);

        // Let migrations and seeding finish before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad sweep must not kill the scheduler for the process's life.
                _logger.LogError(ex, "scheduler.sweep_failed");
            }

            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("scheduler.stopped");
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        // Every sweep, not only an idle one. This used to sit behind an early return
        // taken when nothing was due, so on an instance where some trigger is due on
        // most ticks, a trigger with no NextRunAt — one just imported, or one the sweep
        // below could not compute a next occurrence for — was never given a schedule
        // and simply never ran, with nothing on the row saying why.
        await BackfillNextRunAsync(db, now, ct);

        var due = await db.WorkflowTriggers.AsNoTracking()
            .Where(t => t.IsActive && t.Enabled && t.Type == WorkflowTrigger.TypeCron
                        && t.NextRunAt != null && t.NextRunAt <= now)
            .OrderBy(t => t.NextRunAt)
            .Take(50)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var trigger in due)
        {
            ct.ThrowIfCancellationRequested();

            var scheduled = trigger.NextRunAt!.Value;
            var next = CronSchedule.NextOccurrence(
                trigger.CronExpression ?? string.Empty, trigger.Timezone, now);

            // The atomic claim of this FIRING. Conditioned on NextRunAt still being
            // the due time we read: with N replicas sweeping, exactly one update
            // matches and the rest fall through without enqueueing anything.
            var won = await db.WorkflowTriggers
                .Where(t => t.WorkflowTriggerId == trigger.WorkflowTriggerId && t.NextRunAt == scheduled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.NextRunAt, next)
                    .SetProperty(t => t.UpdatedAt, now), ct);
            if (won == 0) continue;

            // No future occurrence: this firing is the last one. Say so on the row and
            // stop, rather than leaving a trigger that reads as enabled with an empty
            // next-run and never fires again — which is indistinguishable, on screen,
            // from a scheduler that has stopped working.
            if (next is null)
            {
                await DisableAsync(db, trigger.WorkflowTriggerId,
                    "the cron expression has no occurrence after this one", now, ct);
                _logger.LogWarning(
                    "scheduler.disabled_unschedulable trigger={Trigger} expression={Expression} timezone={Timezone}",
                    trigger.Name, trigger.CronExpression, trigger.Timezone);
            }

            var stale = now - scheduled > CatchUpWindow;
            if (stale)
            {
                _logger.LogWarning(
                    "scheduler.skipped_stale trigger={Trigger} due={Due} late_by={Late}",
                    trigger.Name, scheduled, now - scheduled);
                // A schedule that came due while the platform was down and was then
                // skipped is the "why didn't my nightly job run" question, answered.
                _trace.Event(TraceEvent.CategoryScheduler, "scheduler.trigger.skipped_stale",
                    new { trigger = trigger.Name, due = scheduled, late_by_seconds = (int)(now - scheduled).TotalSeconds },
                    error: "the firing was more than the catch-up window late and was not run");
                // The trace answers it for whoever thinks to look; this puts it on the
                // trigger itself, which is where somebody asking "why didn't it run
                // last night" is already looking. Cleared by the next run's outcome.
                //
                // Not when the trigger was just disabled: "no occurrence after this
                // one" explains why it is off and will never fire again, which the
                // reader needs more than the reason this particular firing was late.
                if (next is not null)
                    await RecordSkipAsync(db, trigger.WorkflowTriggerId, scheduled, now, ct);
                continue;
            }

            var job = await queue.EnqueueWorkflowRunAsync(
                trigger.WorkflowId,
                ParseObject(trigger.InputDefaultsJson),
                ParseGuids(trigger.TargetDevicesJson),
                trigger.WorkflowTriggerId,
                deliveryKey: null,
                ct,
                RunTrigger.Schedule);

            _logger.LogInformation(
                "scheduler.enqueued trigger={Trigger} job={Job}", trigger.Name, job.JobId);
            // The firing itself. Nothing else records that a cron came due: the job row
            // says a job exists, not that a schedule produced it.
            _trace.Event(TraceEvent.CategoryScheduler, "scheduler.trigger.fire",
                new
                {
                    trigger = trigger.Name, trigger_id = trigger.WorkflowTriggerId,
                    job_id = job.JobId, due = scheduled, next = next,
                });
        }
    }

    private static Task DisableAsync(
        AppDbContext db, Guid triggerId, string reason, DateTime now, CancellationToken ct) =>
        db.WorkflowTriggers
            .Where(t => t.WorkflowTriggerId == triggerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Enabled, false)
                .SetProperty(t => t.LastError, reason)
                .SetProperty(t => t.UpdatedAt, now), ct);

    private static Task RecordSkipAsync(
        AppDbContext db, Guid triggerId, DateTime due, DateTime now, CancellationToken ct)
    {
        var minutes = (int)(now - due).TotalMinutes;
        var reason = $"the firing due at {due:u} was skipped: it was {minutes} minute(s) late, "
                     + "past the catch-up window, so it was not run";
        return db.WorkflowTriggers
            .Where(t => t.WorkflowTriggerId == triggerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.LastError, reason)
                .SetProperty(t => t.UpdatedAt, now), ct);
    }

    // A trigger with no NextRunAt (just created, or edited) gets one computed here,
    // so the sweep is the single place that owns the schedule.
    private async Task BackfillNextRunAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var pending = await db.WorkflowTriggers
            .Where(t => t.IsActive && t.Enabled && t.Type == WorkflowTrigger.TypeCron && t.NextRunAt == null)
            .Take(50)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        foreach (var t in pending)
        {
            t.NextRunAt = CronSchedule.NextOccurrence(t.CronExpression ?? string.Empty, t.Timezone, now);
            t.UpdatedAt = now;
            if (t.NextRunAt is null)
            {
                // No future occurrence at all: disable rather than re-evaluate it
                // every tick forever.
                t.Enabled = false;
                t.LastError = "the cron expression has no future occurrence";
                _logger.LogWarning("scheduler.disabled_unschedulable trigger={Trigger}", t.Name);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<Guid> ParseGuids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
                .Select(e => Guid.Parse(e.GetString()!))
                .ToList();
        }
        catch (JsonException) { return []; }
    }
}
