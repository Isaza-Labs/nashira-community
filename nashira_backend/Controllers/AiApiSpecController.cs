using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.AiApiSpec;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using AiApiSpecEntity = nashira_backend.Data.Models.AiApiSpec;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Admin-only CRUD for the tenant's OpenAPI specs (the agent's discover/detail/
// execute catalog). Every mutation reparses the YAML (to refresh the operation
// count) and reloads the in-memory index.
[ApiController]
[Route("api/ai/specs")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AiApiSpecController : ControllerBase
{
    private static readonly string[] AllowedAuthTypes = ["none", "token", "bearer", "basic", "header"];

    private readonly AppDbContext _db;
    private readonly IApiSpecIndex _index;
    private readonly ICurrentUser _user;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;
    private readonly IAuditLogger _audit;
    private readonly ModuleSelection _modules;

    public AiApiSpecController(
        AppDbContext db, IApiSpecIndex index, ICurrentUser user,
        ITemplateSecurityValidator validator, IValidationRecorder recorder, IAuditLogger audit,
        ModuleSelection modules)
    {
        _db = db;
        _index = index;
        _user = user;
        _validator = validator;
        _recorder = recorder;
        _audit = audit;
        _modules = modules;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AiApiSpecResponse>>> Get(
        Guid? integrationId = null, bool globalOnly = false,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var hiddenBuiltinApis = ModuleContentCatalog.AllSpecs
            .Where(api => !ModuleContentCatalog.IsSpecAvailable(api, _modules))
            .ToArray();
        var q = _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IsActive && !hiddenBuiltinApis.Contains(s.Api));
        // "Global" means "not owned by an integration anyone can open", not merely
        // "IntegrationId is null". A spec whose owner was deleted satisfies neither the
        // old filter nor any per-integration view, so it vanished from the UI entirely
        // while still holding its api name against the unique index — the name could
        // not be reused and the spec could not be found to release it. An owner that no
        // longer exists is not an owner.
        if (globalOnly)
            q = q.Where(s => s.IntegrationId == null
                || !_db.Integrations.Any(i => i.IntegrationId == s.IntegrationId && i.IsActive));
        else if (integrationId is { } iid) q = q.Where(s => s.IntegrationId == iid);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(s => s.Api).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<AiApiSpecResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AiApiSpecDetailResponse>> GetById(Guid id, CancellationToken ct)
    {
        var s = await Find(id, ct);
        var content = YamlSpecIndex.FilterContent(s.Api, s.Content, _modules);
        return new AiApiSpecDetailResponse
        {
            AiApiSpecId = s.AiApiSpecId, Api = s.Api,
            OperationCount = YamlSpecIndex.ParseOperations(s.Api, content).Count(),
            BaseUrl = s.BaseUrl, AuthType = s.AuthType, VerifySsl = s.VerifySsl,
            AllowPrivateNetwork = s.AllowPrivateNetwork,
            IntegrationId = s.IntegrationId,
            CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt, Content = content,
        };
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AiApiSpecResponse>> Post([FromBody] CreateAiApiSpec dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Api)) throw new ValidationException("api is required");
        if (string.IsNullOrWhiteSpace(dto.Content)) throw new ValidationException("content is required");
        var api = dto.Api.Trim().ToLowerInvariant();
        if (ModuleContentCatalog.IsBuiltinSpec(api))
            throw new ConflictException(
                $"api name '{api}' is reserved for a built-in spec",
                "spec_api_reserved");
        var authType = NormalizeAuthType(dto.AuthType);
        await RequireIntegrationAsync(dto.IntegrationId, ct);
        await ValidateAndRecordAsync(api, dto.Content, ct);

        if (await _db.AiApiSpecs.AnyAsync(s => s.Api == api && s.IsActive, ct))
            throw new ConflictException("a spec with this api name already exists", "spec_api_taken");

        var count = CountOperations(api, dto.Content);
        var now = DateTime.UtcNow;
        var row = new AiApiSpecEntity
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = api,
            Content = dto.Content,
            OperationCount = count,
            BaseUrl = dto.BaseUrl,
            AuthType = authType,
            AuthConfig = dto.AuthConfig,
            VerifySsl = dto.VerifySsl ?? true,
            AllowPrivateNetwork = dto.AllowPrivateNetwork ?? true,
            IntegrationId = dto.IntegrationId,
            CreatedBy = _user.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AiApiSpecs.Add(row);
        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);

        // A spec is what the agent is allowed to call. The document is far too big for
        // the trail, so what is recorded is its identity, where it points and how many
        // operations it opened up.
        await _audit.LogAsync("ai_api_spec", row.AiApiSpecId, "create", after: Snapshot(row), ct: ct);

        return new CreatedAtActionResult(nameof(GetById), "AiApiSpec", new { id = row.AiApiSpecId }, ToResponse(row));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AiApiSpecResponse>> Update(Guid id, [FromBody] UpdateAiApiSpec dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        if (dto.Content is not null)
        {
            await ValidateAndRecordAsync(row.Api, dto.Content, ct);
            row.Content = dto.Content;
            row.OperationCount = CountOperations(row.Api, dto.Content);
        }
        if (dto.BaseUrl is not null) row.BaseUrl = dto.BaseUrl;
        if (dto.AuthType is not null) row.AuthType = NormalizeAuthType(dto.AuthType);
        if (dto.AuthConfig is not null) row.AuthConfig = dto.AuthConfig;
        if (dto.VerifySsl is not null) row.VerifySsl = dto.VerifySsl.Value;
        if (dto.AllowPrivateNetwork is not null) row.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.ClearIntegration == true) row.IntegrationId = null;
        else if (dto.IntegrationId is not null)
        {
            await RequireIntegrationAsync(dto.IntegrationId, ct);
            row.IntegrationId = dto.IntegrationId;
        }
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);

        await _audit.LogAsync("ai_api_spec", row.AiApiSpecId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AiApiSpecResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _index.ReloadAsync(ct);

        await _audit.LogAsync("ai_api_spec", row.AiApiSpecId, "delete", before: before, ct: ct);

        return ToResponse(row);
    }

    // Identity and wiring, not the document. auth_config can carry a ${secret:…}
    // reference, which describes the secret store's layout — a flag is enough.
    private static object Snapshot(AiApiSpecEntity s) => new
    {
        api = s.Api,
        operation_count = s.OperationCount,
        base_url = s.BaseUrl,
        auth_type = s.AuthType,
        has_auth_config = !string.IsNullOrWhiteSpace(s.AuthConfig),
        verify_ssl = s.VerifySsl,
        allow_private_network = s.AllowPrivateNetwork,
        integration_id = s.IntegrationId,
    };

    // Security-validate the spec content and record the outcome; reject on errors.
    private async Task ValidateAndRecordAsync(string api, string content, CancellationToken ct)
    {
        var result = _validator.ValidateSpec(api, content);
        await _recorder.RecordAsync(nashira_backend.Data.Models.ValidationRecord.KindSpec, api, result, ct);
        if (!result.Ok)
        {
            var errors = string.Join("; ", result.Issues
                .Where(i => i.Severity == TemplateSecurityValidator.Error).Select(i => i.Message));
            throw new ValidationException($"spec failed validation: {errors}");
        }
    }

    private static int CountOperations(string api, string content)
    {
        try { return YamlSpecIndex.ParseOperations(api, content).Count(); }
        catch (Exception ex) { throw new ValidationException($"content is not valid OpenAPI YAML: {ex.Message}"); }
    }

    // A spec's integration link is what supplies its base URL and credentials at call
    // time, so a link pointing at nothing produces anonymous requests against a URL the
    // spec had better carry itself — with no error until the upstream refuses.
    private async Task RequireIntegrationAsync(Guid? integrationId, CancellationToken ct)
    {
        if (integrationId is not { } id) return;
        if (!await _db.Integrations.AnyAsync(i => i.IntegrationId == id && i.IsActive, ct))
            throw new ValidationException("integration_id not found");
    }

    private static string NormalizeAuthType(string? raw)
    {
        var v = (raw ?? "none").Trim().ToLowerInvariant();
        if (!AllowedAuthTypes.Contains(v))
            throw new ValidationException($"auth_type must be one of: {string.Join(", ", AllowedAuthTypes)}");
        return v;
    }

    private async Task<AiApiSpecEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.AiApiSpecs
            .FirstOrDefaultAsync(s => s.AiApiSpecId == id && s.IsActive, ct);
        if (row is null || !ModuleContentCatalog.IsSpecAvailable(row.Api, _modules))
            throw new NotFoundException("api spec not found");
        return row;
    }

    private AiApiSpecResponse ToResponse(AiApiSpecEntity s) => new()
    {
        AiApiSpecId = s.AiApiSpecId,
        Api = s.Api,
        OperationCount = YamlSpecIndex.ParseOperations(
            s.Api, YamlSpecIndex.FilterContent(s.Api, s.Content, _modules)).Count(),
        BaseUrl = s.BaseUrl,
        AuthType = s.AuthType,
        VerifySsl = s.VerifySsl,
        AllowPrivateNetwork = s.AllowPrivateNetwork,
        IntegrationId = s.IntegrationId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
