namespace nashira_backend.Data.Models;

// A registered Git repository. nashira keeps a local working copy per repo under
// {Git:Root}/{GitRepositoryId}. Auth reuses the Credential store
// (a Credential with Type="git_token": PAT in EncryptedPassword, Username optional).
// HTTPS+token only in v1 (SSH-key transport is deferred). Tenant-scoped.
public class GitRepository : BaseModel
{
    public Guid GitRepositoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public Guid? AuthCredentialId { get; set; }
    public string? LocalPath { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public string? Description { get; set; }
}
