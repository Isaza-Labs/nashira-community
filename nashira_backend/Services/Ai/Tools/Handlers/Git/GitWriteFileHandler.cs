using System.Text.Json;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitWriteFileHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "path":{"type":"string","description":"File path within the repo"},
          "content":{"type":"string","description":"Full new file contents (UTF-8)"},
          "commit_message":{"type":"string","description":"Commit message"},
          "branch":{"type":"string","description":"Branch to write on (checked out/created; current branch if omitted)"},
          "push":{"type":"boolean","default":false,"description":"Push to origin after committing"},
          "author_name":{"type":"string"},
          "author_email":{"type":"string"}
        },"required":["repository_id","path","content","commit_message"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitWriteFileHandler(IGitService git) => _git = git;

    public string Name => "git_write_file";
    public string Description =>
        "Writes a file into a registered Git repository and commits it (optionally pushing). " +
        "Overwrites the whole file with the provided content.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            var req = args.Deserialize<GitWriteFileRequest>() ?? new GitWriteFileRequest();
            return GitToolSupport.Ok(await _git.WriteFileAsync(repoId, req, ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
