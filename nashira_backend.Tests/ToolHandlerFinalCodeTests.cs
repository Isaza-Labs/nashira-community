using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Ai.Tools.Handlers.Git;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Git;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Tests;

// Some tool handlers turn a DomainException into an `{error}` result. That used to drop the code,
// so the dispatcher could not tell a denied secret lookup or a refused redirect from an ordinary
// failure and offered a learned correction, which runs the call again: the denied lookup, or the
// original (mutating) request. These tests go through the real handler, the real service and, for
// the secret case, the real resolver, as an Operator, with a store that holds a matching
// parameter adjustment for every error. Only the transport and the learning store are doubles.
public class ToolHandlerFinalCodeTests
{
    private const string Denied = "secret_access_denied";
    private const string Refused = "redirect_refused";
    private const string StoredRef = "${secret:secret:example-token:value}";

    private sealed class Harness : IDisposable
    {
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"tool-final-codes-{Guid.NewGuid()}").Options);
        public MutableCurrentUser User { get; } = new();
        public CountingProtector Protector { get; } = new();
        public Store Learnings { get; } = new();
        public RedirectRefusalMappingTests.RedirectingFactory Http { get; } = new();
        public ISecretResolver Resolver { get; }
        public Guid CredentialId { get; } = Guid.NewGuid();

        public Harness(string role = "operator")
        {
            User.Bind(Guid.NewGuid(), "example-user", [role]);
            Db.Secrets.Add(new Secret
            {
                SecretId = Guid.NewGuid(), Name = "example-token", EncryptedValue = Encoding.UTF8.GetBytes("example-value"),
            });
            Db.Credentials.Add(new Credential
            {
                CredentialId = CredentialId, Name = "example-git", Type = "git_token",
                AuthMethod = Credential.AuthMethodToken, EncryptedToken = [1], IsActive = true,
            });
            Db.SaveChanges();
            Resolver = new SecretResolver(Db, Protector, User, new HttpContextAccessor { HttpContext = null },
                new JwtTokenService(Options.Create(new JwtOptions
                {
                    Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                    Key = "test-signing-key-that-is-long-enough-for-hmac-256",
                }), NullLogger<JwtTokenService>.Instance),
                new QuietTrace(), NullLogger<SecretResolver>.Instance);
        }

        public InventorySource AddSource(string? tokenRef)
        {
            var source = new InventorySource
            {
                InventorySourceId = Guid.NewGuid(), Name = "example-netbox", BaseUrl = "https://203.0.113.30",
                TokenSecretRef = tokenRef, IsActive = true,
            };
            Db.InventorySources.Add(source);
            Db.SaveChanges();
            return source;
        }

        public IToolHandler NetBoxTool() => new SyncNetBoxInventoryHandler(Db, User, new NetBoxSyncService(
            Db, User, Resolver, new RedirectRefusalMappingTests.AllowAllGuard(), Http,
            NullLogger<NetBoxSyncService>.Instance));

        private GitHubService GitHub() => new(Db, new FixedProtector(), new RedirectRefusalMappingTests.AllowAllGuard(),
            Http, new ConfigurationBuilder().Build(), NullLogger<GitHubService>.Instance);

        public IToolHandler CreatePrTool() => new GitHubCreatePrHandler(GitHub());
        public IToolHandler MergePrTool() => new GitHubMergePrHandler(GitHub());
        // register=false in these cases, so the git service is never reached.
        public IToolHandler CreateRepoTool() => new GitHubCreateRepoHandler(
            GitHub(), null!, NullLogger<GitHubCreateRepoHandler>.Instance);

        public async Task<ToolCallOutput> DispatchAsync(IToolHandler tool, object args)
        {
            var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
            registry.Register(tool);
            var services = new ServiceCollection().AddSingleton(tool.GetType(), tool).BuildServiceProvider();
            var dispatcher = new ToolDispatcher(
                registry, services, User,
                new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
                new OpenGuard(), new NoAutonomy(), new NoAudit(),
                new SelfCorrectionEngine(Learnings, NullLogger<SelfCorrectionEngine>.Instance),
                NullLogger<ToolDispatcher>.Instance, new QuietTrace());
            return await dispatcher.DispatchAsync(tool.Name, JsonSerializer.SerializeToElement(args), default);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task An_operator_whose_netbox_token_is_a_stored_secret_gets_the_denial_code_and_nothing_runs_again()
    {
        using var h = new Harness();
        h.AddSource(StoredRef);

        var output = await h.DispatchAsync(h.NetBoxTool(), new { });

        Assert.False(output.Success);
        Assert.Equal(Denied, output.Result.GetProperty("error_code").GetString());
        Assert.Equal(0, h.Learnings.Lookups);
        Assert.Equal(0, h.Protector.Decryptions);
        Assert.Equal(0, h.Http.Requests);
    }

    [Fact]
    public async Task A_refused_netbox_redirect_is_sent_once_and_keeps_its_code()
    {
        using var h = new Harness();
        h.AddSource(null);

        var output = await h.DispatchAsync(h.NetBoxTool(), new { });

        Assert.False(output.Success);
        Assert.Equal(Refused, output.Result.GetProperty("error_code").GetString());
        Assert.Equal(1, h.Http.Requests);
        Assert.Equal(0, h.Learnings.Lookups);
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, output.Result.GetRawText());
    }

    [Fact]
    public async Task An_ordinary_netbox_tool_failure_is_still_offered_a_learned_correction()
    {
        using var h = new Harness();

        var output = await h.DispatchAsync(h.NetBoxTool(), new { source_id = Guid.NewGuid().ToString() });

        Assert.False(output.Success);
        Assert.Equal(1, h.Learnings.Lookups);
        Assert.False(output.Result.TryGetProperty("error_code", out _));
        Assert.Equal(0, h.Http.Requests);
    }

    public static TheoryData<string> GitHubTools => new() { "create_pr", "merge_pr", "create_repo" };

    [Theory]
    [MemberData(nameof(GitHubTools))]
    public async Task A_refused_github_redirect_is_sent_once_and_keeps_its_code(string tool)
    {
        using var h = new Harness();
        var (handler, args) = tool switch
        {
            "create_pr" => (h.CreatePrTool(), (object)new
            {
                credential_id = h.CredentialId, owner = "example-owner", repo = "example-repo",
                title = "Example", head = "feature", @base = "main",
            }),
            "merge_pr" => (h.MergePrTool(), new
            {
                credential_id = h.CredentialId, owner = "example-owner", repo = "example-repo", number = 7,
            }),
            _ => (h.CreateRepoTool(), new { credential_id = h.CredentialId, name = "example-repo", register = false }),
        };

        var output = await h.DispatchAsync(handler, args);

        Assert.False(output.Success);
        Assert.Equal(Refused, output.Result.GetProperty("error_code").GetString());
        Assert.Contains("HTTP 302", output.Result.GetProperty("error").GetString());
        Assert.Equal(1, h.Http.Requests);
        Assert.Equal(0, h.Learnings.Lookups);
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, output.Result.GetRawText());
    }

    [Fact]
    public async Task An_ordinary_github_failure_is_still_offered_a_learned_correction()
    {
        using var h = new Harness();

        // No owner: the service rejects the request before any send.
        var output = await h.DispatchAsync(h.CreatePrTool(), new
        {
            credential_id = h.CredentialId, owner = "", repo = "example-repo", title = "Example", head = "f", @base = "main",
        });

        Assert.False(output.Success);
        Assert.Equal(1, h.Learnings.Lookups);
        Assert.False(output.Result.TryGetProperty("error_code", out _));
        Assert.Equal(0, h.Http.Requests);
    }

    [Fact]
    public async Task A_redirect_refusal_that_escapes_a_handler_is_final_too()
    {
        using var h = new Harness();

        var output = await h.DispatchAsync(new EscapingHandler(), new { });

        Assert.False(output.Success);
        Assert.Equal(Refused, output.Result.GetProperty("error_code").GetString());
        Assert.Equal(1, EscapingHandler.Calls);
        Assert.Equal(0, h.Learnings.Lookups);
    }

    [Fact]
    public void Only_the_final_codes_are_reported_as_error_codes()
    {
        Assert.Equal(Denied, ToolFailure.From(new ForbiddenException("example", Denied)).GetProperty("error_code").GetString());
        Assert.Equal(Refused, ToolFailure.From(new ValidationException("example", Refused)).GetProperty("error_code").GetString());
        Assert.False(ToolFailure.From(new ValidationException("example")).TryGetProperty("error_code", out _));
        Assert.False(ToolFailure.From(new ForbiddenException("example")).TryGetProperty("error_code", out _));
        Assert.False(ToolFailure.From(new NotFoundException("example")).TryGetProperty("error_code", out _));
    }

    private sealed class EscapingHandler : IToolHandler
    {
        public static int Calls;
        public string Name => "execute_operation";
        public string Description => "example";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            throw new RedirectRefusedException(302);
        }
    }

    private sealed class CountingProtector : ISecretProtector
    {
        public int Decryptions;
        public byte[]? Encrypt(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        public string? Decrypt(byte[]? value)
        {
            Decryptions++;
            return value is null ? null : Encoding.UTF8.GetString(value);
        }
    }

    private sealed class FixedProtector : ISecretProtector
    {
        public byte[]? Encrypt(string? plaintext) => plaintext is null ? null : [1];
        public string? Decrypt(byte[]? ciphertext) => ciphertext is null ? null : "example-token";
    }

    // Offers a parameter adjustment for any error, so a retry is always available.
    private sealed class Store : ILearningsStore
    {
        public int Lookups;
        public Task<AgentLearning?> FindBestFixAsync(string toolName, string serviceType, string errorMessage, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult<AgentLearning?>(new AgentLearning
            {
                AgentLearningId = Guid.NewGuid(), FixStrategy = AgentLearning.StrategyParameterAdjust,
                FixParamsJson = """{"dry_run":true}""", Confidence = 0.9,
            });
        }
        public Task RecordOutcomeAsync(Guid learningId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task RecordDiscoveredAsync(string errorPattern, string errorCategory, string serviceType,
            string toolName, string fixStrategy, string? fixParamsJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OpenGuard : IToolResourceGuard
    {
        public Task<string?> DenyReasonAsync(string toolName, JsonElement args, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoAutonomy : IToolAutonomyResolver
    {
        public Task<bool> IsReadOnlyCallAsync(string toolName, JsonElement args, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class NoAudit : IAuditLogger
    {
        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class QuietTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null) { }
        public ITraceScope Begin(string category, string action, object? metadata = null) => new Scope();
        private sealed class Scope : ITraceScope
        {
            public void Complete(object? metadata = null) { }
            public void Fail(string error, object? metadata = null) { }
            public void Dispose() { }
        }
    }
}
