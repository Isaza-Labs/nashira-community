using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Metrics;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Read-only aggregates behind the admin dashboard. Admin-only, and nothing here
// mutates, so none of it reaches the audit trail.
//
// Every windowed series returns one bucket per day in the range, zero-run days
// included. Backfilling gaps in the client means every consumer reimplements the same
// loop, and a chart whose x-axis silently skips quiet days misreads as continuous
// activity.
[ApiController]
[Route("api/admin/metrics")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AdminMetricsController : ControllerBase
{
    private const int DefaultDays = 7;
    private const int MaxDays = 90;

    private readonly AppDbContext _db;

    public AdminMetricsController(AppDbContext db) => _db = db;

    // Workflow runs by day and status, plus what has been failing.
    [HttpGet("runs")]
    public async Task<ActionResult<RunMetricsResponse>> Runs(
        int days = DefaultDays, CancellationToken ct = default)
    {
        var (window, from) = Window(days);

        // StartedAt, not the row's creation: a run is bucketed on the day it ran.
        var rows = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive && r.StartedAt >= from)
            .GroupBy(r => new { Date = r.StartedAt.Date, r.Status })
            .Select(g => new { g.Key.Date, Key = g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var topFailing = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive && r.StartedAt >= from && r.Status == "failed")
            .GroupBy(r => r.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, Failed = g.Count() })
            .OrderByDescending(x => x.Failed)
            .Take(5)
            .ToListAsync(ct);

        var failingIds = topFailing.Select(t => t.WorkflowId).ToList();
        var names = await _db.Workflows.AsNoTracking()
            .Where(w => failingIds.Contains(w.WorkflowId))
            .Select(w => new { w.WorkflowId, w.Name })
            .ToDictionaryAsync(w => w.WorkflowId, w => w.Name, ct);

        // The distinction Nashira's engine draws and flow-weaver's does not: a failed
        // run whose changes were all reversed is a different operational fact from one
        // that left the world half-changed. Surfaced here because the second is the
        // number somebody has to act on.
        var finalStates = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive && r.StartedAt >= from && r.FinalState != "")
            .GroupBy(r => r.FinalState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.State, x => x.Count, ct);

        var byEnvironment = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive && r.StartedAt >= from)
            .GroupBy(r => r.Environment)
            .Select(g => new { Environment = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Environment, x => x.Count, ct);

        return new RunMetricsResponse
        {
            Days = window,
            From = from,
            Series = DailyBuckets(from, window, rows.Select(r => (r.Date, r.Key, r.Count))),
            FinalStates = finalStates,
            ByEnvironment = byEnvironment,
            TopFailing = topFailing.Select(t => new TopFailingWorkflow
            {
                WorkflowId = t.WorkflowId,
                WorkflowName = names.GetValueOrDefault(t.WorkflowId, "(deleted)"),
                FailedCount = t.Failed,
            }).ToList(),
        };
    }

    // Sign-ins by day and kind. A spike of failures next to a flat line of successes is
    // the shape this exists to make visible.
    [HttpGet("auth")]
    public async Task<ActionResult<AuthMetricsResponse>> Auth(
        int days = DefaultDays, CancellationToken ct = default)
    {
        var (window, from) = Window(days);

        var rows = await _db.AuthEvents.AsNoTracking()
            .Where(e => e.At >= from)
            .GroupBy(e => new { Date = e.At.Date, e.Event })
            .Select(g => new { g.Key.Date, Key = g.Key.Event, Count = g.Count() })
            .ToListAsync(ct);

        // Counted separately from the series so the headline numbers do not depend on
        // which kinds happen to appear in the window.
        var failures = rows.Where(r => r.Key == Data.Models.AuthEvent.LoginFailure).Sum(r => r.Count);
        var lockouts = rows.Where(r => r.Key == Data.Models.AuthEvent.Lockout).Sum(r => r.Count);

        return new AuthMetricsResponse
        {
            Days = window,
            From = from,
            Series = DailyBuckets(from, window, rows.Select(r => (r.Date, r.Key, r.Count))),
            Successes = rows.Where(r => r.Key == Data.Models.AuthEvent.LoginSuccess).Sum(r => r.Count),
            Failures = failures,
            Lockouts = lockouts,
        };
    }

    // Queue depth right now, not over a window: the question is whether work is piling
    // up at this moment.
    [HttpGet("queue")]
    public async Task<ActionResult<QueueMetricsResponse>> Queue(CancellationToken ct = default)
    {
        var byStatus = await _db.Jobs.AsNoTracking()
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        // The age of the oldest thing still waiting. Depth alone does not distinguish a
        // busy queue from a stuck one — ten jobs queued for a second is healthy, one
        // queued for an hour is not.
        var oldestQueuedAt = await _db.Jobs.AsNoTracking()
            .Where(j => j.Status == Data.Models.Job.StatusQueued)
            .OrderBy(j => j.CreatedAt)
            .Select(j => (DateTime?)j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new QueueMetricsResponse
        {
            ByStatus = byStatus,
            Queued = byStatus.GetValueOrDefault(Data.Models.Job.StatusQueued),
            Claimed = byStatus.GetValueOrDefault(Data.Models.Job.StatusClaimed),
            OldestQueuedAt = oldestQueuedAt,
            OldestQueuedSeconds = oldestQueuedAt is { } t
                ? (int)Math.Max(0, (DateTime.UtcNow - t).TotalSeconds)
                : null,
        };
    }

    // Inventory as it stands. Grouped by every dimension the screens filter on, so the
    // numbers here are reproducible by running the same filter in the device list.
    [HttpGet("devices")]
    public async Task<ActionResult<DeviceMetricsResponse>> Devices(CancellationToken ct = default)
    {
        var q = _db.Devices.AsNoTracking().Where(d => d.IsActive);

        return new DeviceMetricsResponse
        {
            Total = await q.CountAsync(ct),
            ByStatus = await GroupAsync(q.GroupBy(d => d.Status), ct),
            ByVendor = await GroupAsync(q.GroupBy(d => d.Vendor), ct),
            BySite = await GroupAsync(q.GroupBy(d => d.Site), ct),
            // Imported devices re-sync; hand-added ones never do, so an operator
            // wondering why a NetBox change did not appear starts from this split.
            FromInventory = await q.CountAsync(d => d.SourceId != null, ct),
            Manual = await q.CountAsync(d => d.SourceId == null, ct),
            // What a run may actually touch. Production defaults to true and QA to
            // false, so a QA count of zero is the expected state rather than a fault.
            AllowDraft = await q.CountAsync(d => d.AllowDraft, ct),
            AllowQa = await q.CountAsync(d => d.AllowQa, ct),
            AllowProduction = await q.CountAsync(d => d.AllowProduction, ct),
        };
    }

    private static (int Window, DateTime From) Window(int days)
    {
        var window = Math.Clamp(days, 1, MaxDays);
        // Inclusive of today, which is why it is window - 1: asking for 7 days should
        // return today and the six before it, not today and seven before it.
        return (window, DateTime.UtcNow.Date.AddDays(-(window - 1)));
    }

    private static async Task<Dictionary<string, int>> GroupAsync<T>(
        IQueryable<IGrouping<string, T>> grouped, CancellationToken ct)
    {
        var rows = await grouped
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        // An unset dimension is a real answer — devices with no site are still devices —
        // so it is labelled rather than dropped.
        return rows.ToDictionary(r => string.IsNullOrWhiteSpace(r.Key) ? "(unset)" : r.Key, r => r.Count);
    }

    // One bucket per day in the window, each carrying the per-key counts for that day.
    // Days with no activity come back as empty buckets rather than being absent.
    private static List<DailyBucket> DailyBuckets(
        DateTime from, int days, IEnumerable<(DateTime Date, string Key, int Count)> rows)
    {
        var byDate = rows
            .GroupBy(r => r.Date.Date)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Key, r => r.Count));

        return Enumerable.Range(0, days)
            .Select(offset =>
            {
                var date = from.AddDays(offset);
                return new DailyBucket
                {
                    Date = date,
                    Counts = byDate.GetValueOrDefault(date) ?? [],
                };
            })
            .ToList();
    }
}
