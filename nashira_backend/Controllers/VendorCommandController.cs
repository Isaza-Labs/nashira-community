using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using CommandEntity = nashira_backend.Data.Models.VendorCommand;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class WriteVendorCommand
{
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("read_only")] public bool? ReadOnly { get; set; }
    [JsonPropertyName("parser_template")] public string? ParserTemplate { get; set; }
}

// One platform the deployment knows about, with what makes it known.
public class VendorPlatformResponse
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = string.Empty;
    [JsonPropertyName("command_count")] public int CommandCount { get; set; }
    [JsonPropertyName("device_count")] public int DeviceCount { get; set; }
}

public class VendorCommandResponse
{
    [JsonPropertyName("vendor_command_id")] public Guid Id { get; set; }
    [JsonPropertyName("intent")] public string Intent { get; set; } = string.Empty;
    [JsonPropertyName("platform")] public string Platform { get; set; } = string.Empty;
    [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }
    [JsonPropertyName("parser_template")] public string? ParserTemplate { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// The intent → platform → command catalog that makes one workflow multi-vendor.
[ApiController]
[Route("api/vendor-commands")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class VendorCommandController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public VendorCommandController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<VendorCommandResponse>>> Get(
        string? intent = null, string? platform = null, int limit = 100, int offset = 0,
        CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.VendorCommands.AsNoTracking().Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(intent)) q = q.Where(c => c.Intent == intent.Trim().ToLower());
        if (!string.IsNullOrWhiteSpace(platform)) q = q.Where(c => c.Platform == platform.Trim().ToLower());

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Intent).ThenBy(c => c.Platform)
            .Skip(offset).Take(limit).ToListAsync(ct);

        return new OkObjectResult(new ListResponse<VendorCommandResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total, Limit = limit, Offset = offset,
        });
    }

    // Every platform this deployment knows, so a picker does not have to be a
    // free-text box that silently mints `cisco-ios` next to `cisco_ios` — the
    // resolve lookup is an exact match on that string, so a typo is a 404 at run
    // time and nothing before it.
    //
    // Derived rather than declared: a hardcoded list in the frontend goes stale
    // the moment a vendor YAML is added under Skills/vendors, and one stored in
    // the browser is invisible to everyone else. The union of what the catalogue
    // covers and what the inventory actually runs is the honest answer, and the
    // two counts are what tell an admin which platforms have devices but no
    // commands — the gap that makes a workflow fail on exactly one vendor.
    [HttpGet("platforms")]
    public async Task<ActionResult<ListResponse<VendorPlatformResponse>>> Platforms(
        CancellationToken ct = default)
    {
        var catalogued = await _db.VendorCommands.AsNoTracking()
            .Where(c => c.IsActive)
            .GroupBy(c => c.Platform)
            .Select(g => new { Platform = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Device platforms are free text on the inventory form, so they are
        // normalised the same way a write to the catalogue normalises them, and
        // grouped in SQL rather than here — an inventory is thousands of rows and
        // this needs a dozen counts. Blank ones are dropped: a device with no
        // platform set is a gap in the inventory, not a platform called "".
        var deployed = await _db.Devices.AsNoTracking()
            .Where(d => d.IsActive && d.Platform != "")
            .GroupBy(d => d.Platform.ToLower())
            .Select(g => new { Platform = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var rows = new Dictionary<string, VendorPlatformResponse>(StringComparer.Ordinal);

        foreach (var c in catalogued)
        {
            var key = c.Platform.Trim().ToLowerInvariant();
            if (key.Length == 0) continue;
            Row(key).CommandCount += c.Count;
        }

        foreach (var d in deployed)
        {
            var key = d.Platform.Trim();
            if (key.Length == 0) continue;
            Row(key).DeviceCount += d.Count;
        }

        return new OkObjectResult(new ListResponse<VendorPlatformResponse>
        {
            Items = rows.Values.OrderBy(r => r.Platform, StringComparer.Ordinal).ToList(),
            Total = rows.Count,
            Limit = rows.Count,
            Offset = 0,
        });

        VendorPlatformResponse Row(string key)
        {
            if (!rows.TryGetValue(key, out var row))
                rows[key] = row = new VendorPlatformResponse { Platform = key };
            return row;
        }
    }

    // The lookup a workflow actually performs: give me the command for this intent
    // on this device's platform.
    [HttpGet("resolve")]
    public async Task<ActionResult<object>> Resolve(
        string intent, string? platform = null, Guid? deviceId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intent)) throw new ValidationException("intent is required");

        var target = platform?.Trim().ToLowerInvariant();
        if (target is null && deviceId is { } id)
        {
            var device = await _db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DeviceId == id && d.IsActive, ct);
            if (device is null) throw new NotFoundException("device not found");
            target = device.Platform.ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(target))
            throw new ValidationException("pass either a platform or a device_id");

        var row = await _db.VendorCommands.AsNoTracking().FirstOrDefaultAsync(
            c => c.IsActive && c.Intent == intent.Trim().ToLower() && c.Platform == target, ct);

        // A miss is reported, not guessed at. Falling back to another platform's
        // syntax would send a Juniper command to a Cisco box.
        if (row is null)
            throw new NotFoundException($"no command for intent '{intent}' on platform '{target}'");

        return new OkObjectResult(ToResponse(row));
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<VendorCommandResponse>> Post(
        [FromBody] WriteVendorCommand dto, CancellationToken ct)
    {
        var (intent, platform) = RequireKey(dto.Intent, dto.Platform);
        if (string.IsNullOrWhiteSpace(dto.Command)) throw new ValidationException("command is required");

        if (await _db.VendorCommands.AnyAsync(
                c => c.Intent == intent && c.Platform == platform && c.IsActive, ct))
            throw new ConflictException(
                $"'{intent}' is already defined for '{platform}'", "vendor_command_taken");

        var now = DateTime.UtcNow;
        var row = new CommandEntity
        {
            VendorCommandId = Guid.NewGuid(),
            Intent = intent,
            Platform = platform,
            Command = dto.Command.Trim(),
            Description = dto.Description,
            ReadOnly = dto.ReadOnly ?? true,
            ParserTemplate = dto.ParserTemplate,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.VendorCommands.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<VendorCommandResponse>> Update(
        Guid id, [FromBody] WriteVendorCommand dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        // Intent and platform are the key; changing them is a different row, and
        // editing them in place would silently rewrite what a workflow resolves.
        if (dto.Command is not null && dto.Command.Trim().Length > 0) row.Command = dto.Command.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.ReadOnly is not null) row.ReadOnly = dto.ReadOnly.Value;
        if (dto.ParserTemplate is not null) row.ParserTemplate = dto.ParserTemplate;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<VendorCommandResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    private static (string Intent, string Platform) RequireKey(string? intent, string? platform)
    {
        if (string.IsNullOrWhiteSpace(intent)) throw new ValidationException("intent is required");
        if (string.IsNullOrWhiteSpace(platform)) throw new ValidationException("platform is required");
        return (intent.Trim().ToLowerInvariant(), platform.Trim().ToLowerInvariant());
    }

    private async Task<CommandEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.VendorCommands.FirstOrDefaultAsync(c => c.VendorCommandId == id && c.IsActive, ct);
        if (row is null) throw new NotFoundException("vendor command not found");
        return row;
    }

    private static VendorCommandResponse ToResponse(CommandEntity c) => new()
    {
        Id = c.VendorCommandId,
        Intent = c.Intent,
        Platform = c.Platform,
        Command = c.Command,
        Description = c.Description,
        ReadOnly = c.ReadOnly,
        ParserTemplate = c.ParserTemplate,
        UpdatedAt = c.UpdatedAt,
    };
}
