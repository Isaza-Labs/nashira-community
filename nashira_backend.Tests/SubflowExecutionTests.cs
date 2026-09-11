using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.DevicePools;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Policy;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using DeviceEntity = nashira_backend.Data.Models.Device;
using RunEntity = nashira_backend.Data.Models.WorkflowRun;
using RunTrigger = nashira_backend.Data.Models.RunTrigger;
using SnippetEntity = nashira_backend.Data.Models.Snippet;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

// A `subflow` node runs another workflow (execution/SPEC.md §5).
//
// These drive the REAL path end to end — a container, a nested DI scope per child, real
// WorkflowRun and StepRun rows — rather than a stubbed runner, because almost every
// property §5 fixes is about what crosses the boundary between two runs: which input
// arrives, which devices and environment the child inherits, which error comes back, and
// which idempotency tier the parent inherits from what the child actually did. A fake
// runner would assert that the parent asked for the right thing and prove nothing about
// what happened.
public class SubflowExecutionTests
{
    // ─── graph builders ───────────────────────────────────────────────────────
    //
    // Plain concatenation rather than raw string literals: these graphs are mostly
    // braces, and counting `$` sigils against them is a way to spend an afternoon.

    private static string Q(object v) => "\"" + v + "\"";

    private static string StartNode(string id) =>
        "{" + Q("id") + ":" + Q(id) + "," + Q("snippet_id") + ":" + Q("__start__") + "}";

    private static string EndNode(string id) =>
        "{" + Q("id") + ":" + Q(id) + "," + Q("snippet_id") + ":" + Q("__end__") + "}";

    private static string TaskNode(string id, Guid snippetId) =>
        "{" + Q("id") + ":" + Q(id) + "," + Q("snippet_id") + ":" + Q(snippetId) + "}";

    /// <param name="extraOverrides">
    /// Raw JSON members, each with its leading comma — the child's input, in the form an
    /// author writes it beside <c>subflow_workflow_id</c>.
    /// </param>
    private static string SubflowNode(string id, object? childId, string extraOverrides = "")
    {
        var idPart = childId is null ? "" : Q("subflow_workflow_id") + ":" + Q(childId);
        var extra = idPart.Length == 0 && extraOverrides.StartsWith(',')
            ? extraOverrides[1..]
            : extraOverrides;
        return "{" + Q("id") + ":" + Q(id) + "," + Q("snippet_id") + ":" + Q("subflow")
            + "," + Q("type") + ":" + Q("subflow")
            + "," + Q("config_overrides") + ":{" + idPart + extra + "}}";
    }

    private static string Nodes(params string[] nodes) => "[" + string.Join(",", nodes) + "]";

    // Every graph here is a chain, so the edges are boilerplate.
    private static string Chain(params string[] nodeIds) => JsonSerializer.Serialize(
        nodeIds.Zip(nodeIds.Skip(1), (a, b) => new { source = a, target = b, type = "success" }));

    private const string NoEdges = "[]";

    // ─── harness ──────────────────────────────────────────────────────────────

    private sealed class FakeUser : ICurrentUser
    {
        public Guid CompanyId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["operator"];
        public bool IsAuthenticated => true;
    }

    private sealed class NoAudit : IAuditLogger
    {
        public Task LogAsync(
            string entityType, Guid? entityId, string action, object? before = null, object? after = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    // One snippet type, one fixed verdict. `DefaultIdempotency` is the handler floor the
    // tier resolution starts from, which is what the rollback assertions turn on.
    private sealed class StubHandler(
        string type, IdempotencyKind tier, bool success, StepChange changed, string? error) : ISnippetHandler
    {
        public string Type => type;
        public IdempotencyKind DefaultIdempotency => tier;

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct) =>
            Task.FromResult(success
                ? SnippetResult.Ok(new { ran = type, saw = request.Input }, changed)
                : SnippetResult.Fail(error ?? "stub failure", "stub_failed"));
    }

    // Cancels the run's token from inside a node and then returns normally.
    //
    // Returning rather than throwing is the whole point: AttemptAsync catches every
    // exception a handler raises and turns it into a failed step, so a handler that threw
    // OperationCanceledException would prove the opposite of what this is for. Cancelling
    // and stepping aside leaves the token to be observed where the engine checks it, which
    // is what a real handler aborted mid-flight does.
    private sealed class TripHandler(CancellationTokenSource cts) : ISnippetHandler
    {
        public string Type => "trip";
        public IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

        /// <summary>Whether the child actually got this far — i.e. the trip was mid-run.</summary>
        public bool Ran { get; private set; }

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            Ran = true;
            cts.Cancel();
            return Task.FromResult(SnippetResult.Ok(new { ran = "trip" }, StepChange.Unchanged));
        }
    }

    // Counts its own executions, so a node that must never run can say so.
    private sealed class CountingHandler : ISnippetHandler
    {
        public string Type => "count";
        public IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

        public int Runs { get; private set; }

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(SnippetResult.Ok(new { ran = "count" }, StepChange.Unchanged));
        }
    }

    private sealed class StubRegistry(IReadOnlyList<ISnippetHandler> handlers) : ISnippetHandlerRegistry
    {
        private readonly Dictionary<string, ISnippetHandler> _byType =
            handlers.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> KnownTypes => _byType.Keys;

        public ISnippetHandler? Resolve(string type, IServiceProvider scope) =>
            _byType.GetValueOrDefault(type);
    }

    // The four handler types the graphs below draw on.
    private static ISnippetHandler[] Handlers() =>
    [
        // A read: idempotent, and it says so — nothing to roll back.
        new StubHandler("probe", IdempotencyKind.Idempotent, success: true, changed: StepChange.Unchanged, null),
        // A reversible mutation: it belongs in a rollback plan.
        new StubHandler("tweak", IdempotencyKind.RequiresCompensation, success: true, changed: StepChange.Changed, null),
        // An email, morally: done is done.
        new StubHandler("shout", IdempotencyKind.NonReversible, success: true, changed: StepChange.Changed, null),
        // It fails, so nothing was changed — and the factory's own Fail path says so.
        new StubHandler("boom", IdempotencyKind.Idempotent, success: false, changed: StepChange.Unchanged,
            "the child could not reach 10.0.0.9"),
    ];

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;

        /// <param name="extra">
        /// Handlers beyond the four fixed stubs — a test that needs a node to do something
        /// the stubs cannot (cancel the run, count its own calls) registers it here.
        /// </param>
        public Harness(params ISnippetHandler[] extra)
        {
            var name = $"subflow-{Guid.NewGuid()}";
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<ICurrentUser>(User);
            services.AddSingleton<IAuditLogger, NoAudit>();
            services.AddSingleton<ISnippetHandlerRegistry>(new StubRegistry([.. Handlers(), .. extra]));
            services.AddScoped<WorkflowExecutionScope>();
            services.AddScoped<IVariableResolver, VariableResolver>();
            services.AddScoped<IConditionEvaluator, ConditionEvaluator>();
            services.AddScoped<IPolicyEvaluator, PolicyEvaluator>();
            services.AddScoped<IDevicePoolResolver, DevicePoolResolver>();
            services.AddScoped<SnippetNodeExecutor>();
            services.AddScoped<INodeExecutor>(sp => sp.GetRequiredService<SnippetNodeExecutor>());
            // The registration under test: a subflow node opens the child its own scope.
            services.AddScoped<ISubflowRunner, SubflowRunner>();
            services.AddScoped<WorkflowRunService>();
            _root = services.BuildServiceProvider();
        }

        public FakeUser User { get; } = new();

        // A scope of its own, the way a request would get one. The parent run and the
        // seeding both take one; the children open theirs through SubflowRunner.
        public IServiceScope Scope() => _root.CreateScope();

        public async Task SeedAsync(Action<AppDbContext> seed)
        {
            using var scope = Scope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            seed(db);
            await db.SaveChangesAsync();
        }

        public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
        {
            using var scope = Scope();
            return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async Task<(RunEntity Run, WorkflowRunResult Result)> RunAsync(
            Guid workflowId, string? input = null, IReadOnlyList<Guid>? targets = null,
            CancellationToken ct = default)
        {
            using var scope = Scope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var wf = await db.Workflows.FirstAsync(w => w.WorkflowId == workflowId);
            JsonElement? payload = input is null ? null : JsonDocument.Parse(input).RootElement.Clone();
            return await sp.GetRequiredService<WorkflowRunService>()
                .RunDetailedAsync(wf, payload, targets ?? [], ct);
        }

        // The STATIC answer — what GET /workflows/{id}/plan scores this graph at —
        // assembled exactly the way WorkflowController.Plan assembles it.
        public async Task<NodeTiers> PlanTiersAsync(Guid workflowId)
        {
            using var scope = Scope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var wf = await db.Workflows.AsNoTracking().FirstAsync(w => w.WorkflowId == workflowId);
            using var nodes = JsonDocument.Parse(wf.NodesJson);
            using var edges = JsonDocument.Parse(wf.EdgesJson);
            var dag = Dag.Parse(nodes.RootElement, edges.RootElement);
            var resolver = new NodeTierResolver(db, sp.GetRequiredService<ISnippetHandlerRegistry>(), sp);
            return await resolver.ResolveAsync(dag, workflowId, CancellationToken.None);
        }

        public void Dispose() => _root.Dispose();
    }

    private static SnippetEntity Snippet(string name, string type) => new()
    {
        SnippetId = Guid.NewGuid(), Name = name, Slug = name, Type = type, IsActive = true,
    };

    private static WorkflowEntity Workflow(Guid id, string name, string nodes, string edges) => new()
    {
        WorkflowId = id,
        Name = name,
        Environment = WorkflowEntity.EnvQa,
        SchemaHash = "H",
        NodesJson = nodes,
        EdgesJson = edges,
        IsActive = true,
    };

    private static NodeExecution Step(WorkflowRunResult result, string nodeId) =>
        result.Steps.Single(s => s.NodeId == nodeId);

    private static JsonElement Output(WorkflowRunResult result, string nodeId) =>
        JsonDocument.Parse(Step(result, nodeId).Output!).RootElement.Clone();

    // ─── the child reference ──────────────────────────────────────────────────

    [Fact]
    public async Task A_subflow_naming_a_workflow_that_does_not_exist_fails_with_subflow_missing()
    {
        using var h = new Harness();
        var parent = Guid.NewGuid();
        var ghost = Guid.NewGuid();
        await h.SeedAsync(db => db.Workflows.Add(Workflow(
            parent, "parent",
            Nodes(StartNode("start"), SubflowNode("sub", ghost)),
            Chain("start", "sub"))));

        var (run, result) = await h.RunAsync(parent);

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("subflow_missing", step.ErrorCode);
        Assert.Contains(ghost.ToString(), step.Error);
        Assert.Equal("failed", run.Status);
        // Nothing was started: the only run is the parent's.
        Assert.Equal(1, await h.ReadAsync(db => db.WorkflowRuns.CountAsync()));
    }

    [Fact]
    public async Task A_subflow_whose_id_is_not_a_guid_fails_with_subflow_missing()
    {
        using var h = new Harness();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db => db.Workflows.Add(Workflow(
            parent, "parent",
            Nodes(StartNode("start"), SubflowNode("sub", "the-other-one")),
            Chain("start", "sub"))));

        var (_, result) = await h.RunAsync(parent);

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("subflow_missing", step.ErrorCode);
    }

    // No id at all. A node with no child to run is not "a subflow with no input" — and
    // it must not be `skipped`, because a skipped node stops every `success` edge out of
    // it and the downstream half of the graph would silently never run.
    [Fact]
    public async Task A_subflow_with_no_workflow_id_fails_rather_than_skipping_the_graph()
    {
        using var h = new Harness();
        var probe = Snippet("probe", "probe");
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.Add(probe);
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(SubflowNode("sub", null), TaskNode("after", probe.SnippetId)),
                Chain("sub", "after")));
        });

        var (run, result) = await h.RunAsync(parent);

        Assert.Equal("subflow_missing", Step(result, "sub").ErrorCode);
        Assert.Equal("failed", run.Status);
        // The walk stopped rather than reporting `completed` over a graph that did not run.
        Assert.Equal(NodeResult.Skipped, Step(result, "after").Result);
    }

    // ─── cycles ───────────────────────────────────────────────────────────────

    // The direct case: a workflow whose subflow node names itself.
    [Fact]
    public async Task A_workflow_that_calls_itself_fails_with_subflow_cycle()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        await h.SeedAsync(db => db.Workflows.Add(Workflow(
            a, "loop", Nodes(StartNode("start"), SubflowNode("sub", a)), Chain("start", "sub"))));

        var (_, result) = await h.RunAsync(a);

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("subflow_cycle", step.ErrorCode);
        Assert.Contains("'loop'", step.Error);
        Assert.Equal(1, await h.ReadAsync(db => db.WorkflowRuns.CountAsync()));
    }

    // The transitive case, and the reason the guard walks the stored graph rather than
    // only the ancestry: from inside A's run, A → B → A is invisible until B has already
    // run — and §5 says the step fails BEFORE anything runs. So B must never start.
    [Fact]
    public async Task A_transitive_cycle_fails_before_the_child_run_is_created()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Workflows.Add(Workflow(
                a, "A", Nodes(StartNode("start"), SubflowNode("sub", b)), Chain("start", "sub")));
            db.Workflows.Add(Workflow(
                b, "B", Nodes(StartNode("start"), SubflowNode("sub", a)), Chain("start", "sub")));
        });

        var (_, result) = await h.RunAsync(a);

        var step = Step(result, "sub");
        Assert.Equal("subflow_cycle", step.ErrorCode);
        // The message names the loop, in order — "a cycle was detected" is not actionable.
        Assert.Contains("'A' → 'B' → 'A'", step.Error);
        Assert.Equal(1, await h.ReadAsync(db => db.WorkflowRuns.CountAsync()));
    }

    // Two branches calling the same shared subflow is a diamond, not a loop. A guard
    // that reported it would make the most ordinary form of reuse unrunnable.
    [Fact]
    public async Task A_shared_child_reached_twice_is_not_a_cycle()
    {
        using var h = new Harness();
        var probe = Snippet("probe", "probe");
        var shared = Guid.NewGuid();
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.Add(probe);
            db.Workflows.Add(Workflow(shared, "shared", Nodes(TaskNode("work", probe.SnippetId)), NoEdges));
            db.Workflows.Add(Workflow(left, "left", Nodes(SubflowNode("sub", shared)), NoEdges));
            db.Workflows.Add(Workflow(right, "right", Nodes(SubflowNode("sub", shared)), NoEdges));
            db.Workflows.Add(Workflow(
                parent, "parent", Nodes(SubflowNode("l", left), SubflowNode("r", right)), NoEdges));
        });

        var (run, result) = await h.RunAsync(parent);

        Assert.Equal("completed", run.Status);
        Assert.All(result.Steps, s => Assert.NotEqual(NodeResult.Failed, s.Result));
        // parent + left + right + shared twice.
        Assert.Equal(5, await h.ReadAsync(db => db.WorkflowRuns.CountAsync()));
    }

    // ─── depth ────────────────────────────────────────────────────────────────

    // Ten workflows in a chain. The root is depth 0, so runs exist down to depth 8 and
    // the node that would make a ninth refuses. The cap is not decoration: each level is
    // a nested synchronous run holding a scope and a DbContext open.
    [Fact]
    public async Task Subflow_nesting_is_capped_at_eight_levels()
    {
        using var h = new Harness();
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        await h.SeedAsync(db =>
        {
            for (var i = 0; i < ids.Length; i++)
                db.Workflows.Add(Workflow(
                    ids[i], $"w{i}",
                    i == ids.Length - 1
                        ? Nodes(EndNode("leaf"))
                        : Nodes(SubflowNode("sub", ids[i + 1])),
                    NoEdges));
        });

        var (run, result) = await h.RunAsync(ids[0]);

        Assert.Equal("failed", run.Status);
        // The root's own step reports the child's failure, not the depth breach — the
        // breach happened eight levels down.
        Assert.Equal("subflow_failed", Step(result, "sub").ErrorCode);

        var runs = await h.ReadAsync(db => db.WorkflowRuns.ToListAsync());
        Assert.Equal(SubflowLimits.MaxDepth + 1, runs.Count);
        // w9 never ran; w8's node is the one that refused.
        Assert.DoesNotContain(runs, r => r.WorkflowId == ids[9]);
        var deepest = runs.Single(r => r.WorkflowId == ids[8]);
        var breach = await h.ReadAsync(db => db.StepRuns
            .SingleAsync(s => s.WorkflowRunId == deepest.WorkflowRunId));
        Assert.Equal("subflow_depth_exceeded", breach.ErrorCode);
    }

    // ─── the child's input ────────────────────────────────────────────────────

    // §5: the parent run's input, shallow-merged with the node's OTHER config_overrides
    // keys, config_overrides winning. `subflow_workflow_id` is not input — it names the
    // child — and must not arrive in the child's `{{ input.* }}` namespace under a name
    // that reads like configuration.
    [Fact]
    public async Task The_child_input_is_the_run_input_overridden_by_config_overrides()
    {
        using var h = new Harness();
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Workflows.Add(Workflow(child, "child", Nodes(EndNode("leaf")), NoEdges));
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(SubflowNode("sub", child, ",\"site\":\"bogota\",\"depth\":2")),
                NoEdges));
        });

        await h.RunAsync(parent, """{"site":"medellin","keep":"me","depth":1}""");

        var childRun = await h.ReadAsync(db => db.WorkflowRuns.SingleAsync(r => r.WorkflowId == child));
        using var input = JsonDocument.Parse(childRun.InputJson!);
        // config_overrides wins…
        Assert.Equal("bogota", input.RootElement.GetProperty("site").GetString());
        Assert.Equal(2, input.RootElement.GetProperty("depth").GetInt32());
        // …and the keys it does not mention survive.
        Assert.Equal("me", input.RootElement.GetProperty("keep").GetString());
        Assert.False(input.RootElement.TryGetProperty("subflow_workflow_id", out _));
    }

    // The merge is resolved in the PARENT's context, and a `{{ … }}` left standing after
    // that fails the step. It must not travel: it would become the child run's input and
    // from there the recorded input of every step the child ran, where nobody would
    // connect it back to the node that wrote it.
    [Fact]
    public async Task A_residual_template_in_the_child_input_fails_with_unresolved_template()
    {
        using var h = new Harness();
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Workflows.Add(Workflow(child, "child", Nodes(EndNode("leaf")), NoEdges));
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(SubflowNode("sub", child, ",\"site\":\"{{ steps.nowhere.output.site }}\"")),
                NoEdges));
        });

        var (_, result) = await h.RunAsync(parent, """{"other":1}""");

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("unresolved_template", step.ErrorCode);
        // The child never started.
        Assert.Equal(0, await h.ReadAsync(db => db.WorkflowRuns.CountAsync(r => r.WorkflowId == child)));
    }

    // A reference the parent CAN resolve travels resolved, not literal.
    [Fact]
    public async Task The_child_input_is_resolved_against_the_parent_run()
    {
        using var h = new Harness();
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Workflows.Add(Workflow(child, "child", Nodes(EndNode("leaf")), NoEdges));
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(SubflowNode("sub", child, ",\"where\":\"{{ input.site }}-edge\"")),
                NoEdges));
        });

        await h.RunAsync(parent, """{"site":"cali"}""");

        var childRun = await h.ReadAsync(db => db.WorkflowRuns.SingleAsync(r => r.WorkflowId == child));
        using var input = JsonDocument.Parse(childRun.InputJson!);
        Assert.Equal("cali-edge", input.RootElement.GetProperty("where").GetString());
    }

    // ─── the child as a real run ──────────────────────────────────────────────

    [Fact]
    public async Task The_child_is_a_real_run_linked_both_ways()
    {
        using var h = new Harness();
        var probe = Snippet("probe", "probe");
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.Add(probe);
            db.Workflows.Add(Workflow(child, "child", Nodes(TaskNode("work", probe.SnippetId)), NoEdges));
            db.Workflows.Add(Workflow(parent, "parent", Nodes(SubflowNode("sub", child)), NoEdges));
        });

        var (parentRun, result) = await h.RunAsync(parent);

        var childRun = await h.ReadAsync(db => db.WorkflowRuns.SingleAsync(r => r.WorkflowId == child));
        Assert.Equal(parentRun.WorkflowRunId, childRun.ParentRunId);
        Assert.Equal(RunTrigger.Subflow, childRun.Trigger);
        Assert.Null(parentRun.ParentRunId);
        Assert.Equal(RunTrigger.Manual, parentRun.Trigger);

        var step = await h.ReadAsync(db => db.StepRuns
            .SingleAsync(s => s.WorkflowRunId == parentRun.WorkflowRunId && s.NodeId == "sub"));
        Assert.Equal(childRun.WorkflowRunId, step.ChildRunId);

        // §5's output shape, so a downstream template can address the child's steps.
        var output = Output(result, "sub");
        Assert.Equal(childRun.WorkflowRunId.ToString(), output.GetProperty("run_id").GetString());
        Assert.Equal("completed", output.GetProperty("status").GetString());
        Assert.Equal("completed", output.GetProperty("final_state").GetString());
        Assert.Equal("probe", output.GetProperty("steps").GetProperty("work").GetProperty("ran").GetString());
    }

    // The child does the parent's work on the parent's devices, in the parent's
    // environment — the child row here is a DRAFT, and a qa run that quietly dropped into
    // draft would skip exactly the device checks the environments exist to enforce.
    [Fact]
    public async Task The_child_inherits_the_parents_devices_and_environment()
    {
        using var h = new Harness();
        var device = new DeviceEntity
        {
            DeviceId = Guid.NewGuid(), DeviceName = "edge-1", IpAddress = "10.0.0.1",
            AllowDraft = false, AllowQa = true, AllowProduction = false, IsActive = true,
        };
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Devices.Add(device);
            var c = Workflow(child, "child", Nodes(EndNode("leaf")), NoEdges);
            c.Environment = WorkflowEntity.EnvDraft;
            db.Workflows.Add(c);
            db.Workflows.Add(Workflow(parent, "parent", Nodes(SubflowNode("sub", child)), NoEdges));
        });

        var (_, result) = await h.RunAsync(parent, targets: [device.DeviceId]);

        Assert.Equal(NodeResult.NoChange, Step(result, "sub").Result);
        var childRun = await h.ReadAsync(db => db.WorkflowRuns.SingleAsync(r => r.WorkflowId == child));
        Assert.Equal(WorkflowEntity.EnvQa, childRun.Environment);
        Assert.Equal("[\"" + device.DeviceId + "\"]", childRun.TargetDevicesJson);
    }

    // ─── the child's verdict ──────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_child_fails_the_parent_step_and_carries_its_error()
    {
        using var h = new Harness();
        var boom = Snippet("boom", "boom");
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.Add(boom);
            db.Workflows.Add(Workflow(child, "child", Nodes(TaskNode("blow-up", boom.SnippetId)), NoEdges));
            db.Workflows.Add(Workflow(parent, "parent", Nodes(SubflowNode("sub", child)), NoEdges));
        });

        var (parentRun, result) = await h.RunAsync(parent);

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("subflow_failed", step.ErrorCode);
        // The child's own message, not "the subflow failed".
        Assert.Contains("the child could not reach 10.0.0.9", step.Error);
        Assert.Contains("blow-up", step.Error);
        Assert.Equal("failed", parentRun.Status);
        // The child run exists and is linked, so the operator can open it.
        var childRun = await h.ReadAsync(db => db.WorkflowRuns.SingleAsync(r => r.WorkflowId == child));
        Assert.Equal(childRun.WorkflowRunId, step.ChildRunId);
    }

    [Fact]
    public async Task The_step_changes_only_when_a_child_step_changed()
    {
        using var h = new Harness();
        var probe = Snippet("probe", "probe");
        var tweak = Snippet("tweak", "tweak");
        var readOnly = Guid.NewGuid();
        var mutating = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.AddRange(probe, tweak);
            db.Workflows.Add(Workflow(readOnly, "read", Nodes(TaskNode("n", probe.SnippetId)), NoEdges));
            db.Workflows.Add(Workflow(mutating, "write", Nodes(TaskNode("n", tweak.SnippetId)), NoEdges));
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(SubflowNode("r", readOnly), SubflowNode("w", mutating)), NoEdges));
        });

        var (_, result) = await h.RunAsync(parent);

        Assert.Equal(NodeResult.NoChange, Step(result, "r").Result);
        Assert.Equal(NodeResult.Changed, Step(result, "w").Result);
    }

    // ─── tier propagation ─────────────────────────────────────────────────────

    // §5: the step's tier is the strictest among the child's executed nodes. This is the
    // reason it matters — the rollback plan. A subflow that sent an email is not
    // reversible, and a parent that listed the node anyway would report `rolled_back` for
    // a run whose effects are still out there.
    [Theory]
    [InlineData("tweak", true, "rolled_back")]
    [InlineData("shout", false, "failed")]
    public async Task The_strictest_child_tier_decides_the_parents_rollback_plan(
        string childSnippetType, bool inPlan, string finalState)
    {
        using var h = new Harness();
        var work = Snippet(childSnippetType, childSnippetType);
        var probe = Snippet("probe", "probe");
        var boom = Snippet("boom", "boom");
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.AddRange(work, probe, boom);
            // A read beside the mutation, so "strictest" is doing real work rather than
            // reading a one-node child.
            db.Workflows.Add(Workflow(
                child, "child",
                Nodes(TaskNode("look", probe.SnippetId), TaskNode("act", work.SnippetId)),
                Chain("look", "act")));
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(SubflowNode("sub", child), TaskNode("after", boom.SnippetId)),
                Chain("sub", "after")));
        });

        var (parentRun, result) = await h.RunAsync(parent);

        Assert.Equal(NodeResult.Changed, Step(result, "sub").Result);
        Assert.Equal(NodeResult.Failed, Step(result, "after").Result);
        Assert.Equal(inPlan, result.RollbackPlan.Contains("sub"));
        Assert.Equal(finalState, parentRun.FinalState);
    }

    // A child that ran nothing constrains nothing. Left at the unannotated default it
    // would drag every empty subflow into `requires_compensation` and out of the
    // reversible set for no reason anyone could point at.
    [Fact]
    public void A_child_in_which_nothing_ran_is_idempotent()
    {
        var outcome = new SubflowOutcome(Guid.NewGuid(), "completed", "completed", null,
            [new NodeExecution("skipped-one", NodeResult.Skipped, null, false, null)]);

        Assert.Equal(IdempotencyKind.Idempotent, outcome.StrictestTier);
    }

    // ─── merge helper ─────────────────────────────────────────────────────────

    // Shallow, not deep: a deep merge would let a node half-override a nested object the
    // child's schema treats as one value, and the graph would not say which half arrived.
    [Fact]
    public void The_merge_is_shallow_and_config_overrides_win()
    {
        using var input = JsonDocument.Parse("""{"a":1,"nested":{"keep":true},"b":"from-input"}""");
        using var overrides = JsonDocument.Parse("""{"b":"from-node","nested":{"other":1}}""");

        var merged = SnippetNodeExecutor.MergePayloads(
            input.RootElement.Clone(), overrides.RootElement.Clone());

        Assert.Equal(1, merged.GetProperty("a").GetInt32());
        Assert.Equal("from-node", merged.GetProperty("b").GetString());
        Assert.False(merged.GetProperty("nested").TryGetProperty("keep", out _));
        Assert.Equal(1, merged.GetProperty("nested").GetProperty("other").GetInt32());
    }
    // ─── cancellation ─────────────────────────────────────────────────────────

    // §5: cancelling the parent cancels the child. The token is plumbed parent walk →
    // SubflowRunner → child RunDetailedAsync → child walk, and SubflowRunner rethrows
    // OperationCanceledException rather than folding it into SubflowOutcome.Refused —
    // which is what stops a cancellation from arriving as an ordinary `subflow_failed`
    // step the parent then carries on past.
    //
    // The trip is deterministic: the child's first node cancels the token and returns
    // normally, and the child's second node is a per_device fan-out, whose loop asks the
    // token before every target. So the abort lands inside the child, at a check the
    // engine already makes, with no timing to get wrong.
    [Fact]
    public async Task Cancelling_mid_child_run_aborts_the_child_and_propagates()
    {
        using var cts = new CancellationTokenSource();
        var trip = new TripHandler(cts);
        var after = new CountingHandler();
        using var h = new Harness(trip, after);

        var device = new DeviceEntity
        {
            DeviceId = Guid.NewGuid(), DeviceName = "edge-1", IpAddress = "10.0.0.1",
            AllowDraft = false, AllowQa = true, AllowProduction = false, IsActive = true,
        };
        var tripper = Snippet("trip", "trip");
        var fanOut = Snippet("count", "count");
        fanOut.TargetMode = SnippetEntity.TargetPerDevice;

        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Devices.Add(device);
            db.Snippets.AddRange(tripper, fanOut);
            db.Workflows.Add(Workflow(
                child, "child",
                Nodes(TaskNode("trip", tripper.SnippetId), TaskNode("after", fanOut.SnippetId)),
                Chain("trip", "after")));
            db.Workflows.Add(Workflow(
                parent, "parent",
                Nodes(StartNode("start"), SubflowNode("sub", child)),
                Chain("start", "sub")));
        });

        // It propagates: the parent's walk does not swallow it into a failed step.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.RunAsync(parent, targets: [device.DeviceId], ct: cts.Token));

        // …from INSIDE the child, not before it: the first child node ran, the second
        // never did.
        Assert.True(trip.Ran);
        Assert.Equal(0, after.Runs);

        // And the record left behind says nothing untrue. RunDetailedAsync writes its run
        // row after the walk, so an aborted walk leaves none at all — above all none
        // claiming `completed` for a child that stopped at its first node.
        var runs = await h.ReadAsync(db => db.WorkflowRuns.ToListAsync());
        Assert.DoesNotContain(runs, r => r.Status == RunEntity.StatusCompleted);
        Assert.Empty(runs);
        Assert.Empty(await h.ReadAsync(db => db.StepRuns.ToListAsync()));
    }

    // ─── the static plan ──────────────────────────────────────────────────────

    // GET /workflows/{id}/plan is where an operator is told whether a failed run can be
    // undone. A resolver that stopped at the parent's graph scored every subflow node at
    // the unannotated default, so a workflow whose child sends an email was reported
    // fully reversible and running it proved otherwise — the one promise the rollback
    // analysis exists to make, broken by the one node type that can hide work.
    [Theory]
    [InlineData("probe", IdempotencyKind.Idempotent)]
    [InlineData("tweak", IdempotencyKind.RequiresCompensation)]
    [InlineData("shout", IdempotencyKind.NonReversible)]
    public async Task The_plan_scores_a_subflow_node_at_its_childs_strictest_tier(
        string childSnippetType, IdempotencyKind expected)
    {
        using var h = new Harness();
        var probe = Snippet("probe", "probe");
        var work = childSnippetType == "probe" ? probe : Snippet(childSnippetType, childSnippetType);
        var child = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.Add(probe);
            if (!ReferenceEquals(work, probe)) db.Snippets.Add(work);
            // A read beside the mutation, so "strictest" is doing real work rather than
            // reading a one-node child.
            db.Workflows.Add(Workflow(
                child, "child",
                Nodes(TaskNode("look", probe.SnippetId), TaskNode("act", work.SnippetId)),
                Chain("look", "act")));
            db.Workflows.Add(Workflow(parent, "parent", Nodes(SubflowNode("sub", child)), NoEdges));
        });

        var tiers = await h.PlanTiersAsync(parent);

        Assert.Equal(expected, tiers.Tiers["sub"]);
        Assert.Empty(tiers.Unresolvable);
    }

    // Transitively, because a subflow's child may itself be a subflow — and a resolver
    // that descended only one level would score the middle workflow at the default and
    // land back where it started.
    [Fact]
    public async Task The_plan_follows_a_chain_of_subflows_to_the_bottom()
    {
        using var h = new Harness();
        var shout = Snippet("shout", "shout");
        var leaf = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Snippets.Add(shout);
            db.Workflows.Add(Workflow(leaf, "leaf", Nodes(TaskNode("send", shout.SnippetId)), NoEdges));
            db.Workflows.Add(Workflow(middle, "middle", Nodes(SubflowNode("sub", leaf)), NoEdges));
            db.Workflows.Add(Workflow(parent, "parent", Nodes(SubflowNode("sub", middle)), NoEdges));
        });

        var tiers = await h.PlanTiersAsync(parent);

        Assert.Equal(IdempotencyKind.NonReversible, tiers.Tiers["sub"]);
        Assert.Empty(tiers.Unresolvable);
    }

    // A child that no longer exists, a chain that re-enters itself, and a chain past the
    // depth cap are one class of problem: the plan cannot see the work. It says so, and
    // scores the node at the ceiling — reporting a tier it cannot support is what this
    // resolver exists to stop, and the reason is what tells a reader whether the block is
    // a design decision or a broken reference nobody has noticed.
    [Fact]
    public async Task The_plan_reports_a_missing_child_rather_than_guessing_its_tier()
    {
        using var h = new Harness();
        var parent = Guid.NewGuid();
        var ghost = Guid.NewGuid();
        await h.SeedAsync(db => db.Workflows.Add(Workflow(
            parent, "parent", Nodes(SubflowNode("sub", ghost)), NoEdges)));

        var tiers = await h.PlanTiersAsync(parent);

        Assert.Equal(IdempotencyKind.NonReversible, tiers.Tiers["sub"]);
        var unresolvable = Assert.Single(tiers.Unresolvable);
        Assert.Equal("sub", unresolvable.NodeId);
        // The code the run would fail this node with, so plan and run name it alike.
        Assert.Equal("subflow_missing", unresolvable.Code);
        Assert.Contains(ghost.ToString(), unresolvable.Reason);
    }

    // A → B → A. The endpoint must answer, not recurse until the stack gives out.
    [Fact]
    public async Task The_plan_reports_a_subflow_cycle_instead_of_recursing_forever()
    {
        using var h = new Harness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await h.SeedAsync(db =>
        {
            db.Workflows.Add(Workflow(a, "A", Nodes(SubflowNode("sub", b)), NoEdges));
            db.Workflows.Add(Workflow(b, "B", Nodes(SubflowNode("sub", a)), NoEdges));
        });

        var tiers = await h.PlanTiersAsync(a);

        Assert.Equal(IdempotencyKind.NonReversible, tiers.Tiers["sub"]);
        var unresolvable = Assert.Single(tiers.Unresolvable);
        Assert.Equal("subflow_cycle", unresolvable.Code);
        // Named down to the workflow and the node the loop closes at.
        Assert.Contains("'B'", unresolvable.Reason);
    }

    // The same cap the executor enforces, applied before anything is read rather than
    // after eight levels of queries.
    [Fact]
    public async Task The_plan_stops_at_the_subflow_depth_cap()
    {
        using var h = new Harness();
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        await h.SeedAsync(db =>
        {
            for (var i = 0; i < ids.Length; i++)
                db.Workflows.Add(Workflow(
                    ids[i], $"w{i}",
                    i == ids.Length - 1
                        ? Nodes(EndNode("leaf"))
                        : Nodes(SubflowNode("sub", ids[i + 1])),
                    NoEdges));
        });

        var tiers = await h.PlanTiersAsync(ids[0]);

        Assert.Equal(IdempotencyKind.NonReversible, tiers.Tiers["sub"]);
        var unresolvable = Assert.Single(tiers.Unresolvable);
        Assert.Equal("subflow_depth_exceeded", unresolvable.Code);
        Assert.Contains(SubflowLimits.MaxDepth.ToString(), unresolvable.Reason);
    }
}

