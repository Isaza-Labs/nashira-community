using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Data.DTos.Permission;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Navigation;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Navigation visibility is separate from tool permissions: these settings decide
// which product areas the UI offers, while API policies remain the security boundary.
[ApiController]
[Route("api/navigation-permissions")]
[Authorize]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class NavigationPermissionsController : ControllerBase
{
    private readonly NavigationPermissionService _permissions;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public NavigationPermissionsController(
        NavigationPermissionService permissions, ICurrentUser user, IAuditLogger audit)
    {
        _permissions = permissions;
        _user = user;
        _audit = audit;
    }

    [HttpGet("me")]
    public async Task<ActionResult<IEnumerable<NavigationVisibilityItem>>> GetMine(
        CancellationToken ct)
    {
        var values = await _permissions.GetEffectiveAsync(_user.UserId, ct);
        return new OkObjectResult(values
            .OrderBy(p => p.Key)
            .Select(p => ToItem(p.Key, p.Value))
            .ToList());
    }

    [HttpGet("roles/{role}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IEnumerable<NavigationVisibilityItem>>> GetRole(
        string role, CancellationToken ct)
    {
        var values = await _permissions.ListRoleAsync(role, ct);
        return new OkObjectResult(values.Select(ToItem).ToList());
    }

    [HttpPut("roles/{role}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit] // self-audits below with the complete before/after scope
    public async Task<ActionResult<IEnumerable<NavigationVisibilityItem>>> UpdateRole(
        string role, [FromBody] UpdateNavigationVisibilityRequest request, CancellationToken ct)
    {
        var before = await _permissions.ListRoleAsync(role, ct);
        await _permissions.UpdateRoleAsync(role, request.Permissions.Select(ToUpdate).ToList(), ct);
        var after = await _permissions.ListRoleAsync(role, ct);
        await _audit.LogAsync(
            "navigation_permission",
            null,
            "set_role",
            before: new { role, permissions = before },
            after: new { role, permissions = after },
            ct: ct);
        return new OkObjectResult(after.Select(ToItem).ToList());
    }

    [HttpGet("users/{userId:guid}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IEnumerable<NavigationVisibilityItem>>> GetUser(
        Guid userId, CancellationToken ct)
    {
        var values = await _permissions.ListUserAsync(userId, ct);
        return new OkObjectResult(values.Select(ToItem).ToList());
    }

    [HttpPut("users/{userId:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit] // self-audits below with the complete before/after scope
    public async Task<ActionResult<IEnumerable<NavigationVisibilityItem>>> UpdateUser(
        Guid userId,
        [FromBody] UpdateNavigationVisibilityRequest request,
        CancellationToken ct)
    {
        var before = await _permissions.ListUserAsync(userId, ct);
        await _permissions.UpdateUserAsync(
            userId, request.Permissions.Select(ToUpdate).ToList(), ct);
        var after = await _permissions.ListUserAsync(userId, ct);
        await _audit.LogAsync(
            "navigation_permission",
            userId,
            "set_user",
            before: new { user_id = userId, permissions = before },
            after: new { user_id = userId, permissions = after },
            ct: ct);
        return new OkObjectResult(after.Select(ToItem).ToList());
    }

    private static NavigationVisibilityUpdate ToUpdate(NavigationVisibilityItem item) =>
        new(item.PageKey, item.Inherit == true ? null : item.Visible);

    private static NavigationVisibilityItem ToItem(NavigationVisibility item) =>
        ToItem(item.PageKey, item.Visible);

    private static NavigationVisibilityItem ToItem(string pageKey, bool visible) => new()
    {
        PageKey = pageKey,
        Visible = visible,
    };
}
