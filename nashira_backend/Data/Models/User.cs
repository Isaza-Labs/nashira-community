namespace nashira_backend.Data.Models;

// Auth principal. Inherits BaseModel: IsActive for
// soft-disable, plus CreatedAt/UpdatedAt. Auth-specific fields (lockout, MFA,
// password rotation) live here.
public class User : BaseModel
{
    public Guid UserId { get; set; }

    // Login handle. Unique within a company (composite index in AppDbContext).
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    // Hashed by Microsoft.AspNetCore.Identity.PasswordHasher<User>. Never plaintext.
    public string PasswordHash { get; set; } = string.Empty;

    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;

    // One of "admin" | "operator" | "viewer".
    public string Role { get; set; } = "viewer";

    // Lockout tracking. Login resets to 0 on success.
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }

    // Optional agent persona assignment + per-user free-text appended to it.
    public Guid? ProfileId { get; set; }
    public string? CustomProfileText { get; set; }
}
