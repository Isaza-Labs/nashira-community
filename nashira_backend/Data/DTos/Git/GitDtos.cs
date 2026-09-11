using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Git;

public class CreateGitRepository
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("default_branch")] public string DefaultBranch { get; set; } = "main";
    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class UpdateGitRepository
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class GitRepositoryResponse
{
    [JsonPropertyName("git_repository_id")] public Guid GitRepositoryId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("default_branch")] public string DefaultBranch { get; set; } = "main";
    [JsonPropertyName("auth_credential_id")] public Guid? AuthCredentialId { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("local_path")] public string? LocalPath { get; set; }
    [JsonPropertyName("last_fetched_at")] public DateTime? LastFetchedAt { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class GitFileEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "blob";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class GitListFilesResponse
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public List<GitFileEntry> Entries { get; set; } = [];
}

public class GitReadFileResponse
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("is_binary")] public bool IsBinary { get; set; }
}

public class GitWriteFileRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("commit_message")] public string CommitMessage { get; set; } = string.Empty;
    [JsonPropertyName("author_name")] public string? AuthorName { get; set; }
    [JsonPropertyName("author_email")] public string? AuthorEmail { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("push")] public bool Push { get; set; }
}

public class GitCommitRequest
{
    [JsonPropertyName("commit_message")] public string CommitMessage { get; set; } = string.Empty;
    [JsonPropertyName("author_name")] public string? AuthorName { get; set; }
    [JsonPropertyName("author_email")] public string? AuthorEmail { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
    [JsonPropertyName("push")] public bool Push { get; set; }
}

public class GitOpResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("commit_sha")] public string? CommitSha { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
}

public class GitBranchesResponse
{
    [JsonPropertyName("current")] public string Current { get; set; } = string.Empty;
    [JsonPropertyName("branches")] public List<string> Branches { get; set; } = [];
}

// Working-tree state. `clean` is the question the agent actually asks before
// committing; the per-path lists explain the answer.
public class GitStatusResponse
{
    [JsonPropertyName("branch")] public string Branch { get; set; } = string.Empty;
    [JsonPropertyName("clean")] public bool Clean { get; set; }
    [JsonPropertyName("staged")] public List<string> Staged { get; set; } = [];
    [JsonPropertyName("modified")] public List<string> Modified { get; set; } = [];
    [JsonPropertyName("untracked")] public List<string> Untracked { get; set; } = [];
    [JsonPropertyName("missing")] public List<string> Missing { get; set; } = [];
    // Ahead/behind vs the tracked upstream; null when the branch has no upstream.
    [JsonPropertyName("ahead")] public int? Ahead { get; set; }
    [JsonPropertyName("behind")] public int? Behind { get; set; }
}

public class GitDiffResponse
{
    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;
    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("patch")] public string Patch { get; set; } = string.Empty;
}
