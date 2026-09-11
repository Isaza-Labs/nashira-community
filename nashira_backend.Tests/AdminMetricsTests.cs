using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Metrics;
using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// The dashboard aggregates.
//
// The bugs these catch are the quiet ones: an off-by-one that drops today from a
// seven-day window, a chart whose x-axis skips the days nothing happened, a queue depth
// that cannot tell busy from stuck.
public class AdminMetricsTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"metrics-{Guid.NewGuid()}").Options);

    private static AdminMetricsController NewController(AppDbContext db) => new(db);

    private static void AddRun(AppDbContext db, string status, DateTime startedAt,
        Guid? workflowId = null, string environment = "production", string finalState = "")
    {
        db.WorkflowRuns.Add(new WorkflowRun
        {
            WorkflowRunId = Guid.NewGuid(),
            WorkflowId = workflowId ?? Guid.NewGuid(),
            Environment = environment,
            Status = status,
            FinalState = finalState,
            StartedAt = startedAt,
            IsActive = true,
            CreatedAt = startedAt,
            UpdatedAt = startedAt,
        });
        db.SaveChanges();
    }

    private static T Body<T>(ActionResult<T> result) where T : class
    {
        if (result.Value is { } v) return v;
        throw new InvalidOperationException($"expected a {typeof(T).Name} body");
    }

    // A window of N days means today and the N-1 before it. Off by one here shifts every
    // bar on the chart by a day, which nobody notices and everybody misreads.
    [Fact]
    public async Task A_window_covers_today_and_the_days_before_it()
    {
        using var db = NewDb();
        var metrics = Body(await NewController(db).Runs(days: 7));

        Assert.Equal(7, metrics.Days);
        Assert.Equal(7, metrics.Series.Count);
        Assert.Equal(DateTime.UtcNow.Date, metrics.Series[^1].Date);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-6), metrics.Series[0].Date);
    }

    // A day with nothing on it is a fact worth drawing. Absent buckets make a quiet
    // week look like a continuous one.
    [Fact]
    public async Task Quiet_days_come_back_as_empty_buckets()
    {
        using var db = NewDb();
        AddRun(db, "completed", DateTime.UtcNow.Date);

        var metrics = Body(await NewController(db).Runs(days: 5));

        Assert.Equal(5, metrics.Series.Count);
        Assert.All(metrics.Series[..4], b => Assert.Empty(b.Counts));
        Assert.Equal(1, metrics.Series[^1].Counts["completed"]);
    }

    [Fact]
    public async Task Runs_are_bucketed_by_day_and_status()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        AddRun(db, "completed", today.AddHours(1));
        AddRun(db, "completed", today.AddHours(2));
        AddRun(db, "failed", today.AddHours(3));
        AddRun(db, "completed", today.AddDays(-1).AddHours(5));

        var metrics = Body(await NewController(db).Runs(days: 3));

        Assert.Equal(2, metrics.Series[^1].Counts["completed"]);
        Assert.Equal(1, metrics.Series[^1].Counts["failed"]);
        Assert.Equal(1, metrics.Series[^2].Counts["completed"]);
    }

    // Anything older than the window must not leak into the first bucket.
    [Fact]
    public async Task Runs_outside_the_window_are_excluded()
    {
        using var db = NewDb();
        AddRun(db, "completed", DateTime.UtcNow.Date.AddDays(-30));

        var metrics = Body(await NewController(db).Runs(days: 7));

        Assert.All(metrics.Series, b => Assert.Empty(b.Counts));
    }

    // rolled_back and failed are different operational facts: one reversed everything it
    // had done, the other left the world half-changed. Only the second needs anybody.
    [Fact]
    public async Task Final_states_separate_rolled_back_from_failed()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        AddRun(db, "failed", today.AddHours(1), finalState: "rolled_back");
        AddRun(db, "failed", today.AddHours(2), finalState: "failed");
        AddRun(db, "failed", today.AddHours(3), finalState: "failed");

        var metrics = Body(await NewController(db).Runs(days: 7));

        Assert.Equal(1, metrics.FinalStates["rolled_back"]);
        Assert.Equal(2, metrics.FinalStates["failed"]);
    }

    [Fact]
    public async Task Top_failing_workflows_are_ranked_and_named()
    {
        using var db = NewDb();
        var noisy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            WorkflowId = noisy, Name = "nightly-backup", Environment = "production",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var today = DateTime.UtcNow.Date;
        AddRun(db, "failed", today.AddHours(1), noisy);
        AddRun(db, "failed", today.AddHours(2), noisy);
        AddRun(db, "failed", today.AddHours(3), quiet);

        var metrics = Body(await NewController(db).Runs(days: 7));

        Assert.Equal(2, metrics.TopFailing.Count);
        Assert.Equal("nightly-backup", metrics.TopFailing[0].WorkflowName);
        Assert.Equal(2, metrics.TopFailing[0].FailedCount);
        // A workflow deleted since its run still has to appear — the failures happened.
        Assert.Equal("(deleted)", metrics.TopFailing[1].WorkflowName);
    }

    [Fact]
    public async Task Auth_headline_counts_come_from_the_window()
    {
        using var db = NewDb();
        var today = DateTime.UtcNow.Date;
        foreach (var (kind, at) in new[]
        {
            (AuthEvent.LoginSuccess, today.AddHours(1)),
            (AuthEvent.LoginFailure, today.AddHours(2)),
            (AuthEvent.LoginFailure, today.AddHours(3)),
            (AuthEvent.Lockout, today.AddHours(4)),
        })
        {
            db.AuthEvents.Add(new AuthEvent
            {
                AuthEventId = Guid.NewGuid(), Event = kind, At = at, Ip = "10.0.0.1",
            });
        }
        await db.SaveChangesAsync();

        var metrics = Body(await NewController(db).Auth(days: 7));

        Assert.Equal(1, metrics.Successes);
        Assert.Equal(2, metrics.Failures);
        Assert.Equal(1, metrics.Lockouts);
    }

    // Depth alone cannot distinguish a busy queue from a stuck one, which is the only
    // question worth asking of it.
    [Fact]
    public async Task Queue_reports_depth_and_the_age_of_the_oldest_wait()
    {
        using var db = NewDb();
        var old = DateTime.UtcNow.AddMinutes(-42);
        db.Jobs.Add(new Job
        {
            JobId = Guid.NewGuid(), Status = Job.StatusQueued, CreatedAt = old, UpdatedAt = old,
            IsActive = true,
        });
        db.Jobs.Add(new Job
        {
            JobId = Guid.NewGuid(), Status = Job.StatusQueued, CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow, IsActive = true,
        });
        db.Jobs.Add(new Job
        {
            JobId = Guid.NewGuid(), Status = Job.StatusClaimed, CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow, IsActive = true,
        });
        await db.SaveChangesAsync();

        var metrics = Body(await NewController(db).Queue());

        Assert.Equal(2, metrics.Queued);
        Assert.Equal(1, metrics.Claimed);
        Assert.NotNull(metrics.OldestQueuedSeconds);
        Assert.True(metrics.OldestQueuedSeconds >= 2400, $"was {metrics.OldestQueuedSeconds}s");
    }

    [Fact]
    public async Task An_empty_queue_reports_no_oldest_wait()
    {
        using var db = NewDb();
        var metrics = Body(await NewController(db).Queue());

        Assert.Equal(0, metrics.Queued);
        Assert.Null(metrics.OldestQueuedAt);
        Assert.Null(metrics.OldestQueuedSeconds);
    }

    // A device with no site is still a device. Dropping the group would make the
    // breakdown disagree with the total.
    [Fact]
    public async Task Devices_label_an_unset_dimension_rather_than_dropping_it()
    {
        using var db = NewDb();
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(), DeviceName = "r1", IpAddress = "10.0.0.1",
            Vendor = "cisco", Site = "", Status = "reachable",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var metrics = Body(await NewController(db).Devices());

        Assert.Equal(1, metrics.Total);
        Assert.Equal(1, metrics.BySite["(unset)"]);
        Assert.Equal(1, metrics.ByVendor["cisco"]);
        Assert.Equal(metrics.Total, metrics.BySite.Values.Sum());
    }

    [Fact]
    public async Task The_window_is_clamped()
    {
        using var db = NewDb();

        Assert.Equal(1, Body(await NewController(db).Runs(days: 0)).Days);
        Assert.Equal(1, Body(await NewController(db).Runs(days: -5)).Days);
        Assert.Equal(90, Body(await NewController(db).Runs(days: 9999)).Days);
    }
}
