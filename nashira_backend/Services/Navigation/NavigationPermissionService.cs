using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Navigation;

public sealed record NavigationVisibility(string PageKey, bool Visible);

// null means "inherit" and removes the stored override.
public sealed record NavigationVisibilityUpdate(string PageKey, bool? Visible);

public sealed class NavigationPermissionService
{
    private static readonly HashSet<string> Roles =
        new(["admin", "operator", "viewer"], StringComparer.OrdinalIgnoreCase);

    private readonly AppDbContext _db;

    public NavigationPermissionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<NavigationVisibility>> ListRoleAsync(
        string role, CancellationToken ct)
    {
        role = NormalizeRole(role);
        return await _db.NavigationPermissions.AsNoTracking()
            .Where(p => p.IsActive && p.Role == role)
            .OrderBy(p => p.PageKey)
            .Select(p => new NavigationVisibility(p.PageKey, p.Visible))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NavigationVisibility>> ListUserAsync(
        Guid userId, CancellationToken ct)
    {
        await EnsureUserAsync(userId, ct);
        return await _db.NavigationPermissions.AsNoTracking()
            .Where(p => p.IsActive && p.UserId == userId)
            .OrderBy(p => p.PageKey)
            .Select(p => new NavigationVisibility(p.PageKey, p.Visible))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetEffectiveAsync(
        Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct)
            ?? throw new NotFoundException("user not found");
        var role = NormalizeRole(user.Role);

        var roleValues = await _db.NavigationPermissions.AsNoTracking()
            .Where(p => p.IsActive && p.Role == role)
            .ToListAsync(ct);
        var userValues = await _db.NavigationPermissions.AsNoTracking()
            .Where(p => p.IsActive && p.UserId == userId)
            .ToListAsync(ct);

        var effective = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in roleValues)
            effective[item.PageKey] = item.Visible;
        foreach (var item in userValues)
            effective[item.PageKey] = item.Visible;
        return effective;
    }

    public Task UpdateRoleAsync(
        string role, IReadOnlyCollection<NavigationVisibilityUpdate> updates, CancellationToken ct)
    {
        role = NormalizeRole(role);
        return UpdateAsync(role, null, updates, ct);
    }

    public async Task UpdateUserAsync(
        Guid userId, IReadOnlyCollection<NavigationVisibilityUpdate> updates, CancellationToken ct)
    {
        await EnsureUserAsync(userId, ct);
        await UpdateAsync(null, userId, updates, ct);
    }

    private async Task UpdateAsync(
        string? role,
        Guid? userId,
        IReadOnlyCollection<NavigationVisibilityUpdate> updates,
        CancellationToken ct)
    {
        var normalized = updates
            .Select(u => new NavigationVisibilityUpdate(NormalizePageKey(u.PageKey), u.Visible))
            .ToList();
        if (normalized.Select(u => u.PageKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != normalized.Count)
            throw new ValidationException("navigation page keys must be unique");

        var existing = await _db.NavigationPermissions
            .Where(p => p.IsActive && p.Role == role && p.UserId == userId)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var update in normalized)
        {
            var row = existing.FirstOrDefault(p =>
                string.Equals(p.PageKey, update.PageKey, StringComparison.OrdinalIgnoreCase));
            if (update.Visible is null)
            {
                if (row is not null)
                {
                    _db.NavigationPermissions.Remove(row);
                    existing.Remove(row);
                }
                continue;
            }

            if (row is null)
            {
                row = new NavigationPermission
                {
                    NavigationPermissionId = Guid.NewGuid(),
                    Role = role,
                    UserId = userId,
                    PageKey = update.PageKey,
                    CreatedAt = now,
                };
                _db.NavigationPermissions.Add(row);
                existing.Add(row);
            }
            row.Visible = update.Visible.Value;
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task EnsureUserAsync(Guid userId, CancellationToken ct)
    {
        if (!await _db.Users.AnyAsync(u => u.UserId == userId && u.IsActive, ct))
            throw new NotFoundException("user not found");
    }

    private static string NormalizeRole(string role)
    {
        var normalized = (role ?? string.Empty).Trim().ToLowerInvariant();
        if (!Roles.Contains(normalized))
            throw new ValidationException("role must be admin, operator, or viewer");
        return normalized;
    }

    private static string NormalizePageKey(string pageKey)
    {
        var normalized = (pageKey ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 200 ||
            !normalized.StartsWith('/') ||
            normalized.Contains('?') ||
            normalized.Contains('#'))
            throw new ValidationException("page_key must be an application path without query or fragment");
        return normalized;
    }
}
