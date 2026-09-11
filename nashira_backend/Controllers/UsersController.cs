using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.User;
using nashira_backend.Exceptions;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Common;
using nashira_backend.Services.Email;
using nashira_backend.Services.Identity;
using UserEntity = nashira_backend.Data.Models.User;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Admin-only CRUD for users. The password hash is never
// taken from the caller's claims — never from the body — so an admin cannot
// touch another tenant's users.
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class UsersController : ControllerBase
{
    private static readonly string[] AllowedRoles = ["admin", "operator", "viewer"];

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<UserEntity> _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly IUserCredentialsNotifier _credentials;

    public UsersController(
        AppDbContext db, IPasswordHasher<UserEntity> hasher, IPasswordPolicy policy,
        ICurrentUser user, IAuditLogger audit, IUserCredentialsNotifier credentials)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _user = user;
        _audit = audit;
        _credentials = credentials;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<UserResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.Users.AsNoTracking().Where(u => u.IsActive);
        var total = await q.CountAsync(ct);
        var users = await q.OrderBy(u => u.Username).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<UserResponse>
        {
            Items = users.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserResponse>> GetById(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);
        if (user is null) throw new NotFoundException("user not found");
        return ToResponse(user);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<UserResponse>> Post([FromBody] CreateUser dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Username)) throw new ValidationException("username is required");
        if (string.IsNullOrWhiteSpace(dto.Email)) throw new ValidationException("email is required");

        var role = (dto.Role ?? "viewer").ToLowerInvariant();
        if (!AllowedRoles.Contains(role))
            throw new ValidationException($"role must be one of: {string.Join(", ", AllowedRoles)}");

        var policy = _policy.Validate(dto.Password, dto.Username, dto.Email);
        if (!policy.IsValid) throw new ValidationException(policy.Error ?? "password rejected", "password_policy");

        if (await _db.Users.AnyAsync(u => u.Username == dto.Username, ct))
            throw new ConflictException("username already exists", "username_taken");

        var now = DateTime.UtcNow;
        var user = new UserEntity
        {
            UserId = Guid.NewGuid(),
            Username = dto.Username,
            Email = dto.Email,
            Role = role,
            PasswordChangedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = _hasher.HashPassword(user, dto.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("user", user.UserId, "create", after: Snapshot(user), ct: ct);

        // The new user gets their credentials by email; when that cannot happen the
        // admin gets told, in the same response, instead of assuming it did.
        var warning = await _credentials.SendCredentialsAsync(user.Email, user.Username, dto.Password, ct);

        var response = ToResponse(user);
        response.CredentialsEmailSent = warning is null;
        response.Warning = warning;
        return new CreatedAtActionResult(nameof(GetById), "Users", new { id = user.UserId }, response);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<UserResponse>> Update(Guid id, [FromBody] UpdateUser dto, CancellationToken ct)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);
        if (user is null) throw new NotFoundException("user not found");
        var before = Snapshot(user);

        if (dto.Email is not null) user.Email = dto.Email;
        if (dto.Role is not null)
        {
            var role = dto.Role.ToLowerInvariant();
            if (!AllowedRoles.Contains(role))
                throw new ValidationException($"role must be one of: {string.Join(", ", AllowedRoles)}");
            // Privilege changes on your own account need a second pair of hands: an
            // admin demoting themselves loses, in the same request, the very right
            // that authorized it. Only another admin may change an admin's role.
            if (id == _user.UserId && role != user.Role)
                throw new ValidationException("cannot change your own role", "self_demote");
            user.Role = role;
        }
        if (dto.IsActive is not null)
        {
            // Same reasoning as the self-delete guard below — deactivating is the
            // soft-delete this controller performs, arriving through another field.
            if (id == _user.UserId && !dto.IsActive.Value)
                throw new ValidationException("cannot deactivate your own user", "self_deactivate");
            user.IsActive = dto.IsActive.Value;
        }
        if (!string.IsNullOrEmpty(dto.Password))
        {
            var policy = _policy.Validate(dto.Password, user.Username, user.Email);
            if (!policy.IsValid) throw new ValidationException(policy.Error ?? "password rejected", "password_policy");
            user.PasswordHash = _hasher.HashPassword(user, dto.Password);
            user.PasswordChangedAt = DateTime.UtcNow;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // A role change is a privilege change, and a password reset by an admin is the
        // step before acting as someone else. Both belong in the trail; neither carries
        // the password itself, only whether one was set.
        await _audit.LogAsync("user", user.UserId, "update",
            before: before,
            after: Snapshot(user, passwordChanged: !string.IsNullOrEmpty(dto.Password)),
            ct: ct);

        return ToResponse(user);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<UserResponse>> Delete(Guid id, CancellationToken ct)
    {
        if (id == _user.UserId) throw new ValidationException("cannot delete your own user", "self_delete");

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive, ct);
        if (user is null) throw new NotFoundException("user not found");

        var before = Snapshot(user);
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("user", user.UserId, "delete", before: before, ct: ct);

        return ToResponse(user);
    }

    // Identity and privilege, never the credential. password_changed says an admin
    // reset it without recording what to.
    private static object Snapshot(UserEntity u, bool passwordChanged = false) => new
    {
        username = u.Username,
        email = u.Email,
        role = u.Role,
        is_active = u.IsActive,
        password_changed = passwordChanged,
    };

    private static UserResponse ToResponse(UserEntity u) => new()
    {
        UserId = u.UserId,
        Username = u.Username,
        Email = u.Email,
        Role = u.Role,
        IsActive = u.IsActive,
        Locked = u.LockedUntil is { } t && t > DateTime.UtcNow,
        ProfileId = u.ProfileId,
        PasswordChangedAt = u.PasswordChangedAt,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };
}
