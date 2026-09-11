using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;

namespace nashira_backend.Services.Ai.Permissions;

// One grantable thing: a capability domain, or a specific external system.
//
// `Key` is what lands in UserToolPermission.ToolDomain. Resource keys are prefixed
// (`mcp:Splunk`, `api:netbox`, `integration:netbox-lab`) so they cannot collide with a
// capability domain, and so the kind is readable straight off the stored row.
public sealed record PermissionResource(string Key, string Kind, string Label, string? Description);

public interface IPermissionCatalog
{
    Task<IReadOnlyList<PermissionResource>> ListAsync(CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
}

// The set of things a permission can be granted on, built from what is actually
// registered rather than from a list in the source.
//
// The old catalogue was a hardcoded `["awx", "netbox", "infoblox", "servicenow", …]`
// that named four vendors this installation may never have heard of, did not name the
// domains the tools actually carry, and could not name the NetBox that was registered
// yesterday. Granting "netbox" meant nothing to the agent and everything to the admin
// reading the screen.
public sealed class PermissionCatalog : IPermissionCatalog
{
    public const string KindDomain = "domain";
    public const string KindIntegration = "integration";
    public const string KindMcp = "mcp";
    public const string KindApi = "api";

    public const string IntegrationPrefix = "integration:";
    public const string McpPrefix = "mcp:";
    public const string ApiPrefix = "api:";

    private readonly AppDbContext _db;

    public PermissionCatalog(AppDbContext db) => _db = db;

    // Capability domains come from the tool matrix itself, so a tool added tomorrow
    // brings its domain with it instead of needing a second list updated by hand.
    public static IReadOnlyList<PermissionResource> Domains() =>
        PermissionClassifier.Matrix.Values
            .Select(p => p.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new PermissionResource(d, KindDomain, d, $"Every {d} tool"))
            .ToList();

    public async Task<IReadOnlyList<PermissionResource>> ListAsync(CancellationToken ct)
    {
        var result = new List<PermissionResource>(Domains());

        var integrations = await _db.Integrations.AsNoTracking()
            .Where(i => i.IsActive)
            .OrderBy(i => i.Name)
            .Select(i => new { i.Name, i.Slug, i.Type })
            .ToListAsync(ct);
        result.AddRange(integrations.Select(i => new PermissionResource(
            IntegrationPrefix + i.Slug, KindIntegration, i.Name,
            string.IsNullOrWhiteSpace(i.Type) ? "Integration" : $"{i.Type} integration")));

        var mcpServers = await _db.McpServers.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Name, s.ToolCount })
            .ToListAsync(ct);
        result.AddRange(mcpServers.Select(s => new PermissionResource(
            McpPrefix + s.Name, KindMcp, s.Name, $"MCP server · {s.ToolCount} tools")));

        var specs = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Api)
            .Select(s => new { s.Api, s.OperationCount })
            .ToListAsync(ct);
        result.AddRange(specs.Select(s => new PermissionResource(
            ApiPrefix + s.Api, KindApi, s.Api, $"API spec · {s.OperationCount} operations")));

        return result;
    }

    // Keys are compared case-insensitively because an MCP server's name and an
    // integration's slug are written by hand in two different screens.
    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        var k = (key ?? string.Empty).Trim();
        if (k.Length == 0) return false;
        var all = await ListAsync(ct);
        return all.Any(r => string.Equals(r.Key, k, StringComparison.OrdinalIgnoreCase));
    }
}
