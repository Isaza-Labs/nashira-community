using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Net;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;

namespace nashira_backend.Tests;

// A guarded MCP transport refuses redirects. The refusal is a configuration fault (the endpoint
// moved), so both MCP callers must report it as final: the workflow step as a non-retryable
// `redirect_refused`, and the agent tool with an `error_code` the dispatcher treats as final. The
// SDK sits between the transport and the callers and may wrap what the transport threw, so the
// cases cover a bare refusal, wrapped ones and a full run through the real client.
public class McpRedirectFinalityTests
{
    private const string Code = "redirect_refused";

    public static TheoryData<string> Failures => new()
    {
        "direct", "io_wrapped", "http_wrapped_twice", "aggregate",
    };

    private static Exception Build(string kind)
    {
        var refusal = new RedirectRefusedException(302, RedirectRefusalReason.TargetNotAllowed);
        return kind switch
        {
            "direct" => refusal,
            "io_wrapped" => new IOException("example transport failure", refusal),
            "http_wrapped_twice" => new InvalidOperationException(
                "example outer", new HttpRequestException("example http failure", refusal)),
            "aggregate" => new AggregateException(new TimeoutException("example other"), refusal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static async Task<AppDbContext> DbWithServerAsync()
    {
        var db = RedirectRefusalMappingTests.NewDb();
        var serverId = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = serverId, Name = "example-mcp", Url = "https://203.0.113.30/mcp",
            IsActive = true, Enabled = true,
        });
        db.McpTools.Add(new McpTool
        {
            McpToolId = Guid.NewGuid(), McpServerId = serverId, Name = "example_tool",
            IsActive = true, Enabled = true,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static readonly object Input = new { server = "example-mcp", tool = "example_tool" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_workflow_step_reports_a_refused_redirect_as_final(string kind)
    {
        using var db = await DbWithServerAsync();
        var handler = new McpCallSnippetHandler(db, new ThrowingMcp(Build(kind)));

        var result = await handler.ExecuteAsync(RedirectRefusalMappingTests.Request("mcp_call", Input), default);

        RedirectRefusalMappingTests.AssertRefusal(result, 302);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_agent_tool_reports_a_refused_redirect_with_the_final_code(string kind)
    {
        using var db = await DbWithServerAsync();
        var handler = new McpCallHandler(
            db, new HardeningTestUser(), new ThrowingMcp(Build(kind)), NullLogger<McpCallHandler>.Instance);

        var result = await handler.ExecuteAsync(JsonSerializer.SerializeToElement(Input), default);

        Assert.Equal(Code, result.GetProperty("error_code").GetString());
        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("configure the final endpoint", error);
        Assert.Contains("HTTP 302", error);
    }

    [Fact]
    public async Task Other_mcp_failures_keep_their_generic_handling()
    {
        using var db = await DbWithServerAsync();
        var failure = new IOException("example connection reset", new TimeoutException("example timeout"));
        var step = await new McpCallSnippetHandler(db, new ThrowingMcp(failure))
            .ExecuteAsync(RedirectRefusalMappingTests.Request("mcp_call", Input), default);
        var tool = await new McpCallHandler(
                db, new HardeningTestUser(), new ThrowingMcp(failure), NullLogger<McpCallHandler>.Instance)
            .ExecuteAsync(JsonSerializer.SerializeToElement(Input), default);

        Assert.Equal("mcp_error", step.ErrorCode);
        Assert.True(step.Retryable);
        Assert.False(tool.TryGetProperty("error_code", out _));
        Assert.Contains("MCP call failed", tool.GetProperty("error").GetString());
    }

    // The whole chain: real handler, real service and client, the SDK's HTTP transport and the real
    // refusing handler, with a transport that answers every request with a redirect. Nothing leaves
    // the process.
    [Fact]
    public async Task A_redirecting_server_is_final_through_the_real_client_for_both_callers()
    {
        using var db = await DbWithServerAsync();
        var transport = new RedirectRefusalMappingTests.RedirectingFactory();
        var service = new McpServerService(
            db, new McpClient(new FixedConnection(), transport, NullLoggerFactory.Instance),
            NullLogger<McpServerService>.Instance);

        var step = await new McpCallSnippetHandler(db, service)
            .ExecuteAsync(RedirectRefusalMappingTests.Request("mcp_call", Input), default);
        var tool = await new McpCallHandler(
                db, new HardeningTestUser(), service, NullLogger<McpCallHandler>.Instance)
            .ExecuteAsync(JsonSerializer.SerializeToElement(Input), default);

        RedirectRefusalMappingTests.AssertRefusal(step, 302);
        Assert.Equal(Code, tool.GetProperty("error_code").GetString());
        Assert.DoesNotContain(RedirectRefusalMappingTests.LocationToken, tool.GetRawText());
        Assert.True(transport.Requests >= 1);
    }


    private sealed class FixedConnection : IMcpConnectionFactory
    {
        public Task<McpConnection> CreateAsync(McpServer server, CancellationToken ct) =>
            Task.FromResult(new McpConnection(new Uri("https://203.0.113.30/mcp"), [], false));
    }

    private sealed class ThrowingMcp(Exception error) : IMcpServerService
    {
        public Task<McpCallResult> CallAsync(Guid serverId, string toolName, JsonElement arguments, CancellationToken ct) =>
            throw error;
        public Task<McpSyncResult> SyncToolsAsync(Guid serverId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IntegrationHealthLike> CheckAsync(Guid serverId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
