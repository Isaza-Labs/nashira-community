using System.Text.Json.Serialization;

namespace nashira_backend.Services.Git;

// GitHub REST operations that have no local-git equivalent: creating a remote
// repository and opening/merging pull requests. Everything else (clone, commit,
// push, diff) stays in IGitService against the working copy.
//
// Auth is a Credential row with auth_method "token" — the same PAT the git
// transport already uses — so there is one place to rotate the token.
public interface IGitHubService
{
    // Resolves the credential reference the agent was given — a uuid, or the name the
    // user actually said ("the github credential") — to the row id every request below
    // carries. Name matching is case-insensitive, exact first and substring as a
    // fallback; an ambiguous name is an error rather than a guess.
    Task<Guid> ResolveCredentialAsync(Guid? credentialId, string? credentialName, CancellationToken ct);

    Task<GitHubRepoResult> CreateRepoAsync(GitHubCreateRepoRequest req, CancellationToken ct);
    Task<GitHubPrResult> CreatePullRequestAsync(GitHubCreatePrRequest req, CancellationToken ct);
    Task<GitHubMergeResult> MergePullRequestAsync(GitHubMergePrRequest req, CancellationToken ct);
}

public sealed class GitHubCreateRepoRequest
{
    public Guid CredentialId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Private { get; set; } = true;
    public bool AutoInit { get; set; } = true;
    // Create under an organization instead of the token owner's account.
    public string? Organization { get; set; }
}

public sealed class GitHubCreatePrRequest
{
    public Guid CredentialId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Head { get; set; } = string.Empty;
    public string Base { get; set; } = string.Empty;
    public string? Body { get; set; }
    public bool Draft { get; set; }
}

public sealed class GitHubMergePrRequest
{
    public Guid CredentialId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public int Number { get; set; }
    // merge | squash | rebase
    public string Method { get; set; } = "merge";
    public string? CommitTitle { get; set; }
}

public sealed class GitHubRepoResult
{
    [JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
    [JsonPropertyName("clone_url")] public string CloneUrl { get; set; } = string.Empty;
    [JsonPropertyName("private")] public bool Private { get; set; }
    [JsonPropertyName("default_branch")] public string DefaultBranch { get; set; } = string.Empty;
}

public sealed class GitHubPrResult
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("draft")] public bool Draft { get; set; }
}

public sealed class GitHubMergeResult
{
    [JsonPropertyName("merged")] public bool Merged { get; set; }
    [JsonPropertyName("sha")] public string? Sha { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}
