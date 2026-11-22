using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
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

// A child run opens a scope of its own, which has no HTTP context. Two things must still
// hold across that boundary: the child is authorized as the parent's caller (and as nobody
// when the parent had none), and `redact_output` on the subflow node withholds what the
// child's steps produced from the parent's result and from downstream templates.
public class SubflowCallerAndPrivacyTests
{
    private const string StoredValue = "example-stored-value";
    private const string PublicValue = "note-from-pub";
    private const string Denied = "secret_access_denied";

    // ─── identity across the scope boundary ───────────────────────────────────

    [Fact]
    public async Task A_child_run_uses_stored_secrets_as_the_admin_who_started_the_parent()
    {
        using var h = new Harness();
        var parent = await h.SeedSecretChainAsync();

        var result = await h.RunAsync(parent, role: "admin");

        Assert.Equal(NodeResult.NoChange, Step(result, "sub").Result);
        Assert.Equal("completed", result.Status);
        // The child step resolved a stored secret, so its recorded output is withheld
        // and the stored value appears nowhere in what the parent holds.
        Assert.DoesNotContain(StoredValue, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    [InlineData(null)]
    public async Task A_child_run_does_not_gain_stored_secret_access_the_parents_caller_lacks(string? role)
    {
        using var h = new Harness();
        var parent = await h.SeedSecretChainAsync();

        var result = await h.RunAsync(parent, role);

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("failed", result.Status);
        Assert.DoesNotContain(StoredValue, JsonSerializer.Serialize(result));
        var child = await h.ReadAsync(db => db.StepRuns.SingleAsync(s => s.NodeId == "use"));
        Assert.Equal(Denied, child.ErrorCode);
    }

    // ─── redact_output on the subflow node ────────────────────────────────────

    [Fact]
    public async Task Redact_output_withholds_the_childs_output_from_the_result_and_from_downstream_templates()
    {
        using var h = new Harness();
        var parent = await h.SeedPublicChainAsync(subflowOverrides: ",\"redact_output\":true");

        var result = await h.RunAsync(parent, role: "admin");

        Assert.Equal("completed", result.Status);
        var output = Step(result, "sub").Output!;
        Assert.Equal("\"[redacted]\"", output);
        // The next node is handed the withheld value, not the child's data.
        var next = Step(result, "after").Output!;
        Assert.DoesNotContain(PublicValue, next);
        Assert.Contains("[redacted]", next);
    }

    [Fact]
    public async Task Redact_output_withholds_the_childs_error_text()
    {
        using var h = new Harness();
        var parent = await h.SeedFailingChainAsync(subflowOverrides: ",\"redact_output\":true");

        var result = await h.RunAsync(parent, role: "admin");

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("subflow_failed", step.ErrorCode);
        Assert.Equal("[redacted]", step.Error);
        Assert.Equal("\"[redacted]\"", step.Output);
        Assert.DoesNotContain("203.0.113.5", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Without_the_flag_the_childs_output_and_error_are_unchanged()
    {
        using var h = new Harness();
        var visible = await h.SeedPublicChainAsync(subflowOverrides: ",\"redact_output\":false");
        var failing = await h.SeedFailingChainAsync(subflowOverrides: "");

        var ok = await h.RunAsync(visible, role: "admin");
        var failed = await h.RunAsync(failing, role: "admin");

        Assert.Contains(PublicValue, Step(ok, "sub").Output);
        Assert.Contains("203.0.113.5", Step(failed, "sub").Error);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task A_flag_that_is_not_a_boolean_stops_the_node_before_any_child_runs(string badValue)
    {
        using var h = new Harness();
        var parent = await h.SeedPublicChainAsync(subflowOverrides: $",\"redact_output\":{badValue}");

        var result = await h.RunAsync(parent, role: "admin");

        var step = Step(result, "sub");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal("invalid_redact_output", step.ErrorCode);
        Assert.Equal(1, await h.ReadAsync(db => db.WorkflowRuns.CountAsync()));
    }

    // ─── plumbing ─────────────────────────────────────────────────────────────

    private static NodeExecution Step(WorkflowRunResult result, string nodeId) =>
        result.Steps.Single(s => s.NodeId == nodeId);

    private static string Q(object v) => JsonSerializer.Serialize(v);

    private static string Start() => "{\"id\":\"start\",\"snippet_id\":\"__start__\"}";

    private static string TaskNode(string id, Guid snippetId, string overrides = "") =>
        $"{{\"id\":{Q(id)},\"snippet_id\":{Q(snippetId)},\"config_overrides\":{{{overrides}}}}}";

    private static string Sub(Guid child, string extra) =>
        $"{{\"id\":\"sub\",\"snippet_id\":\"subflow\",\"type\":\"subflow\"," +
        $"\"config_overrides\":{{\"subflow_workflow_id\":{Q(child)}{extra}}}}}";

    private static string Chain(params string[] ids) => JsonSerializer.Serialize(
        ids.Zip(ids.Skip(1), (a, b) => new { source = a, target = b, type = "success" }));

    private static WorkflowEntity Workflow(Guid id, string name, string[] nodes, string[] chain) => new()
    {
        WorkflowId = id, Name = name, Environment = WorkflowEntity.EnvQa, SchemaHash = "H",
        NodesJson = "[" + string.Join(",", nodes) + "]", EdgesJson = Chain(chain), IsActive = true,
    };

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly Snippet _useSecret = new()
            { SnippetId = Guid.NewGuid(), Name = "use-secret", Slug = "use-secret", Type = "use_secret", IsActive = true };
        private readonly Snippet _plain = new()
            { SnippetId = Guid.NewGuid(), Name = "plain", Slug = "plain", Type = "plain", IsActive = true };
        private readonly Snippet _fail = new()
            { SnippetId = Guid.NewGuid(), Name = "fail", Slug = "fail", Type = "fail", IsActive = true };

        public Harness()
        {
            var name = $"subflow-caller-{Guid.NewGuid()}";
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
            services.AddScoped<SnippetNodeExecutor>();
            services.AddScoped<INodeExecutor>(sp => sp.GetRequiredService<SnippetNodeExecutor>());
            services.AddScoped<ISubflowRunner, SubflowRunner>();
            services.AddScoped<WorkflowRunService>();
            _root = services.BuildServiceProvider();
        }

        private bool _baseSeeded;

        public async Task SeedAsync(Action<AppDbContext> seed)
        {
            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!_baseSeeded)
            {
                db.Snippets.AddRange(_useSecret, _plain, _fail);
                db.Secrets.Add(new Secret
                    { SecretId = Guid.NewGuid(), Name = "example-secret", EncryptedValue = Encoding.UTF8.GetBytes(StoredValue) });
                _baseSeeded = true;
            }
            seed(db);
            await db.SaveChangesAsync();
        }

        public async Task<Guid> SeedSecretChainAsync()
        {
            var parent = Guid.NewGuid();
            var child = Guid.NewGuid();
            await SeedAsync(db =>
            {
                db.Workflows.Add(Workflow(child, "child", [Start(), TaskNode("use", _useSecret.SnippetId)], ["start", "use"]));
                db.Workflows.Add(Workflow(parent, "parent", [Start(), Sub(child, "")], ["start", "sub"]));
            });
            return parent;
        }

        public async Task<Guid> SeedPublicChainAsync(string subflowOverrides)
        {
            var parent = Guid.NewGuid();
            var child = Guid.NewGuid();
            await SeedAsync(db =>
            {
                db.Workflows.Add(Workflow(child, "child", [Start(), TaskNode("pub", _plain.SnippetId)], ["start", "pub"]));
                db.Workflows.Add(Workflow(parent, "parent", [
                    Start(), Sub(child, subflowOverrides),
                    TaskNode("after", _plain.SnippetId, "\"carried\":\"{{ steps.sub.output }}\""),
                ], ["start", "sub", "after"]));
            });
            return parent;
        }

        public async Task<Guid> SeedFailingChainAsync(string subflowOverrides)
        {
            var parent = Guid.NewGuid();
            var child = Guid.NewGuid();
            await SeedAsync(db =>
            {
                db.Workflows.Add(Workflow(child, "child", [Start(), TaskNode("bad", _fail.SnippetId)], ["start", "bad"]));
                db.Workflows.Add(Workflow(parent, "parent", [Start(), Sub(child, subflowOverrides)], ["start", "sub"]));
            });
            return parent;
        }

        // Runs the parent on a scope of its own with the given caller bound, the way a
        // background worker would: no HTTP context, only the scope's mutable user.
        public async Task<WorkflowRunResult> RunAsync(Guid workflowId, string? role)
        {
            using var scope = _root.CreateScope();
            var sp = scope.ServiceProvider;
            if (role is not null)
                sp.GetRequiredService<MutableCurrentUser>().Bind(Guid.NewGuid(), "test", [role]);
            var db = sp.GetRequiredService<AppDbContext>();
            var wf = await db.Workflows.FirstAsync(w => w.WorkflowId == workflowId);
            var (_, result) = await sp.GetRequiredService<WorkflowRunService>().RunDetailedAsync(wf, null, [], default);
            return result;
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
        public IReadOnlyCollection<string> KnownTypes => ["use_secret", "plain", "fail"];

        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => type switch
        {
            "use_secret" => new UseSecretHandler(scope.GetRequiredService<ISecretResolver>()),
            "plain" => new PlainHandler(),
            "fail" => new FailHandler(),
            _ => null,
        };
    }

    // Resolves a stored secret, then (carelessly) puts its value in the output.
    private sealed class UseSecretHandler(ISecretResolver secrets) : ISnippetHandler
    {
        public string Type => "use_secret";

        public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            var value = await secrets.SubstituteAsync("${secret:secret:example-secret:value}", ct);
            return SnippetResult.Ok(new { value }, StepChange.Unchanged);
        }
    }

    private sealed class PlainHandler : ISnippetHandler
    {
        public string Type => "plain";

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct) =>
            Task.FromResult(SnippetResult.Ok(new { note = $"note-from-{request.NodeId}", saw = request.Input }, StepChange.Unchanged));
    }

    private sealed class FailHandler : ISnippetHandler
    {
        public string Type => "fail";

        public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct) =>
            Task.FromResult(SnippetResult.Fail("example failure reaching 203.0.113.5", "stub_failed"));
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
