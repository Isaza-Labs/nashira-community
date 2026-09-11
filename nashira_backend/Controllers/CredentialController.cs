using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Credential;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using CredentialEntity = nashira_backend.Data.Models.Credential;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;

namespace nashira_backend.Controllers;

// Admin-only credential CRUD. auth_method selects the material the row carries:
// password | key (SSH) | token (bearer/PAT) | api_key | oauth2 (client credentials).
// Per-method completeness is enforced by CredentialRules, shared with the agent
// tools. Secret bytes are encrypted at rest and never returned — only has_* flags
// plus the non-secret OAuth2 coordinates (client id / token URL / scopes).
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class CredentialController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public CredentialController(
        AppDbContext db, ISecretProtector crypto, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<CredentialResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.Credentials.AsNoTracking().Where(c => c.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(c => c.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<CredentialResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CredentialResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<CredentialResponse>> Post([FromBody] CreateCredential dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");

        if (await _db.Credentials.AnyAsync(c => c.Name == dto.Name.Trim() && c.IsActive, ct))
            throw new ConflictException("a credential with this name already exists", "credential_name_taken");

        var now = DateTime.UtcNow;
        var row = new CredentialEntity
        {
            CredentialId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Type = string.IsNullOrWhiteSpace(dto.Type) ? "ssh" : dto.Type.Trim(),
            Username = dto.Username,
            AuthMethod = CredentialRules.Normalize(dto.AuthMethod),
            EncryptedPassword = _crypto.Encrypt(dto.Password),
            EncryptedPrivateKey = _crypto.Encrypt(dto.PrivateKey),
            EncryptedKeyPassphrase = _crypto.Encrypt(dto.KeyPassphrase),
            EncryptedToken = _crypto.Encrypt(dto.Token),
            ApiKeyHeader = Trimmed(dto.ApiKeyHeader),
            ClientId = Trimmed(dto.ClientId),
            EncryptedClientSecret = _crypto.Encrypt(dto.ClientSecret),
            TokenUrl = Trimmed(dto.TokenUrl),
            Scopes = Trimmed(dto.Scopes),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (CredentialRules.MissingMaterial(row) is { } missing) throw new ValidationException(missing);

        _db.Credentials.Add(row);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("credential", row.CredentialId, "create",
            after: Snapshot(row), ct: ct);

        return new CreatedAtActionResult(nameof(GetById), "Credential", new { id = row.CredentialId }, ToResponse(row));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<CredentialResponse>> Update(Guid id, [FromBody] UpdateCredential dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        // Taken before the mutation: the trail's value is the difference, and `row` is
        // tracked, so a snapshot read afterwards would show the new state twice.
        var before = Snapshot(row);
        if (dto.Name is not null) row.Name = dto.Name.Trim();
        if (dto.Type is not null) row.Type = dto.Type.Trim();
        if (dto.Username is not null) row.Username = dto.Username;
        if (dto.Password is not null) row.EncryptedPassword = _crypto.Encrypt(dto.Password);
        if (dto.PrivateKey is not null) row.EncryptedPrivateKey = _crypto.Encrypt(dto.PrivateKey);
        if (dto.KeyPassphrase is not null) row.EncryptedKeyPassphrase = _crypto.Encrypt(dto.KeyPassphrase);
        if (dto.Token is not null) row.EncryptedToken = _crypto.Encrypt(dto.Token);
        if (dto.ApiKeyHeader is not null) row.ApiKeyHeader = Trimmed(dto.ApiKeyHeader);
        if (dto.ClientId is not null) row.ClientId = Trimmed(dto.ClientId);
        if (dto.ClientSecret is not null) row.EncryptedClientSecret = _crypto.Encrypt(dto.ClientSecret);
        if (dto.TokenUrl is not null) row.TokenUrl = Trimmed(dto.TokenUrl);
        if (dto.Scopes is not null) row.Scopes = Trimmed(dto.Scopes);
        if (dto.AuthMethod is not null) row.AuthMethod = CredentialRules.Normalize(dto.AuthMethod);

        // Validate the FINAL state — an update may switch auth_method, so the
        // stored material must satisfy whatever method the row ends up with.
        if (CredentialRules.MissingMaterial(row) is { } missing) throw new ValidationException(missing);

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("credential", row.CredentialId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<CredentialResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Deleting a credential silently breaks every device, integration and repo
        // pointing at it, so this row is often the only explanation for what starts
        // failing afterwards.
        await _audit.LogAsync("credential", row.CredentialId, "delete", before: before, ct: ct);

        return ToResponse(row);
    }

    // What goes in the trail: the shape of the credential, never the material. An
    // append-only table is the last place a token should end up, and `has_*` answers
    // the only question an auditor actually has about it.
    private static object Snapshot(CredentialEntity c) => new
    {
        name = c.Name,
        type = c.Type,
        auth_method = c.AuthMethod,
        username = c.Username,
        has_password = c.EncryptedPassword is not null,
        has_private_key = c.EncryptedPrivateKey is not null,
        has_token = c.EncryptedToken is not null,
        has_client_secret = c.EncryptedClientSecret is not null,
        api_key_header = c.ApiKeyHeader,
        client_id = c.ClientId,
        token_url = c.TokenUrl,
        scopes = c.Scopes,
    };

    private static string? Trimmed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private async Task<CredentialEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.Credentials
            .FirstOrDefaultAsync(c => c.CredentialId == id && c.IsActive, ct);
        if (row is null) throw new NotFoundException("credential not found");
        return row;
    }

    private static CredentialResponse ToResponse(CredentialEntity c) => new()
    {
        CredentialId = c.CredentialId,
        Name = c.Name,
        Type = c.Type,
        Username = c.Username,
        AuthMethod = string.IsNullOrWhiteSpace(c.AuthMethod) ? "password" : c.AuthMethod,
        HasPassword = c.EncryptedPassword is { Length: > 0 },
        HasPrivateKey = c.EncryptedPrivateKey is { Length: > 0 },
        HasToken = c.EncryptedToken is { Length: > 0 },
        HasClientSecret = c.EncryptedClientSecret is { Length: > 0 },
        ApiKeyHeader = c.ApiKeyHeader,
        ClientId = c.ClientId,
        TokenUrl = c.TokenUrl,
        Scopes = c.Scopes,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}
