namespace nashira_backend.Data.Models;

// Per-user, per-domain tool grant. Read/write/execute flags gate what the agent
// may do on a given domain (netbox, servicenow, awx, device, ...) on behalf of
// the user. Unique per (UserId, ToolDomain).
public class UserToolPermission : BaseModel
{
    public Guid UserToolPermissionId { get; set; }
    public Guid UserId { get; set; }
    public string ToolDomain { get; set; } = string.Empty;
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanExecute { get; set; }
}
