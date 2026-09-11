using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Permissions;

public interface IToolResourceGuard
{
    // Null when the call may proceed; otherwise the reason to report to the agent.
    Task<string?> DenyReasonAsync(string toolName, JsonElement args, CancellationToken ct);
}

// Enforces the per-user permission rows.
//
// Until now those rows were written by /api/permissions and by the agent's own
// set_user_permissions, and then read by nothing at all: an administrator could revoke
// a user's access to NetBox, see it stored, and watch the agent keep calling NetBox on
// their behalf. Role was the only real gate.
//
// The posture is the platform's existing one — default-allow, opt-in-deny, the same as
// the Policy engine. A row is a restriction: if none names the resource, the role gate
// alone decides, which is exactly how every installation behaves today. That keeps an
// upgrade from silently locking people out of tools they use, while making the
// permissions screen mean something for the first time.
//
// Both the capability domain and the specific resource are checked, so "no NetBox for
// this user" and "no MCP at all for this user" are both expressible, and the narrower
// grant cannot be bypassed through the broader one.
public sealed class ToolResourceGuard : IToolResourceGuard
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiSpecIndex _specs;
    private readonly PermissionClassifier _classifier;
    private readonly ILogger<ToolResourceGuard> _logger;

    public ToolResourceGuard(
        AppDbContext db, ICurrentUser user, IApiSpecIndex specs,
        PermissionClassifier classifier, ILogger<ToolResourceGuard> logger)
    {
        _db = db;
        _user = user;
        _specs = specs;
        _classifier = classifier;
        _logger = logger;
    }

    public async Task<string?> DenyReasonAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        if (!_user.IsAuthenticated) return null;

        var grants = await _db.UserToolPermissions.AsNoTracking()
            .Where(p => p.UserId == _user.UserId)
            .ToListAsync(ct);
        // The overwhelmingly common case: nobody has restricted this user, so there is
        // nothing to evaluate and no reason to resolve a resource.
        if (grants.Count == 0) return null;

        var permission = _classifier.GetPermission(toolName);
        var needed = CapabilityFor(permission.Level);

        foreach (var key in await KeysForAsync(toolName, permission.Domain, args, ct))
        {
            var row = grants.FirstOrDefault(g =>
                string.Equals(g.ToolDomain, key, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue; // unrestricted for this key

            var granted = needed switch
            {
                Capability.Read => row.CanRead,
                Capability.Write => row.CanWrite,
                _ => row.CanExecute,
            };
            if (granted) continue;

            _logger.LogWarning(
                "ai.permission.resource_denied tool={Tool} resource={Resource} capability={Capability} user={User}",
                toolName, key, needed, _user.UserId);
            return $"permission denied: your account is not granted {needed.ToString().ToLowerInvariant()} " +
                   $"access to '{key}'. An administrator can change this under Permissions.";
        }

        return null;
    }

    private enum Capability { Read, Write, Execute }

    private static Capability CapabilityFor(string level) => level switch
    {
        "read" => Capability.Read,
        "write" => Capability.Write,
        _ => Capability.Execute, // execute and dangerous both spend the execute grant
    };

    // Which keys this call touches: always its capability domain, plus the specific
    // system when the arguments name one. A tool that names no system is domain-scoped
    // and nothing more — list_integrations reads metadata about all of them.
    private async Task<List<string>> KeysForAsync(
        string toolName, string domain, JsonElement args, CancellationToken ct)
    {
        var keys = new List<string> { domain };

        switch (toolName)
        {
            case "mcp_call":
            case "list_mcp_tools":
                if (Str(args, "server") is { Length: > 0 } server)
                    keys.Add(PermissionCatalog.McpPrefix + server);
                break;

            case "discover_operations":
                if (Str(args, "api") is { Length: > 0 } api)
                    keys.Add(PermissionCatalog.ApiPrefix + api);
                break;

            // The operation id is the only handle here, and it is the spec index that
            // knows which API owns it — the same lookup the executor does.
            case "execute_operation":
            case "operation_detail":
                if (Str(args, "operation_id") is { Length: > 0 } operationId)
                {
                    await _specs.EnsureLoadedAsync(ct);
                    if (_specs.GetByOperationId(operationId) is { } op)
                    {
                        keys.Add(PermissionCatalog.ApiPrefix + op.Api);
                        // A spec bound to an integration inherits its restriction: denying
                        // NetBox has to mean NetBox, whichever door the call came through.
                        var slug = await _db.AiApiSpecs.AsNoTracking()
                            .Where(s => s.Api == op.Api && s.IsActive && s.IntegrationId != null)
                            .Join(_db.Integrations.AsNoTracking().Where(i => i.IsActive),
                                s => s.IntegrationId!.Value, i => i.IntegrationId, (_, i) => i.Slug)
                            .FirstOrDefaultAsync(ct);
                        if (!string.IsNullOrWhiteSpace(slug))
                            keys.Add(PermissionCatalog.IntegrationPrefix + slug);
                    }
                }
                break;

            case "list_integrations":
                if (Str(args, "name") is { Length: > 0 } name)
                {
                    var slug = await _db.Integrations.AsNoTracking()
                        .Where(i => i.IsActive && i.Name == name)
                        .Select(i => i.Slug)
                        .FirstOrDefaultAsync(ct);
                    if (!string.IsNullOrWhiteSpace(slug))
                        keys.Add(PermissionCatalog.IntegrationPrefix + slug);
                }
                break;
        }

        return keys;
    }

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;
}
