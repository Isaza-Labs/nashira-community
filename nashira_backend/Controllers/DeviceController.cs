using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Device;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using DeviceEntity = nashira_backend.Data.Models.Device;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Device inventory. Reads open to any authenticated user; writes require Operator.
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class DeviceController : ControllerBase
{
    private static readonly JsonElement EmptyProperties = JsonDocument.Parse("{}").RootElement;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeviceController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<DeviceResponse>>> Get(
        string? site = null, string? role = null, string? q = null,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var query = _db.Devices.AsNoTracking().Where(d => d.IsActive);
        if (!string.IsNullOrWhiteSpace(site)) query = query.Where(d => d.Site == site);
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(d => d.Role == role);
        // Same fields the devices view lets people filter by: whatever they remember
        // about the box — its name, address, site or role.
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLowerInvariant();
            query = query.Where(d =>
                d.DeviceName.ToLower().Contains(needle)
                || d.IpAddress.ToLower().Contains(needle)
                || d.Site.ToLower().Contains(needle)
                || d.Role.ToLower().Contains(needle));
        }
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(d => d.DeviceName).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<DeviceResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    // Aggregates for the overview dashboard. Grouped in SQL so the numbers cover the
    // whole inventory, not whatever page a list call happens to return.
    [HttpGet("stats")]
    public async Task<ActionResult<DeviceStatsResponse>> Stats(CancellationToken ct)
    {
        var groups = await _db.Devices.AsNoTracking()
            .Where(d => d.IsActive)
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return new DeviceStatsResponse
        {
            Total = groups.Sum(g => g.Count),
            ByStatus = groups.ToDictionary(g => g.Status, g => g.Count),
        };
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeviceResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<DeviceResponse>> Post([FromBody] CreateDevice dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceName)) throw new ValidationException("device_name is required");
        if (string.IsNullOrWhiteSpace(dto.IpAddress)) throw new ValidationException("ip_address is required");
        await EnsureCredentialAsync(dto.CredentialId, ct);
        await EnsureSourceAsync(dto.SourceId, ct);

        var now = DateTime.UtcNow;
        var row = new DeviceEntity
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = dto.DeviceName.Trim(),
            IpAddress = dto.IpAddress.Trim(),
            Platform = dto.Platform ?? string.Empty,
            Vendor = dto.Vendor ?? string.Empty,
            OsVersion = dto.OsVersion ?? string.Empty,
            Site = dto.Site ?? string.Empty,
            Role = dto.Role ?? string.Empty,
            Status = dto.Status ?? string.Empty,
            CredentialId = dto.CredentialId,
            SourceId = dto.SourceId,
            ExternalId = Trimmed(dto.ExternalId),
            Properties = ReadProperties(dto.Properties),
            AllowDraft = dto.AllowDraft ?? true,
            AllowQa = dto.AllowQa ?? false,
            AllowProduction = dto.AllowProduction ?? true,
            ExpectedSshHostKeyFingerprint = dto.ExpectedSshHostKeyFingerprint,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Devices.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(GetById), "Device", new { id = row.DeviceId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<DeviceResponse>> Update(Guid id, [FromBody] UpdateDevice dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        if (dto.DeviceName is not null) row.DeviceName = dto.DeviceName.Trim();
        if (dto.IpAddress is not null) row.IpAddress = dto.IpAddress.Trim();
        if (dto.Platform is not null) row.Platform = dto.Platform;
        if (dto.Vendor is not null) row.Vendor = dto.Vendor;
        if (dto.OsVersion is not null) row.OsVersion = dto.OsVersion;
        if (dto.Site is not null) row.Site = dto.Site;
        if (dto.Role is not null) row.Role = dto.Role;
        if (dto.Status is not null) row.Status = dto.Status;
        if (dto.CredentialId is not null)
        {
            await EnsureCredentialAsync(dto.CredentialId, ct);
            row.CredentialId = dto.CredentialId;
        }
        if (dto.SourceId is not null)
        {
            await EnsureSourceAsync(dto.SourceId, ct);
            row.SourceId = dto.SourceId;
        }
        if (dto.ExternalId is not null) row.ExternalId = Trimmed(dto.ExternalId);
        if (dto.Properties is not null) row.Properties = ReadProperties(dto.Properties);
        if (dto.AllowDraft is not null) row.AllowDraft = dto.AllowDraft.Value;
        if (dto.AllowQa is not null) row.AllowQa = dto.AllowQa.Value;
        if (dto.AllowProduction is not null) row.AllowProduction = dto.AllowProduction.Value;
        if (dto.ExpectedSshHostKeyFingerprint is not null) row.ExpectedSshHostKeyFingerprint = dto.ExpectedSshHostKeyFingerprint;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<DeviceResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    private async Task EnsureCredentialAsync(Guid? credentialId, CancellationToken ct)
    {
        if (credentialId is not { } cid) return;
        var exists = await _db.Credentials.AnyAsync(c => c.CredentialId == cid && c.IsActive, ct);
        if (!exists) throw new ValidationException("credential_id does not exist");
    }

    private async Task EnsureSourceAsync(Guid? sourceId, CancellationToken ct)
    {
        if (sourceId is not { } sid) return;
        var exists = await _db.InventorySources.AnyAsync(s => s.InventorySourceId == sid && s.IsActive, ct);
        if (!exists) throw new ValidationException("source_id does not exist");
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // `properties` must be a JSON object (or null/absent). Cloned so the row does not
    // hold a reference into the request's pooled JsonDocument buffer.
    private static JsonElement ReadProperties(JsonElement? raw)
    {
        if (raw is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return EmptyProperties;
        if (el.ValueKind != JsonValueKind.Object)
            throw new ValidationException("properties must be a JSON object");
        return el.Clone();
    }

    private async Task<DeviceEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.Devices
            .FirstOrDefaultAsync(d => d.DeviceId == id && d.IsActive, ct);
        if (row is null) throw new NotFoundException("device not found");
        return row;
    }

    private static DeviceResponse ToResponse(DeviceEntity d) => new()
    {
        DeviceId = d.DeviceId,
        DeviceName = d.DeviceName,
        IpAddress = d.IpAddress,
        Platform = d.Platform,
        Vendor = d.Vendor,
        OsVersion = d.OsVersion,
        Site = d.Site,
        Role = d.Role,
        Status = d.Status,
        CredentialId = d.CredentialId,
        SourceId = d.SourceId,
        ExternalId = d.ExternalId,
        LastSyncAt = d.LastSyncAt,
        Properties = d.Properties.ValueKind == JsonValueKind.Undefined ? EmptyProperties : d.Properties,
        AllowDraft = d.AllowDraft,
        AllowQa = d.AllowQa,
        AllowProduction = d.AllowProduction,
        HasHostKeyFingerprint = !string.IsNullOrEmpty(d.ExpectedSshHostKeyFingerprint),
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };
}
