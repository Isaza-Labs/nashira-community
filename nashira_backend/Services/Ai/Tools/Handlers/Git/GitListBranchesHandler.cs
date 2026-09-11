using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Lists local + remote branches and reports the current one. Read → autonomous:
// the agent needs to know what it can check out before proposing one.
public sealed class GitListBranchesHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"}
        },"required":["repository_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitListBranchesHandler(IGitService git) => _git = git;

    public string Name => "git_list_branches";
    public string Description =>
        "Lists the branches of a registered Git repository and reports which one is currently checked out.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return GitToolSupport.Ok(await _git.ListBranchesAsync(GitToolSupport.RepoId(args), ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
