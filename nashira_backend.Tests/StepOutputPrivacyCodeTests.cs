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

// Withholding a step's output must hide the payload, logs and error text. It may still
// report which host decision stopped the step, because that decision is made before, or
// instead of, running any snippet or integration code and names a fixed condition. Every
// other code is replaced, since handlers build those from script exits, remote responses
// and status lines.
public class StepOutputPrivacyCodeTests
{
    private const string Secret = "example-token-value";

    private static SnippetResult Failure(string code) => new()
    {
        Success = false,
        Change = StepChange.AuthorDecides,
        Retryable = false,
        ErrorCode = code,
        Output = JsonSerializer.SerializeToElement(new { leaked = Secret }),
        Logs = Secret,
        Error = $"failed with {Secret}",
    };

    [Theory]
    [InlineData("secret_access_denied")]
    [InlineData("unsandboxed_admin_required")]
    [InlineData("sandbox_not_configured")]
    [InlineData("sandbox_invalid")]
    [InlineData("runtime_unavailable")]
    public void Host_decided_codes_survive_but_payload_logs_and_error_are_withheld(string code)
    {
        var withheld = StepOutputPrivacy.Withhold(Failure(code));

        Assert.False(withheld.Success);
        Assert.Equal(code, withheld.ErrorCode);
        Assert.Equal(StepOutputPrivacy.Redacted, withheld.Error);
        Assert.Equal(StepOutputPrivacy.Redacted, withheld.Logs);
        Assert.Equal(StepOutputPrivacy.Redacted, withheld.Output.GetString());
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(withheld));
    }

    [Theory]
    [InlineData("script_error")]
    [InlineData("timeout")]
    [InlineData("bad_output")]
    [InlineData("sandbox_error")]
    [InlineData("http_401")]
    [InlineData("tool_error")]
    [InlineData("exception")]
    [InlineData("example-token-value")]
    [InlineData("Secret_Access_Denied")]
    [InlineData(" secret_access_denied")]
    [InlineData("sandbox_invalid:example-token-value")]
    [InlineData("")]
    public void Any_other_code_becomes_output_redacted(string code)
    {
        var withheld = StepOutputPrivacy.Withhold(Failure(code));

        Assert.Equal("output_redacted", withheld.ErrorCode);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(withheld));
    }

    [Fact]
    public void A_failure_without_a_code_becomes_output_redacted()
    {
        var result = new SnippetResult
        {
            Success = false, Change = StepChange.AuthorDecides, Error = Secret,
            Output = JsonSerializer.SerializeToElement(new { v = Secret }),
        };
        Assert.Equal("output_redacted", StepOutputPrivacy.Withhold(result).ErrorCode);
    }

    [Fact]
    public void Retryable_and_change_are_kept_as_reported_for_a_kept_code()
    {
        var withheld = StepOutputPrivacy.Withhold(Failure("secret_access_denied"));
        Assert.False(withheld.Retryable);
        Assert.Equal(StepChange.AuthorDecides, withheld.Change);
    }

    [Fact]
    public void A_success_never_carries_a_code_even_when_one_was_set()
    {
        var ok = new SnippetResult
        {
            Success = true, Change = StepChange.AuthorDecides, ErrorCode = "secret_access_denied",
            Output = JsonSerializer.SerializeToElement(new { v = Secret }), Logs = Secret,
        };
        var withheld = StepOutputPrivacy.Withhold(ok);
        Assert.True(withheld.Success);
        Assert.Null(withheld.ErrorCode);
        Assert.Equal(string.Empty, withheld.Error);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(withheld));
    }

    // The Python handler's withheld path does not copy the sandbox result, so the code has to
    // be handed to Withhold explicitly. A sandbox gate reaches the caller; a failing script does not.
    private sealed class FixedSandbox(string kind, string stderr) : IPythonSandbox
    {
        public Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct) =>
            Task.FromResult(new PythonRunResult(
                false, JsonDocument.Parse("{}").RootElement.Clone(), Secret, stderr, kind, 1));
    }

    private sealed class NoSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string> SubstituteAsync(string template, CancellationToken ct) => Task.FromResult(template);
        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
            Task.FromResult(template);
    }

    private static async Task<SnippetResult> RunPythonAsync(string kind)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"stepoutput-codes-{Guid.NewGuid()}").Options);
        var handler = new PythonSnippetHandler(
            db, new FixedSandbox(kind, $"Traceback {Secret}"), new NoSecrets(),
            new ConfigurationBuilder().Build(), NullLogger<PythonSnippetHandler>.Instance);
        return await handler.ExecuteAsync(new SnippetRequest
        {
            NodeId = "py", WorkflowId = Guid.NewGuid(), SnippetId = Guid.NewGuid(),
            SnippetType = Snippet.TypePythonSnippet, Code = "result = 1",
            Input = JsonSerializer.SerializeToElement(new { redact_output = true }), TimeoutSeconds = 30,
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("unsandboxed_admin_required")]
    [InlineData("sandbox_not_configured")]
    [InlineData("sandbox_invalid")]
    [InlineData("runtime_unavailable")]
    public async Task Python_sandbox_gates_are_reported_under_redact_output(string kind)
    {
        var result = await RunPythonAsync(kind);

        Assert.False(result.Success);
        Assert.Equal(kind, result.ErrorCode);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("script_error")]
    [InlineData("timeout")]
    [InlineData("bad_output")]
    [InlineData("sandbox_error")]
    public async Task Python_script_outcomes_stay_output_redacted_under_redact_output(string kind)
    {
        var result = await RunPythonAsync(kind);

        Assert.False(result.Success);
        Assert.Equal("output_redacted", result.ErrorCode);
        Assert.Equal(kind == "timeout", result.Retryable);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }
}
