using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// The operational trail.
//
// The failure worth guarding against is silent: a trail that quietly records success
// for work that crashed is worse than no trail, because it is consulted precisely when
// something went wrong and it answers "everything was fine".
public class TraceLoggerTests
{
    private static TraceLogger NewLogger(HttpContext? ctx = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new TraceLogger(accessor, NullLogger<TraceLogger>.Instance);
    }

    private static List<TraceEvent> Drain(TraceLogger logger)
    {
        var rows = new List<TraceEvent>();
        while (logger.Reader.TryRead(out var row)) rows.Add(row);
        return rows;
    }

    [Fact]
    public void An_event_is_one_completed_row()
    {
        var logger = NewLogger();
        logger.Event(TraceEvent.CategorySystem, "system.boot", new { version = "1.2.3" });

        var row = Assert.Single(Drain(logger));
        Assert.Equal("system.boot", row.Action);
        Assert.Equal(TraceEvent.StatusCompleted, row.Status);
        // Zero, not null: an instantaneous event was measured and took no time, where
        // null would mean it is still running.
        Assert.Equal(0, row.DurationMs);
        Assert.Contains("1.2.3", row.MetadataJson);
    }

    [Fact]
    public void An_event_with_an_error_is_recorded_as_failed()
    {
        var logger = NewLogger();
        logger.Event(TraceEvent.CategoryWorker, "worker.lease.expired", error: "a worker died");

        var row = Assert.Single(Drain(logger));
        Assert.Equal(TraceEvent.StatusFailed, row.Status);
        Assert.Equal("a worker died", row.ErrorMessage);
    }

    // The start is written immediately, before the work runs. That is the entire point:
    // an operation that never returns has to have left something behind.
    [Fact]
    public void A_scope_writes_its_start_before_the_work_finishes()
    {
        var logger = NewLogger();
        using var scope = logger.Begin(TraceEvent.CategoryWorker, "worker.job.process");

        var row = Assert.Single(Drain(logger));
        Assert.Equal(TraceEvent.StatusStarted, row.Status);
        Assert.Null(row.DurationMs);
    }

    // Both halves share an id so the writer can collapse them into one row rather than
    // leaving the reader to pair them up by eye.
    [Fact]
    public void Completion_reuses_the_id_of_the_start_it_closes()
    {
        var logger = NewLogger();
        var scope = logger.Begin(TraceEvent.CategoryTool, "tool.list_devices");
        scope.Complete();

        var rows = Drain(logger);
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].TraceEventId, rows[1].TraceEventId);
        Assert.Equal(TraceEvent.StatusStarted, rows[0].Status);
        Assert.Equal(TraceEvent.StatusCompleted, rows[1].Status);
        Assert.NotNull(rows[1].DurationMs);
    }

    // The one that matters most. A scope left through an exception never reports an
    // outcome; defaulting that to success would turn every crash into a clean run in
    // the one table that exists to show otherwise.
    [Fact]
    public void A_scope_abandoned_without_a_verdict_is_recorded_as_failed()
    {
        var logger = NewLogger();

        try
        {
            using var scope = logger.Begin(TraceEvent.CategoryTool, "tool.explodes");
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
            // The exception is the caller's business; the row is ours.
        }

        var rows = Drain(logger);
        Assert.Equal(2, rows.Count);
        Assert.Equal(TraceEvent.StatusFailed, rows[1].Status);
        Assert.Contains("did not report an outcome", rows[1].ErrorMessage);
    }

    [Fact]
    public void A_verdict_is_recorded_once_and_dispose_does_not_overwrite_it()
    {
        var logger = NewLogger();

        using (var scope = logger.Begin(TraceEvent.CategoryTool, "tool.ok"))
        {
            scope.Complete();
            scope.Fail("this should be ignored");
        }

        var rows = Drain(logger);
        Assert.Equal(2, rows.Count);
        Assert.Equal(TraceEvent.StatusCompleted, rows[1].Status);
        Assert.Null(rows[1].ErrorMessage);
    }

    [Fact]
    public void Failure_carries_its_reason()
    {
        var logger = NewLogger();
        using (var scope = logger.Begin(TraceEvent.CategoryWorker, "worker.job.process"))
            scope.Fail("the workflow no longer exists");

        var rows = Drain(logger);
        Assert.Equal(TraceEvent.StatusFailed, rows[1].Status);
        Assert.Equal("the workflow no longer exists", rows[1].ErrorMessage);
    }

    // Automation has no signed-in user. Without the ambient actor these rows would be
    // the anonymous nulls the actor field exists to abolish.
    [Fact]
    public void Background_work_is_attributed_to_the_ambient_actor()
    {
        var logger = NewLogger();
        using (AuditActor.Use(AuditActor.WorkflowRunner))
            logger.Event(TraceEvent.CategoryWorker, "worker.job.process");

        Assert.Equal(AuditActor.WorkflowRunner, Assert.Single(Drain(logger)).Actor);
    }

    // An unserialisable payload must not take down the operation being traced. The row
    // still goes in, saying why it is bare.
    [Fact]
    public void Unserialisable_metadata_does_not_throw_and_does_not_lose_the_row()
    {
        var logger = NewLogger();
        var cyclic = new Cyclic();
        cyclic.Self = cyclic;

        logger.Event(TraceEvent.CategorySystem, "system.boot", cyclic);

        var row = Assert.Single(Drain(logger));
        Assert.Equal("system.boot", row.Action);
        Assert.Contains("could not be serialised", row.MetadataJson);
    }

    private sealed class Cyclic { public Cyclic? Self { get; set; } }

    [Fact]
    public void Oversized_metadata_and_errors_are_clipped()
    {
        var logger = NewLogger();
        logger.Event(TraceEvent.CategorySystem, "system.big",
            new { blob = new string('x', 20_000) },
            error: new string('e', 20_000));

        var row = Assert.Single(Drain(logger));
        Assert.True(row.MetadataJson!.Length <= 4096, $"metadata was {row.MetadataJson.Length}");
        Assert.True(row.ErrorMessage!.Length <= 1024, $"error was {row.ErrorMessage.Length}");
    }

    // ── the writer ────────────────────────────────────────────────────────

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"trace-{Guid.NewGuid()}").Options);

    // The pair collapses to a single stored row, which is what makes a screenful of
    // `started` rows meaningful: only work that has not finished leaves one.
    [Fact]
    public async Task A_start_and_its_completion_become_one_row()
    {
        using var db = NewDb();
        var logger = NewLogger();
        var scope = logger.Begin(TraceEvent.CategoryTool, "tool.thing");
        scope.Complete(new { ok = true });

        await WriteAllAsync(db, logger);

        var row = Assert.Single(db.TraceEvents);
        Assert.Equal(TraceEvent.StatusCompleted, row.Status);
        Assert.NotNull(row.DurationMs);
    }

    // The halves can land in different batches. The second must update the first, not
    // fail on a duplicate key or insert a second row.
    [Fact]
    public async Task A_completion_in_a_later_batch_updates_the_row_it_closes()
    {
        using var db = NewDb();
        var logger = NewLogger();

        var scope = logger.Begin(TraceEvent.CategoryWorker, "worker.job.process");
        await WriteAllAsync(db, logger);
        Assert.Equal(TraceEvent.StatusStarted, Assert.Single(db.TraceEvents).Status);

        scope.Complete();
        await WriteAllAsync(db, logger);

        var row = Assert.Single(db.TraceEvents);
        Assert.Equal(TraceEvent.StatusCompleted, row.Status);
    }

    // Mirrors TraceWriterHostedService.WriteAsync. Kept here rather than reaching into
    // the hosted service so the test does not have to start and stop a host to assert
    // on an upsert.
    private static async Task WriteAllAsync(AppDbContext db, TraceLogger logger)
    {
        var batch = Drain(logger);
        if (batch.Count == 0) return;

        var latest = batch.GroupBy(r => r.TraceEventId).ToDictionary(g => g.Key, g => g.Last());
        var ids = latest.Keys.ToList();
        var existing = await db.TraceEvents.Where(t => ids.Contains(t.TraceEventId)).ToListAsync();

        foreach (var row in existing)
        {
            var update = latest[row.TraceEventId];
            row.Status = update.Status;
            row.DurationMs = update.DurationMs;
            row.ErrorMessage = update.ErrorMessage;
            row.MetadataJson = update.MetadataJson;
            latest.Remove(row.TraceEventId);
        }

        if (latest.Count > 0) db.TraceEvents.AddRange(latest.Values);
        await db.SaveChangesAsync();
    }
}
