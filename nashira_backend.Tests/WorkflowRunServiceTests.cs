using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Engine;
using nashira_backend.Services.DevicePools;
using nashira_backend.Services.Policy;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;
using RunTrigger = nashira_backend.Data.Models.RunTrigger;

namespace nashira_backend.Tests;

// End-to-end run persistence over the in-memory provider: the executor result is written
// as a WorkflowRun + StepRun rows with the right status/counts.
public class WorkflowRunServiceTests
{
    private sealed class FakeTenant : ICurrentUser
    {
        public Guid CompanyId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["operator"];
        public bool IsAuthenticated => true;
    }

    private sealed class NoAudit : IAuditLogger
    {
        public Task LogAsync(string entityType, Guid? entityId, string action, object? before = null, object? after = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeNodes(Dictionary<string, NodeOutcome> map) : INodeExecutor
    {
        public Task<NodeOutcome> ExecuteAsync(WorkflowNode node, CancellationToken ct) =>
            Task.FromResult(map.GetValueOrDefault(node.Id, new NodeOutcome(NodeResult.Changed)));
    }

    private static WorkflowRunService Service(AppDbContext db, ICurrentUser user, INodeExecutor nodes) =>
        new(db, user, nodes, new NoAudit(), new WorkflowExecutionScope(),
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            new PolicyEvaluator(db, NullLogger<PolicyEvaluator>.Instance),
            new DevicePoolResolver(db), NullLogger<WorkflowRunService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WorkflowEntity Workflow(Guid companyId) => new()
    {
        WorkflowId = Guid.NewGuid(),
        Name = "wf",
        SchemaHash = "H1",
        Environment = "qa",
        NodesJson = """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
        EdgesJson = """[{"source":"a","target":"b","type":"success"}]""",
        IsActive = true,
    };

    // A result word and an error code do not explain a failure. Everything the
    // executor learned on the way has to survive onto the row, or the only way to
    // find out what a step was handed is to run it again and watch.
    [Fact]
    public async Task Step_diagnostics_are_persisted()
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var startedAt = DateTime.UtcNow.AddSeconds(-5);
        var svc = Service(db, user, new FakeNodes(new()
        {
            ["a"] = new NodeOutcome(NodeResult.NoChange)
            {
                Logs = "checked 3 things, all already correct",
                Input = """{"host":"10.0.0.1"}""",
                Attempts = 1,
                StartedAt = startedAt,
                FinishedAt = startedAt.AddSeconds(1),
            },
            ["b"] = new NodeOutcome(NodeResult.Failed, "unreachable", true, """{"error":"timed out"}""")
            {
                Error = "10.0.0.2 did not answer within 5s",
                Attempts = 3,
                StartedAt = startedAt.AddSeconds(1),
                FinishedAt = startedAt.AddSeconds(4),
            },
        }));

        await svc.RunAsync(Workflow(user.CompanyId), null, [], CancellationToken.None);

        var steps = await db.StepRuns.OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal("checked 3 things, all already correct", steps[0].Logs);
        Assert.Equal("""{"host":"10.0.0.1"}""", steps[0].InputJson);
        Assert.Equal(1, steps[0].Attempts);
        Assert.NotNull(steps[0].StartedAt);
        Assert.NotNull(steps[0].FinishedAt);

        // The failure message in its own column, not dug out of the output payload.
        Assert.Equal("10.0.0.2 did not answer within 5s", steps[1].Error);
        Assert.Equal(3, steps[1].Attempts);
    }

    // The executor's default. A node that reached its handler and returned must never
    // record 0 attempts, because 0 is what the panel reads as "never ran" — and a step
    // claiming that beside its own output is a screen contradicting itself.
    [Fact]
    public async Task A_step_that_ran_never_records_zero_attempts()
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()
        {
            ["a"] = new NodeOutcome(NodeResult.NoChange),
            ["b"] = new NodeOutcome(NodeResult.Changed, Output: "{\"ok\":true}"),
        }));

        await svc.RunAsync(Workflow(user.CompanyId), null, [], CancellationToken.None);

        Assert.All(await db.StepRuns.ToListAsync(), step => Assert.True(step.Attempts >= 1));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("schedule")]
    [InlineData("git_webhook")]
    public async Task The_run_records_what_started_it(string trigger)
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()));

        var run = await svc.RunAsync(Workflow(user.CompanyId), null, [], CancellationToken.None, trigger);

        Assert.Equal(trigger, run.Trigger);
    }

    // An agent-started run must not be recorded as "a person did this". This used to be
    // guaranteed by a no-input/no-targets overload that defaulted the trigger — which is
    // exactly why the agent could not pass either, so the guarantee is now the caller's
    // and the assertion moves with it.
    [Fact]
    public async Task An_agent_run_does_not_report_itself_as_manual()
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()));

        var run = await svc.RunAsync(
            Workflow(user.CompanyId), null, [], CancellationToken.None, RunTrigger.Agent);

        Assert.Equal("agent", run.Trigger);
    }

    // The agent reaches the same entry point a person does, so it can supply the same
    // input. It could not before: its only overload passed null, so a workflow whose
    // nodes reference {{ input.X }} ran with that unresolved and nothing said so.
    [Fact]
    public async Task An_agent_run_carries_its_input_onto_the_row()
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()));
        var input = JsonDocument.Parse("""{"host":"10.0.0.9"}""").RootElement;

        var run = await svc.RunAsync(
            Workflow(user.CompanyId), input, [], CancellationToken.None, RunTrigger.Agent);

        Assert.Equal("""{"host":"10.0.0.9"}""", run.InputJson);
    }

    // Orchestration can fail before any step exists. Until this was recorded the
    // caller got an exception and the runs list showed nothing at all, so "I pressed
    // run and nothing happened" was true and had no trail to follow.
    [Fact]
    public async Task A_run_refused_before_it_began_is_still_recorded()
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()));

        var broken = Workflow(user.CompanyId);
        broken.NodesJson = "not json at all";

        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.RunAsync(broken, null, [], CancellationToken.None, "schedule"));

        var run = Assert.Single(await db.WorkflowRuns.ToListAsync());
        Assert.Equal("failed", run.Status);
        // Not "failed", which here means "stopped part-way and left changes behind".
        Assert.Equal("refused", run.FinalState);
        Assert.Equal(0, run.NodeCount);
        Assert.Equal("schedule", run.Trigger);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
        Assert.Empty(await db.StepRuns.ToListAsync());
    }

    // The refusal record must never replace the original failure: the caller needs the
    // policy denial, not a database error raised while writing about it.
    [Fact]
    public async Task The_original_failure_is_what_the_caller_sees()
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()));

        var broken = Workflow(user.CompanyId);
        broken.EdgesJson = "{ not an array }";

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.RunAsync(broken, null, [], CancellationToken.None));

        Assert.IsNotType<DbUpdateException>(ex);
    }

    // ---- Conditional edges over the run's own namespaces (templates/SPEC.md §9) ----
    //
    // The evaluator has always accepted an input payload and a run context; the
    // executor called it with neither, so every `{{ input.* }}` / `{{ run.* }}`
    // condition resolved unresolved and evaluated false. These go through the
    // service because the wiring, not the evaluator, is what was broken.

    // A workflow whose single edge is conditional: `b` runs only if it fires.
    private static WorkflowEntity ConditionalWorkflow(string condition) => new()
    {
        WorkflowId = Guid.NewGuid(),
        Name = "wf",
        SchemaHash = "H1",
        Environment = "qa",
        NodesJson = """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"s"}]""",
        EdgesJson = JsonSerializer.Serialize(new[]
        {
            new { source = "a", target = "b", type = "conditional", condition },
        }),
        IsActive = true,
    };

    private static async Task<bool> BranchRunsAsync(string condition, string? input = null)
    {
        var user = new FakeTenant();
        await using var db = NewDb();
        var svc = Service(db, user, new FakeNodes(new()));

        JsonElement? payload = input is null ? null : JsonDocument.Parse(input).RootElement.Clone();
        await svc.RunAsync(ConditionalWorkflow(condition), payload, [], CancellationToken.None);

        var branch = await db.StepRuns.SingleAsync(s => s.NodeId == "b");
        return branch.Result != NodeResult.Skipped;
    }

    // Branching on the run's own input is the case conditions exist for: one
    // definition serving a dry run and a real one.
    [Theory]
    [InlineData("""{"apply":true}""", true)]
    [InlineData("""{"apply":false}""", false)]
    public async Task A_condition_branches_on_the_run_input(string input, bool expected)
    {
        Assert.Equal(expected, await BranchRunsAsync("{{ input.apply }} == true", input));
    }

    // Same run context the step payloads read, so a condition and a payload can
    // never disagree about the run they belong to.
    [Theory]
    [InlineData("{{ run.environment }} == 'qa'", true)]
    [InlineData("{{ run.environment }} == 'production'", false)]
    public async Task A_condition_branches_on_the_run_namespace(string condition, bool expected)
    {
        Assert.Equal(expected, await BranchRunsAsync(condition));
    }

    // An edge fires once at DAG level after its source completes, so there is no
    // single current device even when the source fanned out: `{{ device.* }}` is
    // unresolved in a condition, and an unresolved comparison is false.
    [Fact]
    public async Task Device_in_a_condition_is_unresolved_and_therefore_false()
    {
        Assert.False(await BranchRunsAsync("{{ device.ip }} == '10.0.0.1'"));
    }

    // The reference grammar inside a condition is the payload's grammar, filters
    // included — otherwise an author writes a condition that reads correctly and
    // evaluates against nothing.
    [Fact]
    public async Task A_filter_applies_inside_a_condition_reference()
    {
        // truncate, not upper: the comparison is case-insensitive, so only a filter
        // that changes more than case proves the chain ran.
        Assert.True(await BranchRunsAsync(
            "{{ input.mode | truncate(3) }} == 'app'", """{"mode":"applying"}"""));
        Assert.True(await BranchRunsAsync(
            "{{ input.missing | default('none') }} == 'none'", """{"mode":"apply"}"""));
    }

    [Fact]
    public async Task Run_persists_run_and_steps()
    {
        var tenant = new FakeTenant();
        await using var db = NewDb();
        var svc = new WorkflowRunService(db, tenant, new FakeNodes(new()
        {
            ["a"] = new NodeOutcome(NodeResult.NoChange),
            ["b"] = new NodeOutcome(NodeResult.Changed),
        }), new NoAudit(), new WorkflowExecutionScope(),
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            new PolicyEvaluator(db, NullLogger<PolicyEvaluator>.Instance),
            new DevicePoolResolver(db), NullLogger<WorkflowRunService>.Instance);

        var run = await svc.RunAsync(Workflow(tenant.CompanyId), null, [], CancellationToken.None);

        Assert.Equal("completed", run.Status);
        Assert.Equal("completed", run.FinalState);
        Assert.Equal(2, run.NodeCount);
        Assert.Equal(1, run.ChangedCount);
        Assert.Equal(2, await db.StepRuns.CountAsync());
        Assert.Equal(1, await db.WorkflowRuns.CountAsync());
    }

    [Fact]
    public async Task Failed_node_makes_the_run_failed()
    {
        var tenant = new FakeTenant();
        await using var db = NewDb();
        var svc = new WorkflowRunService(db, tenant, new FakeNodes(new()
        {
            ["a"] = new NodeOutcome(NodeResult.Changed),
            ["b"] = new NodeOutcome(NodeResult.Failed, "BOOM"),
        }), new NoAudit(), new WorkflowExecutionScope(),
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            new PolicyEvaluator(db, NullLogger<PolicyEvaluator>.Instance),
            new DevicePoolResolver(db), NullLogger<WorkflowRunService>.Instance);

        var run = await svc.RunAsync(Workflow(tenant.CompanyId), null, [], CancellationToken.None);

        Assert.Equal("failed", run.Status);
        Assert.Equal(1, run.FailedCount);
        var steps = await db.StepRuns.OrderBy(s => s.Sequence).ToListAsync();
        Assert.Equal("BOOM", steps.Single(s => s.NodeId == "b").ErrorCode);
    }
}
