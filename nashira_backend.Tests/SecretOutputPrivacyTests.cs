using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

public partial class SecretAuthorizationTests
{
    public static IEnumerable<object[]> PrivacyScripts() =>
        from script in ExtractionScripts()
        from redactSibling in new[] { false, true }
        select new object[] { script[0], script[1], redactSibling };

    [Theory]
    [MemberData(nameof(PrivacyScripts))]
    public async Task Secret_step_is_withheld_but_independent_public_sibling_obeys_its_flag(
        string technique, string code, bool redactSibling)
    {
        Assert.NotNull(PythonSandboxTests.Exe);
        using var fixture = new Fixture("admin");
        var sandbox = new CountingSandbox();
        var secret = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = technique, Type = Snippet.TypePythonSnippet,
            Code = code, ChangesState = false, Idempotency = "idempotent",
        };
        var sibling = new Snippet
        {
            SnippetId = Guid.NewGuid(), Name = "public", Type = Snippet.TypePythonSnippet,
            Code = "print('healthy')\nresult = {'status': 'healthy', 'count': 3}", ChangesState = false,
        };
        fixture.Db.Snippets.AddRange(secret, sibling);
        await fixture.Db.SaveChangesAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var executor = new SnippetNodeExecutor(fixture.Db, new Registry(fixture.Handler(sandbox)), services,
            new VariableResolver(NullLogger<VariableResolver>.Instance), new WorkflowExecutionScope(),
            NullLogger<SnippetNodeExecutor>.Instance);
        var config = JsonSerializer.SerializeToElement(new object[]
        {
            new { id = "secret", snippet_id = secret.SnippetId, config_overrides = new { tok = Marker, redact_output = false } },
            new { id = "public", snippet_id = sibling.SnippetId, config_overrides = new { redact_output = redactSibling } },
        });
        var edges = JsonSerializer.SerializeToElement(new[] { new { source = "secret", target = "public", type = "success" } });
        var run = await new WorkflowExecutor().RunAsync(Dag.Parse(config, edges), executor,
            new WorkflowExecutionContext(Guid.NewGuid(), "test", "admin", DateTime.UtcNow), true, default);
        Assert.Equal("completed", run.Status);
        Assert.Equal(2, sandbox.Calls);
        Assert.Equal(1, fixture.Protector.Decryptions);
        var protectedStep = Assert.Single(run.Steps, s => s.NodeId == "secret");
        Assert.Equal("\"[redacted]\"", protectedStep.Output);
        Assert.Equal("[redacted]", protectedStep.Logs);
        var publicStep = Assert.Single(run.Steps, s => s.NodeId == "public");
        if (redactSibling)
        {
            Assert.Equal("\"[redacted]\"", publicStep.Output);
            Assert.Equal("[redacted]", publicStep.Logs);
        }
        else
        {
            Assert.Equal("healthy", JsonDocument.Parse(publicStep.Output!).RootElement.GetProperty("status").GetString());
            Assert.Contains("healthy", publicStep.Logs);
        }
        Assert.DoesNotContain(Value, JsonSerializer.Serialize(run));
    }

    [Theory]
    [InlineData("username")]
    [InlineData("client_id")]
    public async Task Cleartext_credential_fields_cannot_be_extracted_by_admin_script_output(string field)
    {
        using var fixture = new Fixture("admin");
        var result = await fixture.Handler(new CountingSandbox()).ExecuteAsync(new SnippetRequest
        {
            NodeId = "py", WorkflowId = Guid.NewGuid(), SnippetId = Guid.NewGuid(), SnippetType = Snippet.TypePythonSnippet,
            Code = "result = input['tok'][::-1]",
            Input = JsonSerializer.SerializeToElement(new { tok = $"${{secret:credential:protected:{field}}}", redact_output = false }),
        }, default);
        Assert.True(result.Success, result.Error);
        Assert.Equal("[redacted]", result.Output.GetString());
    }

    [Theory]
    [InlineData("print(input['tok'][::-1])\nraise ValueError(input['tok'][::-1])")]
    [InlineData("result = {input['tok'][::-1]: True}")]
    [InlineData("result = [ord(c) for c in input['tok']]")]
    [InlineData("result = {str(i): c for i, c in enumerate(input['tok'])}")]
    public async Task Secret_material_cannot_escape_in_errors_logs_keys_numbers_or_split_fields(string code)
    {
        using var fixture = new Fixture("admin");
        var result = await fixture.Handler(new CountingSandbox()).ExecuteAsync(new SnippetRequest
        {
            NodeId = "py", WorkflowId = Guid.NewGuid(), SnippetId = Guid.NewGuid(), SnippetType = Snippet.TypePythonSnippet,
            Code = code, Input = JsonSerializer.SerializeToElement(new { tok = Marker, redact_output = false }),
        }, default);
        Assert.Equal("[redacted]", result.Output.GetString());
        Assert.Equal("[redacted]", result.Logs);
        Assert.Equal(result.Success ? "" : "[redacted]", result.Error);
    }

    private sealed class ResolvingHandler(ISecretResolver resolver, bool throws) : ISnippetHandler
    {
        public string Type => "test_secret_consumer";
        public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
        {
            var value = await resolver.ResolveAsync("secret", "protected", "value", ct);
            var encoded = new string(value!.Reverse().ToArray());
            if (throws) throw new InvalidOperationException(encoded);
            return SnippetResult.Ok(new { public_value = "healthy", encoded }, StepChange.Unchanged, encoded);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Central_resolver_provenance_protects_non_python_handlers_including_exceptions(bool throws)
    {
        using var fixture = new Fixture("admin");
        var handler = new ResolvingHandler(fixture.Resolver, throws);
        var snippet = new Snippet { SnippetId = Guid.NewGuid(), Name = "consumer", Type = handler.Type };
        fixture.Db.Snippets.Add(snippet);
        await fixture.Db.SaveChangesAsync();
        using var services = new ServiceCollection().BuildServiceProvider();
        var executor = new SnippetNodeExecutor(fixture.Db, new Registry(handler), services,
            new VariableResolver(NullLogger<VariableResolver>.Instance), new WorkflowExecutionScope(), NullLogger<SnippetNodeExecutor>.Instance);
        var nodes = JsonSerializer.SerializeToElement(new[]
        { new { id = "consumer", snippet_id = snippet.SnippetId, config_overrides = new { redact_output = false } } });
        var run = await new WorkflowExecutor().RunAsync(Dag.Parse(nodes, JsonSerializer.SerializeToElement(Array.Empty<object>())), executor,
            new WorkflowExecutionContext(Guid.NewGuid(), "test", "admin", DateTime.UtcNow), true, default);
        var step = Assert.Single(run.Steps);
        Assert.Equal("\"[redacted]\"", step.Output);
        Assert.Equal("[redacted]", step.Logs);
        Assert.DoesNotContain(new string(Value.Reverse().ToArray()), JsonSerializer.Serialize(run));
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("{}")]
    public async Task Invalid_redaction_flags_stop_before_secret_resolution_or_execution(string flag)
    {
        using var fixture = new Fixture("admin");
        var sandbox = new CountingSandbox();
        var result = await fixture.Handler(sandbox).ExecuteAsync(new SnippetRequest
        {
            NodeId = "py", WorkflowId = Guid.NewGuid(), SnippetId = Guid.NewGuid(), SnippetType = Snippet.TypePythonSnippet,
            Code = "result = input", Input = JsonDocument.Parse($$"""{"tok":"{{Marker}}","redact_output":{{flag}}}""").RootElement.Clone(),
        }, default);
        Assert.False(result.Success);
        Assert.Equal("invalid_redact_output", result.ErrorCode);
        Assert.Equal(0, fixture.Protector.Decryptions);
        Assert.Equal(0, sandbox.Calls);
    }
}
