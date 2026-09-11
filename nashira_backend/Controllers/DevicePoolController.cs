using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.DevicePools;
using nashira_backend.Services.Identity;
using PoolEntity = nashira_backend.Data.Models.DevicePool;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class WriteDevicePool
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("filter_rules")] public string? FilterRules { get; set; }
    [JsonPropertyName("static_members")] public List<Guid>? StaticMembers { get; set; }
    [JsonPropertyName("allow_draft")] public bool? AllowDraft { get; set; }
    [JsonPropertyName("allow_qa")] public bool? AllowQa { get; set; }
    [JsonPropertyName("allow_production")] public bool? AllowProduction { get; set; }
}

public class DevicePoolResponse
{
    [JsonPropertyName("device_pool_id")] public Guid DevicePoolId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("filter_rules")] public string? FilterRules { get; set; }
    [JsonPropertyName("static_members")] public JsonElement StaticMembers { get; set; }
    [JsonPropertyName("allow_draft")] public bool AllowDraft { get; set; }
    [JsonPropertyName("allow_qa")] public bool AllowQa { get; set; }
    [JsonPropertyName("allow_production")] public bool AllowProduction { get; set; }
    [JsonPropertyName("member_count")] public int MemberCount { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// Named groups of devices. Reads Viewer, writes Operator — a pool decides what a
// workflow touches, so it is authoring, not administration.
[ApiController]
[Route("api/device-pools")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class DevicePoolController : ControllerBase
{
    private static readonly string[] KnownRules = ["site", "role", "vendor", "platform", "status"];

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IDevicePoolResolver _resolver;

    public DevicePoolController(AppDbContext db, ICurrentUser user, IDevicePoolResolver resolver)
    {
        _db = db;
        _user = user;
        _resolver = resolver;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<DevicePoolResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.DevicePools.AsNoTracking().Where(p => p.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(p => p.Name).Skip(offset).Take(limit).ToListAsync(ct);

        var items = new List<DevicePoolResponse>(rows.Count);
        foreach (var row in rows)
        {
            var members = await _resolver.MembersAsync(row.DevicePoolId, ct);
            items.Add(ToResponse(row, members.Count));
        }

        return new OkObjectResult(new ListResponse<DevicePoolResponse>
        {
            Items = items, Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DevicePoolResponse>> GetById(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var members = await _resolver.MembersAsync(id, ct);
        return ToResponse(row, members.Count);
    }

    // The resolved membership. A pool defined by rules is otherwise opaque — an
    // author needs to see what it currently matches before pointing a run at it.
    [HttpGet("{id:guid}/members")]
    public async Task<ActionResult<object>> Members(Guid id, string? environment = null, CancellationToken ct = default)
    {
        await Find(id, ct);

        if (string.IsNullOrWhiteSpace(environment))
        {
            var all = await _resolver.MembersAsync(id, ct);
            return new OkObjectResult(new
            {
                members = all.Select(d => new { device_id = d.DeviceId, name = d.DeviceName, ip = d.IpAddress }),
                count = all.Count,
            });
        }

        var resolution = await _resolver.ResolveAsync(id, environment, ct);
        return new OkObjectResult(new
        {
            environment,
            pool_allows_environment = resolution.PoolAllowsEnvironment,
            members = resolution.Allowed.Select(d => new { device_id = d.DeviceId, name = d.DeviceName, ip = d.IpAddress }),
            count = resolution.Allowed.Count,
            excluded = resolution.Excluded,
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<DevicePoolResponse>> Post([FromBody] WriteDevicePool dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        ValidateRules(dto.FilterRules);
        await EnsureDevicesAsync(dto.StaticMembers, ct);

        var name = dto.Name.Trim();
        if (await _db.DevicePools.AnyAsync(p => p.Name == name && p.IsActive, ct))
            throw new ConflictException("a pool with this name already exists", "pool_name_taken");

        var taken = new HashSet<string>(await _db.DevicePools.Select(p => p.Slug).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var row = new PoolEntity
        {
            DevicePoolId = Guid.NewGuid(),
            Name = name,
            Slug = Slug.Unique(name, taken.Contains),
            Description = dto.Description,
            FilterRulesJson = dto.FilterRules,
            StaticMembersJson = JsonSerializer.Serialize(dto.StaticMembers ?? []),
            AllowDraft = dto.AllowDraft ?? true,
            AllowQa = dto.AllowQa ?? false,
            AllowProduction = dto.AllowProduction ?? true,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.DevicePools.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(GetById), "DevicePool", new { id = row.DevicePoolId }, ToResponse(row, 0));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<DevicePoolResponse>> Update(
        Guid id, [FromBody] WriteDevicePool dto, CancellationToken ct)
    {
        var row = await Find(id, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.DevicePools.AnyAsync(p => p.Name == name && p.IsActive && p.DevicePoolId != id, ct))
                throw new ConflictException("a pool with this name already exists", "pool_name_taken");
            row.Name = name;
        }
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.FilterRules is not null) { ValidateRules(dto.FilterRules); row.FilterRulesJson = dto.FilterRules; }
        if (dto.StaticMembers is not null)
        {
            await EnsureDevicesAsync(dto.StaticMembers, ct);
            row.StaticMembersJson = JsonSerializer.Serialize(dto.StaticMembers);
        }
        if (dto.AllowDraft is not null) row.AllowDraft = dto.AllowDraft.Value;
        if (dto.AllowQa is not null) row.AllowQa = dto.AllowQa.Value;
        if (dto.AllowProduction is not null) row.AllowProduction = dto.AllowProduction.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        var members = await _resolver.MembersAsync(id, ct);
        return ToResponse(row, members.Count);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<DevicePoolResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, 0);
    }

    // An unknown rule key would silently match everything, which is the opposite of
    // what the author meant.
    private static void ValidateRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { throw new ValidationException("filter_rules is not valid JSON"); }

        if (root.ValueKind != JsonValueKind.Object)
            throw new ValidationException("filter_rules must be a JSON object");

        var unknown = root.EnumerateObject()
            .Select(p => p.Name.ToLowerInvariant())
            .Where(n => !KnownRules.Contains(n))
            .ToList();
        if (unknown.Count > 0)
            throw new ValidationException(
                $"unknown filter rule(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", KnownRules)}");
    }

    private async Task EnsureDevicesAsync(List<Guid>? ids, CancellationToken ct)
    {
        if (ids is null || ids.Count == 0) return;
        var known = await _db.Devices.AsNoTracking()
            .Where(d => d.IsActive && ids.Contains(d.DeviceId))
            .Select(d => d.DeviceId).ToListAsync(ct);
        var missing = ids.Distinct().Except(known).ToList();
        if (missing.Count > 0)
            throw new ValidationException($"unknown device(s): {string.Join(", ", missing)}");
    }

    private async Task<PoolEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.DevicePools.FirstOrDefaultAsync(p => p.DevicePoolId == id && p.IsActive, ct);
        if (row is null) throw new NotFoundException("device pool not found");
        return row;
    }

    private static DevicePoolResponse ToResponse(PoolEntity p, int memberCount) => new()
    {
        DevicePoolId = p.DevicePoolId,
        Name = p.Name,
        Slug = p.Slug,
        Description = p.Description,
        FilterRules = p.FilterRulesJson,
        StaticMembers = Parse(p.StaticMembersJson),
        AllowDraft = p.AllowDraft,
        AllowQa = p.AllowQa,
        AllowProduction = p.AllowProduction,
        MemberCount = memberCount,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    private static JsonElement Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("[]").RootElement.Clone();
        }
    }
}
