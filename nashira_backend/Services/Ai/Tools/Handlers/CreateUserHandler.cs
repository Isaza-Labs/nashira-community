using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Email;
using nashira_backend.Services.Identity;
using UserEntity = nashira_backend.Data.Models.User;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates a user in the caller's company. Admin. Password is policy-checked + hashed, never
// returned. Write → single_confirm. Mirrors UsersController.Post.
public sealed class CreateUserHandler : IToolHandler
{
    private static readonly string[] AllowedRoles = ["admin", "operator", "viewer"];

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "username":{"type":"string"},
          "email":{"type":"string"},
          "role":{"type":"string","enum":["admin","operator","viewer"],"description":"Default viewer"},
          "password":{"type":"string"}
        },"required":["username","email","password"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<UserEntity> _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly ICurrentUser _user;
    private readonly IUserCredentialsNotifier _credentials;

    public CreateUserHandler(
        AppDbContext db, IPasswordHasher<UserEntity> hasher, IPasswordPolicy policy,
        ICurrentUser user, IUserCredentialsNotifier credentials)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _user = user;
        _credentials = credentials;
    }

    public string Name => "create_user";
    public string Description =>
        "Creates a user in your company (admin): username, email, role (admin/operator/viewer), password. " +
        "The password is validated against policy and hashed; it is never returned. The new user is emailed " +
        "their credentials; when the email service is not configured the result carries a warning instead.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var username = Str(args, "username");
        if (string.IsNullOrWhiteSpace(username)) return Err("username is required");
        var email = Str(args, "email");
        if (string.IsNullOrWhiteSpace(email)) return Err("email is required");

        var role = (Str(args, "role") ?? "viewer").ToLowerInvariant();
        if (!AllowedRoles.Contains(role)) return Err($"role must be one of: {string.Join(", ", AllowedRoles)}");

        var password = Str(args, "password") ?? string.Empty;
        var policy = _policy.Validate(password, username, email);
        if (!policy.IsValid) return Err(policy.Error ?? "password rejected");

        if (await _db.Users.AnyAsync(u => u.Username == username, ct))
            return Err("username already exists in this company");

        var now = DateTime.UtcNow;
        var user = new UserEntity
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Email = email,
            Role = role,
            PasswordChangedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = _hasher.HashPassword(user, password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        // Same contract as UsersController.Post: the user is emailed their temporary
        // credentials, and when that cannot happen the admin is told in the result.
        var warning = await _credentials.SendCredentialsAsync(user.Email, user.Username, password, ct);

        return JsonSerializer.SerializeToElement(new
        {
            user_id = user.UserId, username = user.Username, email = user.Email, role = user.Role,
            credentials_email_sent = warning is null,
            warning,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
