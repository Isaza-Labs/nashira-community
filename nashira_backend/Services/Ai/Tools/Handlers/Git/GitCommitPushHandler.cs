using System.Text.Json;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitCommitPushHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "commit_message":{"type":"string","description":"Commit message"},
          "paths":{"type":"array","items":{"type":"string"},"description":"Paths to stage (all changes if omitted)"},
          "push":{"type":"boolean","default":false,"description":"Push to origin after committing"},
          "author_name":{"type":"string"},
          "author_email":{"type":"string"}
        },"required":["repository_id","commit_message"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitCommitPushHandler(IGitService git) => _git = git;

    public string Name => "git_commit_push";
    public string Description =>
        "Stages changes in a registered Git repository's working copy, commits them, and " +
        "optionally pushes to origin. Stages all changes unless specific paths are given.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            var req = args.Deserialize<GitCommitRequest>() ?? new GitCommitRequest();
            return GitToolSupport.Ok(await _git.CommitAsync(repoId, req, ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
