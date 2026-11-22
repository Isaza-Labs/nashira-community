using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.SelfCorrection;
using nashira_backend.Services.Ai.Tools;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Net;
using nashira_backend.Services.Trace;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Tests;

// An authorization denial from the integration auth path is not a statement about the
// system being probed or about the arguments of a tool call. The operator-facing health
// check must answer 403 instead of persisting a false "unreachable", and the agent's
// tool dispatcher must report the denial once, with a stable code and no retry.
public class IntegrationHealthDenialTests
{
    private static readonly ForbiddenException Denial =
        new(SecretAccessPolicy.DeniedMessage, SecretResolver.AccessDeniedCode);

    [Fact]
    public async Task An_authorization_denial_is_not_reported_as_an_unreachable_system()
    {
        var checker = Checker(new ThrowingApplier(Denial));

        var error = await Assert.ThrowsAsync<ForbiddenException>(
            () => checker.CheckAsync(NewIntegration(), default));

        Assert.Equal(SecretResolver.AccessDeniedCode, error.Code);
        Assert.Equal(403, error.Status);
    }

    [Fact]
    public async Task Any_other_failure_is_still_reported_as_unreachable()
    {
        var checker = Checker(new ThrowingApplier(new InvalidOperationException("example token endpoint failed")));

        var health = await checker.CheckAsync(NewIntegration(), default);

        Assert.Equal(IntegrationEntity.StatusUnreachable, health.Status);
        Assert.Equal("example token endpoint failed", health.Error);
    }

    [Fact]
    public async Task A_denied_operator_check_leaves_the_recorded_status_untouched()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"health-denial-{Guid.NewGuid()}").Options);
        var row = NewIntegration();
        row.Status = IntegrationEntity.StatusHealthy;
        row.LastCheckError = null;
        db.Integrations.Add(row);
        await db.SaveChangesAsync();
        var controller = new IntegrationController(
            db, new Operator(), Checker(new ThrowingApplier(Denial)), null!, null!, null!);

        await Assert.ThrowsAsync<ForbiddenException>(() => controller.Check(row.IntegrationId, default));

        var stored = await db.Integrations.AsNoTracking().SingleAsync(i => i.IntegrationId == row.IntegrationId);
        Assert.Equal(IntegrationEntity.StatusHealthy, stored.Status);
        Assert.Null(stored.LastCheckError);
        Assert.Null(stored.LastCheckedAt);
    }

    private static IntegrationHealthChecker Checker(IIntegrationAuthApplier applier) =>
        new(applier, new EchoResolver(), new AllowAllGuard(), new NoNetwork(), NullLogger<IntegrationHealthChecker>.Instance);

    private static IntegrationEntity NewIntegration() => new()
    {
        IntegrationId = Guid.NewGuid(), Name = "example-api", Slug = "example-api", Type = "generic",
        BaseUrl = "https://203.0.113.10", HealthCheckPath = "/status",
        AuthConfig = """{"method":"oauth2_client_credentials"}""",
    };

    private sealed class ThrowingApplier(Exception error) : IIntegrationAuthApplier
    {
        public Task ApplyAsync(HttpRequestMessage request, IntegrationEntity integration, CancellationToken ct) =>
            Task.FromException(error);
    }

    private sealed class EchoResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string> SubstituteAsync(string template, CancellationToken ct) => Task.FromResult(template);
        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
            Task.FromResult(template);
    }

    private sealed class AllowAllGuard : IUrlGuard
    {
        public void EnsureSafe(string url, bool allowPrivate = false) { }
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in this test");
    }

    private sealed class Operator : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "operator-user";
        public IReadOnlyList<string> Roles => ["operator"];
        public bool IsAuthenticated => true;
    }
}

public class ToolDispatcherDenialTests
{
    [Fact]
    public async Task A_secret_access_denial_is_reported_with_its_code_and_is_not_retried()
    {
        var kit = new Kit(new ForbiddenException(SecretAccessPolicy.DeniedMessage, SecretResolver.AccessDeniedCode));

        var output = await kit.DispatchAsync();

        Assert.False(output.Success);
        Assert.Equal(SecretAccessPolicy.DeniedMessage, output.Result.GetProperty("error").GetString());
        Assert.Equal(SecretResolver.AccessDeniedCode, output.Result.GetProperty("code").GetString());
        Assert.Equal(1, kit.HandlerCalls);
        Assert.Equal(0, kit.LearningLookups);
        Assert.DoesNotContain(kit.Logs, l => l.Level >= LogLevel.Error);
        Assert.DoesNotContain(kit.Logs, l => l.HadException);
    }

    [Fact]
    public async Task Other_failures_keep_the_existing_path_with_logging_and_self_correction()
    {
        var kit = new Kit(new InvalidOperationException("example failure"));

        var output = await kit.DispatchAsync();

        Assert.False(output.Success);
        Assert.Equal("example failure", output.Result.GetProperty("error").GetString());
        Assert.False(output.Result.TryGetProperty("code", out _));
        Assert.Equal(1, kit.LearningLookups);
        Assert.Contains(kit.Logs, l => l.Level == LogLevel.Error && l.HadException);
    }

    [Fact]
    public async Task A_forbidden_error_with_another_code_is_not_given_the_denial_treatment()
    {
        var kit = new Kit(new ForbiddenException("example forbidden", "forbidden"));

        var output = await kit.DispatchAsync();

        Assert.False(output.Success);
        Assert.False(output.Result.TryGetProperty("code", out _));
        Assert.Equal(1, kit.LearningLookups);
    }

    private sealed class Kit
    {
        private readonly ToolDispatcher _dispatcher;
        private readonly Handler _handler;
        private readonly Store _store = new();
        private readonly RecordingLogger _logger = new();

        public int HandlerCalls => _handler.Calls;
        public int LearningLookups => _store.Lookups;
        public IReadOnlyList<LogEntry> Logs => _logger.Entries;

        public Kit(Exception handlerError)
        {
            _handler = new Handler(handlerError);
            var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
            registry.Register(_handler);
            var services = new ServiceCollection().AddSingleton(_handler).BuildServiceProvider();
            _dispatcher = new ToolDispatcher(
                registry, services, new Admin(),
                new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
                new OpenGuard(), new NoAutonomy(), new NoAudit(),
                new SelfCorrectionEngine(_store, NullLogger<SelfCorrectionEngine>.Instance),
                _logger, new NoTrace());
        }

        public Task<ToolCallOutput> DispatchAsync() =>
            _dispatcher.DispatchAsync("execute_operation", JsonDocument.Parse("""{"operation_id":"example_op"}""").RootElement, default);
    }

    private sealed class Handler(Exception error) : IToolHandler
    {
        public int Calls;
        public string Name => "execute_operation";
        public string Description => "example";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            throw error;
        }
    }

    private sealed class Store : ILearningsStore
    {
        public int Lookups;
        public Task<AgentLearning?> FindBestFixAsync(string toolName, string serviceType, string errorMessage, CancellationToken ct)
        {
            Lookups++;
            return Task.FromResult<AgentLearning?>(null);
        }
        public Task RecordOutcomeAsync(Guid learningId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task RecordDiscoveredAsync(string errorPattern, string errorCategory, string serviceType,
            string toolName, string fixStrategy, string? fixParamsJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record LogEntry(LogLevel Level, bool HadException);

    private sealed class RecordingLogger : ILogger<ToolDispatcher>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new LogEntry(logLevel, exception is not null));
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

    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "admin-user";
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => true;
    }

    private sealed class NoTrace : ITraceLogger
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
