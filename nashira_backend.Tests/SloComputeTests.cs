using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Slo;

namespace nashira_backend.Tests;

// The service-level objectives.
//
// The bugs worth catching here are the ones that read as fine: a percentile that
// indexes past the end on exactly twenty samples, a "no data" window reported as a
// perfect score, an even-sized median that reports the slower half as typical, and a
// breach rule that pages somebody about a throughput objective on an install where
// nothing ran.
public class SloComputeTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"slo-{Guid.NewGuid()}").Options);

    private static SloComputeService NewService(AppDbContext db) => new(db);

    private static void AddRun(AppDbContext db, string status, DateTime startedAt,
        double? durationSeconds = 10, string finalState = "")
    {
        db.WorkflowRuns.Add(new WorkflowRun
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Environment = "production",
            Status = status,
            FinalState = finalState,
            StartedAt = startedAt,
            FinishedAt = durationSeconds is { } d ? startedAt.AddSeconds(d) : null,
            IsActive = true,
            CreatedAt = startedAt,
            UpdatedAt = startedAt,
        });
        db.SaveChanges();
    }

    private static SloEntry Entry(SloSnapshot s, string key) =>
        s.Slos.Single(e => e.Key == key);

    // ── the window ────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_defined_objective_is_reported()
    {
        using var db = NewDb();
        var snapshot = await NewService(db).ComputeAsync(7);

        Assert.Equal(SloDefinition.All.Count, snapshot.Slos.Count);
        Assert.Equal(
            SloDefinition.All.Select(d => d.Key).ToHashSet(),
            snapshot.Slos.Select(e => e.Key).ToHashSet());
    }

    [Fact]
    public async Task The_window_is_clamped_and_inclusive_of_today()
    {
        using var db = NewDb();
        var svc = NewService(db);

        Assert.Equal(1, (await svc.ComputeAsync(0)).Days);
        Assert.Equal(90, (await svc.ComputeAsync(9999)).Days);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-6), (await svc.ComputeAsync(7)).From);
    }

    // ── latency ───────────────────────────────────────────────────────────

    // Nearest-rank on exactly twenty samples puts ceil(20 × 0.95) at 20, one past the
    // last index. Unclamped this throws, and the endpoint 500s on a perfectly ordinary
    // day's worth of runs.
    [Fact]
    public async Task The_percentile_holds_at_the_boundary_of_the_sample()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        for (var i = 1; i <= 20; i++)
            AddRun(db, WorkflowRun.StatusCompleted, today.AddMinutes(i), durationSeconds: i);

        var p95 = Entry(await NewService(db).ComputeAsync(7), "run_latency_p95_seconds").Value;

        // The 19th of twenty ordered samples, not the 20th and not an exception.
        Assert.Equal(19, p95);
    }

    [Fact]
    public async Task A_single_run_is_its_own_percentile()
    {
        using var db = NewDb();
        AddRun(db, WorkflowRun.StatusCompleted, DateTime.UtcNow.Date, durationSeconds: 42);

        Assert.Equal(42, Entry(await NewService(db).ComputeAsync(7), "run_latency_p95_seconds").Value);
    }

    // A run still going has no duration. Counting it as zero would drag the percentile
    // down exactly when the platform is busiest — the opposite of what it should show.
    [Fact]
    public async Task Unfinished_runs_are_not_measured()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        AddRun(db, WorkflowRun.StatusCompleted, today, durationSeconds: 100);
        AddRun(db, WorkflowRun.StatusRunning, today, durationSeconds: null);

        var snapshot = await NewService(db).ComputeAsync(7);

        Assert.Equal(100, Entry(snapshot, "run_latency_p95_seconds").Value);
        // The running one is absent from the error rate's denominator too.
        Assert.Equal(0d, Entry(snapshot, "error_rate").Value);
    }

    [Fact]
    public async Task An_empty_window_measures_nothing_rather_than_zero()
    {
        using var db = NewDb();
        var snapshot = await NewService(db).ComputeAsync(7);

        Assert.Null(Entry(snapshot, "run_latency_p95_seconds").Value);
        Assert.Null(Entry(snapshot, "error_rate").Value);
        Assert.Null(Entry(snapshot, "promotion_latency_seconds").Value);
        Assert.Null(Entry(snapshot, "contained_failure_rate").Value);
        Assert.Null(Entry(snapshot, "throughput_jobs_per_hour").Value);
    }

    // ── error rate and containment ────────────────────────────────────────

    [Fact]
    public async Task Error_rate_is_failures_over_everything_that_finished()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        AddRun(db, WorkflowRun.StatusCompleted, today.AddMinutes(1));
        AddRun(db, WorkflowRun.StatusCompleted, today.AddMinutes(2));
        AddRun(db, WorkflowRun.StatusCompleted, today.AddMinutes(3));
        AddRun(db, WorkflowRun.StatusFailed, today.AddMinutes(4), finalState: "failed");

        Assert.Equal(0.25, Entry(await NewService(db).ComputeAsync(7), "error_rate").Value);
    }

    // The distinction Nashira's engine records and flow-weaver's cannot: of the runs
    // that failed, the ones that put everything back.
    [Fact]
    public async Task Containment_is_measured_over_the_failures_only()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        AddRun(db, WorkflowRun.StatusCompleted, today.AddMinutes(1), finalState: "completed");
        AddRun(db, WorkflowRun.StatusFailed, today.AddMinutes(2), finalState: "rolled_back");
        AddRun(db, WorkflowRun.StatusFailed, today.AddMinutes(3), finalState: "rolled_back");
        AddRun(db, WorkflowRun.StatusFailed, today.AddMinutes(4), finalState: "rolled_back");
        AddRun(db, WorkflowRun.StatusFailed, today.AddMinutes(5), finalState: "failed");

        // Three of four failures rolled back — the completed run is not in the ratio.
        Assert.Equal(0.75, Entry(await NewService(db).ComputeAsync(7), "contained_failure_rate").Value);
    }

    // A window with nothing to contain must not read as a perfect score: green here
    // would claim the platform reverted failures it never had.
    [Fact]
    public async Task Containment_reports_no_signal_when_nothing_failed()
    {
        using var db = NewDb();
        AddRun(db, WorkflowRun.StatusCompleted, DateTime.UtcNow.Date, finalState: "completed");

        Assert.Null(Entry(await NewService(db).ComputeAsync(7), "contained_failure_rate").Value);
    }

    [Fact]
    public async Task Runs_outside_the_window_are_excluded()
    {
        using var db = NewDb();
        AddRun(db, WorkflowRun.StatusFailed, DateTime.UtcNow.Date.AddDays(-30), finalState: "failed");

        Assert.Null(Entry(await NewService(db).ComputeAsync(7), "error_rate").Value);
    }

    // ── throughput ────────────────────────────────────────────────────────

    [Fact]
    public async Task Throughput_is_succeeded_jobs_over_the_hours_of_the_window()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 48; i++)
        {
            db.Jobs.Add(new Job
            {
                JobId = Guid.NewGuid(), Status = Job.StatusSucceeded,
                CreatedAt = now, UpdatedAt = now, IsActive = true,
            });
        }
        // Failures and queued work are not throughput.
        db.Jobs.Add(new Job
        {
            JobId = Guid.NewGuid(), Status = Job.StatusFailed,
            CreatedAt = now, UpdatedAt = now, IsActive = true,
        });
        await db.SaveChangesAsync();

        // 48 jobs over a 2-day window = 48 / 48 hours = 1 per hour.
        Assert.Equal(1d, Entry(await NewService(db).ComputeAsync(2), "throughput_jobs_per_hour").Value);
    }

    // The regression that shipped: with nothing queued, throughput was reported as
    // zero, so an install where nobody had run anything missed the objective every
    // single day — the exact false alarm IsBreach's null handling exists to prevent.
    [Fact]
    public async Task Throughput_is_no_signal_when_nothing_was_queued()
    {
        using var db = NewDb();
        var entry = Entry(await NewService(db).ComputeAsync(7), "throughput_jobs_per_hour");

        Assert.Null(entry.Value);
        Assert.False(SloComputeService.IsBreach(entry));
    }

    // But work that was queued and did not succeed is a real zero, and a real breach.
    [Fact]
    public async Task Throughput_of_zero_is_reported_when_work_was_queued_and_failed()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            db.Jobs.Add(new Job
            {
                JobId = Guid.NewGuid(), Status = Job.StatusFailed,
                CreatedAt = now, UpdatedAt = now, IsActive = true,
            });
        }
        await db.SaveChangesAsync();

        var entry = Entry(await NewService(db).ComputeAsync(7), "throughput_jobs_per_hour");

        Assert.Equal(0d, entry.Value);
        Assert.True(SloComputeService.IsBreach(entry));
    }

    // ── promotion latency ─────────────────────────────────────────────────

    private static Guid AddWorkflow(AppDbContext db, DateTime createdAt,
        Guid? promotedFrom = null, DateTime? promotedAt = null)
    {
        var id = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            WorkflowId = id,
            Name = $"wf-{id:N}",
            Environment = promotedFrom is null ? Workflow.EnvDraft : Workflow.EnvProduction,
            PromotedFrom = promotedFrom,
            PromotedAt = promotedAt,
            IsActive = true,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        db.SaveChanges();
        return id;
    }

    // An even count takes the mean of the middle pair. Taking the upper of the two
    // would report a week as typical when half the promotions took an hour.
    [Fact]
    public async Task Promotion_latency_is_a_true_median()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;
        var src1 = AddWorkflow(db, now.AddHours(-10));
        var src2 = AddWorkflow(db, now.AddHours(-20));
        AddWorkflow(db, now, promotedFrom: src1, promotedAt: now);   // 10 h
        AddWorkflow(db, now, promotedFrom: src2, promotedAt: now);   // 20 h

        var median = Entry(await NewService(db).ComputeAsync(7), "promotion_latency_seconds").Value;

        Assert.NotNull(median);
        Assert.Equal(15 * 3600, median!.Value, precision: 0);
    }

    // A promotion whose source has been hard-deleted cannot be timed. Counting it as
    // zero would report instant promotions the moment somebody tidied up old drafts.
    [Fact]
    public async Task A_promotion_whose_source_is_gone_is_skipped_not_zeroed()
    {
        using var db = NewDb();
        var now = DateTime.UtcNow;
        var src = AddWorkflow(db, now.AddHours(-6));
        AddWorkflow(db, now, promotedFrom: src, promotedAt: now);
        AddWorkflow(db, now, promotedFrom: Guid.NewGuid(), promotedAt: now);

        var median = Entry(await NewService(db).ComputeAsync(7), "promotion_latency_seconds").Value;

        Assert.NotNull(median);
        Assert.Equal(6 * 3600, median!.Value, precision: 0);
    }

    // ── targets ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Objectives_report_their_built_in_target_when_nothing_overrides_it()
    {
        using var db = NewDb();
        var entry = Entry(await NewService(db).ComputeAsync(7), "error_rate");

        Assert.Equal(0.05, entry.Target);
        Assert.Equal(0.05, entry.DefaultTarget);
        Assert.Null(entry.UpdatedBy);
    }

    [Fact]
    public async Task A_stored_target_overrides_the_default_and_names_who_moved_it()
    {
        using var db = NewDb();
        db.SloTargets.Add(new SloTarget
        {
            SloTargetId = Guid.NewGuid(), Key = "error_rate", Target = 0.2,
            UpdatedBy = "rlozada", IsActive = true,
        });
        await db.SaveChangesAsync();

        var entry = Entry(await NewService(db).ComputeAsync(7), "error_rate");

        Assert.Equal(0.2, entry.Target);
        // The default is still reported, so the screen can offer "put it back" without
        // keeping its own copy of the number.
        Assert.Equal(0.05, entry.DefaultTarget);
        Assert.Equal("rlozada", entry.UpdatedBy);
    }

    // ── the breach rule ───────────────────────────────────────────────────

    [Fact]
    public void A_missing_measurement_is_never_a_breach()
    {
        var entry = new SloEntry("k", "l", "s", 600, 600, null, SloDefinition.Lower, "m", null);
        Assert.False(SloComputeService.IsBreach(entry));
    }

    [Fact]
    public void Direction_decides_the_comparison()
    {
        SloEntry With(double value, string better) =>
            new("k", "l", "s", 100, 100, value, better, "m", null);

        Assert.True(SloComputeService.IsBreach(With(101, SloDefinition.Lower)));
        Assert.False(SloComputeService.IsBreach(With(100, SloDefinition.Lower)));
        Assert.False(SloComputeService.IsBreach(With(99, SloDefinition.Lower)));

        Assert.True(SloComputeService.IsBreach(With(99, SloDefinition.Higher)));
        Assert.False(SloComputeService.IsBreach(With(100, SloDefinition.Higher)));
        Assert.False(SloComputeService.IsBreach(With(101, SloDefinition.Higher)));
    }

    // Every definition must declare a direction the rule understands. A typo would make
    // the objective permanently green — the worst possible failure for an alarm.
    [Fact]
    public void Every_definition_declares_a_usable_direction()
    {
        Assert.All(SloDefinition.All, d =>
            Assert.True(d.Better is SloDefinition.Lower or SloDefinition.Higher,
                $"'{d.Key}' has direction '{d.Better}', which IsBreach cannot evaluate"));
    }

    [Fact]
    public void Definition_keys_are_unique()
    {
        Assert.Equal(SloDefinition.All.Count, SloDefinition.All.Select(d => d.Key).Distinct().Count());
    }

    // A ratio objective whose default sits outside 0..1 would be unreachable from the
    // moment it shipped.
    [Fact]
    public void Ratio_objectives_default_to_a_reachable_target()
    {
        Assert.All(SloDefinition.All.Where(d => d.Unit == "ratio"), d =>
            Assert.InRange(d.DefaultTarget, 0d, 1d));
    }
}
