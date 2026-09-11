namespace nashira_backend.Data.Models;

// Base for every persisted entity. IsActive drives the soft-delete pattern used
// across CRUD services; CreatedAt/UpdatedAt are stamped by the services.
public abstract class BaseModel
{
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
