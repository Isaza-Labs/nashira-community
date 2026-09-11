using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Identity;
using UserEntity = nashira_backend.Data.Models.User;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Assigns an assistant profile to a user (or clears it). Mirrors the cross-user
// assignment endpoint. Write → single_confirm.
public sealed class AssignProfileHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "user_id":{"type":"string","description":"Target user id (or identify by username)"},
          "username":{"type":"string","description":"Identify the target user by username if user_id is omitted"},
          "profile_id":{"type":"string","description":"Profile id to assign (or identify by profile_name). Omit both to clear the user's profile."},
          "profile_name":{"type":"string","description":"Identify the profile to assign by name if profile_id is omitted"},
          "custom_profile_text":{"type":"string","description":"Free text appended to the persona (max 500 chars). Replaces the user's existing value; omit to clear it."}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public AssignProfileHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "assign_profile";
    public string Description =>
        "Assigns an assistant profile to a user (identified by user_id or username). Identify the profile by " +
        "profile_id or profile_name; omit both to clear the assignment. custom_profile_text replaces the user's " +
        "existing custom text (omit to clear it).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var user = await ResolveUserAsync(args, ct);
        if (user is null) return Err("user not found (pass a valid user_id or username)");

        Guid? profileId = null;
        if (Str(args, "profile_id")?.Trim() is { Length: > 0 } pid)
        {
            if (!Guid.TryParse(pid, out var id)) return Err("profile_id must be a valid uuid (or use profile_name)");
            var exists = await _db.Profiles.AnyAsync(
                p => p.ProfileId == id && p.IsActive, ct);
            if (!exists) return Err("profile_id does not exist");
            profileId = id;
        }
        else if (Str(args, "profile_name")?.Trim() is { Length: > 0 } pname)
        {
            var match = await _db.Profiles.FirstOrDefaultAsync(
                p => p.Name == pname && p.IsActive, ct);
            if (match is null) return Err("profile not found (pass a valid profile_id or profile_name)");
            profileId = match.ProfileId;
        }

        var customText = Str(args, "custom_profile_text");
        customText = string.IsNullOrWhiteSpace(customText) ? null : customText.Trim();
        if (customText is { Length: > UserProfileContext.MaxCustomTextChars })
            return Err($"custom_profile_text must be at most {UserProfileContext.MaxCustomTextChars} characters");

        user.ProfileId = profileId;
        user.CustomProfileText = customText;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            user_id = user.UserId,
            profile_id = user.ProfileId,
            custom_profile_text = user.CustomProfileText,
            assigned = user.ProfileId is not null,
        });
    }

    private async Task<UserEntity?> ResolveUserAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "user_id")?.Trim() is { Length: > 0 } uid && Guid.TryParse(uid, out var id))
            return await _db.Users.FirstOrDefaultAsync(
                u => u.UserId == id && u.IsActive, ct);
        if (Str(args, "username")?.Trim() is { Length: > 0 } username)
            return await _db.Users.FirstOrDefaultAsync(
                u => u.Username == username && u.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
