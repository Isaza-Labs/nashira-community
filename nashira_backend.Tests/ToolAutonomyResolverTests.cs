using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Tests;

// Which confirmed calls are allowed to run unconfirmed.
//
// The gate exists to stop the agent changing things without being asked, and it was
// applied per tool — so `execute_operation` on a GET and on a DELETE were the same
// question, and every read-only conversation became a wall of dialogs. Approving
// without reading is the failure mode this creates, which is worse than the friction.
//
// The asymmetry under test: an operation's HTTP method comes from a spec an admin
// uploaded here, while an MCP tool's readOnlyHint comes from the server being asked
// about itself. The first is evidence. The second is a claim, and only counts where an
// admin has vouched for the claimant.
public class ToolAutonomyResolverTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"autonomy-{Guid.NewGuid()}").Options);

    private sealed class StubSpecIndex : IApiSpecIndex
    {
        private readonly Dictionary<string, ApiOperation> _ops;
        public StubSpecIndex(params ApiOperation[] ops) =>
            _ops = ops.ToDictionary(o => o.OperationId, StringComparer.OrdinalIgnoreCase);

        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => _ops.Values.ToList();
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId) =>
            _ops.GetValueOrDefault(operationId);
    }

    private static ApiOperation Op(string id, string method) => new()
    {
        Api = "netbox",
        OperationId = id,
        Method = method,
        Path = "/dcim/devices/",
        Summary = id,
        Tags = [],
    };

    private static ToolAutonomyResolver Resolver(AppDbContext db, params ApiOperation[] ops) =>
        new(db, new StubSpecIndex(ops), NullLogger<ToolAutonomyResolver>.Instance);

    private static JsonElement Args(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static Guid AddServer(AppDbContext db, string name, bool trustHints)
    {
        var id = Guid.NewGuid();
        db.McpServers.Add(new McpServer
        {
            McpServerId = id, Name = name, Url = "http://mcp.invalid/mcp",
            TrustToolHints = trustHints, Enabled = true, IsActive = true,
        });
        db.SaveChanges();
        return id;
    }

    private static void AddTool(AppDbContext db, Guid serverId, string name, bool readOnlyHint)
    {
        db.McpTools.Add(new McpTool
        {
            McpToolId = Guid.NewGuid(), McpServerId = serverId, Name = name,
            ReadOnlyHint = readOnlyHint, Enabled = true, IsActive = true,
        });
        db.SaveChanges();
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    [InlineData("DELETE", false)]
    public async Task An_operations_method_decides_whether_it_is_a_read(string method, bool expected)
    {
        using var db = NewDb();
        var resolver = Resolver(db, Op("netboxOp", method));

        Assert.Equal(expected, await resolver.IsReadOnlyCallAsync(
            "execute_operation", Args("""{"operation_id":"netboxOp"}"""), default));
    }

    // An operation the index cannot resolve is not evidence of anything.
    [Fact]
    public async Task An_unknown_operation_still_asks()
    {
        using var db = NewDb();
        Assert.False(await Resolver(db).IsReadOnlyCallAsync(
            "execute_operation", Args("""{"operation_id":"nope"}"""), default));
    }

    // The heart of it: the hint alone is not enough.
    [Fact]
    public async Task A_read_only_hint_is_ignored_on_a_server_nobody_vouched_for()
    {
        using var db = NewDb();
        var server = AddServer(db, "Splunk", trustHints: false);
        AddTool(db, server, "list_indexes", readOnlyHint: true);

        Assert.False(await Resolver(db).IsReadOnlyCallAsync(
            "mcp_call", Args("""{"server":"Splunk","tool":"list_indexes"}"""), default));
    }

    [Fact]
    public async Task A_read_only_hint_counts_once_an_admin_trusts_the_server()
    {
        using var db = NewDb();
        var server = AddServer(db, "Splunk", trustHints: true);
        AddTool(db, server, "list_indexes", readOnlyHint: true);

        Assert.True(await Resolver(db).IsReadOnlyCallAsync(
            "mcp_call", Args("""{"server":"Splunk","tool":"list_indexes"}"""), default));
    }

    // Trusting a server is not trusting every tool on it: the ones that make no claim
    // are still confirmed.
    [Fact]
    public async Task A_trusted_server_does_not_waive_its_unannotated_tools()
    {
        using var db = NewDb();
        var server = AddServer(db, "Splunk", trustHints: true);
        AddTool(db, server, "delete_index", readOnlyHint: false);

        Assert.False(await Resolver(db).IsReadOnlyCallAsync(
            "mcp_call", Args("""{"server":"Splunk","tool":"delete_index"}"""), default));
    }

    [Fact]
    public async Task An_unknown_server_or_tool_still_asks()
    {
        using var db = NewDb();
        AddServer(db, "Splunk", trustHints: true);

        var resolver = Resolver(db);
        Assert.False(await resolver.IsReadOnlyCallAsync(
            "mcp_call", Args("""{"server":"Ghost","tool":"anything"}"""), default));
        Assert.False(await resolver.IsReadOnlyCallAsync(
            "mcp_call", Args("""{"server":"Splunk","tool":"not_in_catalog"}"""), default));
    }

    // Everything else is confirmed for what it is, not for what it was called with.
    [Theory]
    [InlineData("delete_workflow")]
    [InlineData("set_secret")]
    [InlineData("device_connect")]
    [InlineData("run_workflow")]
    public async Task Other_tools_are_never_waived_by_their_arguments(string tool)
    {
        using var db = NewDb();
        Assert.False(await Resolver(db).IsReadOnlyCallAsync(tool, Args("{}"), default));
    }

    [Fact]
    public async Task Missing_arguments_are_not_treated_as_a_read()
    {
        using var db = NewDb();
        var resolver = Resolver(db, Op("netboxOp", "GET"));

        Assert.False(await resolver.IsReadOnlyCallAsync("execute_operation", Args("{}"), default));
        Assert.False(await resolver.IsReadOnlyCallAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
    }
}
