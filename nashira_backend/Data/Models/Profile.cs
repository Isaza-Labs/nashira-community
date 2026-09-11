namespace nashira_backend.Data.Models;

// Agent persona applied to a user's conversations: display name, described
// skills, and a response style the agent adopts. Tenant-scoped; Name is unique
// within a company.
public class Profile : BaseModel
{
    public Guid ProfileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Skills { get; set; } = [];
    public string? ResponseStyle { get; set; }
    public int DisplayOrder { get; set; }
}
