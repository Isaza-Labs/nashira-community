using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using ModuleEntity = nashira_backend.Data.Models.AllowedPythonModule;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Controllers;

public class WriteAllowedModule
{
    [JsonPropertyName("module")] public string? Module { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("requires_network")] public bool? RequiresNetwork { get; set; }

    // `stdlib` — already on the interpreter, nothing to install.
    // `pip`    — the provisioner installs PipSpec before the module becomes usable.
    [JsonPropertyName("source")] public string? Source { get; set; }

    // The pip requirement. Defaults to the module name, which is right often enough
    // to be a sensible default and wrong often enough to be worth setting explicitly:
    // `pip install pyyaml` imports as `yaml`.
    [JsonPropertyName("pip_spec")] public string? PipSpec { get; set; }

    // Install targets — pip rows only; a stdlib row has nothing to install and refuses
    // them. On create, install_in_system defaults to true (the system Python, as
    // before targets existed) and environment_ids to none. On update, null leaves
    // either unchanged; `environment_ids: []` removes every environment target.
    [JsonPropertyName("install_in_system")] public bool? InstallInSystem { get; set; }
    [JsonPropertyName("environment_ids")] public List<Guid>? EnvironmentIds { get; set; }
}

// One uv environment a pip row is installed in, with what the environments screen
// would show for it.
public class ModuleEnvironmentTarget
{
    [JsonPropertyName("python_environment_id")] public Guid PythonEnvironmentId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
}

public class AllowedModuleResponse
{
    [JsonPropertyName("allowed_python_module_id")] public Guid Id { get; set; }
    [JsonPropertyName("module")] public string Module { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("requires_network")] public bool RequiresNetwork { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = ModuleEntity.SourceStdlib;
    [JsonPropertyName("pip_spec")] public string? PipSpec { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = ModuleEntity.StatusReady;
    [JsonPropertyName("installed_version")] public string? InstalledVersion { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    // The install targets. Meaningful for pip rows only; a stdlib row always reports an
    // empty environments list.
    [JsonPropertyName("install_in_system")] public bool InstallInSystem { get; set; } = true;
    [JsonPropertyName("environments")] public List<ModuleEnvironmentTarget> Environments { get; set; } = [];
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// The python_snippet import allowlist. Admin-only: every row here widens what
// arbitrary snippet code can reach, so adding one is a platform decision, not an
// authoring convenience.
//
// Two kinds of row, and the difference is whether anything has to be installed. A
// stdlib row is born `ready` because the standard library is already on disk; a pip
// row is born `pending` and waits for PythonPackageProvisionerHostedService. Only
// `ready` rows are offered to a snippet, so a package that failed to install can
// never be imported — the failure surfaces here, next to the admin who caused it,
// instead of as an ImportError inside somebody else's workflow run.
//
// A pip row also has INSTALL TARGETS: the system Python (InstallInSystem, the
// provisioner's site dir) and any number of uv environments (python_module_environments).
// An environment receives the package through its effective requirements, so every
// change here that alters them — a target added or removed, a new pip spec, a revoked
// row — puts the affected environments back to pending. The allowlist itself remains the
// only gate on what a snippet may import, whichever interpreter runs it.
[ApiController]
[Route("api/python-modules")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public partial class AllowedPythonModuleController : ControllerBase
{
    // A Python root-module identifier, nothing fancier. The value lands verbatim
    // inside the sandbox harness's allowlist set, so this is also what keeps an
    // admin typo from injecting into generated Python.
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$")]
    private static partial Regex DistributionNameRegex();

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$")]
    private static partial Regex ModuleNameRegex();

    // A pip requirement specifier: name, extras, version pins. Deliberately excludes
    // whitespace and every shell metacharacter. The spec reaches pip through
    // ArgumentList with no shell involved, so this is defence in depth — but it is
    // also what stops `requests --index-url http://…` from smuggling a second
    // argument through a field that reads like it only takes a package name.
    [GeneratedRegex(@"^[A-Za-z0-9._\-\[\]=<>!~,]+$")]
    private static partial Regex PipSpecRegex();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public AllowedPythonModuleController(AppDbContext db, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AllowedModuleResponse>>> Get(
        int limit = 200, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.AllowedPythonModules.AsNoTracking().Where(m => m.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(m => m.Module).Skip(offset).Take(limit).ToListAsync(ct);
        var targets = await TargetsAsync(rows.Select(r => r.AllowedPythonModuleId).ToList(), ct);
        return new OkObjectResult(new ListResponse<AllowedModuleResponse>
        {
            Items = rows.Select(r => ToResponse(r, targets.GetValueOrDefault(r.AllowedPythonModuleId))).ToList(),
            Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AllowedModuleResponse>> Post(
        [FromBody] WriteAllowedModule dto, CancellationToken ct)
    {
        var source = RequireSource(dto.Source);
        var module = RequireModuleName(dto.Module, source);
        var pipSpec = source == ModuleEntity.SourcePip ? RequirePipSpec(dto.PipSpec, module) : null;

        var installInSystem = true;
        List<PythonEnvironment> envTargets = [];
        if (source == ModuleEntity.SourcePip)
        {
            installInSystem = dto.InstallInSystem ?? true;
            if (!installInSystem) RequireImportNameWithoutSystem(module);
            envTargets = await PythonEnvironmentService.RequireActiveEnvironmentsAsync(
                _db, Distinct(dto.EnvironmentIds), ct);
        }
        else
        {
            RequireNoTargetsForStdlib(dto);
        }

        if (await _db.AllowedPythonModules.AnyAsync(m => m.Module == module && m.IsActive, ct))
            throw new ConflictException($"'{module}' is already on the allowlist", "module_already_allowed");

        // The unique index on Module spans soft-deleted rows, so a module an admin
        // removed and is now re-adding has to be revived rather than inserted again.
        // Reviving resets the install state on purpose: the package may well be gone
        // from the volume, and a surviving `ready` would let a snippet import
        // something that is not there.
        var revived = await _db.AllowedPythonModules
            .FirstOrDefaultAsync(m => m.Module == module && !m.IsActive, ct);

        var now = DateTime.UtcNow;
        var row = revived ?? new ModuleEntity
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            Module = module,
            CreatedAt = now,
        };

        row.Description = dto.Description;
        row.RequiresNetwork = dto.RequiresNetwork ?? false;
        row.Source = source;
        row.PipSpec = pipSpec;
        row.Status = source == ModuleEntity.SourceStdlib
            ? ModuleEntity.StatusReady
            : ModuleEntity.StatusPending;
        row.InstalledVersion = null;
        row.Error = null;
        row.InstallInSystem = installInSystem;
        row.CreatedBy = _user.IsAuthenticated ? _user.UserId : null;
        row.IsActive = true;
        row.UpdatedAt = now;

        if (revived is null) _db.AllowedPythonModules.Add(row);

        // A revived row starts from the targets asked for now. Its old links were
        // removed with it, but a restore through the audit screen does not re-add
        // them, so anything left over is cleared rather than trusted.
        if (revived is not null)
            _db.PythonModuleEnvironments.RemoveRange(await _db.PythonModuleEnvironments
                .Where(l => l.AllowedPythonModuleId == row.AllowedPythonModuleId).ToListAsync(ct));
        foreach (var env in envTargets)
            _db.PythonModuleEnvironments.Add(new PythonModuleEnvironment
            {
                AllowedPythonModuleId = row.AllowedPythonModuleId,
                PythonEnvironmentId = env.PythonEnvironmentId,
                CreatedAt = now,
            });
        // Each targeted environment's effective requirements just gained a line.
        await PythonEnvironmentService.MarkEnvironmentsPendingAsync(
            _db, envTargets.Select(e => e.PythonEnvironmentId), ct);
        await _db.SaveChangesAsync(ct);

        // Approving a pip package ends with third-party code executing wherever
        // snippets run. It is the highest-privilege action on this screen, and the
        // exact version pin is the artefact an incident review needs — so PipSpec is
        // recorded as written, with only index credentials masked (the same masking as
        // an environment's requirements; the charset above cannot carry any today).
        await _audit.LogAsync("allowed_python_module", row.AllowedPythonModuleId, "create",
            after: new
            {
                row.Module, row.Source, PipSpec = Mask(row.PipSpec), row.Status, row.RequiresNetwork,
                row.InstallInSystem, Environments = envTargets.Select(e => e.Name).OrderBy(n => n).ToList(),
            }, ct: ct);

        return await ToResponseAsync(row, ct);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AllowedModuleResponse>> Update(
        Guid id, [FromBody] WriteAllowedModule dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        // The module name is the identity — renaming it is a different permission,
        // and editing it in place would silently change what snippets may import.
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.RequiresNetwork is not null) row.RequiresNetwork = dto.RequiresNetwork.Value;

        if (row.Source != ModuleEntity.SourcePip) RequireNoTargetsForStdlib(dto);

        var now = DateTime.UtcNow;
        var links = await _db.PythonModuleEnvironments
            .Where(l => l.AllowedPythonModuleId == row.AllowedPythonModuleId)
            .ToListAsync(ct);
        // Environments whose effective requirements this update changes. Each goes
        // back to pending — the old target as much as the new one, since the package
        // has to leave its build too.
        var affected = new HashSet<Guid>();

        // A different pip spec is a different package. Re-queue it rather than leave
        // a `ready` row describing an install nobody performed.
        if (dto.PipSpec is not null && row.Source == ModuleEntity.SourcePip)
        {
            var pipSpec = RequirePipSpec(dto.PipSpec, row.Module);
            if (!string.Equals(pipSpec, row.PipSpec, StringComparison.Ordinal))
            {
                row.PipSpec = pipSpec;
                row.Status = ModuleEntity.StatusPending;
                row.InstalledVersion = null;
                row.Error = null;
                affected.UnionWith(links.Select(l => l.PythonEnvironmentId));
            }
        }

        if (row.Source == ModuleEntity.SourcePip && dto.EnvironmentIds is not null)
        {
            var wanted = Distinct(dto.EnvironmentIds);
            await PythonEnvironmentService.RequireActiveEnvironmentsAsync(_db, wanted, ct);
            var current = links.Select(l => l.PythonEnvironmentId).ToHashSet();

            var removed = links.Where(l => !wanted.Contains(l.PythonEnvironmentId)).ToList();
            _db.PythonModuleEnvironments.RemoveRange(removed);
            affected.UnionWith(removed.Select(l => l.PythonEnvironmentId));

            foreach (var envId in wanted.Where(id => !current.Contains(id)))
            {
                _db.PythonModuleEnvironments.Add(new PythonModuleEnvironment
                {
                    AllowedPythonModuleId = row.AllowedPythonModuleId,
                    PythonEnvironmentId = envId,
                    CreatedAt = now,
                });
                affected.Add(envId);
            }
        }

        if (row.Source == ModuleEntity.SourcePip
            && dto.InstallInSystem is { } inSystem && inSystem != row.InstallInSystem)
        {
            if (!inSystem) RequireImportNameWithoutSystem(row.Module);
            row.InstallInSystem = inSystem;
            if (inSystem)
            {
                // Now it has to be on the system interpreter's disk before it is offered.
                row.Status = ModuleEntity.StatusPending;
                row.InstalledVersion = null;
                row.Error = null;
            }
            else if (row.Status != ModuleEntity.StatusReady)
            {
                // A pending or failed system install no longer applies; the provisioner
                // re-plans the row, skips the install and marks it ready. A row that was
                // already ready stays ready — and its files stay in the site dir, since
                // nothing uninstalls them.
                row.Status = ModuleEntity.StatusPending;
                row.Error = null;
            }
        }

        await PythonEnvironmentService.MarkEnvironmentsPendingAsync(_db, affected, ct);
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return await ToResponseAsync(row, ct);
    }

    // Re-queue a failed install. Separate from PUT because it takes no body and means
    // exactly one thing — "try that again" — after the admin fixed whatever pip
    // complained about: a typo, a version that does not exist, no wheel available.
    [HttpPost("{id:guid}/retry")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AllowedModuleResponse>> Retry(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row.Source != ModuleEntity.SourcePip)
            throw new ValidationException("only pip modules have an install to retry");

        row.Status = ModuleEntity.StatusPending;
        row.Error = null;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("allowed_python_module", row.AllowedPythonModuleId, "retry_install",
            after: new { row.Module, PipSpec = Mask(row.PipSpec), row.Status }, ct: ct);

        return await ToResponseAsync(row, ct);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AllowedModuleResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        // Read before the links go, for the response and the audit entry.
        var response = await ToResponseAsync(row, ct);

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;

        // A revoked package leaves every environment it was installed in: those
        // environments rebuild without it. The system site dir keeps its files — there
        // is no uninstall — but the allowlist no longer offers the module.
        var links = await _db.PythonModuleEnvironments
            .Where(l => l.AllowedPythonModuleId == row.AllowedPythonModuleId)
            .ToListAsync(ct);
        _db.PythonModuleEnvironments.RemoveRange(links);
        await PythonEnvironmentService.MarkEnvironmentsPendingAsync(
            _db, links.Select(l => l.PythonEnvironmentId), ct);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("allowed_python_module", row.AllowedPythonModuleId, "delete",
            before: new
            {
                row.Module, row.Source, PipSpec = Mask(row.PipSpec), row.Status, row.InstallInSystem,
                Environments = response.Environments.Select(e => e.Name).ToList(),
            }, ct: ct);

        response.UpdatedAt = row.UpdatedAt;
        return response;
    }

    // `module` is the name a snippet WRITES after `import`, which is often not the
    // name pip installs: python-dateutil imports as dateutil, beautifulsoup4 as bs4,
    // Pillow as PIL.
    //
    // For a pip package the admin is not asked to know that. A distribution name is
    // accepted here, and the provisioner asks the INSTALLED distribution what it
    // provides and points the row at that name — see DiscoverImportNameAsync. The
    // row stays `pending` until it does, and only `ready` rows are offered to a
    // snippet, so there is no window where the interim name is live.
    //
    // A stdlib entry has no installer to ask, so its name must be the import name.
    /// <summary>Test seam for the name rule; the controller's own path is below.</summary>
    internal static string ValidateModuleNameForTests(string? raw, string source = ModuleEntity.SourceStdlib)
        => RequireModuleName(raw, source);

    private static string RequireModuleName(string? raw, string source)
    {
        var module = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (module.Length == 0) throw new ValidationException("module is required");
        if (ModuleNameRegex().IsMatch(module)) return module;

        // A pip package may be named the way pip names it. The import name is
        // discovered on install, because nothing derives one from the other.
        //
        // Before the submodule check, and that ordering is the point: a dot means
        // different things on either side of it. `ruamel.yaml` and `zope.interface`
        // are distribution names; `urllib.request` is a submodule. Nothing in the
        // string tells them apart — the source does.
        if (source == ModuleEntity.SourcePip && DistributionNameRegex().IsMatch(module))
            return module;

        // Stdlib only, therefore. Approval is granted at the root, so naming
        // `urllib.request` would approve something no import statement resolves to.
        if (module.Contains('.'))
            throw new ValidationException(
                $"'{module}' names a submodule — approve its root instead. Allowing "
                + $"`{module.Split('.')[0]}` covers it, and a snippet may still write "
                + $"`import {module}`.",
                "module_is_submodule");

        throw new ValidationException(
            source == ModuleEntity.SourcePip
                ? "module must be a package name (letters, digits and . _ -) or the name a "
                  + "snippet writes after `import`."
                : "a stdlib module must be the name a snippet writes after `import`: lowercase "
                  + "letters, digits and underscores. For a pip package choose source `pip` — "
                  + "the package name is then accepted and the import name is discovered on "
                  + "install.",
            "invalid_module_name");
    }

    private static string RequireSource(string? raw)
    {
        // Defaults to stdlib: an omitted source means an older client, and every row
        // that existed before pip support was a standard-library entry.
        var source = (raw ?? ModuleEntity.SourceStdlib).Trim().ToLowerInvariant();
        if (source != ModuleEntity.SourceStdlib && source != ModuleEntity.SourcePip)
            throw new ValidationException("source must be 'stdlib' or 'pip'");
        return source;
    }

    private static string RequirePipSpec(string? raw, string module)
    {
        var spec = (raw ?? string.Empty).Trim();
        if (spec.Length == 0) spec = module;
        if (!PipSpecRegex().IsMatch(spec))
            throw new ValidationException(
                "pip_spec may only contain letters, digits and . _ - [ ] = < > ! ~ , "
                + "— no spaces: one requirement per row, and pip flags do not belong here");
        return spec;
    }

    private async Task<ModuleEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.AllowedPythonModules
            .FirstOrDefaultAsync(m => m.AllowedPythonModuleId == id && m.IsActive, ct);
        if (row is null) throw new NotFoundException("module not found");
        return row;
    }

    // ── install targets ─────────────────────────────────────────────────────

    private static List<Guid> Distinct(List<Guid>? ids) =>
        ids is null ? [] : ids.Where(id => id != Guid.Empty).Distinct().ToList();

    private static void RequireNoTargetsForStdlib(WriteAllowedModule dto)
    {
        if (dto.EnvironmentIds is { Count: > 0 } || dto.InstallInSystem == false)
            throw new ValidationException(
                "install targets apply to pip modules only — a stdlib module is already part of every "
                + "interpreter and has nothing to install",
                "install_targets_not_applicable");
    }

    // Without the system install there is no installed distribution to ask what it
    // imports as (the provisioner discovers that after `pip install --target`), so the
    // row has to carry the import name already.
    private static void RequireImportNameWithoutSystem(string module)
    {
        if (!ModuleNameRegex().IsMatch(module))
            throw new ValidationException(
                $"'{module}' is a package name, and the name it imports as is only discovered by installing it "
                + "in the system Python. To install it in environments only, use the import name — the name a "
                + "script writes after `import` (python-dateutil imports as dateutil).",
                "module_import_name_required");
    }

    // Pip specs and pip's error output get the same credential masking as an
    // environment's requirements, everywhere they are echoed.
    private static string? Mask(string? text) => PythonEnvironmentRules.MaskCredentials(text);

    // The environments each row is installed in, with their names and statuses.
    private async Task<Dictionary<Guid, List<ModuleEnvironmentTarget>>> TargetsAsync(
        IReadOnlyCollection<Guid> moduleIds, CancellationToken ct)
    {
        if (moduleIds.Count == 0) return [];
        var rows = await (
            from l in _db.PythonModuleEnvironments.AsNoTracking()
            join e in _db.PythonEnvironments.AsNoTracking() on l.PythonEnvironmentId equals e.PythonEnvironmentId
            where e.IsActive && moduleIds.Contains(l.AllowedPythonModuleId)
            select new { l.AllowedPythonModuleId, e.PythonEnvironmentId, e.Name, e.Status })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.AllowedPythonModuleId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new ModuleEnvironmentTarget
                {
                    PythonEnvironmentId = r.PythonEnvironmentId, Name = r.Name, Status = r.Status,
                })
                .ToList());
    }

    private async Task<AllowedModuleResponse> ToResponseAsync(ModuleEntity m, CancellationToken ct) =>
        ToResponse(m, (await TargetsAsync([m.AllowedPythonModuleId], ct)).GetValueOrDefault(m.AllowedPythonModuleId));

    private static AllowedModuleResponse ToResponse(ModuleEntity m, List<ModuleEnvironmentTarget>? targets) => new()
    {
        Id = m.AllowedPythonModuleId,
        Module = m.Module,
        Description = m.Description,
        RequiresNetwork = m.RequiresNetwork,
        Source = m.Source,
        PipSpec = Mask(m.PipSpec),
        Status = m.Status,
        InstalledVersion = m.InstalledVersion,
        Error = Mask(m.Error),
        InstallInSystem = m.InstallInSystem,
        Environments = m.Source == ModuleEntity.SourcePip ? targets ?? [] : [],
        UpdatedAt = m.UpdatedAt,
    };
}
