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

// What the builder finds when it is about to mark a build ready.
public enum BuildClaimState
{
    // Still claimed, and the effective requirements are the ones that were built.
    Current,
    // The claim was released or taken over (an edit, a rebuild, a delete).
    Lost,
    // Still claimed, but the effective requirements changed while the build ran.
    DefinitionChanged,
}

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

    public Task<Dictionary<Guid, IReadOnlyList<string>>> IncludedSpecsAsync(
        IReadOnlyCollection<Guid> envIds, CancellationToken ct) => IncludedSpecsAsync(_db, envIds, ct);

    public Task<IReadOnlyList<string>> IncludedSpecsAsync(Guid envId, CancellationToken ct) =>
        IncludedSpecsAsync(_db, envId, ct);

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

        // Allowlist rows that also installed into this environment lose that target.
        // Not a reason to refuse the delete: the package is still on the allowlist,
        // and the system Python or other environments it targets are unaffected.
        var links = await _db.PythonModuleEnvironments
            .Where(l => l.PythonEnvironmentId == id)
            .ToListAsync(ct);
        _db.PythonModuleEnvironments.RemoveRange(links);

        await _db.SaveChangesAsync(ct);
        return row;
    }

    // ── effective requirements ─────────────────────────────────────────────

    /// <summary>
    /// The pip specs allowlist rows contribute to each environment, keyed by
    /// environment id (normalised: deduplicated and sorted). <paramref name="envIds"/>
    /// null means every environment.
    /// </summary>
    /// <remarks>
    /// Every ACTIVE pip row that targets the environment counts, whatever its own
    /// status: that status is the system-Python install, and a row that failed there
    /// (or does not install there at all) can still belong in an environment. Rows are
    /// created by an administrator, so an active row is an approved one.
    /// </remarks>
    public static async Task<Dictionary<Guid, IReadOnlyList<string>>> IncludedSpecsAsync(
        AppDbContext db, IReadOnlyCollection<Guid>? envIds, CancellationToken ct)
    {
        var q = from l in db.PythonModuleEnvironments.AsNoTracking()
                join m in db.AllowedPythonModules.AsNoTracking()
                    on l.AllowedPythonModuleId equals m.AllowedPythonModuleId
                where m.IsActive && m.Source == AllowedPythonModule.SourcePip
                select new { l.PythonEnvironmentId, Spec = m.PipSpec ?? m.Module };
        if (envIds is not null) q = q.Where(x => envIds.Contains(x.PythonEnvironmentId));

        var rows = await q.ToListAsync(ct);
        return rows
            .GroupBy(r => r.PythonEnvironmentId)
            .ToDictionary(g => g.Key, g => PythonEnvironmentRules.NormalizeIncluded(g.Select(r => r.Spec)));
    }

    public static async Task<IReadOnlyList<string>> IncludedSpecsAsync(
        AppDbContext db, Guid envId, CancellationToken ct) =>
        (await IncludedSpecsAsync(db, [envId], ct)).GetValueOrDefault(envId) ?? [];

    /// <summary>The hash a build of <paramref name="env"/>'s current definition gets.</summary>
    public static async Task<string> EffectiveHashAsync(AppDbContext db, PythonEnvironment env, CancellationToken ct) =>
        PythonEnvironmentRules.EffectiveHash(
            env.PythonVersion, env.Requirements, await IncludedSpecsAsync(db, env.PythonEnvironmentId, ct));

    /// <summary>
    /// Puts every active environment in <paramref name="envIds"/> back to pending, on the
    /// caller's tracked context; the caller saves.
    /// </summary>
    /// <remarks>
    /// Called whenever an allowlist change alters what an environment's effective
    /// requirements would be — targets, pip spec, a deleted row. MarkPending also
    /// releases a running build's claim, so that build is discarded rather than marked
    /// ready with the packages of the definition it no longer matches.
    /// </remarks>
    public static async Task<int> MarkEnvironmentsPendingAsync(
        AppDbContext db, IEnumerable<Guid> envIds, CancellationToken ct)
    {
        var ids = envIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        var rows = await db.PythonEnvironments
            .Where(e => e.IsActive && ids.Contains(e.PythonEnvironmentId))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            MarkPending(row);
            row.UpdatedAt = now;
        }
        return rows.Count;
    }

    /// <summary>
    /// Validates the environments an allowlist row is to be installed in: each must
    /// exist and be active. Returns them, for the response.
    /// </summary>
    public static async Task<List<PythonEnvironment>> RequireActiveEnvironmentsAsync(
        AppDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var found = await db.PythonEnvironments.AsNoTracking()
            .Where(e => e.IsActive && ids.Contains(e.PythonEnvironmentId))
            .ToListAsync(ct);
        var missing = ids.Except(found.Select(e => e.PythonEnvironmentId)).ToList();
        if (missing.Count > 0)
            throw new ValidationException(
                $"environment_ids: no active Python environment with id {string.Join(", ", missing)} — "
                + "it may have been deleted",
                "python_env_not_found");
        return found;
    }

    /// <summary>
    /// Whether a build may still be marked ready: the claim is still the builder's, AND
    /// the environment's effective requirements still hash to what the build installed.
    /// </summary>
    /// <remarks>
    /// The claim alone is not enough. Every path that changes an environment's
    /// definition releases the claim, but the effective requirements also depend on
    /// other tables (the allowlist and its targets), and comparing the hash makes the
    /// guard hold even for a change that did not go through MarkPending — a build is
    /// never marked ready with stale packages.
    /// </remarks>
    public static async Task<BuildClaimState> CheckBuildClaimAsync(
        AppDbContext db, Guid id, string claimant, DateTime claimedAt, string builtHash, CancellationToken ct)
    {
        var row = await db.PythonEnvironments.AsNoTracking().FirstOrDefaultAsync(e =>
            e.PythonEnvironmentId == id && e.IsActive
            && e.Status == PythonEnvironment.StatusBuilding
            && e.ClaimedBy == claimant && e.ClaimedAt == claimedAt, ct);
        if (row is null) return BuildClaimState.Lost;
        var current = await EffectiveHashAsync(db, row, ct);
        return string.Equals(current, builtHash, StringComparison.Ordinal)
            ? BuildClaimState.Current
            : BuildClaimState.DefinitionChanged;
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
