using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Notifications;
using nashira_backend.Services.Security;
using ChannelEntity = nashira_backend.Data.Models.NotificationChannel;
using Microsoft.AspNetCore.RateLimiting;

namespace nashira_backend.Controllers;

public class WriteNotificationChannel
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    // Write-only. Omit on update to keep the stored value.
    [JsonPropertyName("webhook_url")] public string? WebhookUrl { get; set; }
    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("allow_private_network")] public bool? AllowPrivateNetwork { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class NotificationChannelResponse
{
    [JsonPropertyName("notification_channel_id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("target_host")] public string? TargetHost { get; set; }
    [JsonPropertyName("has_webhook_url")] public bool HasWebhookUrl { get; set; }
    [JsonPropertyName("headers")] public string? Headers { get; set; }
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("last_check_error")] public string? LastCheckError { get; set; }
    [JsonPropertyName("last_checked_at")] public DateTime? LastCheckedAt { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class SendNotificationRequest
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public class NotificationDeliveryResponse
{
    [JsonPropertyName("notification_delivery_id")] public Guid Id { get; set; }
    [JsonPropertyName("preview")] public string Preview { get; set; } = string.Empty;
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("elapsed_ms")] public int ElapsedMs { get; set; }
    [JsonPropertyName("workflow_run_id")] public Guid? WorkflowRunId { get; set; }
    [JsonPropertyName("sent_at")] public DateTime SentAt { get; set; }
}

// Outbound notification channels.
//
// Admin throughout, including reads: for Slack and Teams the webhook URL IS the
// credential, and even the non-secret fields describe how to reach an operations
// channel.
[ApiController]
[Route("api/notifications/channels")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class NotificationChannelController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;
    private readonly INotificationDispatcher _dispatcher;

    public NotificationChannelController(
        AppDbContext db, ICurrentUser user, ISecretProtector protector, INotificationDispatcher dispatcher)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _dispatcher = dispatcher;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<NotificationChannelResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.NotificationChannels.AsNoTracking().Where(c => c.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<NotificationChannelResponse>
        {
            Items = rows.Select(ToResponse).ToList(), Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NotificationChannelResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<NotificationChannelResponse>> Post(
        [FromBody] WriteNotificationChannel dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        if (string.IsNullOrWhiteSpace(dto.WebhookUrl)) throw new ValidationException("webhook_url is required");
        var kind = NormalizeKind(dto.Kind);
        var host = RequireAbsoluteHttpUrl(dto.WebhookUrl);
        RequireJsonObject(dto.Headers, "headers");

        var name = dto.Name.Trim();
        if (await _db.NotificationChannels.AnyAsync(c => c.Name == name && c.IsActive, ct))
            throw new ConflictException("a channel with this name already exists", "channel_name_taken");

        var taken = new HashSet<string>(
            await _db.NotificationChannels.Select(c => c.Slug).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var row = new ChannelEntity
        {
            NotificationChannelId = Guid.NewGuid(),
            Name = name,
            Slug = Slug.Unique(name, taken.Contains),
            Kind = kind,
            Description = dto.Description,
            WebhookUrlEncrypted = _protector.Encrypt(dto.WebhookUrl.Trim()),
            TargetHost = host,
            HeadersJson = dto.Headers,
            AllowPrivateNetwork = dto.AllowPrivateNetwork ?? false,
            Enabled = dto.Enabled ?? true,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.NotificationChannels.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(
            nameof(GetById), "NotificationChannel", new { id = row.NotificationChannelId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<NotificationChannelResponse>> Update(
        Guid id, [FromBody] WriteNotificationChannel dto, CancellationToken ct)
    {
        var row = await Find(id, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.NotificationChannels.AnyAsync(
                    c => c.Name == name && c.IsActive && c.NotificationChannelId != id, ct))
                throw new ConflictException("a channel with this name already exists", "channel_name_taken");
            row.Name = name;
        }
        if (dto.Kind is not null) row.Kind = NormalizeKind(dto.Kind);
        if (dto.Description is not null) row.Description = dto.Description;
        // Absent means unchanged: the API never returns the URL, so a client has
        // nothing to resubmit and treating absence as "clear" would silently
        // unconfigure a channel on any unrelated edit.
        if (!string.IsNullOrWhiteSpace(dto.WebhookUrl))
        {
            row.TargetHost = RequireAbsoluteHttpUrl(dto.WebhookUrl);
            row.WebhookUrlEncrypted = _protector.Encrypt(dto.WebhookUrl.Trim());
        }
        if (dto.Headers is not null) { RequireJsonObject(dto.Headers, "headers"); row.HeadersJson = dto.Headers; }
        if (dto.AllowPrivateNetwork is not null) row.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<NotificationChannelResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    // Sends a real message. There is no dry run: the only way to know a webhook
    // works is for something to arrive at the far end.
    [HttpPost("{id:guid}/send")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<object>> Send(
        Guid id, [FromBody] SendNotificationRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) throw new ValidationException("text is required");
        var result = await _dispatcher.SendAsync(id, req.Text, null, ct);
        return new OkObjectResult(new
        {
            success = result.Success,
            status_code = result.StatusCode,
            error = result.Error,
            attempts = result.Attempts,
            elapsed_ms = result.ElapsedMs,
        });
    }

    [HttpPost("{id:guid}/check")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit] // diagnostic
    public async Task<ActionResult<object>> Check(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var result = await _dispatcher.SendAsync(
            id, $"Nashira connectivity check for '{row.Name}'.", null, ct);
        return new OkObjectResult(new
        {
            status = result.Success ? "healthy" : "unreachable",
            error = result.Error,
            elapsed_ms = result.ElapsedMs,
        });
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<ActionResult<ListResponse<NotificationDeliveryResponse>>> Deliveries(
        Guid id, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        await Find(id, ct);

        var q = _db.NotificationDeliveries.AsNoTracking().Where(d => d.NotificationChannelId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.SentAt).Skip(offset).Take(limit).ToListAsync(ct);

        return new OkObjectResult(new ListResponse<NotificationDeliveryResponse>
        {
            Items = rows.Select(d => new NotificationDeliveryResponse
            {
                Id = d.NotificationDeliveryId,
                Preview = d.Preview,
                Success = d.Success,
                StatusCode = d.StatusCode,
                Error = d.Error,
                Attempts = d.Attempts,
                ElapsedMs = d.ElapsedMs,
                WorkflowRunId = d.WorkflowRunId,
                SentAt = d.SentAt,
            }).ToList(),
            Total = total, Limit = limit, Offset = offset,
        });
    }

    private static string NormalizeKind(string? raw)
    {
        var v = (raw ?? ChannelEntity.KindSlack).Trim().ToLowerInvariant();
        if (!ChannelEntity.Kinds.Contains(v))
            throw new ValidationException($"kind must be one of: {string.Join(", ", ChannelEntity.Kinds)}");
        return v;
    }

    // Returns the host, which is kept in the clear so the UI can show where a
    // channel points without decrypting the credential.
    private static string RequireAbsoluteHttpUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ValidationException("webhook_url must be an absolute http(s) URL");
        return uri.Host;
    }

    private static void RequireJsonObject(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ValidationException($"{field} must be a JSON object");
        }
        catch (JsonException) { throw new ValidationException($"{field} is not valid JSON"); }
    }

    private async Task<ChannelEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.NotificationChannels.FirstOrDefaultAsync(
            c => c.NotificationChannelId == id && c.IsActive, ct);
        if (row is null) throw new NotFoundException("notification channel not found");
        return row;
    }

    private static NotificationChannelResponse ToResponse(ChannelEntity c) => new()
    {
        Id = c.NotificationChannelId,
        Name = c.Name,
        Slug = c.Slug,
        Kind = c.Kind,
        Description = c.Description,
        TargetHost = c.TargetHost,
        HasWebhookUrl = c.WebhookUrlEncrypted is { Length: > 0 },
        Headers = c.HeadersJson,
        AllowPrivateNetwork = c.AllowPrivateNetwork,
        Status = c.Status,
        LastCheckError = c.LastCheckError,
        LastCheckedAt = c.LastCheckedAt,
        Enabled = c.Enabled,
        UpdatedAt = c.UpdatedAt,
    };
}
