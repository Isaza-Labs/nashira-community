using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// A python_snippet step is handed the plaintext behind every ${secret:…} in its input, which is
// what lets a script log in to a device without the password being stored in the workflow. The
// step's output and logs are another matter: they are stored on the run and are readable by
// anyone who can open it, and a script that returns (or prints, or fails with) its input put the
// plaintext there — a person who may not read the secret store could read the secret by running
// the workflow.
//
// The script still receives the plaintext; what comes back out is masked. This does not make a
// script that deliberately re-encodes a value safe to run — see SecretRedactor.
public class PythonSnippetSecretLeakTests
{
    private const string Token = "example-token-value";
    private const string Marker = "${secret:secret:example-api-token:value}";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"pyleak-{Guid.NewGuid()}").Options);

    // Substitutes what it was told about and leaves the rest literal, like the real resolver.
    private sealed class FakeSecrets : ISecretResolver
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string> SubstituteAsync(string template, CancellationToken ct) =>
            SubstituteAsync(template, false, ct);

        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct)
        {
            var result = template;
            foreach (var (k, v) in Values) result = result.Replace(k, v, StringComparison.Ordinal);
            return Task.FromResult(result);
        }
    }

    // Behaves like the script `result = {"resolved": input}` that also prints its input, or
    // like one that raises with its input in the message.
    private sealed class EchoSandbox : IPythonSandbox
    {
        public bool Fail;
        public string? PrintOverride;
        public JsonElement Received;

        public Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct)
        {
            Received = request.Input.Clone();
            var echoed = request.Input.GetRawText();
            var printed = PrintOverride ?? echoed;
            if (Fail)
                return Task.FromResult(new PythonRunResult(
                    false, JsonDocument.Parse("{}").RootElement.Clone(), printed,
                    "KeyError: " + echoed, "script_error", 1));

            var output = JsonSerializer.SerializeToElement(new { resolved = request.Input });
            return Task.FromResult(new PythonRunResult(true, output, printed, string.Empty, null, 0));
        }
    }

    private static async Task<SnippetResult> RunAsync(
        IPythonSandbox sandbox, string inputJson, FakeSecrets secrets, string code = "result = 1")
    {
        using var db = NewDb();
        var handler = new PythonSnippetHandler(
            db, sandbox, secrets, new ConfigurationBuilder().Build(),
            NullLogger<PythonSnippetHandler>.Instance);

        return await handler.ExecuteAsync(new SnippetRequest
        {
            NodeId = "py",
            WorkflowId = Guid.NewGuid(),
            SnippetId = Guid.NewGuid(),
            SnippetType = Snippet.TypePythonSnippet,
            Code = code,
            Input = JsonDocument.Parse(inputJson).RootElement.Clone(),
            TimeoutSeconds = 30,
        }, CancellationToken.None);
    }

    private static FakeSecrets WithToken()
    {
        var secrets = new FakeSecrets();
        secrets.Values[Marker] = Token;
        return secrets;
    }

    [Fact]
    public async Task A_script_that_returns_its_input_does_not_return_a_resolved_secret()
    {
        var sandbox = new EchoSandbox();

        var result = await RunAsync(sandbox, $$"""{"changes":false,"tok":"{{Marker}}"}""", WithToken());

        Assert.True(result.Success, result.Error);
        // The script was given the plaintext — using a secret still works …
        Assert.Equal(Token, sandbox.Received.GetProperty("tok").GetString());
        // … and what the step stores and shows does not carry it.
        Assert.DoesNotContain(Token, result.Output.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Logs ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("[redacted]", result.Output.GetProperty("resolved").GetProperty("tok").GetString());
    }

    [Fact]
    public async Task A_failing_script_does_not_leak_a_resolved_secret_through_its_error()
    {
        var result = await RunAsync(new EchoSandbox { Fail = true }, $$"""{"tok":"{{Marker}}"}""", WithToken());

        Assert.False(result.Success);
        Assert.DoesNotContain(Token, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Output.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Logs ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_inside_a_longer_value_is_masked_and_the_rest_is_kept()
    {
        var result = await RunAsync(
            new EchoSandbox(), $$"""{"header":"Bearer {{Marker}} suffix","host":"203.0.113.1"}""", WithToken());

        var resolved = result.Output.GetProperty("resolved");
        Assert.Equal("Bearer [redacted] suffix", resolved.GetProperty("header").GetString());
        Assert.Equal("203.0.113.1", resolved.GetProperty("host").GetString());
    }

    [Fact]
    public async Task A_secret_printed_in_its_json_escaped_form_is_masked_in_the_logs()
    {
        const string awkward = "pa\"ss\\word-1234";
        var secrets = new FakeSecrets();
        secrets.Values[Marker] = awkward;
        // What `print(json.dumps(secret))` leaves in the logs: the escaped body, as Python writes it.
        const string escaped = "pa\\\"ss\\\\word-1234";

        var result = await RunAsync(
            new EchoSandbox { PrintOverride = $"password is {escaped}" }, $$"""{"tok":"{{Marker}}"}""", secrets);

        Assert.DoesNotContain(escaped, result.Logs ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("[redacted]", result.Output.GetProperty("resolved").GetProperty("tok").GetString());
    }

    [Fact]
    public async Task An_unresolved_reference_is_not_treated_as_a_secret()
    {
        var result = await RunAsync(new EchoSandbox(), """{"tok":"${secret:secret:typo:value}"}""", new FakeSecrets());

        // The marker the author typed stays visible so the typo can be found.
        Assert.Equal(
            "${secret:secret:typo:value}",
            result.Output.GetProperty("resolved").GetProperty("tok").GetString());
    }

    [Fact]
    public async Task A_credential_username_is_given_to_the_script_but_not_masked()
    {
        var secrets = WithToken();
        secrets.Values["${secret:credential:core-sw:username}"] = "admin";
        secrets.Values["${secret:credential:core-sw:password}"] = "hunter2-hunter2";

        var result = await RunAsync(
            new EchoSandbox(),
            $$"""{"user":"${secret:credential:core-sw:username}","pw":"${secret:credential:core-sw:password}"}""",
            secrets);

        // A username is stored in the clear and shows up in device output all the time; only
        // the password is a secret.
        var resolved = result.Output.GetProperty("resolved");
        Assert.Equal("admin", resolved.GetProperty("user").GetString());
        Assert.Equal("[redacted]", resolved.GetProperty("pw").GetString());
    }

    [Fact]
    public async Task A_real_script_that_echoes_its_input_returns_no_secret()
    {
        var exe = PythonSandboxTests.Exe;
        if (exe is null) return;

        var sandbox = new PythonSandbox(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Python:Executable"] = exe,
                ["Python:AllowUnsandboxed"] = "true",
            }).Build(),
            NullLogger<PythonSandbox>.Instance, new HardeningTestUser());

        var result = await RunAsync(
            sandbox, $$"""{"changes":false,"tok":"{{Marker}}"}""", WithToken(),
            code: "print(input)\nresult = {'resolved': input}");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(Token, result.Output.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.Logs ?? string.Empty, StringComparison.Ordinal);
    }
}
