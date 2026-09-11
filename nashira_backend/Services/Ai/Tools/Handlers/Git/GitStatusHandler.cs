using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Working-tree status. Read → autonomous, and it is the tool that keeps the
// agent from committing blind: git_commit_push stages everything by default, so
// without a status check it cannot tell what it is about to include.
public sealed class GitStatusHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"}
        },"required":["repository_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitStatusHandler(IGitService git) => _git = git;

    public string Name => "git_status";
    public string Description =>
        "Reports the working-tree state of a registered Git repository: current branch, whether it " +
        "is clean, the staged/modified/untracked/deleted paths, and how far ahead or behind the " +
        "upstream it is. Check this before committing so you know what will be included.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return GitToolSupport.Ok(await _git.StatusAsync(GitToolSupport.RepoId(args), ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
