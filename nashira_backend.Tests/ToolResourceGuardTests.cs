using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

// Per-user resource permissions, which until now were stored and never read: an
// administrator could revoke a user's access to NetBox, see it saved, and watch the
// agent keep calling NetBox on their behalf.
//
// Two properties matter more than the rest. An installation that has never touched the
// permissions screen must keep behaving exactly as it does today — that is what makes
// this safe to deploy — and a restriction on a specific system must not be reachable
// through a different tool that targets the same system.
public class ToolResourceGuardTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"guard-{Guid.NewGuid()}").Options);

    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; init; }
        public bool IsAuthenticated { get; init; } = true;
        public string? Username => "e2e";
        public IReadOnlyList<string> Roles { get; init; } = ["admin"];
    }

    // The guard only consults the index for execute_operation / operation_detail; every
    // other path never touches it.
    private sealed class EmptySpecIndex : IApiSpecIndex
    {
        public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId) => null;
    }

    private static ToolResourceGuard Guard(AppDbContext db, bool authenticated = true) =>
        new(db, new FakeUser { UserId = UserId, IsAuthenticated = authenticated }, new EmptySpecIndex(),
            new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
            NullLogger<ToolResourceGuard>.Instance);

    private static JsonElement Args(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static void Restrict(
        AppDbContext db, string target, bool read = false, bool write = false, bool execute = false)
    {
        db.UserToolPermissions.Add(new UserToolPermission
        {
            UserToolPermissionId = Guid.NewGuid(),
            UserId = UserId,
            ToolDomain = target,
            CanRead = read,
            CanWrite = write,
            CanExecute = execute,
            IsActive = true,
        });
        db.SaveChanges();
    }

    // The upgrade case, and the one that would be worst to get wrong.
    [Fact]
    public async Task A_user_with_no_rows_is_never_denied()
    {
        using var db = NewDb();
        Assert.Null(await Guard(db).DenyReasonAsync(
            "mcp_call", Args("""{"server":"Splunk","tool":"run_oneshot_search"}"""), default));
    }

    [Fact]
    public async Task A_restriction_on_one_server_leaves_the_others_reachable()
    {
        using var db = NewDb();
        Restrict(db, "mcp:Splunk"); // restricted, nothing granted

        var guard = Guard(db);
        Assert.NotNull(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
        Assert.Null(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"NetBox"}"""), default));
    }

    // mcp_call is an execute-level tool, so the execute grant is the one that decides;
    // read alone must not open it.
    [Fact]
    public async Task The_capability_that_the_tool_needs_is_the_one_checked()
    {
        using var db = NewDb();
        Restrict(db, "mcp:Splunk", read: true);

        var guard = Guard(db);
        Assert.NotNull(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
        // list_mcp_tools is a read, and read is granted.
        Assert.Null(await guard.DenyReasonAsync("list_mcp_tools", Args("""{"server":"Splunk"}"""), default));
    }

    [Fact]
    public async Task An_execute_grant_lets_the_call_through()
    {
        using var db = NewDb();
        Restrict(db, "mcp:Splunk", read: true, execute: true);

        Assert.Null(await Guard(db).DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
    }

    // The broad statement has to hold on its own: "no MCP for this user" cannot be
    // sidestepped by naming a server nobody wrote a row for.
    [Fact]
    public async Task A_domain_restriction_covers_every_resource_in_it()
    {
        using var db = NewDb();
        Restrict(db, "mcp");

        var guard = Guard(db);
        Assert.NotNull(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
        Assert.NotNull(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"NetBox"}"""), default));
    }

    // And the narrow one cannot be widened by the broad one: a domain that is granted
    // does not un-restrict a server that is denied.
    [Fact]
    public async Task A_granted_domain_does_not_lift_a_resource_restriction()
    {
        using var db = NewDb();
        Restrict(db, "mcp", read: true, write: true, execute: true);
        Restrict(db, "mcp:Splunk");

        var guard = Guard(db);
        Assert.NotNull(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
        Assert.Null(await guard.DenyReasonAsync("mcp_call", Args("""{"server":"NetBox"}"""), default));
    }

    [Fact]
    public async Task Restrictions_match_the_stored_key_case_insensitively()
    {
        using var db = NewDb();
        Restrict(db, "mcp:splunk");

        Assert.NotNull(await Guard(db).DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
    }

    [Fact]
    public async Task Discovery_is_scoped_to_the_api_it_names()
    {
        using var db = NewDb();
        Restrict(db, "api:netbox");

        var guard = Guard(db);
        Assert.NotNull(await guard.DenyReasonAsync("discover_operations", Args("""{"api":"netbox"}"""), default));
        Assert.Null(await guard.DenyReasonAsync("discover_operations", Args("""{"api":"na_devices"}"""), default));
    }

    // A tool that names no system is domain-scoped and nothing more; a restriction on
    // one integration must not blind the user to the list of them.
    [Fact]
    public async Task A_resource_restriction_does_not_leak_into_unrelated_tools()
    {
        using var db = NewDb();
        Restrict(db, "integration:netbox-lab");

        Assert.Null(await Guard(db).DenyReasonAsync("list_integrations", Args("{}"), default));
    }

    // Background work runs without a user; there is no one to have restrictions.
    [Fact]
    public async Task An_unauthenticated_caller_is_not_evaluated()
    {
        using var db = NewDb();
        Restrict(db, "mcp");

        Assert.Null(await Guard(db, authenticated: false)
            .DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default));
    }

    [Fact]
    public async Task The_denial_names_the_resource_so_the_agent_can_relay_it()
    {
        using var db = NewDb();
        Restrict(db, "mcp:Splunk");

        var reason = await Guard(db).DenyReasonAsync("mcp_call", Args("""{"server":"Splunk"}"""), default);
        Assert.Contains("mcp:Splunk", reason);
        Assert.Contains("administrator", reason, StringComparison.OrdinalIgnoreCase);
    }
}
