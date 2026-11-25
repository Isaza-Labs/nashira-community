using System.Net;
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
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// A reference to the caller's own session (`${secret:session:current:jwt}`) is how the built-in
// `na_*` specs authenticate. It lends the caller's own bearer to this backend and reads no stored
// secret, so a step that uses it keeps the output rules it always had. Only a reference that reads
// stored material (a secret, credential, provider key or integration) withholds the step's output.
// The path is real end to end: workflow executor, step executor, `rest_call` handler, catalogue
// executor and secret resolver; only the transport and the spec index are test doubles.
public class SessionReferencePrivacyTests
{
    private const string ApiName = "example-api";
    private const string StoredValue = "example-stored-value";
    private const string Healthy = "example-healthy-marker";

    private sealed class Rig : IDisposable
    {
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session-reference-privacy-{Guid.NewGuid()}").Options);
        public Capture Http { get; } = new();
        public CountingProtector Protector { get; } = new();
        public Guid SnippetId { get; } = Guid.NewGuid();
        private readonly MutableCurrentUser _user = new();

        public Rig(string role, string bearerConfig, string? baseUrl)
        {
            _user.Bind(Guid.NewGuid(), "example-user", [role]);
            Db.Secrets.Add(new Secret
            {
                SecretId = Guid.NewGuid(), Name = "example-token", EncryptedValue = Encoding.UTF8.GetBytes(StoredValue),
            });
            Db.AiApiSpecs.Add(new AiApiSpec
            {
                AiApiSpecId = Guid.NewGuid(), Api = ApiName, Content = "openapi: 3.0.0", OperationCount = 1,
                BaseUrl = baseUrl, AuthType = "bearer", AuthConfig = bearerConfig, IsActive = true,
            });
            Db.Snippets.Add(new Snippet
            {
                SnippetId = SnippetId, Name = "status", Type = Snippet.TypeRestCall, ChangesState = false,
                Idempotency = "idempotent",
            });
            Db.SaveChanges();
            _resolver = new SecretResolver(Db, Protector, _user, new HttpContextAccessor { HttpContext = null },
                new JwtTokenService(Options.Create(new JwtOptions
                {
                    Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                    Key = "test-signing-key-that-is-long-enough-for-hmac-256",
                }), NullLogger<JwtTokenService>.Instance),
                new QuietTrace(), NullLogger<SecretResolver>.Instance);
        }

        private readonly SecretResolver _resolver;

        public async Task<WorkflowRunResult> RunAsync(bool redactOutput)
        {
            var executorForSpec = new RestOperationExecutor(
                Db, new OneOperationIndex(), _resolver, new RedirectRefusalMappingTests.AllowAllGuard(), _user, Http,
                new RedirectRefusalMappingTests.NoAuth(), new ConfigurationBuilder().Build(),
                NullLogger<RestOperationExecutor>.Instance);
            var handler = new RestCallSnippetHandler(
                executorForSpec, new RedirectRefusalMappingTests.AllowAllGuard(), _resolver, Http);
            using var services = new ServiceCollection().BuildServiceProvider();
            var executor = new SnippetNodeExecutor(Db, new Registry(handler), services,
                new VariableResolver(NullLogger<VariableResolver>.Instance), new WorkflowExecutionScope(),
                NullLogger<SnippetNodeExecutor>.Instance);
            var nodes = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    id = "status", snippet_id = SnippetId,
                    config_overrides = new { operation_id = "get_status", redact_output = redactOutput },
                },
            });
            return await new WorkflowExecutor().RunAsync(
                Dag.Parse(nodes, JsonSerializer.SerializeToElement(Array.Empty<object>())), executor,
                new WorkflowExecutionContext(Guid.NewGuid(), "test", "test", DateTime.UtcNow), true, default);
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task A_step_that_authenticates_with_the_session_reference_keeps_its_visible_output(string role)
    {
        using var rig = new Rig(role, $$"""{"value":"{{SecretResolver.SessionJwtRef}}"}""", baseUrl: null);

        var run = await rig.RunAsync(redactOutput: false);

        Assert.Equal("completed", run.Status);
        var step = Assert.Single(run.Steps);
        Assert.Contains(Healthy, step.Output);
        Assert.DoesNotContain("[redacted]", step.Output);
        // The reference did resolve: the request carried a bearer, and no stored secret was read.
        Assert.StartsWith("Bearer ", rig.Http.Authorization);
        Assert.True(rig.Http.Authorization!.Length > "Bearer ".Length);
        Assert.Equal(0, rig.Protector.Decryptions);
    }

    [Fact]
    public async Task The_session_reference_still_honours_an_explicit_request_to_redact()
    {
        using var rig = new Rig("operator", $$"""{"value":"{{SecretResolver.SessionJwtRef}}"}""", baseUrl: null);

        var run = await rig.RunAsync(redactOutput: true);

        var step = Assert.Single(run.Steps);
        Assert.Equal("\"[redacted]\"", step.Output);
    }

    [Fact]
    public async Task A_step_that_authenticates_with_a_stored_secret_is_still_withheld()
    {
        using var rig = new Rig("admin", """{"value":"${secret:secret:example-token:value}"}""", baseUrl: "https://203.0.113.20");

        var run = await rig.RunAsync(redactOutput: false);

        Assert.Equal("completed", run.Status);
        var step = Assert.Single(run.Steps);
        Assert.Equal("\"[redacted]\"", step.Output);
        Assert.DoesNotContain(Healthy, JsonSerializer.Serialize(run));
        Assert.Equal($"Bearer {StoredValue}", rig.Http.Authorization);
        Assert.Equal(1, rig.Protector.Decryptions);
    }

    // Answers the one operation with a small JSON body and records the bearer it was sent.
    private sealed class Capture : IHttpClientFactory
    {
        public string? Authorization;

        public HttpClient CreateClient(string name) => new(new Transport(this));

        private sealed class Transport(Capture owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Authorization = request.Headers.Authorization?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"status":"{{Healthy}}"}""", Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    private sealed class OneOperationIndex : IApiSpecIndex
    {
        private readonly ApiOperation _op = new()
        {
            OperationId = "get_status", Api = ApiName, Method = "GET", Path = "/status",
        };
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [_op];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [_op];
        public ApiOperation? GetByOperationId(string operationId) => operationId == _op.OperationId ? _op : null;
    }

    private sealed class Registry(ISnippetHandler handler) : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => [handler.Type];
        public ISnippetHandler? Resolve(string type, IServiceProvider services) => handler;
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

    private sealed class QuietTrace : ITraceLogger
    {
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null) { }
        public ITraceScope Begin(string category, string action, object? metadata = null) => throw new NotSupportedException();
    }
}
