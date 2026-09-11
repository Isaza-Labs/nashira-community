using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.AiApiSpec;
using nashira_backend.Data.DTos.Integration;
using nashira_backend.Data.DTos.Skill;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Identity;
using AiApiSpecEntity = nashira_backend.Data.Models.AiApiSpec;
using SkillEntity = nashira_backend.Data.Models.AiPromptSkill;

namespace nashira_backend.Services.Integration;

// Conflict codes the UI branches on. A name owned by NOBODY can be adopted with one
// click; a name owned by ANOTHER integration cannot, because taking it would strip it
// from that integration without anyone there being asked.
public static class CatalogConflicts
{
    public const string SkillNameGlobal = "skill_name_global";
    public const string SkillNameTaken = "skill_name_taken";
    public const string SpecApiGlobal = "spec_api_global";
    public const string SpecApiTaken = "spec_api_taken";
}

// What a bundle attach produced. Actions are counted once for the whole batch
// rather than per spec: they are materialised from every linked spec together, so a
// per-spec number would be the running total and read as though the last spec had
// created all of them.
public sealed record BundleAttachResult(
    int SkillsAttached, int SpecsAttached, int ActionsCreated, int ActionsUpdated, int ActionsDisappeared);

public interface IIntegrationCatalog
{
    Task<IntegrationBundleView> GetBundleAsync(Guid integrationId, CancellationToken ct);
    Task<AttachSpecResult> AttachSpecAsync(Guid integrationId, AttachSpecRequest dto, CancellationToken ct);
    Task<AiPromptSkillResponse> AttachSkillAsync(Guid integrationId, AttachSkillRequest dto, CancellationToken ct);

    // Validates every staged item BEFORE writing any of them, so a bad third file
    // cannot leave the first two persisted. Assumes the caller has an open
    // transaction covering the integration row too — the whole point of the bundle
    // endpoint is that a half-created integration never exists.
    Task<BundleAttachResult> AttachBundleAsync(
        Guid integrationId,
        IReadOnlyList<BundledSkill> skills,
        IReadOnlyList<BundledSpec> specs,
        CancellationToken ct);

    // In-memory caches, refreshed AFTER the caller commits. Reloading the spec index
    // from inside an uncommitted transaction would populate it from the old state and
    // then never be told to try again.
    Task RefreshCachesAsync(CancellationToken ct);
}

// Managing the skills and specs that belong to one integration, from the
// integration itself.
//
// The link already existed on both rows and both controllers accepted it — but only
// as a field on a form somewhere else. To give NetBox a spec you went to Admin →
// Specs, uploaded it, remembered to set integration_id, then came back and ran
// sync-actions. Three screens for one intent, and skipping the last step left an
// integration whose spec was registered and whose action list had not moved, with
// nothing on screen saying so.
//
// So: attaching a spec here re-materialises the actions in the same call, and the
// result reports what changed rather than leaving the caller to infer it.
//
// Upsert semantics throughout, keyed on the natural identity (spec `api`, skill
// `name`). Re-uploading a spec updates it in place, which is what "load a new
// version" means — creating a second row would leave the agent with two documents
// describing one API and no way to tell which is current.
public sealed class IntegrationCatalog : IIntegrationCatalog
{
    // The spec parser holds the whole document in memory and the agent's discovery
    // tools slice it per call; past a couple of megabytes that stops being a spec
    // and starts being a denial of service against our own prompt budget.
    private const int MaxSpecBytes = 2 * 1024 * 1024;
    private const int MaxSkillBytes = 512 * 1024;

    // Matches the `api` identifier convention the built-in specs already follow, and
    // it is what the agent types as discover_operations(api="…").
    private static readonly Regex ApiPattern = new(@"^[a-z0-9_\-]+$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiSpecIndex _index;
    private readonly ISkillPromptLoader _loader;
    private readonly IIntegrationActionSync _sync;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;
    private readonly ILogger<IntegrationCatalog> _logger;

    public IntegrationCatalog(
        AppDbContext db,
        ICurrentUser user,
        IApiSpecIndex index,
        ISkillPromptLoader loader,
        IIntegrationActionSync sync,
        ITemplateSecurityValidator validator,
        IValidationRecorder recorder,
        ILogger<IntegrationCatalog> logger)
    {
        _db = db;
        _user = user;
        _index = index;
        _loader = loader;
        _sync = sync;
        _validator = validator;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<IntegrationBundleView> GetBundleAsync(Guid integrationId, CancellationToken ct)
    {
        await RequireIntegrationAsync(integrationId, ct);

        var skills = await _db.AiPromptSkills.AsNoTracking()
            .Where(s => s.IntegrationId == integrationId && s.IsActive)
            .OrderBy(s => s.Priority).ThenBy(s => s.Name)
            .ToListAsync(ct);

        var specs = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IntegrationId == integrationId && s.IsActive)
            .OrderBy(s => s.Api)
            .ToListAsync(ct);

        return new IntegrationBundleView
        {
            // Skill content travels: these are small, and the screen offering an edit
            // box would otherwise have to fetch each one individually to fill it.
            Skills = skills.Select(ToSkillResponse).ToList(),
            // Spec content does NOT: a spec is up to 2 MB and a list of five would be
            // a 10 MB response for a table showing names and operation counts.
            Specs = specs.Select(ToSpecResponse).ToList(),
        };
    }

    public async Task<AttachSpecResult> AttachSpecAsync(
        Guid integrationId, AttachSpecRequest dto, CancellationToken ct)
    {
        await RequireIntegrationAsync(integrationId, ct);

        var spec = await UpsertSpecAsync(integrationId, dto.Api, dto.Content, dto.Adopt == true, ct);

        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);

        // The point of doing this here: one upload updates both catalogues the agent
        // and the workflow builder read, so they cannot drift.
        var sync = await _sync.SyncAsync(integrationId, ct);

        _logger.LogInformation(
            "integration.catalog.spec_attached integration={Integration} api={Api} ops={Ops} "
            + "created={Created} updated={Updated} disappeared={Disappeared}",
            integrationId, spec.Api, spec.OperationCount, sync.Created, sync.Updated, sync.Disappeared);

        return new AttachSpecResult
        {
            Spec = ToSpecResponse(spec),
            ActionsCreated = sync.Created,
            ActionsUpdated = sync.Updated,
            ActionsUnchanged = sync.Unchanged,
            ActionsDisappeared = sync.Disappeared,
        };
    }

    public async Task<BundleAttachResult> AttachBundleAsync(
        Guid integrationId,
        IReadOnlyList<BundledSkill> skills,
        IReadOnlyList<BundledSpec> specs,
        CancellationToken ct)
    {
        // Duplicates inside one submission are caught here rather than by the unique
        // index: the second upsert would silently overwrite the first and the caller
        // would be told two specs were attached when one file's content was thrown away.
        RequireDistinct(specs.Select(s => (s.Api ?? string.Empty).Trim().ToLowerInvariant()),
            "two specs in this bundle use the api name");
        RequireDistinct(skills.Select(s => (s.Name ?? string.Empty).Trim()),
            "two skills in this bundle use the name");

        foreach (var spec in specs)
            await UpsertSpecAsync(integrationId, spec.Api, spec.Content, adopt: false, ct);
        foreach (var skill in skills)
            await UpsertSkillAsync(
                integrationId, skill.Name, skill.Content, skill.Priority, adopt: false, ct);

        await _db.SaveChangesAsync(ct);

        // Runs on the same context, so it sees the specs just written even though the
        // caller has not committed. If anything here throws, the caller's rollback
        // takes the actions with it.
        var sync = specs.Count > 0
            ? await _sync.SyncAsync(integrationId, ct)
            : new ActionSyncResult(0, 0, 0, 0, 0);

        _logger.LogInformation(
            "integration.catalog.bundle_attached integration={Integration} skills={Skills} specs={Specs} "
            + "actions_created={Created}",
            integrationId, skills.Count, specs.Count, sync.Created);

        return new BundleAttachResult(
            skills.Count, specs.Count, sync.Created, sync.Updated, sync.Disappeared);
    }

    public async Task RefreshCachesAsync(CancellationToken ct)
    {
        await _index.ReloadAsync(ct);
        _loader.Invalidate();
    }

    // Validates and upserts one spec row. Does NOT save, reload or sync — the caller
    // decides when, because a bundle does all three once at the end rather than N
    // times.
    private async Task<AiApiSpecEntity> UpsertSpecAsync(
        Guid integrationId, string? rawApi, string? content, bool adopt, CancellationToken ct)
    {
        var api = (rawApi ?? string.Empty).Trim().ToLowerInvariant();
        if (api.Length == 0) throw new ValidationException("api is required");
        if (!ApiPattern.IsMatch(api))
            throw new ValidationException(
                $"'{api}' is not a valid api name — lowercase letters, digits, underscore or hyphen. "
                + "It is the identifier the agent passes to discover_operations");
        if (string.IsNullOrWhiteSpace(content)) throw new ValidationException($"spec '{api}' has no content");
        if (content.Length > MaxSpecBytes)
            throw new ValidationException($"spec '{api}' exceeds {MaxSpecBytes / 1024} KB");

        // Same gate as POST /api/ai/specs. Uploading through a different screen must
        // not be a way around the security validator.
        var result = _validator.ValidateSpec(api, content);
        await _recorder.RecordAsync(Data.Models.ValidationRecord.KindSpec, api, result, ct);
        if (!result.Ok)
            throw new ValidationException($"spec '{api}' failed validation: " + Errors(result));

        // Parsed strictly, unlike a lenient "save it anyway and let them fix it": an
        // unparseable spec materialises no actions, and an integration that reports a
        // successful upload and an unchanged action list is the failure this whole
        // endpoint exists to prevent.
        int operationCount;
        try
        {
            operationCount = YamlSpecIndex.ParseOperations(api, content).Count();
        }
        catch (Exception ex)
        {
            throw new ValidationException($"spec '{api}' is not valid OpenAPI YAML: {ex.Message}");
        }

        // Zero operations is only a warning at /api/ai/specs, where someone may be
        // drafting. Here it is fatal: this endpoint exists to give an integration
        // callable operations, and a document yielding none is almost always the wrong
        // file rather than a deliberately empty one. Parsing does NOT throw on
        // arbitrary text — plain prose is valid YAML — so without this the upload
        // reports success and the action list never moves, which is exactly the
        // failure the strict parse above was meant to prevent.
        if (operationCount == 0)
            throw new ValidationException(
                $"spec '{api}' parsed but describes no operations — check it is an OpenAPI "
                + "document with a `paths:` section, and not a README or the wrong file");

        // At most one row per api name, but the unique index is filtered to active
        // rows: a soft-deleted one can still be sitting there, so prefer the live row
        // and fall back to reviving the tombstone.
        var spec = await _db.AiApiSpecs
            .OrderByDescending(s => s.IsActive)
            .FirstOrDefaultAsync(s => s.Api == api, ct);

        if (spec is not null && spec.IsActive && spec.IntegrationId != integrationId)
        {
            // "Owned by another integration" is only true while that integration still
            // exists. A spec whose owner has been deleted is a tombstone holding an api
            // name hostage: nothing can call it (the executor resolves the owner with an
            // `IsActive` filter and falls through to "no base_url configured"), it does
            // not appear under any integration, and the advice below — unlink it there —
            // names a page that cannot be opened. Taking it over is the only move left,
            // and there is no live integration for it to harm.
            var ownerIsLive = spec.IntegrationId is not { } owner
                || await _db.Integrations.AnyAsync(i => i.IntegrationId == owner && i.IsActive, ct);

            // Owned elsewhere, and that owner is still there: never adoptable from here.
            // Moving it would silently remove operations another integration is calling
            // today.
            if (spec.IntegrationId is not null && ownerIsLive)
            {
                var ownerName = await _db.Integrations
                    .Where(i => i.IntegrationId == spec.IntegrationId && i.IsActive)
                    .Select(i => i.Name)
                    .FirstOrDefaultAsync(ct);
                throw new ConflictException(
                    $"the api name '{api}' belongs to the spec of integration "
                    + $"'{ownerName ?? "another integration"}' — rename this one, or unlink that spec first",
                    CatalogConflicts.SpecApiTaken);
            }

            if (spec.IntegrationId is not null)
            {
                _logger.LogInformation(
                    "integration.spec.reclaimed api={Api} from_deleted_integration={Owner} to={Integration}",
                    api, spec.IntegrationId, integrationId);
            }
            else

            // Unowned: adoptable, but only when asked. A global spec carries its own
            // base URL and auth; re-scoping it makes it inherit this integration's,
            // which is a change of where its calls go and who they authenticate as.
            if (!adopt)
                throw new ConflictException(
                    $"a global API spec named '{api}' already exists. Taking it over would replace its "
                    + "document and repoint it at this integration's base URL and credentials. Either "
                    + "rename this one, or attach it from the integration's Skills & specs panel, where "
                    + "that choice is offered explicitly",
                    CatalogConflicts.SpecApiGlobal);
        }

        // Whether this attach CHANGES who owns the spec, decided before the write.
        // Only a change of ownership justifies rewriting the connection settings; a
        // re-upload onto a spec this integration already owns must not.
        var takingOwnership = spec is null || spec.IntegrationId != integrationId;

        var now = DateTime.UtcNow;
        if (spec is null)
        {
            spec = new AiApiSpecEntity
            {
                AiApiSpecId = Guid.NewGuid(),
                Api = api,
                CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
                CreatedAt = now,
            };
            _db.AiApiSpecs.Add(spec);
        }

        spec.Content = content;
        spec.OperationCount = operationCount;
        spec.IntegrationId = integrationId;
        spec.IsActive = true;
        spec.UpdatedAt = now;

        if (takingOwnership)
        {
            // A spec attached here MUST inherit the integration's URL and credentials —
            // that is what the caller was told and what the link is for. Setting
            // IntegrationId alone does not achieve it: RestOperationExecutor prefers
            // the spec's own BaseUrl and honours its AuthType whenever it is not
            // "none", so an adopted self-contained spec would keep calling its old
            // host with its old token while the integration screen showed those
            // operations as its own.
            //
            // Scoped to the ownership change on purpose. A spec-level BaseUrl override
            // on a spec ALREADY linked here is a supported, deliberate setting (see
            // /admin/specs: "blank unless this document must override the integration
            // endpoint"), and clearing it because someone uploaded a corrected YAML
            // would silently repoint every operation with a 200 and a cheerful toast.
            spec.BaseUrl = null;
            spec.AuthType = "none";
            spec.AuthConfig = null;
            spec.VerifySsl = true;
            spec.AllowPrivateNetwork = true;
        }

        return spec;
    }

    private static void RequireDistinct(IEnumerable<string> values, string message)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
            if (!seen.Add(v))
                throw new ValidationException($"{message} '{v}'");
    }

    public async Task<AiPromptSkillResponse> AttachSkillAsync(
        Guid integrationId, AttachSkillRequest dto, CancellationToken ct)
    {
        await RequireIntegrationAsync(integrationId, ct);

        var skill = await UpsertSkillAsync(
            integrationId, dto.Name, dto.Content, dto.Priority, dto.Adopt == true, ct);

        await _db.SaveChangesAsync(ct);
        // Hot-reload, so the next turn sees it without a restart.
        _loader.Invalidate();

        _logger.LogInformation(
            "integration.catalog.skill_attached integration={Integration} name={Name}",
            integrationId, skill.Name);

        return ToSkillResponse(skill);
    }

    // Validates and upserts one skill row. Does NOT save — see UpsertSpecAsync.
    private async Task<SkillEntity> UpsertSkillAsync(
        Guid integrationId, string? rawName, string? content, int? priority, bool adopt,
        CancellationToken ct)
    {
        var name = (rawName ?? string.Empty).Trim();
        if (name.Length == 0) throw new ValidationException("name is required");
        if (string.IsNullOrWhiteSpace(content)) throw new ValidationException($"skill '{name}' has no content");
        if (content.Length > MaxSkillBytes)
            throw new ValidationException($"skill '{name}' exceeds {MaxSkillBytes / 1024} KB");

        var result = _validator.ValidateSkill(name, content);
        await _recorder.RecordAsync(Data.Models.ValidationRecord.KindSkill, name, result, ct);
        if (!result.Ok)
            throw new ValidationException($"skill '{name}' failed validation: " + Errors(result));

        var skill = await _db.AiPromptSkills.FirstOrDefaultAsync(s => s.Name == name, ct);

        // A skill is not silently annexed by whoever uploads next: the name is already
        // merged into every conversation's prompt, and re-scoping it changes what the
        // agent knows in contexts nobody was looking at.
        if (skill is not null && skill.IsActive && skill.IntegrationId != integrationId)
        {
            if (skill.IntegrationId is not null)
                throw new ConflictException(
                    $"a skill named '{name}' is attached to another integration — rename this one, "
                    + "or detach it there first",
                    CatalogConflicts.SkillNameTaken);

            if (!adopt)
                throw new ConflictException(
                    $"a global skill named '{name}' already exists. Taking it over would replace its "
                    + "content and scope it to this integration, so it stops applying to every other "
                    + "conversation. Either rename this one, or attach it from the integration's "
                    + "Skills & specs panel, where that choice is offered explicitly",
                    CatalogConflicts.SkillNameGlobal);
        }

        var now = DateTime.UtcNow;
        if (skill is null)
        {
            skill = new SkillEntity
            {
                AiPromptSkillId = Guid.NewGuid(),
                Name = name,
                CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
                CreatedAt = now,
            };
            _db.AiPromptSkills.Add(skill);
        }

        skill.Content = content;
        // Integration skills sort after the global ones by default: they qualify the
        // base instructions rather than replacing them.
        if (priority is { } p) skill.Priority = p;
        else if (skill.Priority == 0) skill.Priority = 100;
        skill.IntegrationId = integrationId;
        skill.IsActive = true;
        skill.UpdatedAt = now;
        return skill;
    }

    private static string Errors(TemplateValidationResult result) =>
        string.Join("; ", result.Issues
            .Where(i => i.Severity == TemplateSecurityValidator.Error)
            .Select(i => i.Message));

    private async Task RequireIntegrationAsync(Guid id, CancellationToken ct)
    {
        if (!await _db.Integrations.AsNoTracking().AnyAsync(i => i.IntegrationId == id && i.IsActive, ct))
            throw new NotFoundException("integration not found");
    }

    private static AiPromptSkillResponse ToSkillResponse(SkillEntity s) => new()
    {
        AiPromptSkillId = s.AiPromptSkillId,
        Name = s.Name,
        Content = s.Content,
        Priority = s.Priority,
        IntegrationId = s.IntegrationId,
        CreatedBy = s.CreatedBy,
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };

    private static AiApiSpecResponse ToSpecResponse(AiApiSpecEntity s) => new()
    {
        AiApiSpecId = s.AiApiSpecId,
        Api = s.Api,
        OperationCount = s.OperationCount,
        BaseUrl = s.BaseUrl,
        AuthType = s.AuthType,
        VerifySsl = s.VerifySsl,
        AllowPrivateNetwork = s.AllowPrivateNetwork,
        IntegrationId = s.IntegrationId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
