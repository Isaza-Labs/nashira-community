using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Secret;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;
using SecretEntity = nashira_backend.Data.Models.Secret;
using Microsoft.AspNetCore.RateLimiting;

namespace nashira_backend.Controllers;

// CRUD for named secrets consumed by the REST spec executor, the integration layer
// and the agent's tools. Admin-only — a compromised operator account must not be
// able to exfiltrate or rotate production API keys.
//
// Plaintext values never leave this controller: list/get return only metadata and a
// `has_value` boolean. Admins rotate by writing a new value; there is no "reveal"
// endpoint on purpose.
[ApiController]
[Route("api/secrets")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class SecretsController : ControllerBase
{
    // Secrets are referenced as ${secret:secret:<name>:value} inside templates, so the
    // name must fit a URL/header-safe grammar: lowercase alphanumerics, hyphens and
    // underscores.
    private static readonly Regex NameRegex = new(
        "^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;
    private readonly IAuditLogger _audit;
    private readonly ITraceLogger _trace;

    public SecretsController(
        AppDbContext db, ICurrentUser user, ISecretProtector protector,
        IAuditLogger audit, ITraceLogger trace)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _audit = audit;
        _trace = trace;
    }

    // The projection is materialized before it is handed to the result on purpose.
    // A deferred Select is evaluated during serialization — after 200 and the headers
    // are already on the wire — so anything that throws there truncates the body with
    // no status to signal it: the client hangs, the log records a success, and the
    // admin page backed by this endpoint just never loads.
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SecretResponse>>> List(CancellationToken ct)
    {
        var rows = await _db.Secrets.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        return new OkObjectResult(rows.Select(ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SecretResponse>> Get(Guid id, CancellationToken ct)
    {
        var row = await _db.Secrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SecretId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("secret not found");
        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<SecretResponse>> Create(
        [FromBody] CreateSecretRequest request, CancellationToken ct)
    {
        Validate(request.Name, request.Value);
        var name = request.Name.Trim();

        // Deliberately not scoped to IsActive, and neither is the unique index: a
        // soft-deleted row keeps owning its name, so a re-create cannot silently
        // repoint every template that already names it at somebody else's value.
        if (await _db.Secrets.AnyAsync(s => s.Name == name, ct))
            throw new ConflictException(
                "a secret with that name already exists", code: "secret_name_taken");

        var now = DateTime.UtcNow;
        var row = new SecretEntity
        {
            SecretId = Guid.NewGuid(),
            Name = name,
            Description = request.Description?.Trim(),
            EncryptedValue = _protector.Encrypt(request.Value) ?? Array.Empty<byte>(),
            CreatedBy = _user.IsAuthenticated ? _user.Username : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Secrets.Add(row);
        await _db.SaveChangesAsync(ct);

        // Never put the value in the audit payload — that the row exists, and who made
        // it, is the whole question an append-only table can answer here.
        await _audit.LogAsync("secret", row.SecretId, "create",
            after: new { row.Name, row.Description }, ct: ct);
        _trace.Event(TraceEvent.CategorySystem, "secret.create",
            new { secret_id = row.SecretId, row.Name });

        return CreatedAtAction(nameof(Get), new { id = row.SecretId }, ToResponse(row));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<SecretResponse>> Update(
        Guid id, [FromBody] UpdateSecretRequest request, CancellationToken ct)
    {
        var row = await _db.Secrets
            .FirstOrDefaultAsync(s => s.SecretId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("secret not found");

        var before = new { row.Name, row.Description };

        if (request.Description is not null)
            row.Description = request.Description.Trim();

        // Null leaves the value alone — that is what keeps an "edit the description"
        // save from clobbering the ciphertext. An empty string is not a wipe either:
        // deleting the secret is how a value is removed, and a template that resolves
        // to "" is the failure this rejects.
        if (request.Value is not null)
        {
            if (string.IsNullOrEmpty(request.Value))
                throw new ValidationException(
                    "value cannot be empty — delete the secret to remove it",
                    code: "secret_value_empty");
            row.EncryptedValue = _protector.Encrypt(request.Value) ?? Array.Empty<byte>();
        }

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("secret", row.SecretId, "update",
            before: before,
            after: new { row.Name, row.Description, value_rotated = request.Value is not null },
            ct: ct);
        _trace.Event(TraceEvent.CategorySystem, "secret.update",
            new { secret_id = row.SecretId, row.Name, value_rotated = request.Value is not null });

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _db.Secrets
            .FirstOrDefaultAsync(s => s.SecretId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("secret not found");

        // Soft delete: keeps the audit references intact and the name reserved.
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Every reference to it stops resolving from here on, and this row is usually
        // the only explanation for the 401s that start afterwards.
        await _audit.LogAsync("secret", row.SecretId, "delete",
            before: new { row.Name }, ct: ct);
        _trace.Event(TraceEvent.CategorySystem, "secret.delete",
            new { secret_id = row.SecretId, row.Name });

        return new NoContentResult();
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static void Validate(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || !NameRegex.IsMatch(name.Trim()))
            throw new ValidationException(
                "name must be 2–64 chars, lowercase letters/digits/hyphens/underscores",
                code: "secret_invalid");
        if (string.IsNullOrEmpty(value))
            throw new ValidationException("value is required", code: "secret_invalid");
    }

    private static SecretResponse ToResponse(SecretEntity s) => new()
    {
        SecretId = s.SecretId,
        Name = s.Name,
        Description = s.Description,
        HasValue = s.EncryptedValue is { Length: > 0 },
        CreatedBy = s.CreatedBy,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
