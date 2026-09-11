namespace nashira_backend.Data.Models;

// A visibility override for one navigation destination. Exactly one scope is set:
// Role for the shared role baseline, or UserId for a per-user exception.
// Missing rows inherit the normal role-based navigation.
public class NavigationPermission : BaseModel
{
    public Guid NavigationPermissionId { get; set; }
    public string? Role { get; set; }
    public Guid? UserId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public bool Visible { get; set; }
}
