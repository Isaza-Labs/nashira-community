using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Themes;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

public class CreateReport
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("workflow_run_id")] public Guid? WorkflowRunId { get; set; }
    [JsonPropertyName("retain_days")] public int? RetainDays { get; set; }
}

public class ReportResponse
{
    [JsonPropertyName("report_artifact_id")] public Guid Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = string.Empty;
    [JsonPropertyName("file_name")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("size_bytes")] public int SizeBytes { get; set; }
    [JsonPropertyName("workflow_run_id")] public Guid? WorkflowRunId { get; set; }
    [JsonPropertyName("expires_at")] public DateTime? ExpiresAt { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

// Generated reports. Readable by any authenticated user; writing one is an
// Operator act because it is normally a workflow's output.
[ApiController]
[Route("api/reports")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class ReportsController : ControllerBase
{
    private const int MaxBytes = 10 * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ReportsController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<ReportResponse>>> Get(
        bool includeExpired = false, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var now = DateTime.UtcNow;

        var q = _db.ReportArtifacts.AsNoTracking().Where(r => r.IsActive);
        // RetentionHostedService deletes expired rows hourly; this filter covers
        // the window between expiry and the next sweep, so a report never shows
        // by default after the retention policy says it is gone.
        if (!includeExpired) q = q.Where(r => r.ExpiresAt == null || r.ExpiresAt > now);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.CreatedAt)
            .Skip(offset).Take(limit)
            // Project without Content: the list must not stream every report's bytes.
            .Select(r => new ReportResponse
            {
                Id = r.ReportArtifactId,
                Title = r.Title,
                Description = r.Description,
                ContentType = r.ContentType,
                FileName = r.FileName,
                SizeBytes = r.SizeBytes,
                WorkflowRunId = r.WorkflowRunId,
                ExpiresAt = r.ExpiresAt,
                CreatedAt = r.CreatedAt,
            })
            .ToListAsync(ct);

        return new OkObjectResult(new ListResponse<ReportResponse>
        {
            Items = rows, Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var row = await _db.ReportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportArtifactId == id && r.IsActive, ct);
        if (row is null) throw new NotFoundException("report not found");

        if (row.ExpiresAt is { } exp && exp <= DateTime.UtcNow)
            throw new NotFoundException("this report has expired");

        return File(row.Content, row.ContentType, row.FileName);
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<ReportResponse>> Post([FromBody] CreateReport dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Title)) throw new ValidationException("title is required");
        if (string.IsNullOrWhiteSpace(dto.Content)) throw new ValidationException("content is required");

        var bytes = System.Text.Encoding.UTF8.GetBytes(dto.Content);
        if (bytes.Length > MaxBytes)
            throw new ValidationException($"the report exceeds {MaxBytes / (1024 * 1024)} MB");

        var now = DateTime.UtcNow;
        var row = new ReportArtifact
        {
            ReportArtifactId = Guid.NewGuid(),
            Title = dto.Title.Trim(),
            Description = dto.Description,
            ContentType = string.IsNullOrWhiteSpace(dto.ContentType) ? "text/markdown" : dto.ContentType.Trim(),
            FileName = string.IsNullOrWhiteSpace(dto.FileName) ? $"{Slug.From(dto.Title)}.md" : dto.FileName.Trim(),
            Content = bytes,
            SizeBytes = bytes.Length,
            WorkflowRunId = dto.WorkflowRunId,
            ExpiresAt = dto.RetainDays is > 0 ? now.AddDays(dto.RetainDays.Value) : null,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ReportArtifacts.Add(row);
        await _db.SaveChangesAsync(ct);

        return new ReportResponse
        {
            Id = row.ReportArtifactId, Title = row.Title, Description = row.Description,
            ContentType = row.ContentType, FileName = row.FileName, SizeBytes = row.SizeBytes,
            WorkflowRunId = row.WorkflowRunId, ExpiresAt = row.ExpiresAt, CreatedAt = row.CreatedAt,
        };
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _db.ReportArtifacts.FirstOrDefaultAsync(r => r.ReportArtifactId == id && r.IsActive, ct);
        if (row is null) throw new NotFoundException("report not found");
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public class WriteTheme
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("colors")] public JsonElement? Colors { get; set; }
    // Absent means "leave the stored settings alone"; an explicit {} clears every
    // override. That distinction is what lets the editor return a slider to its
    // default and have the override genuinely go away.
    [JsonPropertyName("settings")] public JsonElement? Settings { get; set; }
    [JsonPropertyName("is_shared")] public bool? IsShared { get; set; }
}

public class ThemeResponse
{
    [JsonPropertyName("theme_id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("colors")] public JsonElement Colors { get; set; }
    [JsonPropertyName("settings")] public JsonElement Settings { get; set; }
    [JsonPropertyName("is_shared")] public bool IsShared { get; set; }
    [JsonPropertyName("is_mine")] public bool IsMine { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// UI themes. Any authenticated user manages their own; sharing one with everybody
// is an admin act, in both directions — un-sharing a theme other people are using
// is as disruptive as sharing one nobody asked for.
[ApiController]
[Route("api/themes")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class ThemeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ThemeController(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<ThemeResponse>>> Get(CancellationToken ct)
    {
        var me = _user.IsAuthenticated ? _user.UserId : Guid.Empty;
        var rows = await _db.Themes.AsNoTracking()
            .Where(t => t.IsActive && (t.IsShared || t.OwnerUserId == me))
            .OrderByDescending(t => t.IsShared).ThenBy(t => t.Name)
            .ToListAsync(ct);

        return new OkObjectResult(new ListResponse<ThemeResponse>
        {
            Items = rows.Select(t => ToResponse(t, me)).ToList(),
            Total = rows.Count, Limit = rows.Count, Offset = 0,
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ThemeResponse>> Post([FromBody] WriteTheme dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        RequireObject(dto.Colors);
        await RequireAdminForSharingAsync(dto.IsShared == true);

        var me = _user.IsAuthenticated ? _user.UserId : Guid.Empty;
        var now = DateTime.UtcNow;
        var row = new Theme
        {
            ThemeId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Description = dto.Description,
            ColorsJson = dto.Colors?.GetRawText() ?? "{}",
            SettingsJson = ThemeSettingsValidator.Normalize(dto.Settings),
            IsShared = dto.IsShared ?? false,
            OwnerUserId = me == Guid.Empty ? null : me,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Themes.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, me);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ThemeResponse>> Update(
        Guid id, [FromBody] WriteTheme dto, CancellationToken ct)
    {
        var me = _user.IsAuthenticated ? _user.UserId : Guid.Empty;
        var row = await Find(id, me, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0) row.Name = dto.Name.Trim();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Colors is not null) { RequireObject(dto.Colors); row.ColorsJson = dto.Colors.Value.GetRawText(); }
        if (dto.Settings is not null) row.SettingsJson = ThemeSettingsValidator.Normalize(dto.Settings);
        if (dto.IsShared is not null && dto.IsShared.Value != row.IsShared)
        {
            await RequireAdminForSharingAsync(true);
            row.IsShared = dto.IsShared.Value;
        }
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row, me);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var me = _user.IsAuthenticated ? _user.UserId : Guid.Empty;
        var row = await Find(id, me, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // A shared theme belongs to everybody, so editing or deleting one is an admin
    // act even for its original author.
    private async Task<Theme> Find(Guid id, Guid me, CancellationToken ct)
    {
        var row = await _db.Themes.FirstOrDefaultAsync(t => t.ThemeId == id && t.IsActive, ct);
        if (row is null) throw new NotFoundException("theme not found");

        var isAdmin = _user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase);
        if (row.IsShared && !isAdmin)
            throw new ForbiddenException("a shared theme can only be changed by an administrator");
        if (!row.IsShared && row.OwnerUserId != me && !isAdmin)
            throw new ForbiddenException("this theme belongs to another user");

        return row;
    }

    private Task RequireAdminForSharingAsync(bool sharing)
    {
        if (!sharing) return Task.CompletedTask;
        if (!_user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            throw new ForbiddenException("sharing a theme with every user requires an administrator");
        return Task.CompletedTask;
    }

    private static void RequireObject(JsonElement? colors)
    {
        if (colors is null) return;
        if (colors.Value.ValueKind != JsonValueKind.Object)
            throw new ValidationException("colors must be a JSON object");
    }

    private static ThemeResponse ToResponse(Theme t, Guid me) => new()
    {
        Id = t.ThemeId,
        Name = t.Name,
        Description = t.Description,
        Colors = Parse(t.ColorsJson),
        Settings = Parse(t.SettingsJson),
        IsShared = t.IsShared,
        IsMine = t.OwnerUserId == me,
        UpdatedAt = t.UpdatedAt,
    };

    private static JsonElement Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}
