using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// `ssh`, `rest_call` and `integration_action` have always substituted ${secret:…} out of
// their input before acting on it. `python_snippet` did not, and the gap had a shape: a
// script that needed a device password could only be given one as a LITERAL in the node's
// config_overrides — which is stored in the workflow definition, travels in every bundle
// export, and is echoed back in the step's input snapshot on every run. The credential
// store exists so that never happens, and this was the one step type that could not reach
// it.
//
// The tests below pin the three properties that make the substitution worth having:
// it reaches nested values, it does not touch anything else, and an unresolvable
// reference stays literal so a typo is visible rather than silently empty.
public class PythonSnippetSecretsTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"pysecrets-{Guid.NewGuid()}").Options);

    // Substitutes what it was told about and leaves the rest literal, exactly like the
    // real resolver — handlers key off that surviving marker to tell "resolved" from
    // "no such credential".
    private sealed class FakeSecrets : ISecretResolver
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public int Calls;

        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string> SubstituteAsync(string template, CancellationToken ct) =>
            SubstituteAsync(template, false, ct);

        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct)
        {
            Calls++;
            var result = template;
            foreach (var (k, v) in Values) result = result.Replace(k, v, StringComparison.Ordinal);
            return Task.FromResult(result);
        }
    }

    // Captures the input the sandbox was handed, then returns a fixed success so the test
    // never depends on a Python interpreter being present.
    private sealed class CapturingSandbox : IPythonSandbox
    {
        public JsonElement Input;

        public Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct)
        {
            Input = request.Input.Clone();
            return Task.FromResult(new PythonRunResult(
                true, JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
                string.Empty, string.Empty, null, 0));
        }
    }

    private static void Allow(AppDbContext db, string module)
    {
        db.AllowedPythonModules.Add(new AllowedPythonModule
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            Module = module,
            Source = AllowedPythonModule.SourceStdlib,
            Status = AllowedPythonModule.StatusReady,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static async Task<JsonElement> RunAsync(
        AppDbContext db, string inputJson, FakeSecrets secrets, CapturingSandbox sandbox)
    {
        var handler = new PythonSnippetHandler(
            db, sandbox, secrets, new ConfigurationBuilder().Build(),
            NullLogger<PythonSnippetHandler>.Instance);

        var result = await handler.ExecuteAsync(new SnippetRequest
        {
            NodeId = "py",
            WorkflowId = Guid.NewGuid(),
            SnippetId = Guid.NewGuid(),
            SnippetType = Snippet.TypePythonSnippet,
            Code = "result = 1",
            Input = JsonDocument.Parse(inputJson).RootElement.Clone(),
            TimeoutSeconds = 30,
        }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        return sandbox.Input;
    }

    [Fact]
    public async Task A_credential_reference_reaches_the_script_as_plaintext()
    {
        using var db = NewDb();
        var secrets = new FakeSecrets();
        secrets.Values["${secret:credential:core-sw:username}"] = "netops";
        secrets.Values["${secret:credential:core-sw:password}"] = "hunter2";
        var sandbox = new CapturingSandbox();

        var input = await RunAsync(
            db,
            """
            {"host":"10.0.0.1",
             "username":"${secret:credential:core-sw:username}",
             "password":"${secret:credential:core-sw:password}"}
            """,
            secrets, sandbox);

        Assert.Equal("netops", input.GetProperty("username").GetString());
        Assert.Equal("hunter2", input.GetProperty("password").GetString());
        // The one that matters: the marker is gone, so nothing downstream can mistake it
        // for a value the script is supposed to send to a device.
        Assert.DoesNotContain("${secret:", input.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reference_nested_in_an_array_is_substituted_too()
    {
        using var db = NewDb();
        var secrets = new FakeSecrets();
        secrets.Values["${secret:secret:new-enable-pw:value}"] = "s3cret";
        var sandbox = new CapturingSandbox();

        // The shape the interactive mode actually uses: the credential is not a top-level
        // field, it is the payload of one step deep inside an array of objects. A resolver
        // that only walked the top level would leave this one literal and the device would
        // be sent the marker text as its new password.
        var input = await RunAsync(
            db,
            """
            {"mode":"shell","host":"h","username":"u","password":"p",
             "steps":[{"send":"set password"},
                      {"send":"${secret:secret:new-enable-pw:value}","secret":true}]}
            """,
            secrets, sandbox);

        var steps = input.GetProperty("steps");
        Assert.Equal("set password", steps[0].GetProperty("send").GetString());
        Assert.Equal("s3cret", steps[1].GetProperty("send").GetString());
        Assert.True(steps[1].GetProperty("secret").GetBoolean());
    }

    [Fact]
    public async Task An_unresolvable_reference_stays_literal_rather_than_becoming_empty()
    {
        using var db = NewDb();
        var secrets = new FakeSecrets();   // knows nothing
        var sandbox = new CapturingSandbox();

        var input = await RunAsync(
            db, """{"password":"${secret:credential:typo:password}"}""", secrets, sandbox);

        // An empty string would reach the device as a blank password and fail as
        // "authentication failed", pointing the operator at the credential instead of at
        // the name they mistyped.
        Assert.Equal("${secret:credential:typo:password}", input.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Values_without_a_reference_are_left_alone_and_never_reach_the_resolver()
    {
        using var db = NewDb();
        Allow(db, "json");
        var secrets = new FakeSecrets();
        var sandbox = new CapturingSandbox();

        var input = await RunAsync(
            db,
            """{"host":"10.0.0.1","port":22,"stop_on_error":true,"commands":["show version"],"nothing":null}""",
            secrets, sandbox);

        Assert.Equal(0, secrets.Calls);
        Assert.Equal("10.0.0.1", input.GetProperty("host").GetString());
        Assert.Equal(22, input.GetProperty("port").GetInt32());
        Assert.True(input.GetProperty("stop_on_error").GetBoolean());
        Assert.Equal("show version", input.GetProperty("commands")[0].GetString());
        Assert.Equal(JsonValueKind.Null, input.GetProperty("nothing").ValueKind);
    }
}
