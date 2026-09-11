using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Integration;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using ActionEntity = nashira_backend.Data.Models.IntegrationAction;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The catalog of operations an integration exposes. Rows are normally produced by
// POST /api/integrations/{id}/sync-actions from the linked OpenAPI specs, so there
// is no create endpoint here — an action without a spec behind it would be a
// definition nothing can keep in step. What an admin can do is curate: rename,
// recategorise, correct read_only, and disable.
[ApiController]
[Route("api/integrations/{integrationId:guid}/actions")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class IntegrationActionController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public IntegrationActionController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<IntegrationActionResponse>>> Get(
        Guid integrationId, string? category = null, string? q = null, bool includeRetired = false,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        await EnsureIntegrationAsync(integrationId, ct);

        var query = _db.IntegrationActions.AsNoTracking().Where(a => a.IntegrationId == integrationId);
        // Retired = the spec stopped describing it. Hidden by default so the catalog
        // reflects what can actually be called today.
        if (!includeRetired) query = query.Where(a => a.IsActive);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(a => a.Category == category);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q}%";
            query = query.Where(a =>
                EF.Functions.ILike(a.Name, pattern)
                || EF.Functions.ILike(a.Path, pattern)
                || (a.OperationId != null && EF.Functions.ILike(a.OperationId, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(a => a.Category).ThenBy(a => a.Path).ThenBy(a => a.Method)
            .Skip(offset).Take(limit).ToListAsync(ct);

        return new OkObjectResult(new ListResponse<IntegrationActionResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<IntegrationActionDetailResponse>> GetById(
        Guid integrationId, Guid id, CancellationToken ct)
    {
        var row = await Find(integrationId, id, ct);
        return new IntegrationActionDetailResponse
        {
            IntegrationActionId = row.IntegrationActionId,
            IntegrationId = row.IntegrationId,
            OperationId = row.OperationId,
            Name = row.Name,
            Description = row.Description,
            Method = row.Method,
            Path = row.Path,
            Category = row.Category,
            ReadOnly = row.ReadOnly,
            Enabled = row.Enabled,
            IsActive = row.IsActive,
            UpdatedAt = row.UpdatedAt,
            PathParams = row.PathParamsJson,
            QueryParams = row.QueryParamsJson,
            RequestBody = row.RequestBodyJson,
        };
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IntegrationActionResponse>> Update(
        Guid integrationId, Guid id, [FromBody] UpdateIntegrationAction dto, CancellationToken ct)
    {
        var row = await Find(integrationId, id, ct);

        // Method and path are the spec's to own — a re-sync would overwrite an edit
        // here, so they are not editable rather than silently reverted later.
        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Category is not null) row.Category = dto.Category.Trim();
        if (dto.ReadOnly is not null) row.ReadOnly = dto.ReadOnly.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    private async Task EnsureIntegrationAsync(Guid integrationId, CancellationToken ct)
    {
        if (!await _db.Integrations.AnyAsync(i => i.IntegrationId == integrationId && i.IsActive, ct))
            throw new NotFoundException("integration not found");
    }

    private async Task<ActionEntity> Find(Guid integrationId, Guid id, CancellationToken ct)
    {
        var row = await _db.IntegrationActions.FirstOrDefaultAsync(
            a => a.IntegrationActionId == id && a.IntegrationId == integrationId, ct);
        if (row is null) throw new NotFoundException("integration action not found");
        return row;
    }

    private static IntegrationActionResponse ToResponse(ActionEntity a) => new()
    {
        IntegrationActionId = a.IntegrationActionId,
        IntegrationId = a.IntegrationId,
        OperationId = a.OperationId,
        Name = a.Name,
        Description = a.Description,
        Method = a.Method,
        Path = a.Path,
        Category = a.Category,
        ReadOnly = a.ReadOnly,
        Enabled = a.Enabled,
        IsActive = a.IsActive,
        UpdatedAt = a.UpdatedAt,
    };
}
