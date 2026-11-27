using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Worker.Python;

// What a caller asks for. Null means "leave it as it is" on update; on create the
// required fields are checked by the service.
public sealed record PythonEnvironmentDraft(
    string? Name = null,
    string? Description = null,
    // "" clears it back to the image's python3; null leaves it unchanged.
    string? PythonVersion = null,
    string? Requirements = null,
    bool? IsSshFallback = null);

// The one place a PythonEnvironment row is validated and changed. The REST controller
// delegates here so the rules — the name, the single SSH fallback, the status reset —
// are enforced in one place and testable without HTTP.
public sealed class PythonEnvironmentService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public PythonEnvironmentService(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<List<PythonEnvironment>> ListAsync(CancellationToken ct) =>
        await _db.PythonEnvironments.AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.Name)
            .ToListAsync(ct);

    public async Task<PythonEnvironment> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.PythonEnvironments
            .FirstOrDefaultAsync(e => e.PythonEnvironmentId == id && e.IsActive, ct);
        return row ?? throw new NotFoundException("python environment not found", "python_env_not_found");
    }

    public async Task<PythonEnvironment> CreateAsync(PythonEnvironmentDraft draft, CancellationToken ct)
    {
        var name = PythonEnvironmentRules.RequireName(draft.Name);
        var version = PythonEnvironmentRules.NormalizePythonVersion(draft.PythonVersion);
        var requirements = PythonEnvironmentRules.NormalizeRequirements(draft.Requirements);

        if (await _db.PythonEnvironments.AnyAsync(e => e.IsActive && e.Name == name, ct))
            throw new ConflictException($"a Python environment named '{name}' already exists", "python_env_name_taken");

        var fallback = draft.IsSshFallback ?? false;
        if (fallback) await RequireNoOtherFallbackAsync(null, ct);

        var now = DateTime.UtcNow;
        var row = new PythonEnvironment
        {
            PythonEnvironmentId = Guid.NewGuid(),
            Name = name,
            Description = PythonEnvironmentRules.NormalizeDescription(draft.Description),
            PythonVersion = version,
            Requirements = requirements,
            IsSshFallback = fallback,
            // Born pending: the builder picks it up on its next sweep.
            Status = PythonEnvironment.StatusPending,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PythonEnvironments.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<PythonEnvironment> UpdateAsync(Guid id, PythonEnvironmentDraft draft, CancellationToken ct)
    {
        var row = await GetAsync(id, ct);

        // The name is the build's directory and what devices and snippets are shown by;
        // renaming in place would orphan the build. Delete and recreate to rename.
        if (draft.Name is not null && !string.Equals(draft.Name.Trim(), row.Name, StringComparison.Ordinal))
            throw new ValidationException(
                "an environment's name cannot be changed — create a new environment instead",
                "python_env_name_immutable");

        var rebuild = false;

        if (draft.PythonVersion is not null)
        {
            var version = PythonEnvironmentRules.NormalizePythonVersion(draft.PythonVersion);
            if (!string.Equals(version, row.PythonVersion, StringComparison.Ordinal))
            {
                row.PythonVersion = version;
                rebuild = true;
            }
        }

        if (draft.Requirements is not null)
        {
            // The edit form was filled from a masked response; put the stored
            // credentials back before comparing, or an unchanged save would look like a
            // change and wipe the index password.
            var restored = PythonEnvironmentRules.RestoreMaskedCredentials(draft.Requirements, row.Requirements);
            var requirements = PythonEnvironmentRules.NormalizeRequirements(restored);
            if (!string.Equals(requirements, row.Requirements, StringComparison.Ordinal))
            {
                row.Requirements = requirements;
                rebuild = true;
            }
        }

        if (draft.Description is not null)
            row.Description = PythonEnvironmentRules.NormalizeDescription(draft.Description);

        if (draft.IsSshFallback is { } fallback && fallback != row.IsSshFallback)
        {
            if (fallback) await RequireNoOtherFallbackAsync(row.PythonEnvironmentId, ct);
            row.IsSshFallback = fallback;
        }

        // A different definition is a different environment. Leaving the row `ready`
        // would let an execution run against packages nobody asked for any more.
        if (rebuild) MarkPending(row);

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<PythonEnvironment> RebuildAsync(Guid id, CancellationToken ct)
    {
        var row = await GetAsync(id, ct);
        MarkPending(row);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    // Soft delete. Refused while a live device or snippet still names the environment:
    // their next execution would fail on a missing environment, and finding that out
    // mid-run is worse than a refused delete.
    public async Task<PythonEnvironment> DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await GetAsync(id, ct);

        var devices = await _db.Devices.AsNoTracking()
            .Where(d => d.IsActive && d.PythonEnvironmentId == id)
            .Select(d => d.DeviceName).Take(5).ToListAsync(ct);
        var snippets = await _db.Snippets.AsNoTracking()
            .Where(s => s.IsActive && s.PythonEnvironmentId == id)
            .Select(s => s.Name).Take(5).ToListAsync(ct);
        if (devices.Count + snippets.Count > 0)
        {
            var users = devices.Select(d => $"device {d}").Concat(snippets.Select(s => $"snippet {s}"));
            throw new ConflictException(
                $"still used by: {string.Join(", ", users)} — point them elsewhere first",
                "python_env_in_use");
        }

        row.IsActive = false;
        // A deleted row cannot be the fallback; clearing it keeps "at most one active
        // fallback" true even if the row is ever restored.
        row.IsSshFallback = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Validates an environment reference on a device or snippet: null passes, anything
    /// else must be an active environment. Any status is accepted — an environment still
    /// building is a legitimate choice, and the resolver refuses it clearly at run time
    /// if it never becomes ready.
    /// </summary>
    public static async Task RequireExistingAsync(AppDbContext db, Guid? id, CancellationToken ct)
    {
        if (id is not { } envId) return;
        if (!await db.PythonEnvironments.AnyAsync(e => e.PythonEnvironmentId == envId && e.IsActive, ct))
            throw new ValidationException("python_environment_id does not exist", "python_env_not_found");
    }

    internal static void MarkPending(PythonEnvironment row)
    {
        row.Status = PythonEnvironment.StatusPending;
        row.LastError = null;
        // Releasing the claim makes the row claimable immediately. A builder still
        // working on the previous definition loses its conditional final update
        // (it checks the claim) and the new definition is built next.
        row.ClaimedBy = null;
        row.ClaimedAt = null;
    }

    // At most one active fallback. Refused rather than silently moved: the flag decides
    // which packages every failing SSH connection is retried with, and taking it from
    // another environment is a decision the admin should make on purpose.
    private async Task RequireNoOtherFallbackAsync(Guid? self, CancellationToken ct)
    {
        var other = await _db.PythonEnvironments.AsNoTracking()
            .Where(e => e.IsActive && e.IsSshFallback && e.PythonEnvironmentId != self)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct);
        if (other is not null)
            throw new ConflictException(
                $"'{other}' is already the SSH fallback environment — turn it off there first; only one "
                + "environment can be the fallback",
                "python_env_fallback_taken");
    }
}
