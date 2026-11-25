using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Auth;
using nashira_backend.Services.DevicePools;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Policy;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Tests;

// A triggered run executes as the trigger's creator, but what it does is decided by
// whoever last saved the trigger, the workflow, its snippets and its subflows. The run
// must never hold a higher role than the least-privileged of those authors, so an
// Operator's edit cannot use stored secrets with the role of an Admin who created the
// trigger. Manual runs are not affected.
public class RunAuthorityTests
{
    private const string StoredValue = "example-stored-value";
    private const string Denied = "secret_access_denied";

    // ─── the run is capped at its authors' roles ─────────────────────────────

    [Fact]
    public async Task A_workflow_last_edited_by_an_operator_runs_as_operator_and_cannot_use_stored_secrets()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Admin);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Operator, Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: h.Admin);

        await AssertDeniedAsync(h, run);
        Assert.Equal("operator", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task A_trigger_last_edited_by_an_operator_runs_as_operator()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Admin);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Admin, Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: h.Operator);

        await AssertDeniedAsync(h, run);
        Assert.Equal("operator", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task A_snippet_last_edited_by_an_operator_runs_as_operator()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Operator);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Admin, Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: h.Admin);

        await AssertDeniedAsync(h, run);
        Assert.Equal("operator", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task A_subflow_child_last_edited_by_an_operator_runs_as_operator()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Admin);
        var child = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Operator, Start(), TaskNode("use", snippet));
        var parent = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Admin, Start(), Sub(child));

        var run = await h.TriggeredRunAsync(parent, triggerAuthor: h.Admin);

        Assert.Equal(NodeResult.Failed, Step(run.Result, "sub").Result);
        Assert.Equal("failed", run.Result.Status);
        Assert.DoesNotContain(StoredValue, JsonSerializer.Serialize(run.Result));
        var step = await h.ReadAsync(db => db.StepRuns.SingleAsync(s => s.NodeId == "use"));
        Assert.Equal(Denied, step.ErrorCode);
    }

    [Fact]
    public async Task A_lowered_parent_carries_its_lowered_role_into_the_child()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Admin);
        // Every child row is Admin-authored; only the parent was last saved by an Operator.
        var child = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Admin, Start(), TaskNode("use", snippet));
        var parent = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Operator, Start(), Sub(child));

        var run = await h.TriggeredRunAsync(parent, triggerAuthor: h.Admin);

        Assert.Equal("failed", run.Result.Status);
        var step = await h.ReadAsync(db => db.StepRuns.SingleAsync(s => s.NodeId == "use"));
        Assert.Equal(Denied, step.ErrorCode);
    }

    [Fact]
    public async Task An_inactive_author_lowers_the_run_to_viewer()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Admin);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.InactiveAdmin, Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: h.Admin);

        await AssertDeniedAsync(h, run);
        Assert.Equal("viewer", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task An_author_who_no_longer_exists_lowers_the_run_to_viewer()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Admin);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: Guid.NewGuid(), Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: h.Admin);

        await AssertDeniedAsync(h, run);
        Assert.Equal("viewer", Assert.Single(run.Roles));
    }

    // ─── what must not change ────────────────────────────────────────────────

    [Fact]
    public async Task Rows_with_no_recorded_author_do_not_lower_the_run()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: null, updatedBy: null);
        var wf = await h.WorkflowAsync(createdBy: null, updatedBy: null, Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: null);

        Assert.Equal("completed", run.Result.Status);
        Assert.Equal("admin", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task A_legacy_row_with_only_a_creator_is_attributed_to_that_creator()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Operator, updatedBy: null);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: null, Start(), TaskNode("use", snippet));

        var run = await h.TriggeredRunAsync(wf, triggerAuthor: h.Admin);

        await AssertDeniedAsync(h, run);
        Assert.Equal("operator", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task An_all_admin_chain_still_resolves_stored_secrets()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.SecondAdmin);
        var child = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.SecondAdmin, Start(), TaskNode("use", snippet));
        var parent = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Admin, Start(), Sub(child));

        var run = await h.TriggeredRunAsync(parent, triggerAuthor: h.SecondAdmin);

        Assert.Equal("completed", run.Result.Status);
        Assert.Equal(NodeResult.NoChange, Step(run.Result, "sub").Result);
        Assert.Equal("admin", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task A_manual_run_is_not_affected()
    {
        using var h = new Harness();
        var snippet = await h.SnippetAsync(createdBy: h.Admin, updatedBy: h.Operator);
        var wf = await h.WorkflowAsync(createdBy: h.Admin, updatedBy: h.Operator, Start(), TaskNode("use", snippet));

        var run = await h.ManualRunAsync(wf);

        Assert.Equal("completed", run.Result.Status);
        Assert.Equal("admin", Assert.Single(run.Roles));
    }

    [Fact]
    public async Task The_role_only_ever_goes_down()
    {
        using var h = new Harness();
        using var scope = h.Scope();
        var sp = scope.ServiceProvider;
        var user = sp.GetRequiredService<MutableCurrentUser>();
        user.Bind(h.Operator, "op", ["operator"]);
        var authority = sp.GetRequiredService<RunAuthority>();
        authority.Arm();

        await authority.ApplyAuthorAsync(h.Admin, "test", default);

        Assert.Equal("operator", Assert.Single(user.Roles));
        Assert.Equal(h.Operator, user.UserId);
    }

    [Fact]
    public async Task An_unbound_scope_is_left_alone()
    {
        using var h = new Harness();
        using var scope = h.Scope();
        var sp = scope.ServiceProvider;
        var authority = sp.GetRequiredService<RunAuthority>();
        authority.Arm();

        await authority.ApplyAuthorAsync(h.Operator, "test", default);

        Assert.False(sp.GetRequiredService<MutableCurrentUser>().IsAuthenticated);
    }

    // ─── trigger controller: settings only the creator or an Admin may change ─

    [Fact]
    public async Task An_operator_who_did_not_create_a_trigger_cannot_rotate_its_secret()
    {
        using var db = ControllerDb(out var workflowId);
        var creator = new HardeningTestUser("operator");
        var created = await CreateWebhookAsync(db, creator, workflowId);

        var other = TriggerController(db, new HardeningTestUser("operator"));
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => other.RotateSecret(workflowId, created.Id, default));
        Assert.Equal(WorkflowTriggerController.OwnerRequiredCode, ex.Code);

        // The creator and an Admin may.
        Assert.NotNull((await TriggerController(db, creator).RotateSecret(workflowId, created.Id, default)).Value!.Secret);
        Assert.NotNull((await TriggerController(db, new HardeningTestUser("admin")).RotateSecret(workflowId, created.Id, default)).Value!.Secret);
    }

    [Fact]
    public async Task An_operator_who_did_not_create_a_trigger_cannot_change_its_signing_or_target_settings()
    {
        using var db = ControllerDb(out var workflowId);
        var created = await CreateWebhookAsync(db, new HardeningTestUser("admin"), workflowId);
        var other = TriggerController(db, new HardeningTestUser("operator"));

        var unsigned = await Assert.ThrowsAsync<ForbiddenException>(() =>
            other.Update(workflowId, created.Id, new WriteWorkflowTrigger { AllowUnsigned = true }, default));
        Assert.Equal(WorkflowTriggerController.OwnerRequiredCode, unsigned.Code);
        var targets = await Assert.ThrowsAsync<ForbiddenException>(() =>
            other.Update(workflowId, created.Id, new WriteWorkflowTrigger { AllowTargetOverride = true }, default));
        Assert.Equal(WorkflowTriggerController.OwnerRequiredCode, targets.Code);

        var row = await db.WorkflowTriggers.AsNoTracking().SingleAsync();
        Assert.False(row.AllowUnsigned);
        Assert.False(row.AllowTargetOverride);
    }

    [Fact]
    public async Task An_operator_may_still_save_the_rest_of_the_form_with_the_same_flag_values()
    {
        using var db = ControllerDb(out var workflowId);
        var admin = new HardeningTestUser("admin");
        var created = await CreateWebhookAsync(db, admin, workflowId);
        var operatorUser = new HardeningTestUser("operator");

        var saved = await TriggerController(db, operatorUser).Update(workflowId, created.Id, new WriteWorkflowTrigger
        {
            Name = "renamed", AllowUnsigned = false, AllowTargetOverride = false,
        }, default);

        Assert.Equal("renamed", saved.Value!.Name);
        var row = await db.WorkflowTriggers.AsNoTracking().SingleAsync();
        Assert.Equal(admin.UserId, row.CreatedBy);
        // The save makes the operator the trigger's last editor, which caps its runs.
        Assert.Equal(operatorUser.UserId, row.UpdatedBy);
    }

    [Fact]
    public async Task An_admin_may_change_the_flags_on_a_trigger_someone_else_created()
    {
        using var db = ControllerDb(out var workflowId);
        var created = await CreateWebhookAsync(db, new HardeningTestUser("operator"), workflowId);

        var saved = await TriggerController(db, new HardeningTestUser("admin")).Update(
            workflowId, created.Id, new WriteWorkflowTrigger { AllowUnsigned = true }, default);

        Assert.True(saved.Value!.AllowUnsigned);
    }

    // ─── plumbing ─────────────────────────────────────────────────────────────

    private static async Task AssertDeniedAsync(Harness h, TriggeredRun run)
    {
        Assert.Equal(NodeResult.Failed, Step(run.Result, "use").Result);
        Assert.Equal("failed", run.Result.Status);
        Assert.DoesNotContain(StoredValue, JsonSerializer.Serialize(run.Result));
        var step = await h.ReadAsync(db => db.StepRuns.SingleAsync(s => s.NodeId == "use"));
        Assert.Equal(Denied, step.ErrorCode);
    }

    private static NodeExecution Step(WorkflowRunResult result, string nodeId) =>
        result.Steps.Single(s => s.NodeId == nodeId);

    private static string Q(object v) => JsonSerializer.Serialize(v);

    private static string Start() => "{\"id\":\"start\",\"snippet_id\":\"__start__\"}";

    private static string TaskNode(string id, Guid snippetId) =>
        $"{{\"id\":{Q(id)},\"snippet_id\":{Q(snippetId)},\"config_overrides\":{{}}}}";

    private static string Sub(Guid child) =>
        $"{{\"id\":\"sub\",\"snippet_id\":\"subflow\",\"type\":\"subflow\"," +
        $"\"config_overrides\":{{\"subflow_workflow_id\":{Q(child)}}}}}";

    private static AppDbContext ControllerDb(out Guid workflowId)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"run-authority-ctl-{Guid.NewGuid()}").Options);
        workflowId = Guid.NewGuid();
        db.Workflows.Add(new WorkflowEntity
        {
            WorkflowId = workflowId, Name = "wf", Environment = WorkflowEntity.EnvDraft, SchemaHash = "H",
            NodesJson = "[]", EdgesJson = "[]", IsActive = true,
        });
        db.SaveChanges();
        return db;
    }

    private static WorkflowTriggerController TriggerController(AppDbContext db, ICurrentUser user) =>
        new(db, user, new PlainProtector());

    private static async Task<WorkflowTriggerResponse> CreateWebhookAsync(
        AppDbContext db, ICurrentUser creator, Guid workflowId)
    {
        var response = await TriggerController(db, creator).Post(
            workflowId, new WriteWorkflowTrigger { Name = "hook", Type = WorkflowTrigger.TypeWebhook }, default);
        return response.Value!;
    }

    private sealed record TriggeredRun(WorkflowRunResult Result, IReadOnlyList<string> Roles);

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;

        public Guid Admin { get; } = Guid.NewGuid();
        public Guid SecondAdmin { get; } = Guid.NewGuid();
        public Guid Operator { get; } = Guid.NewGuid();
        public Guid InactiveAdmin { get; } = Guid.NewGuid();

        public Harness()
        {
            var name = $"run-authority-{Guid.NewGuid()}";
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<IAuditLogger, NoAudit>();
            services.AddSingleton<ISecretProtector>(new PlainProtector());
            services.AddSingleton<ITraceLogger>(new NullTrace());
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = null });
            services.AddSingleton<IOptions<JwtOptions>>(Options.Create(new JwtOptions
            {
                Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                Key = "test-signing-key-that-is-long-enough-for-hmac-256",
            }));
            services.AddScoped<IJwtTokenService, JwtTokenService>();
            // The registration Program.cs uses outside a request: the scope's own mutable user.
            services.AddScoped<CurrentUser>();
            services.AddScoped<MutableCurrentUser>();
            services.AddScoped<ICurrentUser>(sp =>
                sp.GetRequiredService<IHttpContextAccessor>().HttpContext is null
                    ? sp.GetRequiredService<MutableCurrentUser>()
                    : sp.GetRequiredService<CurrentUser>());
            services.AddScoped<ISecretResolver, SecretResolver>();
            services.AddSingleton<ISnippetHandlerRegistry, Registry>();
            services.AddScoped<WorkflowExecutionScope>();
            services.AddScoped<IVariableResolver, VariableResolver>();
            services.AddScoped<IConditionEvaluator, ConditionEvaluator>();
            services.AddScoped<IPolicyEvaluator, PolicyEvaluator>();
            services.AddScoped<IDevicePoolResolver, DevicePoolResolver>();
            services.AddScoped<RunAuthority>();
            services.AddScoped<SnippetNodeExecutor>();
            services.AddScoped<INodeExecutor>(sp => sp.GetRequiredService<SnippetNodeExecutor>());
            services.AddScoped<ISubflowRunner, SubflowRunner>();
            services.AddScoped<WorkflowRunService>();
            _root = services.BuildServiceProvider();

            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                new User { UserId = Admin, Username = "admin-1", Role = "admin" },
                new User { UserId = SecondAdmin, Username = "admin-2", Role = "admin" },
                new User { UserId = Operator, Username = "operator-1", Role = "operator" },
                new User { UserId = InactiveAdmin, Username = "former-admin", Role = "admin", IsActive = false });
            db.Secrets.Add(new Secret
                { SecretId = Guid.NewGuid(), Name = "example-secret", EncryptedValue = Encoding.UTF8.GetBytes(StoredValue) });
            db.SaveChanges();
        }

        public IServiceScope Scope() => _root.CreateScope();

        public async Task<Guid> SnippetAsync(Guid? createdBy, Guid? updatedBy)
        {
            var id = Guid.NewGuid();
            await SeedAsync(db => db.Snippets.Add(new Snippet
            {
                SnippetId = id, Name = $"use-secret-{id:N}", Slug = $"use-secret-{id:N}", Type = "use_secret",
                CreatedBy = createdBy, UpdatedBy = updatedBy, IsActive = true,
            }));
            return id;
        }

        // The nodes given, linked in order by success edges.
        public async Task<Guid> WorkflowAsync(Guid? createdBy, Guid? updatedBy, params string[] nodes)
        {
            var id = Guid.NewGuid();
            var ids = nodes.Select(n => JsonDocument.Parse(n).RootElement.GetProperty("id").GetString()!).ToArray();
            var edges = JsonSerializer.Serialize(
                ids.Zip(ids.Skip(1), (a, b) => new { source = a, target = b, type = "success" }));
            await SeedAsync(db => db.Workflows.Add(new WorkflowEntity
            {
                WorkflowId = id, Name = $"wf-{id:N}", Environment = WorkflowEntity.EnvQa, SchemaHash = "H",
                NodesJson = "[" + string.Join(",", nodes) + "]", EdgesJson = edges, IsActive = true,
                CreatedByUserId = createdBy, UpdatedByUserId = updatedBy,
            }));
            return id;
        }

        // What the job worker does for a trigger-fired run: bind the trigger's creator
        // (an Admin here), arm the scope's RunAuthority, apply the trigger's and the
        // workflow's authors, then run.
        public Task<TriggeredRun> TriggeredRunAsync(Guid workflowId, Guid? triggerAuthor) =>
            RunAsync(workflowId, armed: true, triggerAuthor);

        // A run started by hand: same caller, nothing armed.
        public Task<TriggeredRun> ManualRunAsync(Guid workflowId) =>
            RunAsync(workflowId, armed: false, triggerAuthor: null);

        private async Task<TriggeredRun> RunAsync(Guid workflowId, bool armed, Guid? triggerAuthor)
        {
            using var scope = _root.CreateScope();
            var sp = scope.ServiceProvider;
            var user = sp.GetRequiredService<MutableCurrentUser>();
            user.Bind(Admin, "admin-1", ["admin"]);
            var db = sp.GetRequiredService<AppDbContext>();
            var wf = await db.Workflows.FirstAsync(w => w.WorkflowId == workflowId);
            if (armed)
            {
                var authority = sp.GetRequiredService<RunAuthority>();
                authority.Arm();
                await authority.ApplyAuthorAsync(triggerAuthor, "trigger", default);
                await authority.ApplyAuthorAsync(RunAuthority.AuthorOf(wf), "workflow", default);
            }
            var (_, result) = await sp.GetRequiredService<WorkflowRunService>().RunDetailedAsync(wf, null, [], default);
            return new TriggeredRun(result, user.Roles.ToList());
        }

        private async Task SeedAsync(Action<AppDbContext> seed)
        {
            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            seed(db);
            await db.SaveChangesAsync();
        }

        public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
        {
            using var scope = _root.CreateScope();
            return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public void Dispose() => _root.Dispose();
    }

    private sealed class Registry : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => ["use_secret"];

        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => type switch
        {
            "use_secret" => new UseSecretHandler(scope.GetRequiredService<ISecretResolver>()),
            _ => null,
        };
    }

    // Resolves a stored secret, the step an Operator's edit must not be able to borrow
    // an Admin's role for.
    private sealed class UseSecretHandler(ISecretResolver secrets) : ISnippetHandler
    {
        public string Type => "use_secret";

        public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            var value = await secrets.SubstituteAsync("${secret:secret:example-secret:value}", ct);
            return SnippetResult.Ok(new { value }, StepChange.Unchanged);
        }
    }

    private sealed class NoAudit : IAuditLogger
    {
        public Task LogAsync(
            string entityType, Guid? entityId, string action, object? before = null, object? after = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
    }

    private sealed class NullTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null) { }
        public ITraceScope Begin(string category, string action, object? metadata = null) => throw new NotSupportedException();
    }
}
