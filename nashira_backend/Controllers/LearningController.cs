using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Learning;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using LearningEntity = nashira_backend.Data.Models.AgentLearning;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Curate + inspect agent learnings. Admin-only. Reads include the read-only
// seeded system knowledge (IsSystem); those rows are read-only, writes only affect
// own learnings.
[ApiController]
[Route("api/learnings")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class LearningController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public LearningController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AgentLearningResponse>>> Get(
        string? category = null, string? tool = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.AgentLearnings.AsNoTracking()
            .Where(l => l.IsActive);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(l => l.Category == category);
        if (!string.IsNullOrWhiteSpace(tool)) q = q.Where(l => l.ToolName == tool);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(l => l.Confidence).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<AgentLearningResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentLearningResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AgentLearningResponse>> Post([FromBody] CreateAgentLearning dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.ErrorPattern)) throw new ValidationException("error_pattern is required");
        var strategy = NormalizeStrategy(dto.FixStrategy);

        var now = DateTime.UtcNow;
        var row = new LearningEntity
        {
            AgentLearningId = Guid.NewGuid(),
            ErrorPattern = dto.ErrorPattern.Trim(),
            ErrorCategory = dto.ErrorCategory ?? "unknown",
            ServiceType = dto.ServiceType ?? string.Empty,
            ToolName = dto.ToolName ?? string.Empty,
            FixStrategy = strategy,
            FixParamsJson = dto.FixParams?.GetRawText(),
            Category = LearningEntity.CategoryKnowledge,
            Confidence = 0.8,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AgentLearnings.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(GetById), "Learning", new { id = row.AgentLearningId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AgentLearningResponse>> Update(Guid id, [FromBody] UpdateAgentLearning dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row.IsSystem) throw new ForbiddenException("system knowledge is read-only");

        if (dto.ErrorPattern is not null && dto.ErrorPattern.Trim().Length > 0) row.ErrorPattern = dto.ErrorPattern.Trim();
        if (dto.ErrorCategory is not null) row.ErrorCategory = dto.ErrorCategory;
        if (dto.ServiceType is not null) row.ServiceType = dto.ServiceType;
        if (dto.ToolName is not null) row.ToolName = dto.ToolName;
        if (dto.FixStrategy is not null) row.FixStrategy = NormalizeStrategy(dto.FixStrategy);
        if (dto.FixParams is not null) row.FixParamsJson = dto.FixParams.Value.GetRawText();
        if (dto.IsActive is not null) row.IsActive = dto.IsActive.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AgentLearningResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (row.IsSystem) throw new ForbiddenException("system knowledge is read-only");
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    private static string NormalizeStrategy(string? raw)
    {
        var v = (raw ?? LearningEntity.StrategyParameterAdjust).Trim().ToLowerInvariant();
        return v == LearningEntity.StrategyEscalate ? LearningEntity.StrategyEscalate : LearningEntity.StrategyParameterAdjust;
    }

    private async Task<LearningEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.AgentLearnings.FirstOrDefaultAsync(
            l => l.AgentLearningId == id && l.IsActive, ct);
        if (row is null) throw new NotFoundException("learning not found");
        return row;
    }

    private AgentLearningResponse ToResponse(LearningEntity l) => new()
    {
        AgentLearningId = l.AgentLearningId,
        ErrorPattern = l.ErrorPattern,
        ErrorCategory = l.ErrorCategory,
        ServiceType = l.ServiceType,
        ToolName = l.ToolName,
        FixStrategy = l.FixStrategy,
        FixParams = ParseJson(l.FixParamsJson),
        Category = l.Category,
        Confidence = l.Confidence,
        SuccessCount = l.SuccessCount,
        FailureCount = l.FailureCount,
        IsSystem = l.IsSystem,
        IsActive = l.IsActive,
        CreatedAt = l.CreatedAt,
        UpdatedAt = l.UpdatedAt,
    };

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
