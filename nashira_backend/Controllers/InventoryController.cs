using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Inventory;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Identity;
using SourceEntity = nashira_backend.Data.Models.InventorySource;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Inventory sources (NetBox) + sync trigger. Read = viewer; source CRUD = admin;
// running a sync = operator+.
[ApiController]
[Route("api/inventory")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class InventoryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly INetBoxSyncService _sync;

    public InventoryController(AppDbContext db, ICurrentUser user, INetBoxSyncService sync)
    {
        _db = db;
        _user = user;
        _sync = sync;
    }

    [HttpGet("sources")]
    public async Task<ActionResult<ListResponse<InventorySourceResponse>>> List(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.InventorySources.AsNoTracking().Where(s => s.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(s => s.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<InventorySourceResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("sources/{id:guid}")]
    public async Task<ActionResult<InventorySourceResponse>> Get(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost("sources")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<InventorySourceResponse>> Create([FromBody] CreateInventorySource dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        if (string.IsNullOrWhiteSpace(dto.BaseUrl)) throw new ValidationException("base_url is required");

        var now = DateTime.UtcNow;
        var row = new SourceEntity
        {
            InventorySourceId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Kind = string.IsNullOrWhiteSpace(dto.Kind) ? SourceEntity.KindNetBox : dto.Kind.Trim().ToLowerInvariant(),
            BaseUrl = ValidateBaseUrl(dto.BaseUrl),
            TokenSecretRef = dto.TokenSecretRef is null
                ? null
                : await InventoryTokenRef.NormalizeAsync(_db, dto.TokenSecretRef, ct),
            SiteFilter = dto.SiteFilter,
            AllowPrivateNetwork = dto.AllowPrivateNetwork,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.InventorySources.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(Get), "Inventory", new { id = row.InventorySourceId }, ToResponse(row));
    }

    [HttpPut("sources/{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<InventorySourceResponse>> Update(Guid id, [FromBody] UpdateInventorySource dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.BaseUrl is not null && dto.BaseUrl.Trim().Length > 0) row.BaseUrl = ValidateBaseUrl(dto.BaseUrl);
        // The masked placeholder is what list/get return for a legacy literal token —
        // a form echoing it back means "keep what is stored", not "set it to ***".
        if (dto.TokenSecretRef is not null && dto.TokenSecretRef.Trim() != InventoryTokenRef.Masked)
            row.TokenSecretRef = await InventoryTokenRef.NormalizeAsync(_db, dto.TokenSecretRef, ct);
        if (dto.SiteFilter is not null) row.SiteFilter = dto.SiteFilter;
        if (dto.AllowPrivateNetwork.HasValue) row.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("sources/{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<InventorySourceResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpPost("sources/{id:guid}/sync")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<NetBoxSyncResult>> Sync(Guid id, [FromQuery] bool dryRun = false, CancellationToken ct = default)
        => await _sync.SyncAsync(await Find(id, ct), dryRun, ct);

    private async Task<SourceEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.InventorySources.FirstOrDefaultAsync(
            s => s.InventorySourceId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("inventory source not found");
        return row;
    }

    // A NetBox base_url must be an absolute http(s) URL; reject typos like a bare
    // host (e.g. "www.google.com") at save time. The sync re-checks — defence in depth.
    private static string ValidateBaseUrl(string raw)
    {
        var url = raw.Trim();
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ValidationException(
                "base_url must be an absolute http(s) URL, e.g. https://netbox.example.com");
        return url;
    }

    private static InventorySourceResponse ToResponse(SourceEntity s) => new()
    {
        InventorySourceId = s.InventorySourceId,
        Name = s.Name,
        Kind = s.Kind,
        BaseUrl = s.BaseUrl,
        // Never ship a stored literal token to the client; references are inert.
        TokenSecretRef = InventoryTokenRef.Mask(s.TokenSecretRef),
        SiteFilter = s.SiteFilter,
        AllowPrivateNetwork = s.AllowPrivateNetwork,
        LastSyncedAt = s.LastSyncedAt,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
