using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Security;
using ChannelEntity = nashira_backend.Data.Models.MessagingChannel;

namespace nashira_backend.Controllers;

public class WriteMessagingChannel
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }

    // The three secrets are write-only with three-state semantics:
    //   absent/null → leave unchanged      ""  → clear      value → rotate
    // The API never returns them, so "absent means clear" would silently
    // unconfigure a channel on any unrelated edit.
    [JsonPropertyName("bot_token")] public string? BotToken { get; set; }
    [JsonPropertyName("signing_secret")] public string? SigningSecret { get; set; }
    [JsonPropertyName("app_token")] public string? AppToken { get; set; }

    [JsonPropertyName("external_config")] public Dictionary<string, string?>? ExternalConfig { get; set; }
    [JsonPropertyName("max_role")] public string? MaxRole { get; set; }
    [JsonPropertyName("require_linked_user")] public bool? RequireLinkedUser { get; set; }
    [JsonPropertyName("allowed_external_ids")] public List<string>? AllowedExternalIds { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool? AllowUnsigned { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public class MessagingChannelResponse
{
    [JsonPropertyName("messaging_channel_id")] public Guid Id { get; set; }
    [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("external_config")] public Dictionary<string, string> ExternalConfig { get; set; } = new();
    [JsonPropertyName("max_role")] public string? MaxRole { get; set; }
    [JsonPropertyName("require_linked_user")] public bool RequireLinkedUser { get; set; }
    [JsonPropertyName("allowed_external_ids")] public List<string> AllowedExternalIds { get; set; } = [];
    [JsonPropertyName("allow_unsigned")] public bool AllowUnsigned { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    [JsonPropertyName("has_bot_token")] public bool HasBotToken { get; set; }
    [JsonPropertyName("has_signing_secret")] public bool HasSigningSecret { get; set; }
    [JsonPropertyName("has_app_token")] public bool HasAppToken { get; set; }

    // Where the provider should send its events. Empty when Messaging:PublicBaseUrl
    // is unset, because half a URL is worse than none — an admin would paste it.
    [JsonPropertyName("webhook_url")] public string WebhookUrl { get; set; } = string.Empty;

    [JsonPropertyName("last_delivery_at")] public DateTime? LastDeliveryAt { get; set; }
    [JsonPropertyName("last_delivery_status")] public string? LastDeliveryStatus { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class MessagingInboundEventResponse
{
    [JsonPropertyName("messaging_inbound_event_id")] public Guid Id { get; set; }
    [JsonPropertyName("provider_event_id")] public string ProviderEventId { get; set; } = string.Empty;
    [JsonPropertyName("external_thread_id")] public string? ExternalThreadId { get; set; }
    [JsonPropertyName("conversation_id")] public Guid? ConversationId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
}

public class MessagingDeliveryResponse
{
    [JsonPropertyName("messaging_delivery_id")] public Guid Id { get; set; }
    [JsonPropertyName("conversation_id")] public Guid? ConversationId { get; set; }
    [JsonPropertyName("external_thread_id")] public string? ExternalThreadId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("attempt")] public int Attempt { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
}

public class MessagingChannelActivityResponse
{
    [JsonPropertyName("inbound")] public List<MessagingInboundEventResponse> Inbound { get; set; } = [];
    [JsonPropertyName("deliveries")] public List<MessagingDeliveryResponse> Deliveries { get; set; } = [];
}

public class MessagingIdentityLinkResponse
{
    [JsonPropertyName("messaging_identity_link_id")] public Guid Id { get; set; }
    [JsonPropertyName("messaging_channel_id")] public Guid ChannelId { get; set; }
    [JsonPropertyName("external_workspace_id")] public string ExternalWorkspaceId { get; set; } = string.Empty;
    [JsonPropertyName("external_user_id")] public string ExternalUserId { get; set; } = string.Empty;
    [JsonPropertyName("linked_user_id")] public Guid LinkedUserId { get; set; }
    [JsonPropertyName("linked_username")] public string? LinkedUsername { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

// Bidirectional messaging channels: Slack, Telegram, WhatsApp and Teams.
//
// Admin throughout, including reads. A channel row is a bot credential plus the
// permission ceiling for everyone who talks through it; even the non-secret fields
// describe how to reach the assistant from outside.
[ApiController]
[Route("api/messaging/channels")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class MessagingChannelController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;
    private readonly MessagingOptions _options;

    public MessagingChannelController(
        AppDbContext db, ICurrentUser user, ISecretProtector protector, IOptions<MessagingOptions> options)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<MessagingChannelResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.MessagingChannels.AsNoTracking().Where(c => c.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<MessagingChannelResponse>
        {
            Items = rows.Select(ToResponse).ToList(), Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MessagingChannelResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<MessagingChannelResponse>> Post(
        [FromBody] WriteMessagingChannel dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        var provider = NormalizeProvider(dto.Provider);
        var maxRole = NormalizeMaxRole(dto.MaxRole);
        var config = MessagingChannelConfig.Serialize(dto.ExternalConfig);
        RequireProviderConfig(provider, MessagingChannelConfig.Parse(config));
        ValidateTeamsRelayConnectionString(provider, dto.AppToken);

        var name = dto.Name.Trim();
        if (await _db.MessagingChannels.AnyAsync(c => c.Name == name && c.IsActive, ct))
            throw new ConflictException("a channel with this name already exists", "channel_name_taken");

        var taken = new HashSet<string>(
            await _db.MessagingChannels.Select(c => c.Slug).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var row = new ChannelEntity
        {
            MessagingChannelId = Guid.NewGuid(),
            Provider = provider,
            Name = name,
            Slug = Slug.Unique(name, taken.Contains),
            BotTokenEncrypted = Protect(dto.BotToken),
            SigningSecretEncrypted = Protect(dto.SigningSecret),
            AppTokenEncrypted = Protect(dto.AppToken),
            ExternalConfigJson = config,
            MaxRole = maxRole,
            RequireLinkedUser = dto.RequireLinkedUser ?? true,
            AllowedExternalIdsJson = MessagingChannelConfig.SerializeIdList(dto.AllowedExternalIds),
            AllowUnsigned = dto.AllowUnsigned ?? false,
            Enabled = dto.Enabled ?? true,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MessagingChannels.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(
            nameof(GetById), "MessagingChannel", new { id = row.MessagingChannelId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<MessagingChannelResponse>> Update(
        Guid id, [FromBody] WriteMessagingChannel dto, CancellationToken ct)
    {
        var row = await Find(id, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.MessagingChannels.AnyAsync(
                    c => c.Name == name && c.IsActive && c.MessagingChannelId != id, ct))
                throw new ConflictException("a channel with this name already exists", "channel_name_taken");
            row.Name = name;
        }

        if (dto.Provider is not null) row.Provider = NormalizeProvider(dto.Provider);
        if (dto.ExternalConfig is not null) row.ExternalConfigJson = MessagingChannelConfig.Serialize(dto.ExternalConfig);
        if (dto.MaxRole is not null) row.MaxRole = NormalizeMaxRole(dto.MaxRole);
        if (dto.RequireLinkedUser is not null) row.RequireLinkedUser = dto.RequireLinkedUser.Value;
        if (dto.AllowedExternalIds is not null)
            row.AllowedExternalIdsJson = MessagingChannelConfig.SerializeIdList(dto.AllowedExternalIds);
        if (dto.AllowUnsigned is not null) row.AllowUnsigned = dto.AllowUnsigned.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;

        // Against the request, not the merged row: the stored value is encrypted
        // and an edit that leaves the field alone must not be judged on it.
        ValidateTeamsRelayConnectionString(row.Provider, dto.AppToken);

        if (dto.BotToken is not null) row.BotTokenEncrypted = Protect(dto.BotToken);
        if (dto.SigningSecret is not null) row.SigningSecretEncrypted = Protect(dto.SigningSecret);
        if (dto.AppToken is not null) row.AppTokenEncrypted = Protect(dto.AppToken);

        // Validated against the MERGED row, not the request: switching an existing
        // channel over to Teams without supplying app_id has to fail here, or it
        // 401s in both directions with nothing but the delivery log to explain it.
        RequireProviderConfig(row.Provider, MessagingChannelConfig.External(row));

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<MessagingChannelResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        // Also disabled, so the socket-mode and relay reconcilers drop their
        // listeners for it rather than holding a connection for a deleted channel.
        row.Enabled = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    // Inbound events and outbound deliveries, newest first — the two halves of
    // "why did the bot not answer?".
    [HttpGet("{id:guid}/activity")]
    public async Task<ActionResult<MessagingChannelActivityResponse>> Activity(
        Guid id, int limit = 50, CancellationToken ct = default)
    {
        await Find(id, ct);
        limit = Math.Clamp(limit, 1, 200);

        var inbound = await _db.MessagingInboundEvents.AsNoTracking()
            .Where(e => e.MessagingChannelId == id)
            .OrderByDescending(e => e.At).Take(limit).ToListAsync(ct);

        var deliveries = await _db.MessagingDeliveries.AsNoTracking()
            .Where(d => d.MessagingChannelId == id)
            .OrderByDescending(d => d.At).Take(limit).ToListAsync(ct);

        return new OkObjectResult(new MessagingChannelActivityResponse
        {
            Inbound = inbound.Select(e => new MessagingInboundEventResponse
            {
                Id = e.MessagingInboundEventId,
                ProviderEventId = e.ProviderEventId,
                ExternalThreadId = e.ExternalThreadId,
                ConversationId = e.ConversationId,
                Status = e.Status,
                Event = e.Event,
                Error = e.Error,
                At = e.At,
            }).ToList(),
            Deliveries = deliveries.Select(d => new MessagingDeliveryResponse
            {
                Id = d.MessagingDeliveryId,
                ConversationId = d.ConversationId,
                ExternalThreadId = d.ExternalThreadId,
                Status = d.Status,
                Attempt = d.Attempt,
                Error = d.Error,
                At = d.At,
            }).ToList(),
        });
    }

    [HttpGet("{id:guid}/links")]
    public async Task<ActionResult<ListResponse<MessagingIdentityLinkResponse>>> Links(
        Guid id, CancellationToken ct)
    {
        await Find(id, ct);
        var rows = await _db.MessagingIdentityLinks.AsNoTracking()
            .Where(l => l.MessagingChannelId == id && l.IsActive)
            .Join(_db.Users.AsNoTracking(), l => l.LinkedUserId, u => u.UserId,
                (l, u) => new { Link = l, u.Username })
            .OrderBy(x => x.Username)
            .ToListAsync(ct);

        var items = rows.Select(x => new MessagingIdentityLinkResponse
        {
            Id = x.Link.MessagingIdentityLinkId,
            ChannelId = x.Link.MessagingChannelId,
            ExternalWorkspaceId = x.Link.ExternalWorkspaceId,
            ExternalUserId = x.Link.ExternalUserId,
            LinkedUserId = x.Link.LinkedUserId,
            LinkedUsername = x.Username,
            DisplayName = x.Link.DisplayName,
            CreatedAt = x.Link.CreatedAt,
        }).ToList();

        return new OkObjectResult(new ListResponse<MessagingIdentityLinkResponse>
        {
            Items = items, Total = items.Count, Limit = items.Count, Offset = 0,
        });
    }

    // Revoking a link does not delete the conversation history — it stops the
    // external identity acting as that user from the next message on.
    [HttpDelete("{id:guid}/links/{linkId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<IActionResult> RevokeLink(Guid id, Guid linkId, CancellationToken ct)
    {
        await Find(id, ct);
        var link = await _db.MessagingIdentityLinks.FirstOrDefaultAsync(
            l => l.MessagingIdentityLinkId == linkId && l.MessagingChannelId == id && l.IsActive, ct);
        if (link is null) throw new NotFoundException("identity link not found");

        link.IsActive = false;
        link.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // "" clears, a value rotates. The caller decides which by sending "" or a value;
    // absent never reaches here (the update method checks for null first).
    private byte[]? Protect(string? value) =>
        string.IsNullOrEmpty(value) ? null : _protector.Encrypt(value.Trim());

    private static string NormalizeProvider(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (!ChannelEntity.Providers.Contains(v))
            throw new ValidationException(
                $"provider must be one of: {string.Join(", ", ChannelEntity.Providers)}", "invalid_provider");
        return v;
    }

    private static string? NormalizeMaxRole(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        if (!ChannelEntity.MaxRoles.Contains(v))
            throw new ValidationException(
                $"max_role must be one of: {string.Join(", ", ChannelEntity.MaxRoles)}", "invalid_max_role");
        return v;
    }

    // Per-provider required config. Teams is the one that genuinely cannot work
    // without it: app_id is both the audience the inbound token is validated
    // against and the client_id the reply is minted with.
    private static void RequireProviderConfig(string provider, IReadOnlyDictionary<string, string> config)
    {
        if (provider != ChannelEntity.ProviderTeams) return;
        if (!config.TryGetValue("app_id", out var appId) || string.IsNullOrWhiteSpace(appId))
            throw new ValidationException(
                "a Teams channel needs external_config.app_id (the Azure Bot's Microsoft App ID)",
                "teams_app_id_required");
    }

    // Only Teams uses app_token as a Relay connection string; Slack's is a Socket
    // Mode app-level token and has nothing in common with one.
    private static void ValidateTeamsRelayConnectionString(string provider, string? appToken)
    {
        if (provider != ChannelEntity.ProviderTeams) return;
        TeamsRelayConnectionString.Validate(appToken);
    }

    private async Task<ChannelEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.MessagingChannels.FirstOrDefaultAsync(
            c => c.MessagingChannelId == id && c.IsActive, ct);
        if (row is null) throw new NotFoundException("messaging channel not found");
        return row;
    }

    private MessagingChannelResponse ToResponse(ChannelEntity c)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return new MessagingChannelResponse
        {
            Id = c.MessagingChannelId,
            Provider = c.Provider,
            Name = c.Name,
            Slug = c.Slug,
            ExternalConfig = MessagingChannelConfig.External(c).ToDictionary(kv => kv.Key, kv => kv.Value),
            MaxRole = c.MaxRole,
            RequireLinkedUser = c.RequireLinkedUser,
            AllowedExternalIds = MessagingChannelConfig.AllowedExternalIds(c).ToList(),
            AllowUnsigned = c.AllowUnsigned,
            Enabled = c.Enabled,
            HasBotToken = c.BotTokenEncrypted is { Length: > 0 },
            HasSigningSecret = c.SigningSecretEncrypted is { Length: > 0 },
            HasAppToken = c.AppTokenEncrypted is { Length: > 0 },
            WebhookUrl = string.IsNullOrEmpty(baseUrl)
                ? string.Empty
                : $"{baseUrl}/api/messaging/webhooks/{c.Provider}/{c.MessagingChannelId}",
            LastDeliveryAt = c.LastDeliveryAt,
            LastDeliveryStatus = c.LastDeliveryStatus,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
        };
    }
}
