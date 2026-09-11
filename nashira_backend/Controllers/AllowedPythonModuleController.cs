using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using ModuleEntity = nashira_backend.Data.Models.AllowedPythonModule;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

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
        return new OkObjectResult(new ListResponse<AllowedModuleResponse>
        {
            Items = rows.Select(ToResponse).ToList(), Total = total, Limit = limit, Offset = offset,
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
        row.CreatedBy = _user.IsAuthenticated ? _user.UserId : null;
        row.IsActive = true;
        row.UpdatedAt = now;

        if (revived is null) _db.AllowedPythonModules.Add(row);
        await _db.SaveChangesAsync(ct);

        // Approving a pip package ends with third-party code executing wherever
        // snippets run. It is the highest-privilege action on this screen, and the
        // exact version pin is the artefact an incident review needs — so PipSpec is
        // recorded verbatim rather than summarised.
        await _audit.LogAsync("allowed_python_module", row.AllowedPythonModuleId, "create",
            after: new { row.Module, row.Source, row.PipSpec, row.Status, row.RequiresNetwork }, ct: ct);

        return ToResponse(row);
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
            }
        }

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
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
            after: new { row.Module, row.PipSpec, row.Status }, ct: ct);

        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AllowedModuleResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("allowed_python_module", row.AllowedPythonModuleId, "delete",
            before: new { row.Module, row.Source, row.PipSpec, row.Status }, ct: ct);

        return ToResponse(row);
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

    private static AllowedModuleResponse ToResponse(ModuleEntity m) => new()
    {
        Id = m.AllowedPythonModuleId,
        Module = m.Module,
        Description = m.Description,
        RequiresNetwork = m.RequiresNetwork,
        Source = m.Source,
        PipSpec = m.PipSpec,
        Status = m.Status,
        InstalledVersion = m.InstalledVersion,
        Error = m.Error,
        UpdatedAt = m.UpdatedAt,
    };
}
