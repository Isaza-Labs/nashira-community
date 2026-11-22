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
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Worker.Python;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// Use the real resolver and seeded records: a fake substitution service cannot
// test authorization. The protector counts decryptions; the sandbox counts calls.
public class SecretAuthorizationTests
{
    private const string Value = "example-token-value";
    private const string Marker = "${secret:secret:protected:value}";
    private const string Denied = "secret_access_denied";

    public static IEnumerable<object[]> Fields()
    {
        yield return ["secret", "value"];
        foreach (var field in new[] { "username", "password", "private_key", "privatekey", "passphrase",
                     "key_passphrase", "token", "api_key", "apikey", "client_secret", "clientsecret", "client_id", "clientid" })
            yield return ["credential", field];
        yield return ["ai_provider", "api_key"];
        yield return ["ai_provider", "apikey"];
        yield return ["integration", "token"];
        yield return ["integration", "basic.password"];
        yield return ["integration", "nested"];
    }

    public static IEnumerable<object?[]> DeniedFields() =>
        from role in new string?[] { "operator", "viewer", null }
        from fields in Fields()
        select new object?[] { role, fields[0], fields[1] };

    [Theory]
    [MemberData(nameof(DeniedFields))]
    public async Task Non_admins_cannot_resolve_any_protected_field_through_either_entry_point(
        string? role, string source, string field)
    {
        using var fixture = new Fixture(role);
        // Names and IDs, normalization, cleartext fields and every field alias.
        foreach (var key in new[] { "protected", fixture.Id.ToString() })
        {
            var error = await Assert.ThrowsAsync<ForbiddenException>(() =>
                fixture.Resolver.ResolveAsync(" " + source.ToUpperInvariant() + " ", key, field, default));
            Assert.Equal(Denied, error.Code);
            Assert.Equal(StatusCodes.Status403Forbidden, error.Status);
            foreach (var allowSession in new[] { false, true })
            {
                error = await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Resolver.SubstituteAsync(
                    $"prefix ${{secret:{source}:{key}:{field}}} suffix", allowSession, default));
                Assert.Equal(Denied, error.Code);
            }
        }
        Assert.Equal(0, fixture.Protector.Decryptions);
        Assert.All(fixture.Trace.Errors, error => Assert.Equal(Denied, error));
        Assert.NotEmpty(fixture.Trace.Errors);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Admins_can_resolve_all_protected_sources_and_fields(string source, string field)
    {
        using var fixture = new Fixture("admin");
        Assert.Equal(Value, await fixture.Resolver.ResolveAsync(source, "protected", field, default));
        Assert.Equal(Value, await fixture.Resolver.SubstituteAsync(
            $"${{secret:{source}:{fixture.Id}:{field}}}", default));
    }

    [Fact]
    public async Task Denial_precedes_even_a_database_lookup_and_does_not_reveal_missing_names()
    {
        using var fixture = new Fixture("operator");
        fixture.Db.Dispose(); // Any attempted lookup is invalid, even before decryption.
        foreach (var key in new[] { "protected", "does-not-exist" })
        {
            var error = await Assert.ThrowsAsync<ForbiddenException>(() =>
                fixture.Resolver.ResolveAsync("secret", key, "value", default));
            Assert.Equal(Denied, error.Code);
        }
        Assert.Equal(0, fixture.Protector.Decryptions);
    }

    [Fact]
    public async Task An_unauthenticated_identity_with_an_admin_role_is_still_denied()
    {
        using var fixture = new Fixture(null);
        var resolver = fixture.CreateResolver(new ClaimedAdminWithoutAuthentication());
        await Assert.ThrowsAsync<ForbiddenException>(() => resolver.SubstituteAsync(Marker, default));
        Assert.Equal(0, fixture.Protector.Decryptions);
    }

    [Fact]
    public async Task Authorization_is_rechecked_after_the_background_principal_loses_admin()
    {
        using var fixture = new Fixture("admin");
        Assert.Equal(Value, await fixture.Resolver.SubstituteAsync(Marker, default));
        fixture.User.Bind(fixture.User.UserId, "test", ["operator"]);
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Resolver.SubstituteAsync(Marker, default));
        Assert.Equal(1, fixture.Protector.Decryptions);
    }

    [Fact]
    public async Task A_denial_in_a_nested_integration_reference_is_not_swallowed_by_the_outer_lookup()
    {
        using var fixture = new Fixture("admin");
        var resolver = fixture.CreateResolver(new AdminOnlyOnFirstCheck());
        var error = await Assert.ThrowsAsync<ForbiddenException>(() =>
            resolver.ResolveAsync("integration", "protected", "nested", default));
        Assert.Equal(Denied, error.Code);
        Assert.Equal(0, fixture.Protector.Decryptions);
    }

    [Fact]
    public async Task Unknown_references_remain_literal_only_for_an_authorized_caller()
    {
        using var fixture = new Fixture("admin");
        const string missing = "${secret:secret:missing:value}";
        Assert.Equal(missing, await fixture.Resolver.SubstituteAsync(missing, default));
        fixture.User.Bind(fixture.User.UserId, "test", ["operator"]);
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Resolver.SubstituteAsync(missing, default));
    }

    [Fact]
    public async Task Public_text_and_the_callers_own_opted_in_session_do_not_grant_stored_secret_access()
    {
        using var fixture = new Fixture("operator");
        Assert.Equal("public", await fixture.Resolver.SubstituteAsync("public", default));
        Assert.Equal(SecretResolver.SessionJwtRef,
            await fixture.Resolver.SubstituteAsync(SecretResolver.SessionJwtRef, default));
        var token = await fixture.Resolver.SubstituteAsync(SecretResolver.SessionJwtRef, true, default);
        Assert.NotEqual(SecretResolver.SessionJwtRef, token);
        Assert.NotEmpty(token);
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Resolver.SubstituteAsync(Marker, true, default));
        Assert.Equal(0, fixture.Protector.Decryptions);
    }

    public static IEnumerable<object[]> ExtractionScripts()
    {
        yield return ["echo", "print(input)\nresult = input['tok']"];
        yield return ["reverse", "result = input['tok'][::-1]"];
        yield return ["base64", "import base64\nresult = base64.b64encode(input['tok'].encode()).decode()"];
        yield return ["split", "result = list(input['tok'])"];
    }

    public static IEnumerable<object?[]> DeniedScripts() =>
        from role in new string?[] { "operator", "viewer", null }
        from script in ExtractionScripts()
        select new object?[] { role, script[0], script[1] };

    [Theory]
    [MemberData(nameof(DeniedScripts))]
    public async Task Extraction_stops_the_step_before_decryption_or_interpreter_execution(
        string? role, string technique, string code)
    {
        using var fixture = new Fixture(role);
        var sandbox = new CountingSandbox();
        var handler = fixture.Handler(sandbox);
        var snippet = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = technique, Type = Snippet.TypePythonSnippet,
            Code = code, Idempotency = "idempotent", ChangesState = false,
            RetryPolicyJson = """{"max_retries":2,"initial_delay_seconds":0}""",
        };
        fixture.Db.Snippets.Add(snippet);
        await fixture.Db.SaveChangesAsync();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var nodes = new SnippetNodeExecutor(fixture.Db, new Registry(handler), provider,
            new VariableResolver(NullLogger<VariableResolver>.Instance), new WorkflowExecutionScope(),
            NullLogger<SnippetNodeExecutor>.Instance);
        var config = JsonSerializer.SerializeToElement(new[]
        {
            new { id = "extract", snippet_id = snippet.SnippetId, config_overrides = new { tok = Marker } },
            new { id = "next", snippet_id = snippet.SnippetId, config_overrides = new { tok = "public" } },
        });
        using var edges = JsonDocument.Parse("""[{"source":"extract","target":"next","type":"success"}]""");
        var result = await new WorkflowExecutor().RunAsync(Dag.Parse(config, edges.RootElement), nodes,
            new WorkflowExecutionContext(Guid.NewGuid(), "test", role ?? "anonymous", DateTime.UtcNow),
            stopOnFailure: true, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        var step = Assert.Single(result.Steps, step => step.NodeId == "extract");
        Assert.Equal(NodeResult.Failed, step.Result);
        Assert.Equal(Denied, step.ErrorCode);
        Assert.False(step.Retryable);
        Assert.Equal(1, step.Attempts);
        Assert.DoesNotContain(result.Steps, step => step.NodeId == "next" && step.Result != NodeResult.Skipped);
        Assert.DoesNotContain(Value, JsonSerializer.Serialize(result));
        Assert.Equal(0, fixture.Protector.Decryptions);
        Assert.Equal(0, sandbox.Calls);
    }

    [Theory]
    [MemberData(nameof(ExtractionScripts))]
    public async Task Authorized_controls_execute_the_real_scripts_and_masking_is_only_additional_protection(
        string technique, string code)
    {
        // Missing Python must not silently turn this acceptance test into a pass.
        Assert.NotNull(PythonSandboxTests.Exe);
        using var fixture = new Fixture("admin");
        var sandbox = new CountingSandbox();
        var result = await fixture.Handler(sandbox).ExecuteAsync(new SnippetRequest
        {
            NodeId = "py", WorkflowId = Guid.NewGuid(), SnippetId = Guid.NewGuid(),
            SnippetType = Snippet.TypePythonSnippet, Code = code,
            Input = JsonSerializer.SerializeToElement(new { tok = Marker }), TimeoutSeconds = 10,
        }, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, sandbox.Calls);
        Assert.Equal(1, fixture.Protector.Decryptions);
        Assert.DoesNotContain(Value, result.Logs ?? "");
        var recovered = technique switch
        {
            "echo" => result.Output.GetString(),
            "reverse" => new string(result.Output.GetString()!.Reverse().ToArray()),
            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(result.Output.GetString()!)),
            "split" => string.Concat(result.Output.EnumerateArray().Select(x => x.GetString())),
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(technique == "echo" ? "[redacted]" : Value, recovered);
    }

    private sealed class Fixture : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"secret-authorization-{Guid.NewGuid()}").Options);
        public MutableCurrentUser User { get; } = new();
        public CountingProtector Protector { get; } = new();
        public RecordingTrace Trace { get; } = new();
        public ISecretResolver Resolver { get; }

        public Fixture(string? role)
        {
            if (role is not null) User.Bind(Guid.NewGuid(), "test", [role]);
            var cipher = Encoding.UTF8.GetBytes(Value);
            Db.Secrets.Add(new Secret { SecretId = Id, Name = "protected", EncryptedValue = cipher });
            Db.Credentials.Add(new Credential
            {
                CredentialId = Id, Name = "protected", Username = Value, ClientId = Value,
                EncryptedPassword = cipher, EncryptedPrivateKey = cipher, EncryptedKeyPassphrase = cipher,
                EncryptedToken = cipher, EncryptedClientSecret = cipher,
            });
            Db.AIProviders.Add(new AIProvider
                { AIProviderId = Id, Name = "protected", Type = "openai", EncryptedApiKey = cipher });
            Db.Integrations.Add(new Integration
            {
                IntegrationId = Id, Name = "protected", Slug = "protected", Type = "generic",
                BaseUrl = "https://example.invalid",
                AuthConfig = JsonSerializer.Serialize(new { token = Value, basic = new { password = Value }, nested = Marker }),
            });
            Db.AllowedPythonModules.Add(new AllowedPythonModule
            {
                AllowedPythonModuleId = Guid.NewGuid(), Module = "base64",
                Source = AllowedPythonModule.SourceStdlib, Status = AllowedPythonModule.StatusReady,
            });
            Db.SaveChanges();
            Resolver = CreateResolver(User);
        }

        public SecretResolver CreateResolver(ICurrentUser user) => new(Db, Protector, user,
            new HttpContextAccessor { HttpContext = null }, new JwtTokenService(Options.Create(new JwtOptions
            {
                Issuer = "nashira", Audience = "nashira", AccessTokenMinutes = 15,
                Key = "test-signing-key-that-is-long-enough-for-hmac-256",
            }), NullLogger<JwtTokenService>.Instance), Trace, NullLogger<SecretResolver>.Instance);

        public PythonSnippetHandler Handler(IPythonSandbox sandbox) => new(Db, sandbox, Resolver,
            new ConfigurationBuilder().Build(), NullLogger<PythonSnippetHandler>.Instance);
        public void Dispose() => Db.Dispose();
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

    private sealed class CountingSandbox : IPythonSandbox
    {
        public int Calls;
        public Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct)
        {
            Calls++;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Python:Executable"] = PythonSandboxTests.Exe ?? "python3",
                // These test-only processes handle synthetic inputs, not deployment isolation.
                ["Python:AllowUnsandboxed"] = "true",
            }).Build();
            return new PythonSandbox(config, NullLogger<PythonSandbox>.Instance, new HardeningTestUser()).RunAsync(request, ct);
        }
    }

    private sealed class Registry(ISnippetHandler handler) : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => [handler.Type];
        public ISnippetHandler? Resolve(string type, IServiceProvider services) => handler;
    }

    private sealed class ClaimedAdminWithoutAuthentication : ICurrentUser
    {
        public Guid UserId => throw new InvalidOperationException();
        public string? Username => null;
        public IReadOnlyList<string> Roles => ["admin"];
        public bool IsAuthenticated => false;
    }

    private sealed class AdminOnlyOnFirstCheck : ICurrentUser
    {
        private int _checks;
        public Guid UserId { get; } = Guid.NewGuid();
        public string? Username => "test";
        public IReadOnlyList<string> Roles => ++_checks == 1 ? ["admin"] : ["operator"];
        public bool IsAuthenticated => true;
    }

    private sealed class RecordingTrace : ITraceLogger
    {
        public List<string?> Errors { get; } = [];
        public void Event(string category, string action, object? metadata = null, string? error = null, int? durationMs = null)
            => Errors.Add(error);
        public ITraceScope Begin(string category, string action, object? metadata = null) => throw new NotSupportedException();
    }
}
