using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitPullHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "branch":{"type":"string","description":"Branch to pull (default branch if omitted)"}
        },"required":["repository_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitPullHandler(IGitService git) => _git = git;

    public string Name => "git_pull";
    public string Description => "Fetches and merges (fast-forward) the latest commits for a branch of a registered Git repository.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            return GitToolSupport.Ok(await _git.PullAsync(repoId, GitToolSupport.Str(args, "branch"), ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
