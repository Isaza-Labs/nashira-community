using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Profile;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using ProfileEntity = nashira_backend.Data.Models.Profile;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Agent personas (profiles) + per-user assignment. Reads are open to any
// authenticated user; profile CRUD and cross-user assignment require Admin.
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class ProfilesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public ProfilesController(AppDbContext db, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<ProfileResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.Profiles.AsNoTracking().Where(p => p.IsActive);
        var total = await q.CountAsync(ct);
        var profiles = await q.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name)
            .Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<ProfileResponse>
        {
            Items = profiles.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProfileResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<ProfileResponse>> Post([FromBody] CreateProfile dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        var name = dto.Name.Trim();
        if (await _db.Profiles.AnyAsync(p => p.Name == name && p.IsActive, ct))
            throw new ConflictException("a profile with this name already exists", "profile_name_taken");

        var now = DateTime.UtcNow;
        var profile = new ProfileEntity
        {
            ProfileId = Guid.NewGuid(),
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? name : dto.DisplayName,
            Description = dto.Description,
            Skills = dto.Skills ?? [],
            ResponseStyle = dto.ResponseStyle,
            DisplayOrder = dto.DisplayOrder ?? 0,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Profiles.Add(profile);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("profile", profile.ProfileId, "create", after: Snapshot(profile), ct: ct);

        return new CreatedAtActionResult(nameof(GetById), "Profiles", new { id = profile.ProfileId }, ToResponse(profile));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<ProfileResponse>> Update(Guid id, [FromBody] UpdateProfile dto, CancellationToken ct)
    {
        var p = await Find(id, ct);
        var before = Snapshot(p);
        if (dto.DisplayName is not null) p.DisplayName = dto.DisplayName;
        if (dto.Description is not null) p.Description = dto.Description;
        if (dto.Skills is not null) p.Skills = dto.Skills;
        if (dto.ResponseStyle is not null) p.ResponseStyle = dto.ResponseStyle;
        if (dto.DisplayOrder is not null) p.DisplayOrder = dto.DisplayOrder.Value;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // A profile shapes how the agent answers for whoever it is assigned to, so a
        // change here is a change to what those users see it say.
        await _audit.LogAsync("profile", p.ProfileId, "update", before: before, after: Snapshot(p), ct: ct);

        return ToResponse(p);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<ProfileResponse>> Delete(Guid id, CancellationToken ct)
    {
        var p = await Find(id, ct);
        var before = Snapshot(p);
        p.IsActive = false;
        p.UpdatedAt = DateTime.UtcNow;
        // Detach the profile from any user still pointing at it.
        var assigned = await _db.Users
            .Where(u => u.ProfileId == id).ToListAsync(ct);
        foreach (var u in assigned) { u.ProfileId = null; u.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync(ct);

        // The detach count matters: deleting a profile silently changes the assistant
        // for everyone who had it.
        await _audit.LogAsync("profile", p.ProfileId, "delete",
            before: before, after: new { detached_users = assigned.Count }, ct: ct);

        return ToResponse(p);
    }

    private static object Snapshot(ProfileEntity p) => new
    {
        name = p.Name,
        display_name = p.DisplayName,
        description = p.Description,
        skills = p.Skills,
        response_style = p.ResponseStyle,
        display_order = p.DisplayOrder,
    };

    // ── user ↔ profile assignment ───────────────────────────────────
    [HttpGet("users/me/profile")]
    public Task<ActionResult<UserProfileResponse>> GetMine(CancellationToken ct)
        => GetAsync(_user.UserId, ct);

    // Admin read of someone else's assignment. The PUT below replaces the custom
    // text as well as the profile, so an admin UI must read the text first or it
    // wipes what the user wrote about themselves.
    [HttpGet("users/{userId:guid}/profile")]
    [Authorize(Policy = "Admin")]
    public Task<ActionResult<UserProfileResponse>> GetForUser(Guid userId, CancellationToken ct)
        => GetAsync(userId, ct);

    private async Task<ActionResult<UserProfileResponse>> GetAsync(Guid userId, CancellationToken ct)
    {
        var u = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.IsActive, ct);
        if (u is null) throw new NotFoundException("user not found");
        return new OkObjectResult(new UserProfileResponse { ProfileId = u.ProfileId, CustomProfileText = u.CustomProfileText });
    }

    [HttpPut("users/me/profile")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<UserProfileResponse>> SetMine([FromBody] AssignProfileRequest request, CancellationToken ct)
        => AssignAsync(_user.UserId, request, ct);

    [HttpPut("users/{userId:guid}/profile")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public Task<ActionResult<UserProfileResponse>> SetForUser(
        Guid userId, [FromBody] AssignProfileRequest request, CancellationToken ct)
        => AssignAsync(userId, request, ct);

    private async Task<ActionResult<UserProfileResponse>> AssignAsync(
        Guid userId, AssignProfileRequest request, CancellationToken ct)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == userId && u.IsActive, ct);
        if (user is null) throw new NotFoundException("user not found");

        if (request.ProfileId is { } pid)
        {
            var ok = await _db.Profiles.AnyAsync(
                p => p.ProfileId == pid && p.IsActive, ct);
            if (!ok) throw new ValidationException("profile_id does not exist");
            user.ProfileId = pid;
        }
        else
        {
            user.ProfileId = null;
        }
        var customText = string.IsNullOrWhiteSpace(request.CustomProfileText) ? null : request.CustomProfileText.Trim();
        if (customText is { Length: > UserProfileContext.MaxCustomTextChars })
            throw new ValidationException(
                $"custom_profile_text must be at most {UserProfileContext.MaxCustomTextChars} characters");
        user.CustomProfileText = customText;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new OkObjectResult(new UserProfileResponse { ProfileId = user.ProfileId, CustomProfileText = user.CustomProfileText });
    }

    private async Task<ProfileEntity> Find(Guid id, CancellationToken ct)
    {
        var p = await _db.Profiles
            .FirstOrDefaultAsync(x => x.ProfileId == id && x.IsActive, ct);
        if (p is null) throw new NotFoundException("profile not found");
        return p;
    }

    private static ProfileResponse ToResponse(ProfileEntity p) => new()
    {
        ProfileId = p.ProfileId,
        Name = p.Name,
        DisplayName = p.DisplayName,
        Description = p.Description,
        Skills = p.Skills,
        ResponseStyle = p.ResponseStyle,
        DisplayOrder = p.DisplayOrder,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
