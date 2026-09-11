using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Skill;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Ai.Skills;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using SkillEntity = nashira_backend.Data.Models.AiPromptSkill;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Admin CRUD for tenant-authored prompt skills. Every upload is security-validated
// and recorded; a successful write hot-reloads the agent prompt for the tenant.
[ApiController]
[Route("api/skills")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AiPromptSkillController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ITemplateSecurityValidator _validator;
    private readonly IValidationRecorder _recorder;
    private readonly ISkillPromptLoader _loader;
    private readonly IAuditLogger _audit;

    public AiPromptSkillController(
        AppDbContext db, ICurrentUser user, ITemplateSecurityValidator validator,
        IValidationRecorder recorder, ISkillPromptLoader loader, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _validator = validator;
        _recorder = recorder;
        _loader = loader;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AiPromptSkillResponse>>> Get(
        Guid? integrationId = null, bool globalOnly = false,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.AiPromptSkills.AsNoTracking().Where(s => s.IsActive);
        // Catalog pages split global (unscoped) skills from the ones bound to an
        // integration; both filters ride the IntegrationId index.
        if (globalOnly) q = q.Where(s => s.IntegrationId == null);
        else if (integrationId is { } iid) q = q.Where(s => s.IntegrationId == iid);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(s => s.Priority).ThenBy(s => s.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<AiPromptSkillResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    // Built-in file skills (Skills/*.md) that prefix every tenant prompt, in prompt
    // order. Read-only: they ship with the backend image and are not editable here.
    [HttpGet("builtin")]
    public async Task<ActionResult<IEnumerable<BuiltinSkillResponse>>> Builtin(CancellationToken ct)
    {
        var builtins = await _loader.ListBuiltinsAsync(ct);
        return new OkObjectResult(builtins
            .Select(b => new BuiltinSkillResponse { Name = b.Name, Content = b.Content }).ToList());
    }

    // Edits a built-in file skill in place. Same security validation as uploaded
    // skills. NOTE: the write lands on the container filesystem — a redeploy
    // restores the shipped content unless the Skills dir is volume-mounted.
    [HttpPut("builtin/{**name}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<BuiltinSkillResponse>> UpdateBuiltin(
        string name, [FromBody] UpdateBuiltinSkill dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Content)) throw new ValidationException("content is required");
        await ValidateAndRecordAsync(name, dto.Content, ct);

        if (!await _loader.SaveBuiltinAsync(name, dto.Content, ct))
            throw new NotFoundException($"built-in skill '{name}' not found");

        return new OkObjectResult(new BuiltinSkillResponse { Name = name, Content = dto.Content });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AiPromptSkillResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AiPromptSkillResponse>> Post([FromBody] CreateAiPromptSkill dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        var name = dto.Name.Trim();
        await ValidateAndRecordAsync(name, dto.Content, ct);

        if (await _db.AiPromptSkills.AnyAsync(s => s.Name == name && s.IsActive, ct))
            throw new ConflictException("a skill with this name already exists", "skill_name_taken");

        var now = DateTime.UtcNow;
        var row = new SkillEntity
        {
            AiPromptSkillId = Guid.NewGuid(),
            Name = name,
            Content = dto.Content,
            Priority = dto.Priority ?? 100,
            IntegrationId = dto.IntegrationId,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AiPromptSkills.Add(row);
        await _db.SaveChangesAsync(ct);
        _loader.Invalidate();

        // A skill is text that goes straight into the agent's system prompt, so this is
        // the record of what the agent was told to be. The content travels: it is small,
        // and a diff of it is the entire point.
        await _audit.LogAsync("ai_prompt_skill", row.AiPromptSkillId, "create",
            after: Snapshot(row), ct: ct);
        return new CreatedAtActionResult(nameof(GetById), "AiPromptSkill", new { id = row.AiPromptSkillId }, ToResponse(row));
    }

    // Bulk import: one call, many .md files, one result per file.
    //
    // Skills arrive as a directory of markdown — that is how they are written and how
    // they are shared between installations — and importing them one POST at a time
    // means the first security rejection strands the rest half-loaded. Here a file that
    // fails validation fails alone and says so, and the caller gets a per-file verdict
    // rather than an aggregate number that hides which one broke.
    [HttpPost("import")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ImportSkillsResponse>> Import(
        [FromBody] ImportSkillsRequest dto, CancellationToken ct)
    {
        if (dto.Files.Count == 0) throw new ValidationException("files is required");
        if (dto.Files.Count > MaxImportFiles)
            throw new ValidationException($"at most {MaxImportFiles} files per import");

        var overwrite = dto.Overwrite ?? false;
        var response = new ImportSkillsResponse();
        var now = DateTime.UtcNow;

        foreach (var file in dto.Files)
        {
            var name = SkillNameFrom(file.Name);
            var result = new ImportSkillResult { Name = name };

            try
            {
                if (name.Length == 0) throw new ValidationException("a file needs a name");
                if (string.IsNullOrWhiteSpace(file.Content)) throw new ValidationException("the file is empty");

                // Same security validation as a single upload: a bulk path that skipped
                // it would be the way around it.
                await ValidateAndRecordAsync(name, file.Content, ct);

                var existing = await _db.AiPromptSkills
                    .FirstOrDefaultAsync(s => s.Name == name && s.IsActive, ct);

                if (existing is not null && !overwrite)
                {
                    result.Status = "skipped";
                    result.Error = "a skill with this name already exists; re-run with overwrite to replace it";
                    result.AiPromptSkillId = existing.AiPromptSkillId;
                    response.Skipped++;
                }
                else if (existing is not null)
                {
                    existing.Content = file.Content;
                    // An import without an explicit priority must not reset one that was
                    // tuned by hand.
                    if ((file.Priority ?? dto.Priority) is { } priority) existing.Priority = priority;
                    if (dto.IntegrationId is not null) existing.IntegrationId = dto.IntegrationId;
                    if (_user.IsAuthenticated) existing.CreatedBy = _user.UserId;
                    existing.UpdatedAt = now;

                    result.Status = "updated";
                    result.AiPromptSkillId = existing.AiPromptSkillId;
                    response.Updated++;
                }
                else
                {
                    var row = new SkillEntity
                    {
                        AiPromptSkillId = Guid.NewGuid(),
                        Name = name,
                        Content = file.Content,
                        Priority = file.Priority ?? dto.Priority ?? 100,
                        IntegrationId = dto.IntegrationId,
                        CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    _db.AiPromptSkills.Add(row);

                    result.Status = "created";
                    result.AiPromptSkillId = row.AiPromptSkillId;
                    response.Created++;
                }

                // Saved per file so one rejection cannot roll back the files that were
                // already fine — a half-imported directory the caller can finish is
                // better than an all-or-nothing failure on file 40 of 41.
                await _db.SaveChangesAsync(ct);
            }
            catch (DomainException ex)
            {
                result.Status = "failed";
                result.Error = ex.Message;
                response.Failed++;
            }

            response.Results.Add(result);
        }

        if (response.Created > 0 || response.Updated > 0) _loader.Invalidate();
        return response;
    }

    private const int MaxImportFiles = 200;

    // `netbox-troubleshooting.md`, `Netbox Troubleshooting.MD` and a path from a
    // directory upload all name the same skill.
    private static string SkillNameFrom(string? filename)
    {
        var raw = (filename ?? string.Empty).Trim().Replace('\\', '/');
        var last = raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..] : raw;
        if (last.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) last = last[..^3];
        else if (last.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)) last = last[..^9];
        return last.Trim();
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AiPromptSkillResponse>> Update(Guid id, [FromBody] UpdateAiPromptSkill dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.Content is not null)
        {
            await ValidateAndRecordAsync(row.Name, dto.Content, ct);
            row.Content = dto.Content;
        }
        if (dto.Priority is not null) row.Priority = dto.Priority.Value;
        if (dto.ClearIntegration == true) row.IntegrationId = null;
        else if (dto.IntegrationId is not null) row.IntegrationId = dto.IntegrationId;
        if (dto.IsActive is not null) row.IsActive = dto.IsActive.Value;
        // Last editor, not original author — this is the audit trail for "who changed
        // the agent's prompt", which is the question an admin actually asks.
        if (_user.IsAuthenticated) row.CreatedBy = _user.UserId;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _loader.Invalidate();

        await _audit.LogAsync("ai_prompt_skill", row.AiPromptSkillId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AiPromptSkillResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _loader.Invalidate();

        await _audit.LogAsync("ai_prompt_skill", row.AiPromptSkillId, "delete", before: before, ct: ct);

        return ToResponse(row);
    }

    private static object Snapshot(SkillEntity s) => new
    {
        name = s.Name,
        priority = s.Priority,
        integration_id = s.IntegrationId,
        content = s.Content,
    };

    // Validate the content, persist the validation record, and reject on error-severity issues.
    private async Task ValidateAndRecordAsync(string name, string content, CancellationToken ct)
    {
        var result = _validator.ValidateSkill(name, content);
        await _recorder.RecordAsync(ValidationRecordEntity.KindSkill, name, result, ct);
        if (!result.Ok)
        {
            var errors = string.Join("; ", result.Issues.Where(i => i.Severity == TemplateSecurityValidator.Error).Select(i => i.Message));
            throw new ValidationException($"skill failed security validation: {errors}");
        }
    }

    private async Task<SkillEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.AiPromptSkills.FirstOrDefaultAsync(
            s => s.AiPromptSkillId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("skill not found");
        return row;
    }

    private static AiPromptSkillResponse ToResponse(SkillEntity s) => new()
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
}
