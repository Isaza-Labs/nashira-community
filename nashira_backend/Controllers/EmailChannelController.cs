using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Email;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using ChannelEntity = nashira_backend.Data.Models.EmailChannel;
using Microsoft.AspNetCore.RateLimiting;

namespace nashira_backend.Controllers;

public class WriteEmailChannel
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("security")] public string? Security { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    // Write-only. Omit on update to keep the stored value.
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("clear_password")] public bool? ClearPassword { get; set; }
    [JsonPropertyName("from_address")] public string? FromAddress { get; set; }
    [JsonPropertyName("from_name")] public string? FromName { get; set; }
    [JsonPropertyName("default_recipients")] public string? DefaultRecipients { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    // Inbound (IMAP). Optional — a channel without imap_host is outbound-only.
    // On update, an explicitly empty imap_host clears the inbound configuration.
    [JsonPropertyName("imap_host")] public string? ImapHost { get; set; }
    [JsonPropertyName("imap_port")] public int? ImapPort { get; set; }
    [JsonPropertyName("imap_security")] public string? ImapSecurity { get; set; }
    // Blank = reuse the SMTP username/password for IMAP.
    [JsonPropertyName("imap_username")] public string? ImapUsername { get; set; }
    [JsonPropertyName("imap_password")] public string? ImapPassword { get; set; }
    [JsonPropertyName("clear_imap_password")] public bool? ClearImapPassword { get; set; }
}

public class EmailChannelResponse
{
    [JsonPropertyName("email_channel_id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("security")] public string Security { get; set; } = string.Empty;
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("has_password")] public bool HasPassword { get; set; }
    [JsonPropertyName("from_address")] public string FromAddress { get; set; } = string.Empty;
    [JsonPropertyName("from_name")] public string? FromName { get; set; }
    [JsonPropertyName("default_recipients")] public string? DefaultRecipients { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("imap_host")] public string? ImapHost { get; set; }
    [JsonPropertyName("imap_port")] public int ImapPort { get; set; }
    [JsonPropertyName("imap_security")] public string ImapSecurity { get; set; } = string.Empty;
    [JsonPropertyName("imap_username")] public string? ImapUsername { get; set; }
    [JsonPropertyName("has_imap_password")] public bool HasImapPassword { get; set; }
    [JsonPropertyName("imap_configured")] public bool ImapConfigured { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class TestEmailRequest
{
    // Falls back to the channel's default recipients when omitted.
    [JsonPropertyName("to")] public string? To { get; set; }
}

// Named SMTP relays. Admin throughout — a channel holds credentials, and even the
// non-secret fields describe the mail infrastructure.
[ApiController]
[Route("api/email/channels")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class EmailChannelController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;
    private readonly IEmailChannelSender _sender;
    private readonly IEmailChannelMailbox _mailbox;

    public EmailChannelController(
        AppDbContext db, ICurrentUser user, ISecretProtector protector, IEmailChannelSender sender,
        IEmailChannelMailbox mailbox)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _sender = sender;
        _mailbox = mailbox;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<EmailChannelResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.EmailChannels.AsNoTracking().Where(c => c.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<EmailChannelResponse>
        {
            Items = rows.Select(ToResponse).ToList(), Total = total, Limit = limit, Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EmailChannelResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<EmailChannelResponse>> Post(
        [FromBody] WriteEmailChannel dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        if (string.IsNullOrWhiteSpace(dto.Host)) throw new ValidationException("host is required");
        if (string.IsNullOrWhiteSpace(dto.FromAddress)) throw new ValidationException("from_address is required");
        var security = NormalizeSecurity(dto.Security);
        var port = RequirePort(dto.Port ?? 587);

        var name = dto.Name.Trim();
        if (await _db.EmailChannels.AnyAsync(c => c.Name == name && c.IsActive, ct))
            throw new ConflictException("an email channel with this name already exists", "email_channel_name_taken");

        var taken = new HashSet<string>(
            await _db.EmailChannels.Select(c => c.Slug).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var row = new ChannelEntity
        {
            EmailChannelId = Guid.NewGuid(),
            Name = name,
            Slug = Slug.Unique(name, taken.Contains),
            Description = dto.Description,
            Host = dto.Host.Trim(),
            Port = port,
            Security = security,
            Username = dto.Username?.Trim(),
            PasswordEncrypted = _protector.Encrypt(dto.Password),
            FromAddress = dto.FromAddress.Trim(),
            FromName = dto.FromName,
            DefaultRecipients = dto.DefaultRecipients,
            ImapHost = string.IsNullOrWhiteSpace(dto.ImapHost) ? null : dto.ImapHost.Trim(),
            ImapPort = RequirePort(dto.ImapPort ?? 993),
            ImapSecurity = NormalizeSecurity(dto.ImapSecurity ?? ChannelEntity.SecuritySsl),
            ImapUsername = dto.ImapUsername?.Trim(),
            ImapPasswordEncrypted = _protector.Encrypt(dto.ImapPassword),
            Enabled = dto.Enabled ?? true,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.EmailChannels.Add(row);
        await _db.SaveChangesAsync(ct);
        return new CreatedAtActionResult(nameof(GetById), "EmailChannel", new { id = row.EmailChannelId }, ToResponse(row));
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<EmailChannelResponse>> Update(
        Guid id, [FromBody] WriteEmailChannel dto, CancellationToken ct)
    {
        var row = await Find(id, ct);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.EmailChannels.AnyAsync(c => c.Name == name && c.IsActive && c.EmailChannelId != id, ct))
                throw new ConflictException("an email channel with this name already exists", "email_channel_name_taken");
            row.Name = name;
        }
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Host is not null && dto.Host.Trim().Length > 0) row.Host = dto.Host.Trim();
        if (dto.Port is not null) row.Port = RequirePort(dto.Port.Value);
        if (dto.Security is not null) row.Security = NormalizeSecurity(dto.Security);
        if (dto.Username is not null) row.Username = dto.Username.Trim();
        // Absent = unchanged; the API never returns the password so there is
        // nothing for a client to resubmit. clear_password is the explicit removal.
        if (dto.ClearPassword == true) row.PasswordEncrypted = null;
        else if (!string.IsNullOrEmpty(dto.Password)) row.PasswordEncrypted = _protector.Encrypt(dto.Password);
        // Empty string clears the inbound side (null = untouched, as everywhere else);
        // there is no separate flag because an outbound-only channel simply has no host.
        if (dto.ImapHost is not null)
            row.ImapHost = string.IsNullOrWhiteSpace(dto.ImapHost) ? null : dto.ImapHost.Trim();
        if (dto.ImapPort is not null) row.ImapPort = RequirePort(dto.ImapPort.Value);
        if (dto.ImapSecurity is not null) row.ImapSecurity = NormalizeSecurity(dto.ImapSecurity);
        if (dto.ImapUsername is not null) row.ImapUsername = dto.ImapUsername.Trim();
        if (dto.ClearImapPassword == true) row.ImapPasswordEncrypted = null;
        else if (!string.IsNullOrEmpty(dto.ImapPassword)) row.ImapPasswordEncrypted = _protector.Encrypt(dto.ImapPassword);
        if (dto.FromAddress is not null && dto.FromAddress.Trim().Length > 0) row.FromAddress = dto.FromAddress.Trim();
        if (dto.FromName is not null) row.FromName = dto.FromName;
        if (dto.DefaultRecipients is not null) row.DefaultRecipients = dto.DefaultRecipients;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<EmailChannelResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    // Sends a real email — the only honest test of an SMTP relay.
    [HttpPost("{id:guid}/test")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit] // diagnostic
    public async Task<ActionResult<object>> Test(
        Guid id, [FromBody] TestEmailRequest? req, CancellationToken ct)
    {
        var row = await Find(id, ct);

        var to = SplitRecipients(req?.To) is { Count: > 0 } explicitTo
            ? explicitTo
            : SplitRecipients(row.DefaultRecipients);
        if (to.Count == 0)
            throw new ValidationException("pass `to`, or set default_recipients on the channel");

        await _sender.SendAsync(
            row, to, $"Nashira test — channel '{row.Name}'",
            "This is a connectivity test from Nashira. If you are reading it, the channel works.", ct);

        return new OkObjectResult(new { sent = true, to });
    }

    // Connects and lists folders — the equivalent honest test for the IMAP side.
    [HttpPost("{id:guid}/test-imap")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [SkipAudit] // diagnostic
    public async Task<ActionResult<object>> TestImap(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var folders = await _mailbox.ListFoldersAsync(row, ct);
        var inbox = folders.FirstOrDefault(f => f.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase));
        return new OkObjectResult(new
        {
            ok = true,
            folders = folders.Count,
            inbox_messages = inbox?.Count ?? 0,
            inbox_unread = inbox?.Unread ?? 0,
        });
    }

    private static List<string> SplitRecipients(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string NormalizeSecurity(string? raw)
    {
        var v = (raw ?? ChannelEntity.SecurityStartTls).Trim().ToLowerInvariant();
        if (!ChannelEntity.SecurityModes.Contains(v))
            throw new ValidationException(
                $"security must be one of: {string.Join(", ", ChannelEntity.SecurityModes)}");
        return v;
    }

    private static int RequirePort(int port)
    {
        if (port is < 1 or > 65535) throw new ValidationException("port must be between 1 and 65535");
        return port;
    }

    private async Task<ChannelEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.EmailChannels.FirstOrDefaultAsync(c => c.EmailChannelId == id && c.IsActive, ct);
        if (row is null) throw new NotFoundException("email channel not found");
        return row;
    }

    private static EmailChannelResponse ToResponse(ChannelEntity c) => new()
    {
        Id = c.EmailChannelId,
        Name = c.Name,
        Slug = c.Slug,
        Description = c.Description,
        Host = c.Host,
        Port = c.Port,
        Security = c.Security,
        Username = c.Username,
        HasPassword = c.PasswordEncrypted is { Length: > 0 },
        FromAddress = c.FromAddress,
        FromName = c.FromName,
        DefaultRecipients = c.DefaultRecipients,
        Enabled = c.Enabled,
        ImapHost = c.ImapHost,
        ImapPort = c.ImapPort,
        ImapSecurity = c.ImapSecurity,
        ImapUsername = c.ImapUsername,
        HasImapPassword = c.ImapPasswordEncrypted is { Length: > 0 },
        ImapConfigured = c.ImapConfigured(),
        UpdatedAt = c.UpdatedAt,
    };
}
