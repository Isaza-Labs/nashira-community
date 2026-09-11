using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Policy;

namespace nashira_backend.Tests;

// Promotion gates. A deny asks "is this forbidden?"; a gate asks "has this earned
// it yet?" — satisfiable by doing the work, so it is a precondition, and getting
// its arithmetic wrong either blocks legitimate promotions or waves through
// untested ones.
public class PolicyGateTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"gate-{Guid.NewGuid()}").Options);

    private static PolicyGate Gate(AppDbContext db) => new(db, NullLogger<PolicyGate>.Instance);

    private static readonly Guid Workflow = Guid.NewGuid();

    private static PolicyGateContext Context(string from = "qa", string to = "production") =>
        new("promote", from, to, Workflow);

    private static void AddPolicy(AppDbContext db, string rule, string name = "gate-policy")
    {
        db.Policies.Add(new Policy
        {
            PolicyId = Guid.NewGuid(),
            Name = name,
            RuleJson = rule,
            Enabled = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static void AddRuns(AppDbContext db, int count, string status = WorkflowRun.StatusCompleted,
        Guid? workflowId = null, string environment = "qa", int daysAgo = 0)
    {
        for (var i = 0; i < count; i++)
        {
            db.WorkflowRuns.Add(new WorkflowRun
            {
                WorkflowRunId = Guid.NewGuid(),
                WorkflowId = workflowId ?? Workflow,
                Environment = environment,
                Status = status,
                StartedAt = DateTime.UtcNow.AddDays(-daysAgo),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        db.SaveChanges();
    }

    private const string NeedsThreeRuns =
        """
        {"action":"gate","on":"promote","from":"qa","to":"production",
         "require":[{"type":"successful_runs","min":3}]}
        """;

    [Fact]
    public async Task With_no_policies_a_promotion_passes()
    {
        using var db = NewDb();
        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task A_gate_blocks_until_the_required_runs_exist()
    {
        using var db = NewDb();
        AddPolicy(db, NeedsThreeRuns);
        AddRuns(db, 2);

        var decision = await Gate(db).EvaluateAsync(Context(), CancellationToken.None);

        Assert.True(decision.Denied);
        Assert.Contains("needs 3 successful run(s), has 2", decision.Reason);
    }

    [Fact]
    public async Task A_satisfied_gate_allows_the_promotion()
    {
        using var db = NewDb();
        AddPolicy(db, NeedsThreeRuns);
        AddRuns(db, 3);

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    // A failed run is evidence the workflow does not work — the opposite of what a
    // gate asking for successful runs wants to see.
    [Fact]
    public async Task Failed_runs_do_not_count_towards_the_requirement()
    {
        using var db = NewDb();
        AddPolicy(db, NeedsThreeRuns);
        AddRuns(db, 5, status: WorkflowRun.StatusFailed);

        Assert.True((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task This_workflow_scope_ignores_another_workflows_runs()
    {
        using var db = NewDb();
        AddPolicy(db, NeedsThreeRuns);
        AddRuns(db, 5, workflowId: Guid.NewGuid());

        Assert.True((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task Any_workflow_scope_counts_runs_in_the_source_environment()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[{"type":"successful_runs","min":2,"scope":"any_workflow"}]}
            """);
        AddRuns(db, 2, workflowId: Guid.NewGuid(), environment: "qa");

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    // `within_days` narrows the count to a horizon. Without it a gate asking for
    // three clean runs is satisfied by three from a year ago, which is not what
    // anyone means by it.
    [Fact]
    public async Task Within_days_ignores_successes_older_than_the_window()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[{"type":"successful_runs","min":2,"within_days":7}]}
            """);
        AddRuns(db, 5, daysAgo: 30);

        var decision = await Gate(db).EvaluateAsync(Context(), CancellationToken.None);

        Assert.True(decision.Denied);
        Assert.Contains("needs 2 successful run(s) in the last 7 day(s), has 0", decision.Reason);
    }

    [Fact]
    public async Task Within_days_counts_successes_inside_the_window()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[{"type":"successful_runs","min":2,"within_days":7}]}
            """);
        AddRuns(db, 2, daysAgo: 1);
        AddRuns(db, 3, daysAgo: 90);

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    // 0 is the "at any time" sentinel the builder emits for an unset window, and it
    // has to mean the same thing as leaving the field out.
    [Fact]
    public async Task A_zero_window_counts_every_successful_run()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[{"type":"successful_runs","min":2,"within_days":0}]}
            """);
        AddRuns(db, 2, daysAgo: 400);

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task A_recency_requirement_rejects_a_stale_success()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[{"type":"last_successful_run_within","days":7}]}
            """);
        AddRuns(db, 1, daysAgo: 30);

        var decision = await Gate(db).EvaluateAsync(Context(), CancellationToken.None);

        Assert.True(decision.Denied);
        Assert.Contains("last 7 day(s)", decision.Reason);
    }

    [Fact]
    public async Task A_recency_requirement_accepts_a_recent_success()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[{"type":"last_successful_run_within","days":7}]}
            """);
        AddRuns(db, 1, daysAgo: 1);

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    // Discovering unmet requirements one promotion at a time is a bad way to learn
    // what the organisation expects.
    [Fact]
    public async Task Every_unmet_requirement_is_reported_at_once()
    {
        using var db = NewDb();
        AddPolicy(db, """
            {"action":"gate","require":[
                {"type":"successful_runs","min":3},
                {"type":"last_successful_run_within","days":7}]}
            """);

        var decision = await Gate(db).EvaluateAsync(Context(), CancellationToken.None);

        Assert.True(decision.Denied);
        Assert.Contains("needs 3 successful run(s)", decision.Reason);
        Assert.Contains("last 7 day(s)", decision.Reason);
    }

    [Fact]
    public async Task A_gate_for_a_different_transition_does_not_apply()
    {
        using var db = NewDb();
        AddPolicy(db, NeedsThreeRuns);

        // draft -> qa, but the gate names qa -> production.
        Assert.False((await Gate(db).EvaluateAsync(
            Context(from: "draft", to: "qa"), CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task A_deny_policy_is_ignored_by_the_gate_evaluator()
    {
        // Denies are evaluated at run time by PolicyEvaluator. Applying them here
        // too would block promotions on rules written about executions.
        using var db = NewDb();
        AddPolicy(db, """{"action":"deny","when":{"environment":["production"]}}""");

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    [Fact]
    public async Task A_disabled_gate_does_not_block()
    {
        using var db = NewDb();
        db.Policies.Add(new Policy
        {
            PolicyId = Guid.NewGuid(), Name = "off", RuleJson = NeedsThreeRuns,
            Enabled = false, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        Assert.False((await Gate(db).EvaluateAsync(Context(), CancellationToken.None)).Denied);
    }

    // Fail closed. A typo must not silently turn a gate into no gate while still
    // reading as a guardrail on the policy list.
    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"action":"gate"}""")]
    [InlineData("""{"action":"gate","require":[]}""")]
    [InlineData("""{"action":"gate","require":[{"type":"phase_of_the_moon"}]}""")]
    [InlineData("""{"action":"gate","require":[{"type":"successful_runs"}]}""")]
    [InlineData("""{"action":"gate","require":[{"type":"successful_runs","min":"three"}]}""")]
    [InlineData("""{"action":"gate","require":[{"type":"successful_runs","min":1,"scope":"galaxy"}]}""")]
    [InlineData("""{"action":"gate","require":[{"type":"last_successful_run_within"}]}""")]
    [InlineData("""{"action":"gate","require":"three runs"}""")]
    public async Task An_unevaluable_gate_refuses_and_names_the_policy(string rule)
    {
        using var db = NewDb();
        AddPolicy(db, rule, "broken-gate");
        AddRuns(db, 10);

        var decision = await Gate(db).EvaluateAsync(Context(), CancellationToken.None);

        Assert.True(decision.Denied);
        Assert.Equal("broken-gate", decision.PolicyName);
    }
}
