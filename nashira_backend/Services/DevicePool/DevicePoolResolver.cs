using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Workflow;
using DeviceEntity = nashira_backend.Data.Models.Device;
using PoolEntity = nashira_backend.Data.Models.DevicePool;

namespace nashira_backend.Services.DevicePools;

public interface IDevicePoolResolver
{
    // Every active device the pool currently contains, ignoring environment.
    Task<IReadOnlyList<DeviceEntity>> MembersAsync(Guid poolId, CancellationToken ct);

    // Members a run in `environment` may actually target, plus the ones excluded
    // and why. Returned rather than silently dropped so a caller can report it.
    Task<PoolResolution> ResolveAsync(Guid poolId, string environment, CancellationToken ct);
}

public sealed record PoolResolution(
    IReadOnlyList<DeviceEntity> Allowed, IReadOnlyList<string> Excluded, bool PoolAllowsEnvironment);

public sealed class DevicePoolResolver : IDevicePoolResolver
{
    private readonly AppDbContext _db;

    public DevicePoolResolver(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<DeviceEntity>> MembersAsync(Guid poolId, CancellationToken ct)
    {
        var pool = await _db.DevicePools.AsNoTracking()
            .FirstOrDefaultAsync(p => p.DevicePoolId == poolId && p.IsActive, ct);
        if (pool is null) return [];

        var statics = ParseGuids(pool.StaticMembersJson);
        var rules = ParseRules(pool.FilterRulesJson);

        var query = _db.Devices.AsNoTracking().Where(d => d.IsActive);

        // No rules and no static members means an empty pool, not every device.
        // "Matches nothing" is the safe reading of "specifies nothing" when the
        // result decides what a workflow touches.
        if (rules.Count == 0 && statics.Count == 0) return [];

        if (rules.Count > 0)
        {
            foreach (var (key, value) in rules)
            {
                // Applied as AND: adding a rule must narrow a pool, never widen it.
                query = key switch
                {
                    "site" => query.Where(d => d.Site.ToLower() == value),
                    "role" => query.Where(d => d.Role.ToLower() == value),
                    "vendor" => query.Where(d => d.Vendor.ToLower() == value),
                    "platform" => query.Where(d => d.Platform.ToLower() == value),
                    "status" => query.Where(d => d.Status.ToLower() == value),
                    _ => query,
                };
            }
        }
        else
        {
            // Static-only pool: don't scan the inventory.
            query = query.Where(d => statics.Contains(d.DeviceId));
        }

        var matched = await query.ToListAsync(ct);

        if (rules.Count > 0 && statics.Count > 0)
        {
            var have = matched.Select(d => d.DeviceId).ToHashSet();
            var extra = await _db.Devices.AsNoTracking()
                .Where(d => d.IsActive && statics.Contains(d.DeviceId) && !have.Contains(d.DeviceId))
                .ToListAsync(ct);
            matched.AddRange(extra);
        }

        return matched.OrderBy(d => d.DeviceName, StringComparer.Ordinal).ToList();
    }

    public async Task<PoolResolution> ResolveAsync(Guid poolId, string environment, CancellationToken ct)
    {
        var pool = await _db.DevicePools.AsNoTracking()
            .FirstOrDefaultAsync(p => p.DevicePoolId == poolId && p.IsActive, ct);
        if (pool is null) return new PoolResolution([], [], false);

        var poolAllows = environment switch
        {
            Data.Models.Workflow.EnvDraft => pool.AllowDraft,
            Data.Models.Workflow.EnvQa => pool.AllowQa,
            Data.Models.Workflow.EnvProduction => pool.AllowProduction,
            _ => false,
        };
        if (!poolAllows) return new PoolResolution([], [$"pool '{pool.Name}' does not allow '{environment}'"], false);

        var members = await MembersAsync(poolId, ct);
        var allowed = new List<DeviceEntity>();
        var excluded = new List<string>();

        foreach (var d in members)
        {
            var refusal = DeviceEnvironmentPolicy.Refusal(d, environment);
            if (refusal is null) allowed.Add(d);
            else excluded.Add(refusal);
        }

        return new PoolResolution(allowed, excluded, true);
    }

    private static List<Guid> ParseGuids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
                .Select(e => Guid.Parse(e.GetString()!))
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    private static readonly string[] KnownRules = ["site", "role", "vendor", "platform", "status"];

    private static List<(string Key, string Value)> ParseRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String
                            && KnownRules.Contains(p.Name.ToLowerInvariant()))
                .Select(p => (p.Name.ToLowerInvariant(), (p.Value.GetString() ?? string.Empty).ToLowerInvariant()))
                .Where(r => r.Item2.Length > 0)
                .ToList();
        }
        catch (JsonException) { return []; }
    }
}
