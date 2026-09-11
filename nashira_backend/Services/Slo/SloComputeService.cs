using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Slo;

// What an objective is. Everything except the threshold, which is data.
//
// `Better` says which direction is good, and it is what makes a breach decidable: a
// latency of 900 against a target of 600 is a breach, a throughput of 3 against 5 is a
// breach, and without the direction the two comparisons are indistinguishable.
public sealed record SloDefinition(
    string Key,
    string Label,
    string Unit,
    double DefaultTarget,
    string Better,
    string Method)
{
    public const string Lower = "lower";
    public const string Higher = "higher";

    // The objectives this platform commits to. Adding one means writing its query in
    // ComputeAsync — which is why the set is code and only the threshold is a row.
    public static IReadOnlyList<SloDefinition> All { get; } =
    [
        new("run_latency_p95_seconds", "Run latency (p95)", "s", 600, Lower,
            "95th percentile of FinishedAt − StartedAt across runs that finished in the window."),
        new("error_rate", "Run error rate", "ratio", 0.05, Lower,
            "Runs that ended failed, over all runs that finished in the window."),
        new("throughput_jobs_per_hour", "Throughput", "jobs/hr", 5, Higher,
            "Jobs that succeeded in the window, divided by the hours it spans. Measured "
            + "only when something was queued — an idle platform is not a slow one."),
        new("promotion_latency_seconds", "Promotion latency (median)", "s", 86_400, Lower,
            "Median time from a workflow's creation to the promotion of one of its versions."),
        // Nashira's engine records what a failed run left behind; flow-weaver's has no
        // equivalent. It is the number that decides whether a failure was an incident:
        // a run that reversed everything it did is contained, one that stopped halfway
        // left devices in a state nobody chose.
        new("contained_failure_rate", "Contained failures", "ratio", 0.95, Higher,
            "Of the runs that failed, those that rolled every change back."),
    ];

    public static SloDefinition? Find(string key) =>
        All.FirstOrDefault(d => d.Key == key);
}

// One objective, measured. `Value` is null when the window holds nothing to measure —
// which is not the same as zero, and must not be drawn or alerted on as if it were.
public sealed record SloEntry(
    string Key,
    string Label,
    string Unit,
    double Target,
    double DefaultTarget,
    double? Value,
    string Better,
    string Method,
    string? UpdatedBy);

public sealed record SloSnapshot(int Days, DateTime From, IReadOnlyList<SloEntry> Slos);

// The one place the objectives are computed.
//
// Both the screen and the daily breach sweep call this, so an alert can never disagree
// with the dashboard it came from — the failure mode where a page shows green while the
// audit trail fills with breach rows is a bug that only exists if the two do their own
// arithmetic.
public sealed class SloComputeService
{
    private const int MaxDays = 90;

    private readonly AppDbContext _db;

    public SloComputeService(AppDbContext db) => _db = db;

    public async Task<SloSnapshot> ComputeAsync(int days, CancellationToken ct = default)
    {
        var window = Math.Clamp(days, 1, MaxDays);
        // Inclusive of today: seven days is today and the six before it.
        var from = DateTime.UtcNow.Date.AddDays(-(window - 1));

        var overrides = await _db.SloTargets.AsNoTracking()
            .Where(t => t.IsActive)
            .ToDictionaryAsync(t => t.Key, t => t, ct);

        var values = new Dictionary<string, double?>
        {
            ["run_latency_p95_seconds"] = null,
            ["error_rate"] = null,
            ["throughput_jobs_per_hour"] = null,
            ["promotion_latency_seconds"] = null,
            ["contained_failure_rate"] = null,
        };

        // Runs that actually finished. An in-flight run has no duration yet, and
        // counting it as a zero would drag the percentile down exactly when the
        // platform is busiest.
        var finished = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.IsActive && r.StartedAt >= from && r.FinishedAt != null)
            .Select(r => new { r.Status, r.FinalState, r.StartedAt, FinishedAt = r.FinishedAt!.Value })
            .ToListAsync(ct);

        var latencies = finished
            .Select(r => (r.FinishedAt - r.StartedAt).TotalSeconds)
            // A negative duration is a clock artefact, not a fast run. Dropped rather
            // than clamped to zero, which would understate the percentile.
            .Where(s => s >= 0)
            .OrderBy(s => s)
            .ToList();
        if (latencies.Count > 0)
        {
            // Nearest-rank: the smallest value at or above the 95th percentile position.
            // Index clamped because ceil(n * 0.95) reaches n exactly when n is a
            // multiple of 20, and n is one past the end.
            var rank = (int)Math.Ceiling(latencies.Count * 0.95) - 1;
            values["run_latency_p95_seconds"] = latencies[Math.Clamp(rank, 0, latencies.Count - 1)];
        }

        if (finished.Count > 0)
        {
            var failed = finished.Count(r => r.Status == WorkflowRun.StatusFailed);
            values["error_rate"] = (double)failed / finished.Count;

            // Contention is only defined over failures. With none, the answer is "no
            // signal" rather than a perfect score — a window with nothing to contain
            // should not report the same green as one that contained everything.
            if (failed > 0)
            {
                var contained = finished.Count(r =>
                    r.Status == WorkflowRun.StatusFailed && r.FinalState == "rolled_back");
                values["contained_failure_rate"] = (double)contained / failed;
            }
        }

        // Throughput needs a denominator of demand, not just of hours.
        //
        // Counting succeeded jobs over elapsed time alone reports an idle platform as a
        // failing one: nothing was queued, so nothing succeeded, so the rate is zero and
        // the objective is missed every single day on an install that is simply quiet.
        // That is the false alarm this file's own breach rule was written to avoid, and
        // measuring it this way reintroduced it one screen later.
        //
        // With nothing queued there is no signal — the platform was not asked to keep up
        // with anything. Once work exists, the rate is a real measurement and a low one
        // is a real problem.
        var queuedJobs = await _db.Jobs.AsNoTracking()
            .CountAsync(j => j.IsActive && j.CreatedAt >= from, ct);
        if (queuedJobs > 0)
        {
            var succeededJobs = await _db.Jobs.AsNoTracking()
                .CountAsync(j => j.IsActive && j.CreatedAt >= from && j.Status == Job.StatusSucceeded, ct);
            values["throughput_jobs_per_hour"] = succeededJobs / (window * 24.0);
        }

        // Promotion latency, measured from the source workflow's creation to the moment
        // the promotion happened. Nashira stamps PromotedAt, so this is the elapsed time
        // itself rather than a proxy taken from when the promoted row was inserted.
        var promotions = await _db.Workflows.AsNoTracking()
            .Where(w => w.IsActive
                && w.PromotedAt != null
                && w.PromotedAt >= from
                && w.PromotedFrom != null)
            .Select(w => new { PromotedAt = w.PromotedAt!.Value, Source = w.PromotedFrom!.Value })
            .ToListAsync(ct);

        if (promotions.Count > 0)
        {
            var sourceIds = promotions.Select(p => p.Source).Distinct().ToList();
            var sourceCreated = await _db.Workflows.AsNoTracking()
                .Where(w => sourceIds.Contains(w.WorkflowId))
                .ToDictionaryAsync(w => w.WorkflowId, w => w.CreatedAt, ct);

            var elapsed = promotions
                // A promotion whose source has since been hard-deleted cannot be timed.
                // Skipped rather than counted as zero, which would report instant
                // promotions the moment somebody tidied up an old draft.
                .Where(p => sourceCreated.ContainsKey(p.Source))
                .Select(p => (p.PromotedAt - sourceCreated[p.Source]).TotalSeconds)
                .Where(s => s >= 0)
                .OrderBy(s => s)
                .ToList();

            if (elapsed.Count > 0)
            {
                // True median: the mean of the middle pair on an even count, rather than
                // the upper of the two. With two promotions of one hour and one week,
                // taking the upper reports a week as typical.
                values["promotion_latency_seconds"] = elapsed.Count % 2 == 1
                    ? elapsed[elapsed.Count / 2]
                    : (elapsed[elapsed.Count / 2 - 1] + elapsed[elapsed.Count / 2]) / 2.0;
            }
        }

        var entries = SloDefinition.All.Select(d =>
        {
            var over = overrides.GetValueOrDefault(d.Key);
            return new SloEntry(
                d.Key, d.Label, d.Unit,
                over?.Target ?? d.DefaultTarget,
                d.DefaultTarget,
                values.GetValueOrDefault(d.Key),
                d.Better, d.Method,
                over?.UpdatedBy);
        }).ToList();

        return new SloSnapshot(window, from, entries);
    }

    // Whether the measurement violates its target.
    //
    // No measurement is not a breach. Treating null as zero would page somebody every
    // night on a quiet install for a throughput objective nothing was trying to meet.
    public static bool IsBreach(SloEntry entry) => entry.Value is { } v && entry.Better switch
    {
        SloDefinition.Lower => v > entry.Target,
        SloDefinition.Higher => v < entry.Target,
        _ => false,
    };
}
