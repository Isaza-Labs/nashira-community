using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using UserEntity = nashira_backend.Data.Models.User;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates a user by id. Admin. Password (if given) is policy-checked + hashed, never returned.
// Write → single_confirm. Mirrors UsersController.Update.
public sealed class UpdateUserHandler : IToolHandler
{
    private static readonly string[] AllowedRoles = ["admin", "operator", "viewer"];

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "user_id":{"type":"string","description":"User id from list_users"},
          "email":{"type":"string"},
          "role":{"type":"string","enum":["admin","operator","viewer"]},
          "is_active":{"type":"boolean"},
          "password":{"type":"string"}
        },"required":["user_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<UserEntity> _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly ICurrentUser _user;

    public UpdateUserHandler(AppDbContext db, IPasswordHasher<UserEntity> hasher, IPasswordPolicy policy, ICurrentUser user)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _user = user;
    }

    public string Name => "update_user";
    public string Description =>
        "Updates a user by user_id (admin): email, role, is_active, password. Only provided fields change; " +
        "a new password is policy-checked and hashed.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "user_id")?.Trim() is not { Length: > 0 } sid || !Guid.TryParse(sid, out var id))
            return Err("user_id (a uuid from list_users) is required");

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.UserId == id && u.IsActive, ct);
        if (user is null) return Err("user not found");

        if (Str(args, "email") is { } em) user.Email = em;
        if (Str(args, "role") is { } r)
        {
            var role = r.ToLowerInvariant();
            if (!AllowedRoles.Contains(role)) return Err($"role must be one of: {string.Join(", ", AllowedRoles)}");
            // Same rule as UsersController.Update: only another admin may change an
            // admin's role, so your own role is off limits from your own session.
            if (id == _user.UserId && role != user.Role) return Err("cannot change your own role");
            user.Role = role;
        }
        if (args.TryGetProperty("is_active", out var ia) && ia.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            // Deactivation is the soft-delete delete_user refuses on self.
            if (id == _user.UserId && !ia.GetBoolean()) return Err("cannot deactivate your own user");
            user.IsActive = ia.GetBoolean();
        }
        if (Str(args, "password") is { Length: > 0 } password)
        {
            var policy = _policy.Validate(password, user.Username, user.Email);
            if (!policy.IsValid) return Err(policy.Error ?? "password rejected");
            user.PasswordHash = _hasher.HashPassword(user, password);
            user.PasswordChangedAt = DateTime.UtcNow;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            user_id = user.UserId, username = user.Username, email = user.Email, role = user.Role,
            is_active = user.IsActive, updated = true,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
