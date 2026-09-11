using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Upserts a user's per-domain tool permissions. Admin-only; changes access control →
// elevated_confirm. Mirrors PermissionsController.UpsertForUser.
public sealed class SetUserPermissionsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "user_id":{"type":"string","description":"User id from list_users"},
          "permissions":{"type":"array","items":{"type":"object","properties":{
            "tool_domain":{"type":"string"},
            "can_read":{"type":"boolean"},
            "can_write":{"type":"boolean"},
            "can_execute":{"type":"boolean"},
            "inherit":{"type":"boolean","description":"Remove the restriction on this target so the user's role decides again"}
          },"required":["tool_domain"]},"description":"One entry per target to restrict or release"}
        },"required":["user_id","permissions"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly Permissions.IPermissionCatalog _catalog;

    public SetUserPermissionsHandler(AppDbContext db, ICurrentUser user, Permissions.IPermissionCatalog catalog)
    {
        _db = db;
        _user = user;
        _catalog = catalog;
    }

    public string Name => "set_user_permissions";
    public string Description =>
        "Grants/revokes a user's tool permissions (admin). Takes user_id and a permissions array of " +
        "{ tool_domain, can_read, can_write, can_execute }, where tool_domain is a capability domain " +
        "or a specific system (integration:<slug>, mcp:<server>, api:<api>). A grant is a restriction: " +
        "a target with no row falls back to the user's role. Use list_permission_domains for valid keys.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "user_id")?.Trim() is not { Length: > 0 } sid || !Guid.TryParse(sid, out var id))
            return Err("user_id (a uuid from list_users) is required");
        if (!args.TryGetProperty("permissions", out var permsEl) || permsEl.ValueKind != JsonValueKind.Array)
            return Err("permissions (an array) is required");

        var exists = await _db.Users.AnyAsync(u => u.UserId == id && u.IsActive, ct);
        if (!exists) return Err("user not found");

        var existing = await _db.UserToolPermissions
            .Where(p => p.UserId == id)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var item in permsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // Case preserved: an MCP key carries the server's name as it was registered.
            var domain = (Str(item, "tool_domain") ?? string.Empty).Trim();
            if (!await _catalog.ExistsAsync(domain, ct))
                return Err($"unknown tool_domain '{domain}' — call list_permission_domains for valid keys");

            var row = existing.FirstOrDefault(p =>
                string.Equals(p.ToolDomain, domain, StringComparison.OrdinalIgnoreCase));

            // Same three states as the HTTP endpoint: inherit removes the restriction,
            // which is not the same as granting nothing.
            if (Bool(item, "inherit"))
            {
                if (row is not null)
                {
                    _db.UserToolPermissions.Remove(row);
                    existing.Remove(row);
                }
                continue;
            }

            if (row is null)
            {
                row = new UserToolPermission
                {
                    UserToolPermissionId = Guid.NewGuid(),
                    UserId = id,
                    ToolDomain = domain,
                    CreatedAt = now,
                };
                _db.UserToolPermissions.Add(row);
                existing.Add(row);
            }
            row.CanRead = Bool(item, "can_read");
            row.CanWrite = Bool(item, "can_write");
            row.CanExecute = Bool(item, "can_execute");
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        var result = existing.Select(p => new
        {
            tool_domain = p.ToolDomain, can_read = p.CanRead, can_write = p.CanWrite, can_execute = p.CanExecute,
        });
        return JsonSerializer.SerializeToElement(new { user_id = id, permissions = result });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
