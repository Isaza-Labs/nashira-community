using nashira_backend.Configuration.Modules;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Settings;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Platform settings an admin may change without a redeploy.
//
// The rows carry their own presentation — display name, description, input type, order
// — so the screen renders from the data rather than from a hard-coded form that drifts
// from it. What a setting *is* stays in AppSettingDefinition, because every key here
// has a read site in the code, and a row without one would be a control that controls
// nothing.
//
// Writes go through the global audit filter, which is the point: "who turned the SSH
// safety off, and when" is exactly the question the trail exists for.
[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AdminSettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AppSettingsProvider _settings;
    private readonly IConfiguration _config;
    private readonly ModuleSelection _modules;

    public AdminSettingsController(
        AppDbContext db, AppSettingsProvider settings, IConfiguration config,
        ModuleSelection modules)
    {
        _db = db;
        _settings = settings;
        _config = config;
        _modules = modules;
    }

    [HttpGet]
    public async Task<ActionResult<SettingsResponse>> Get(CancellationToken ct = default)
    {
        var rows = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.IsActive && s.Provider == AppSettingDefinition.Provider)
            .ToListAsync(ct);

        var byKey = rows.ToDictionary(r => r.SettingKey);

        // Driven by the catalog, not by the table: a row for a key the code no longer
        // reads must not appear as something you can still change.
        var items = AppSettingDefinition.For(_modules)
            .OrderBy(d => d.DisplayOrder)
            .Select(d =>
            {
                var row = byKey.GetValueOrDefault(d.Key);
                return new SettingDto
                {
                    Key = d.Key,
                    Category = d.Category,
                    DisplayName = d.DisplayName,
                    Description = d.Description,
                    InputType = d.InputType,
                    // What the platform is actually using right now, resolved through the
                    // same fallback chain the code reads: stored row, then configuration,
                    // then the built-in default.
                    Effective = Effective(d),
                    StoredValue = row?.SettingValue,
                    // Where the effective value came from. Without it, a screen showing
                    // "true" cannot tell an admin whether they set it or an environment
                    // variable did — and only one of those is theirs to change here.
                    Source = row?.SettingValue is not null ? "stored"
                        : _config[d.Key] is not null ? "configuration"
                        : "default",
                    DefaultValue = d.Default,
                };
            })
            .ToList();

        return new SettingsResponse
        {
            RefreshSeconds = (int)AppSettingsProvider.RefreshInterval.TotalSeconds,
            Items = items,
        };
    }

    [HttpPut("{key}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Put(
        string key, [FromBody] SettingRequest body, CancellationToken ct = default)
    {
        var definition = AppSettingDefinition.Find(key, _modules);
        if (definition is null) return NotFound(new { error = $"unknown setting '{key}'" });

        var value = (body.Value ?? string.Empty).Trim();
        if (Validate(definition, value) is { } problem) return BadRequest(new { error = problem });

        var row = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Provider == AppSettingDefinition.Provider
                                      && s.SettingKey == key, ct);
        if (row is null) return NotFound(new { error = $"'{key}' has not been seeded yet" });

        row.SettingValue = value;
        row.IsActive = true;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // This replica now, the others within the refresh interval.
        await _settings.RefreshAsync(ct);
        return Ok(new { key, value, effective = Effective(definition) });
    }

    // Back to whatever configuration says — which is not necessarily the built-in
    // default, and the response says which it landed on.
    [HttpDelete("{key}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Reset(string key, CancellationToken ct = default)
    {
        var definition = AppSettingDefinition.Find(key, _modules);
        if (definition is null) return NotFound(new { error = $"unknown setting '{key}'" });

        var row = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Provider == AppSettingDefinition.Provider
                                      && s.SettingKey == key, ct);
        if (row is not null && row.SettingValue is not null)
        {
            // Cleared rather than deleted: the row also carries the display metadata the
            // screen renders from, and deleting it would remove the setting from the
            // page until the next boot re-seeded it.
            row.SettingValue = null;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        await _settings.RefreshAsync(ct);
        return Ok(new { key, effective = Effective(definition) });
    }

    private string Effective(AppSettingDefinition d) => d.InputType switch
    {
        AppSettingDefinition.TypeBool =>
            _settings.GetBool(d.Key, bool.TryParse(d.Default, out var b) && b)
                .ToString().ToLowerInvariant(),
        AppSettingDefinition.TypeInt =>
            _settings.GetLong(d.Key, long.TryParse(d.Default, out var n) ? n : 0)
                .ToString(),
        _ => d.Default,
    };

    private static string? Validate(AppSettingDefinition d, string value) => d.InputType switch
    {
        AppSettingDefinition.TypeBool when !bool.TryParse(value, out _) =>
            $"'{d.Key}' is a toggle — send true or false, not '{value}'",
        // Long, not int: Git:MaxFileBytes is a byte count and an admin raising it to a
        // couple of gigabytes would otherwise be told their number is not a number.
        AppSettingDefinition.TypeInt when !long.TryParse(value, out _) =>
            $"'{d.Key}' is a number — send an integer, not '{value}'",
        AppSettingDefinition.TypeInt when long.Parse(value) < 0 =>
            $"'{d.Key}' cannot be negative",
        _ => null,
    };
}

public class SettingDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("input_type")] public string InputType { get; set; } = string.Empty;

    /// <summary>What the platform is using right now.</summary>
    [JsonPropertyName("effective")] public string Effective { get; set; } = string.Empty;

    /// <summary>Null when nobody has overridden this from the screen.</summary>
    [JsonPropertyName("stored_value")] public string? StoredValue { get; set; }

    /// <summary>"stored", "configuration" or "default".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;

    [JsonPropertyName("default_value")] public string DefaultValue { get; set; } = string.Empty;
}

public class SettingsResponse
{
    // How long a change takes to reach the other replicas. Shown, because a setting that
    // appears not to have applied is otherwise indistinguishable from one that failed.
    [JsonPropertyName("refresh_seconds")] public int RefreshSeconds { get; set; }
    [JsonPropertyName("items")] public List<SettingDto> Items { get; set; } = [];
}

public class SettingRequest
{
    [Required]
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
