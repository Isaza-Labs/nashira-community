using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Permission;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Permissions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Admin-only per-user tool permissions, on capability domains and on specific systems.
//
// A stored row is a RESTRICTION, not a grant: the platform's posture here is the same
// default-allow / opt-in-deny as the Policy engine. A target nobody has written a row
// for falls back to the user's role, which is how every installation behaves; a row that
// grants nothing denies that target outright; and `inherit` removes the row. Those three
// states are distinct on purpose — "deny everything on NetBox" and "no opinion about
// NetBox" are different answers and must not share a representation.
//
// These rows are enforced by ToolResourceGuard on every agent tool call. Until it
// existed they were written here and read by nothing.
[ApiController]
[Route("api/permissions")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class PermissionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPermissionCatalog _catalog;
    private readonly IAuditLogger _audit;

    public PermissionsController(
        AppDbContext db, ICurrentUser user, IPermissionCatalog catalog, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _catalog = catalog;
        _audit = audit;
    }

    // Everything a permission can be granted on: the capability domains the tools
    // actually carry, plus every registered integration, MCP server and API spec.
    //
    // This used to be a fixed list naming four vendors — awx, netbox, infoblox,
    // servicenow — which meant an administrator could grant "netbox" whether or not one
    // was registered, could not grant the NetBox that was, and could not name any of the
    // domains the tool matrix uses. The list is now built from what exists.
    [HttpGet("domains")]
    public async Task<ActionResult<IEnumerable<PermissionResourceItem>>> GetDomains(CancellationToken ct)
    {
        var resources = await _catalog.ListAsync(ct);
        return new OkObjectResult(resources.Select(r => new PermissionResourceItem
        {
            Key = r.Key,
            Kind = r.Kind,
            Label = r.Label,
            Description = r.Description,
        }).ToList());
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<ActionResult<IEnumerable<PermissionItem>>> GetForUser(Guid userId, CancellationToken ct)
    {
        await EnsureUserAsync(userId, ct);
        var perms = await _db.UserToolPermissions.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);
        // Materialized, not deferred: a Select evaluated during serialization fails
        // after the 200 is already on the wire. Same call as SecretsController.Get.
        return new OkObjectResult(perms.Select(ToItem).ToList());
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("users/{userId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<IEnumerable<PermissionItem>>> UpsertForUser(
        Guid userId, [FromBody] UpdatePermissionsRequest request, CancellationToken ct)
    {
        await EnsureUserAsync(userId, ct);

        var existing = await _db.UserToolPermissions
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);

        // Captured before anything is touched. A PUT carrying `inherit: true` REVOKES a
        // grant, and this is the one entity type where "what was taken away, and by
        // whom" is the entire question — with only the resulting set recorded, a
        // revocation appears nowhere in the trail at all.
        var before = Grants(existing);

        var now = DateTime.UtcNow;
        foreach (var item in request.Permissions)
        {
            // Case is preserved: an MCP server's key carries its name, and "Splunk" is
            // not "splunk" on the screen even though the guard compares case-insensitively.
            var domain = (item.ToolDomain ?? string.Empty).Trim();
            if (!await _catalog.ExistsAsync(domain, ct))
                throw new ValidationException(
                    $"unknown permission target '{item.ToolDomain}' — see GET /api/permissions/domains");

            var row = existing.FirstOrDefault(p =>
                string.Equals(p.ToolDomain, domain, StringComparison.OrdinalIgnoreCase));

            if (item.Inherit == true)
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
                    UserId = userId,
                    ToolDomain = domain,
                    CreatedAt = now,
                };
                _db.UserToolPermissions.Add(row);
                existing.Add(row);
            }
            row.CanRead = item.CanRead;
            row.CanWrite = item.CanWrite;
            row.CanExecute = item.CanExecute;
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        // The whole resulting grant set, not the delta that was sent: a PUT replaces
        // what a user may reach, and reconstructing that from a series of partial
        // payloads is exactly the work an audit trail exists to save.
        await _audit.LogAsync("permission", userId, "set",
            before: new { user_id = userId, grants = before },
            after: new { user_id = userId, grants = Grants(existing) },
            ct: ct);

        return new OkObjectResult(existing.Select(ToItem).ToList());
    }

    // The whole grant set, not a delta: a PUT replaces what a user may reach, and
    // reconstructing that from a series of partial payloads is the work an audit trail
    // exists to save.
    private static List<object> Grants(IEnumerable<UserToolPermission> rows) => rows
        .Where(r => r.IsActive)
        .Select(r => (object)new
        {
            domain = r.ToolDomain,
            read = r.CanRead,
            write = r.CanWrite,
            execute = r.CanExecute,
        })
        .ToList();

    private async Task EnsureUserAsync(Guid userId, CancellationToken ct)
    {
        var exists = await _db.Users.AnyAsync(
            u => u.UserId == userId && u.IsActive, ct);
        if (!exists) throw new NotFoundException("user not found");
    }

    private static PermissionItem ToItem(UserToolPermission p) => new()
    {
        ToolDomain = p.ToolDomain,
        CanRead = p.CanRead,
        CanWrite = p.CanWrite,
        CanExecute = p.CanExecute,
    };
}
