using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Fleet;
using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// Runs and schedules across every workflow.
//
// The bug these guard against is the one flow-weaver's runs page has and documents:
// filtering the current page in the browser while the pager still counts the whole
// server set, so the list says "3 results" beside a total of 412 and the reader
// believes the smaller number.
public class FleetViewTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fleet-{Guid.NewGuid()}").Options);

    private static RunsController NewController(AppDbContext db) => new(db);

    private static ListResponse<T> Body<T>(ActionResult<ListResponse<T>> result) =>
        result.Value ?? throw new InvalidOperationException("expected a list body");

    private static Guid AddWorkflow(AppDbContext db, string name, string env = "production")
    {
        var id = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            WorkflowId = id, Name = name, Environment = env,
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static void AddRun(AppDbContext db, Guid workflowId, string status,
        DateTime startedAt, DateTime? finishedAt = null, string finalState = "",
        string env = "production")
    {
        db.WorkflowRuns.Add(new WorkflowRun
        {
            WorkflowRunId = Guid.NewGuid(), WorkflowId = workflowId,
            Environment = env, Status = status, FinalState = finalState,
            StartedAt = startedAt, FinishedAt = finishedAt,
            IsActive = true, CreatedAt = startedAt, UpdatedAt = startedAt,
        });
        db.SaveChanges();
    }

    private static void AddTrigger(AppDbContext db, Guid workflowId, string name,
        string type = WorkflowTrigger.TypeCron, bool enabled = true, DateTime? nextRunAt = null)
    {
        db.WorkflowTriggers.Add(new WorkflowTrigger
        {
            WorkflowTriggerId = Guid.NewGuid(), WorkflowId = workflowId,
            Name = name, Type = type, Enabled = enabled, NextRunAt = nextRunAt,
            CronExpression = type == WorkflowTrigger.TypeCron ? "0 3 * * *" : null,
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ── runs ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Runs_span_every_workflow_newest_first()
    {
        using var db = NewDb();
        var a = AddWorkflow(db, "nightly-backup");
        var b = AddWorkflow(db, "config-audit");
        var now = DateTime.UtcNow;
        AddRun(db, a, WorkflowRun.StatusCompleted, now.AddHours(-2));
        AddRun(db, b, WorkflowRun.StatusFailed, now.AddHours(-1));

        var page = Body(await NewController(db).Runs());

        Assert.Equal(2, page.Total);
        Assert.Equal("config-audit", page.Items[0].WorkflowName);
        Assert.Equal("nightly-backup", page.Items[1].WorkflowName);
    }

    // The total must describe the filtered set, not the whole table. Otherwise the
    // count and the rows disagree and the pager offers pages that do not exist.
    [Fact]
    public async Task Filtering_narrows_the_total_not_just_the_page()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "wf");
        var now = DateTime.UtcNow;
        for (var i = 0; i < 9; i++) AddRun(db, w, WorkflowRun.StatusCompleted, now.AddMinutes(-i));
        AddRun(db, w, WorkflowRun.StatusFailed, now);

        var all = Body(await NewController(db).Runs());
        var failed = Body(await NewController(db).Runs(status: WorkflowRun.StatusFailed));

        Assert.Equal(10, all.Total);
        Assert.Equal(1, failed.Total);
        Assert.Single(failed.Items);
    }

    // A run whose workflow was deleted still happened. Dropping it would quietly
    // rewrite the record every time somebody tidied up.
    [Fact]
    public async Task A_run_whose_workflow_is_gone_stays_in_the_list()
    {
        using var db = NewDb();
        AddRun(db, Guid.NewGuid(), WorkflowRun.StatusFailed, DateTime.UtcNow);

        var page = Body(await NewController(db).Runs());

        Assert.Equal("(deleted)", Assert.Single(page.Items).WorkflowName);
    }

    [Fact]
    public async Task Runs_can_be_searched_by_workflow_name_regardless_of_case()
    {
        using var db = NewDb();
        var a = AddWorkflow(db, "Nightly-Backup");
        var b = AddWorkflow(db, "config-audit");
        AddRun(db, a, WorkflowRun.StatusCompleted, DateTime.UtcNow);
        AddRun(db, b, WorkflowRun.StatusCompleted, DateTime.UtcNow);

        var page = Body(await NewController(db).Runs(q: "nightly"));

        Assert.Equal(1, page.Total);
        Assert.Equal("Nightly-Backup", page.Items[0].WorkflowName);
    }

    // Null while it is still going, not zero: a zero would read as an instant run and
    // sort to the top of "fastest".
    [Fact]
    public async Task An_unfinished_run_reports_no_duration()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "wf");
        var now = DateTime.UtcNow;
        AddRun(db, w, WorkflowRun.StatusRunning, now.AddMinutes(-5));
        AddRun(db, w, WorkflowRun.StatusCompleted, now.AddMinutes(-20), finishedAt: now.AddMinutes(-18));

        var items = Body(await NewController(db).Runs()).Items;

        Assert.Null(items.Single(r => r.Status == WorkflowRun.StatusRunning).DurationSeconds);
        Assert.Equal(120, items.Single(r => r.Status == WorkflowRun.StatusCompleted).DurationSeconds);
    }

    [Fact]
    public async Task Runs_filter_by_environment_and_final_state()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "wf");
        var now = DateTime.UtcNow;
        AddRun(db, w, WorkflowRun.StatusFailed, now, finalState: "rolled_back", env: "qa");
        AddRun(db, w, WorkflowRun.StatusFailed, now, finalState: "failed", env: "production");

        Assert.Equal(1, Body(await NewController(db).Runs(environment: "qa")).Total);
        Assert.Equal(1, Body(await NewController(db).Runs(finalState: "failed")).Total);
    }

    // ── schedules ─────────────────────────────────────────────────────────

    // Soonest first, and never-firing last. Nulls sorting first would open the page on
    // everything that is not going to run.
    [Fact]
    public async Task Schedules_are_ordered_by_what_fires_next_with_the_dormant_last()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "wf");
        var now = DateTime.UtcNow;
        AddTrigger(db, w, "later", nextRunAt: now.AddHours(6));
        AddTrigger(db, w, "dormant", enabled: false, nextRunAt: null);
        AddTrigger(db, w, "soon", nextRunAt: now.AddMinutes(10));

        var items = Body(await NewController(db).Schedules()).Items;

        Assert.Equal("soon", items[0].Name);
        Assert.Equal("later", items[1].Name);
        Assert.Equal("dormant", items[2].Name);
    }

    // A cron whose next firing is in the past means the scheduler has not been round
    // to it. That is the one thing this page exists to surface.
    [Fact]
    public async Task A_trigger_whose_firing_is_in_the_past_is_flagged_overdue()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "wf");
        var now = DateTime.UtcNow;
        AddTrigger(db, w, "late", nextRunAt: now.AddHours(-1));
        AddTrigger(db, w, "fine", nextRunAt: now.AddHours(1));
        // Disabled: not overdue, because nothing is waiting for it.
        AddTrigger(db, w, "off", enabled: false, nextRunAt: now.AddHours(-1));

        var items = Body(await NewController(db).Schedules()).Items;

        Assert.True(items.Single(t => t.Name == "late").Overdue);
        Assert.False(items.Single(t => t.Name == "fine").Overdue);
        Assert.False(items.Single(t => t.Name == "off").Overdue);
    }

    [Fact]
    public async Task Schedules_filter_by_type_enabled_and_what_is_due_soon()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "wf");
        var now = DateTime.UtcNow;
        AddTrigger(db, w, "cron-soon", nextRunAt: now.AddMinutes(30));
        AddTrigger(db, w, "cron-far", nextRunAt: now.AddDays(3));
        AddTrigger(db, w, "hook", type: WorkflowTrigger.TypeWebhook, nextRunAt: null);
        AddTrigger(db, w, "off", enabled: false, nextRunAt: now.AddMinutes(5));

        Assert.Equal(3, Body(await NewController(db).Schedules(type: WorkflowTrigger.TypeCron)).Total);
        Assert.Equal(1, Body(await NewController(db).Schedules(type: WorkflowTrigger.TypeWebhook)).Total);
        Assert.Equal(3, Body(await NewController(db).Schedules(enabled: true)).Total);
        Assert.Equal(2, Body(await NewController(db).Schedules(dueWithinHours: 1)).Total);
    }

    [Fact]
    public async Task Schedules_carry_the_workflow_they_belong_to()
    {
        using var db = NewDb();
        var w = AddWorkflow(db, "nightly-backup", env: "production");
        AddTrigger(db, w, "3am", nextRunAt: DateTime.UtcNow.AddHours(1));

        var item = Assert.Single(Body(await NewController(db).Schedules()).Items);

        Assert.Equal("nightly-backup", item.WorkflowName);
        Assert.Equal("production", item.WorkflowEnvironment);
        Assert.Equal(w, item.WorkflowId);
    }

    [Fact]
    public async Task Schedules_can_be_searched_by_trigger_or_workflow_name()
    {
        using var db = NewDb();
        var a = AddWorkflow(db, "nightly-backup");
        var b = AddWorkflow(db, "config-audit");
        AddTrigger(db, a, "every-morning");
        AddTrigger(db, b, "on-push", type: WorkflowTrigger.TypeWebhook);

        Assert.Equal(1, Body(await NewController(db).Schedules(q: "nightly")).Total);
        Assert.Equal(1, Body(await NewController(db).Schedules(q: "on-push")).Total);
    }
}
