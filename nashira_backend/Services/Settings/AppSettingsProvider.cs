using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Settings;

// The effective value of a setting, read synchronously.
//
// A singleton holding an immutable snapshot, refreshed on write and on a timer. Three
// things forced that shape:
//
//  - The read sites are synchronous constructors and hot paths (the SSH command gate
//    runs before every command). An async lookup would have rippled `await` through
//    call chains that have no business knowing where a number came from.
//  - Every read would otherwise be a database round-trip for a value that changes
//    perhaps twice a year.
//  - Another replica may be the one that took the write, so a cache invalidated only
//    by the local writer would leave the others stale forever. The timer is what makes
//    a change reach a replica that did not serve it — which is why a setting takes
//    effect within the refresh interval rather than instantly, and the screen says so.
//
// Fallback order: the stored row, then appsettings/environment, then the catalog's
// default. Configuration still wins where no row exists, so an operator who set an
// environment variable does not find it silently ignored.
public sealed class AppSettingsProvider
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<AppSettingsProvider> _logger;

    // Replaced wholesale rather than mutated: a reader holding the old dictionary sees
    // a consistent set of values, never a half-applied update.
    private volatile Dictionary<string, string?> _values = new();

    public AppSettingsProvider(
        IServiceScopeFactory scopes, IConfiguration config, ILogger<AppSettingsProvider> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    public bool GetBool(string key, bool fallback)
    {
        var raw = Raw(key);
        if (raw is null) return _config.GetValue(key, fallback);
        return bool.TryParse(raw, out var v) ? v : _config.GetValue(key, fallback);
    }

    public int GetInt(string key, int fallback)
    {
        var raw = Raw(key);
        if (raw is null) return _config.GetValue(key, fallback);
        return int.TryParse(raw, out var v) ? v : _config.GetValue(key, fallback);
    }

    public long GetLong(string key, long fallback)
    {
        var raw = Raw(key);
        if (raw is null) return _config.GetValue(key, fallback);
        return long.TryParse(raw, out var v) ? v : _config.GetValue(key, fallback);
    }

    /// <summary>The stored value, or null when no row overrides this key.</summary>
    private string? Raw(string key) =>
        _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>
    /// Re-reads every stored value. Called at boot, by the timer, and by the controller
    /// right after a write so the replica that took it does not wait out the interval.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.SystemSettings.AsNoTracking()
                .Where(s => s.IsActive && s.Provider == AppSettingDefinition.Provider)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToListAsync(ct);

            _values = rows.ToDictionary(r => r.SettingKey, r => r.SettingValue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the previous snapshot. Falling back to configuration on a transient
            // database blip would silently re-enable a safety gate an admin turned off,
            // which is the one direction this must never fail in.
            _logger.LogWarning(ex, "settings.refresh.failed — keeping the previous values");
        }
    }

    /// <summary>
    /// Writes the catalog into system_settings, filling in the display metadata the
    /// screen renders from. Values already stored are never touched: this is the shape
    /// of a setting, not its value.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.SystemSettings
            .Where(s => s.Provider == AppSettingDefinition.Provider)
            .ToDictionaryAsync(s => s.SettingKey, ct);

        var now = DateTime.UtcNow;
        foreach (var d in AppSettingDefinition.All)
        {
            if (existing.TryGetValue(d.Key, out var row))
            {
                row.Category = d.Category;
                row.DisplayName = d.DisplayName;
                row.Description = d.Description;
                row.InputType = d.InputType;
                row.DisplayOrder = d.DisplayOrder;
                row.IsEditable = true;
                row.IsActive = true;
                row.UpdatedAt = now;
                continue;
            }

            db.SystemSettings.Add(new SystemSetting
            {
                SystemSettingId = Guid.NewGuid(),
                Provider = AppSettingDefinition.Provider,
                SettingKey = d.Key,
                Category = d.Category,
                DisplayName = d.DisplayName,
                Description = d.Description,
                InputType = d.InputType,
                DisplayOrder = d.DisplayOrder,
                // Null, not the default: an unset row means "configuration still
                // decides", which is not the same as "an admin chose this value".
                SettingValue = null,
                IsEditable = true,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // A key dropped from the catalog leaves its row behind, deactivated rather than
        // deleted: a setting removed in one release and restored in the next should come
        // back with the value it was given.
        var known = AppSettingDefinition.All.Select(d => d.Key).ToHashSet();
        foreach (var (key, row) in existing)
        {
            if (known.Contains(key) || !row.IsActive) continue;
            row.IsActive = false;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await RefreshAsync(ct);
    }
}
