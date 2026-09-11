using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Traces;
using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// The filters on the trace screen.
//
// Each one exists because a question was being asked badly. The three covered here
// were all answerable-in-principle before and useless in practice: a prefix match on
// an action nobody remembers from the left; a request id when what the asker has is a
// name; a slower-than that returned the newest rows rather than the slow ones.
public class TraceFilterTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"traces-{Guid.NewGuid()}").Options);

    private static Guid AddTrace(
        AppDbContext db,
        string action = "workflow.run",
        string category = "workflow",
        string status = TraceEvent.StatusCompleted,
        int? durationMs = 100,
        Guid? userId = null,
        string? actor = null,
        string? requestId = null,
        string? error = null,
        string? metadataJson = null,
        DateTime? at = null)
    {
        var id = Guid.NewGuid();
        var when = at ?? DateTime.UtcNow;
        db.TraceEvents.Add(new TraceEvent
        {
            TraceEventId = id,
            Action = action,
            Category = category,
            Status = status,
            DurationMs = durationMs,
            UserId = userId,
            Actor = actor,
            RequestId = requestId,
            ErrorMessage = error,
            MetadataJson = metadataJson,
            At = when,
            IsActive = true,
            CreatedAt = when,
            UpdatedAt = when,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid AddUser(AppDbContext db, string username, string email)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            UserId = id,
            Username = username,
            Email = email,
            PasswordHash = "x",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static List<TraceEventDto> Rows(ActionResult<TraceListResponse> result) =>
        (result.Value ?? throw new InvalidOperationException("no body")).Items;

    // Not a prefix. The fragment people arrive with is in the middle of the name.
    [Fact]
    public async Task Search_matches_anywhere_in_the_action()
    {
        using var db = NewDb();
        AddTrace(db, action: "worker.job.claim");
        AddTrace(db, action: "ai.chat.stream");

        var rows = Rows(await new TraceEventsController(db).List(search: "job"));

        Assert.Equal("worker.job.claim", Assert.Single(rows).Action);
    }

    [Fact]
    public async Task Search_covers_error_request_id_and_metadata()
    {
        using var db = NewDb();
        AddTrace(db, action: "a.one", error: "upstream timed out");
        AddTrace(db, action: "a.two", requestId: "0HN7ABC");
        AddTrace(db, action: "a.three", metadataJson: "{\"deviceId\":\"router-42\"}");
        AddTrace(db, action: "a.four");

        var c = new TraceEventsController(db);

        Assert.Equal("a.one", Assert.Single(Rows(await c.List(search: "TIMED OUT"))).Action);
        Assert.Equal("a.two", Assert.Single(Rows(await c.List(search: "0hn7"))).Action);
        Assert.Equal("a.three", Assert.Single(Rows(await c.List(search: "router-42"))).Action);
    }

    // A % in the box is a character that was typed, not a wildcard that matches
    // everything while looking like a filter.
    [Fact]
    public async Task Search_treats_wildcard_characters_literally()
    {
        using var db = NewDb();
        AddTrace(db, action: "cpu.report", error: "at 90% of budget");
        AddTrace(db, action: "mem.report", error: "within budget");

        var rows = Rows(await new TraceEventsController(db).List(search: "90%"));

        Assert.Equal("cpu.report", Assert.Single(rows).Action);
    }

    [Fact]
    public async Task User_matches_id_username_or_email()
    {
        using var db = NewDb();
        var ana = AddUser(db, "ana", "ana@example.com");
        AddUser(db, "bob", "bob@example.com");
        AddTrace(db, action: "hers", userId: ana);
        AddTrace(db, action: "his", userId: Guid.NewGuid());

        var c = new TraceEventsController(db);

        Assert.Equal("hers", Assert.Single(Rows(await c.List(user: ana.ToString()))).Action);
        Assert.Equal("hers", Assert.Single(Rows(await c.List(user: "ana"))).Action);
        Assert.Equal("hers", Assert.Single(Rows(await c.List(user: "ANA@example.com"))).Action);
        Assert.Empty(Rows(await c.List(user: "nobody")));
    }

    // Work with no signed-in user behind it — the scheduler, retention, the boot
    // sequence — is only reachable through the actor string. Leaving it out would make
    // the filter silently unable to answer for half the table.
    [Fact]
    public async Task User_matches_the_actor_of_unattended_work()
    {
        using var db = NewDb();
        AddTrace(db, action: "cron.fire", actor: "scheduler");
        AddTrace(db, action: "chat.send", actor: "ana");

        var rows = Rows(await new TraceEventsController(db).List(user: "sched"));

        Assert.Equal("cron.fire", Assert.Single(rows).Action);
    }

    [Fact]
    public async Task Slower_than_excludes_rows_still_in_flight()
    {
        using var db = NewDb();
        AddTrace(db, action: "done", durationMs: 5_000);
        AddTrace(db, action: "hung", status: TraceEvent.StatusStarted, durationMs: null);

        var rows = Rows(await new TraceEventsController(db).List(minDurationMs: 1_000));

        Assert.Equal("done", Assert.Single(rows).Action);
    }

    // The bug this pins: with a page smaller than the match set, a time-ordered query
    // returns the newest qualifying rows, which are almost never the slowest ones —
    // so the filter appeared not to work at all.
    [Fact]
    public async Task Slower_than_returns_the_slowest_first_not_the_newest()
    {
        using var db = NewDb();
        AddTrace(db, action: "slowest", durationMs: 9_000, at: DateTime.UtcNow.AddMinutes(-30));
        AddTrace(db, action: "middling", durationMs: 4_000, at: DateTime.UtcNow.AddMinutes(-15));
        AddTrace(db, action: "newest", durationMs: 2_500, at: DateTime.UtcNow);

        var result = await new TraceEventsController(db).List(minDurationMs: 2_000, limit: 2);

        Assert.Equal(new[] { "slowest", "middling" }, Rows(result).Select(r => r.Action));
        Assert.Equal("duration", result.Value!.SortedBy);
        // The count is over the whole match set, not the page.
        Assert.Equal(3, result.Value.Total);
    }

    // Without the filter the screen is a live tail, and newest-first is the point.
    [Fact]
    public async Task Without_slower_than_the_order_is_still_newest_first()
    {
        using var db = NewDb();
        AddTrace(db, action: "older", durationMs: 9_000, at: DateTime.UtcNow.AddMinutes(-30));
        AddTrace(db, action: "newer", durationMs: 10, at: DateTime.UtcNow);

        var result = await new TraceEventsController(db).List();

        Assert.Equal(new[] { "newer", "older" }, Rows(result).Select(r => r.Action));
        Assert.Equal("at", result.Value!.SortedBy);
    }

    // The list must answer over the same window the summary cards report, or the two
    // disagree on screen with nothing to explain why.
    [Fact]
    public async Task From_bounds_the_list_to_the_window()
    {
        using var db = NewDb();
        AddTrace(db, action: "ancient", at: DateTime.UtcNow.AddDays(-3));
        AddTrace(db, action: "recent", at: DateTime.UtcNow);

        var rows = Rows(await new TraceEventsController(db)
            .List(from: DateTime.UtcNow.AddHours(-1)));

        Assert.Equal("recent", Assert.Single(rows).Action);
    }

    // Filters AND together: the screen promises a conjunction, and a search that
    // widened a category would quietly break that promise.
    [Fact]
    public async Task Filters_and_together()
    {
        using var db = NewDb();
        AddTrace(db, action: "worker.job.claim", category: "worker", durationMs: 5_000);
        AddTrace(db, action: "worker.job.finish", category: "worker", durationMs: 10);
        AddTrace(db, action: "ai.job.plan", category: "ai", durationMs: 5_000);

        var rows = Rows(await new TraceEventsController(db)
            .List(search: "job", category: "worker", minDurationMs: 1_000));

        Assert.Equal("worker.job.claim", Assert.Single(rows).Action);
    }
}
