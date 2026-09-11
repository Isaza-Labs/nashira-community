using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Loader;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Loader;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using ValidationRecordEntity = nashira_backend.Data.Models.ValidationRecord;

using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Loader surface: dry-run template security validation and the validation history.
// Skill/spec uploads themselves go through /api/skills and /api/ai/specs.
[ApiController]
[Route("api/loader")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class LoaderController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ITemplateSecurityValidator _validator;

    public LoaderController(AppDbContext db, ICurrentUser user, ITemplateSecurityValidator validator)
    {
        _db = db;
        _user = user;
        _validator = validator;
    }

    [HttpPost("validate")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit] // dry-run, no mutation
    public ActionResult<TemplateValidationResponse> Validate([FromBody] ValidateTemplateRequest req)
    {
        var kind = (req.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var result = kind switch
        {
            ValidationRecordEntity.KindSkill => _validator.ValidateSkill(req.Name ?? string.Empty, req.Content ?? string.Empty),
            ValidationRecordEntity.KindSpec => _validator.ValidateSpec(req.Name ?? string.Empty, req.Content ?? string.Empty),
            _ => throw new ValidationException("kind must be 'skill' or 'spec'"),
        };
        return new TemplateValidationResponse
        {
            Ok = result.Ok,
            Issues = result.Issues.Select(i => new TemplateIssueDto { Severity = i.Severity, Message = i.Message }).ToList(),
        };
    }

    [HttpGet("validations")]
    public async Task<ActionResult<ListResponse<ValidationRecordResponse>>> Validations(
        string? kind = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.ValidationRecords.AsNoTracking().Where(v => v.IsActive);
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(v => v.Kind == kind);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(v => v.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<ValidationRecordResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    private static ValidationRecordResponse ToResponse(ValidationRecordEntity v) => new()
    {
        ValidationRecordId = v.ValidationRecordId,
        Kind = v.Kind,
        TargetName = v.TargetName,
        Ok = v.Ok,
        Issues = ParseIssues(v.IssuesJson),
        UserId = v.UserId,
        At = v.CreatedAt,
    };

    private static readonly JsonSerializerOptions IssuesOpts = new() { PropertyNameCaseInsensitive = true };

    private static List<TemplateIssueDto> ParseIssues(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<TemplateIssueDto>>(json, IssuesOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
