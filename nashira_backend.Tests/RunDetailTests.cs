using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;

namespace nashira_backend.Tests;

// The run detail behind the fleet runs page.
//
// What these guard: the page must be able to paint every card of a run without
// moving a single step payload, and a payload must arrive intact — or be dropped
// rather than break the page — when one card asks for it.
public class RunDetailTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"run-detail-{Guid.NewGuid()}").Options);

    private static Guid AddWorkflow(AppDbContext db, string name)
    {
        var id = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            WorkflowId = id, Name = name, Environment = "production",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static Guid AddRun(AppDbContext db, Guid workflowId, string? inputJson = null,
        string targetsJson = "[]")
    {
        var id = Guid.NewGuid();
        var started = DateTime.UtcNow.AddMinutes(-5);
        db.WorkflowRuns.Add(new WorkflowRun
        {
            WorkflowRunId = id, WorkflowId = workflowId,
            Environment = "production", Status = WorkflowRun.StatusFailed, FinalState = "rolled_back",
            NodeCount = 2, ChangedCount = 1, FailedCount = 1,
            StartedAt = started, FinishedAt = started.AddSeconds(42),
            Trigger = RunTrigger.Schedule,
            InputJson = inputJson, TargetDevicesJson = targetsJson,
            IsActive = true, CreatedAt = started, UpdatedAt = started,
        });
        db.SaveChanges();
        return id;
    }

    private static void AddStep(AppDbContext db, Guid runId, int seq, string result,
        string? output = null, string? input = null, string? logs = null, string? error = null)
    {
        var at = DateTime.UtcNow.AddMinutes(-4);
        db.StepRuns.Add(new StepRun
        {
            StepRunId = Guid.NewGuid(), WorkflowRunId = runId, NodeId = $"node-{seq}",
            Sequence = seq, Result = result, OutputJson = output, InputJson = input, Logs = logs,
            Error = error, Attempts = 1, StartedAt = at, FinishedAt = at.AddMilliseconds(1500),
            IsActive = true, CreatedAt = at, UpdatedAt = at,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Detail_carries_the_workflow_name_and_step_headers_with_sizes()
    {
        using var db = NewDb();
        var wf = AddWorkflow(db, "nightly-backup");
        var run = AddRun(db, wf, inputJson: "{\"site\":\"bog\"}", targetsJson: "[\"a\",\"b\"]");
        var big = "{\"devices\":[" + string.Join(",", Enumerable.Repeat("{\"ok\":true}", 5000)) + "]}";
        AddStep(db, run, 1, "no_change", output: "{\"ping\":true}", logs: "checked");
        AddStep(db, run, 0, "changed", output: big, input: "{\"cmd\":\"show\"}");

        var detail = (await new RunsController(db).RunDetail(run, CancellationToken.None)).Value!;

        Assert.Equal("nightly-backup", detail.WorkflowName);
        Assert.Equal(RunTrigger.Schedule, detail.Trigger);
        Assert.Equal(42, detail.DurationSeconds);
        Assert.Equal("bog", detail.Input!.Value.GetProperty("site").GetString());
        Assert.Equal(2, detail.TargetDevices.GetArrayLength());

        // Ordered by sequence, whatever order the rows were written in.
        Assert.Equal([0, 1], detail.Steps.Select(s => s.Sequence));

        var first = detail.Steps[0];
        Assert.Equal(big.Length, first.OutputChars);
        Assert.Equal("{\"cmd\":\"show\"}".Length, first.InputChars);
        Assert.Equal(0, first.LogsChars);
        Assert.Equal(1500, first.DurationMs);

        var second = detail.Steps[1];
        Assert.Equal("checked".Length, second.LogsChars);
        Assert.Equal(0, second.InputChars);
    }

    [Fact]
    public async Task Detail_survives_a_deleted_workflow()
    {
        using var db = NewDb();
        var run = AddRun(db, Guid.NewGuid());

        var detail = (await new RunsController(db).RunDetail(run, CancellationToken.None)).Value!;

        Assert.Equal("(deleted)", detail.WorkflowName);
        Assert.Empty(detail.Steps);
        Assert.Null(detail.Input);
    }

    [Fact]
    public async Task Unknown_run_is_not_found()
    {
        using var db = NewDb();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new RunsController(db).RunDetail(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Step_payload_returns_parsed_output_input_and_logs()
    {
        using var db = NewDb();
        var run = AddRun(db, AddWorkflow(db, "wf"));
        AddStep(db, run, 0, "changed", output: "{\"changed\":true}", input: "{\"host\":\"r1\"}", logs: "did it");

        var payload = (await new RunsController(db).StepPayload(run, 0, CancellationToken.None)).Value!;

        Assert.Equal("node-0", payload.NodeId);
        Assert.True(payload.Output!.Value.GetProperty("changed").GetBoolean());
        Assert.Equal("r1", payload.Input!.Value.GetProperty("host").GetString());
        Assert.Equal("did it", payload.Logs);
    }

    // A malformed fragment left by one node must not make the step unreadable: the
    // parts that do parse still come back, the bad one is dropped.
    [Fact]
    public async Task Step_payload_drops_malformed_json_but_keeps_the_rest()
    {
        using var db = NewDb();
        var run = AddRun(db, AddWorkflow(db, "wf"));
        AddStep(db, run, 0, "failed", output: "{not json", logs: "boom");

        var payload = (await new RunsController(db).StepPayload(run, 0, CancellationToken.None)).Value!;

        Assert.Null(payload.Output);
        Assert.Equal("boom", payload.Logs);
    }

    [Fact]
    public async Task Unknown_step_is_not_found()
    {
        using var db = NewDb();
        var run = AddRun(db, AddWorkflow(db, "wf"));
        AddStep(db, run, 0, "changed");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new RunsController(db).StepPayload(run, 7, CancellationToken.None));
    }
}
