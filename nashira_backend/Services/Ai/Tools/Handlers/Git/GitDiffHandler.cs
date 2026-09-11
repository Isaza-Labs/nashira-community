using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitDiffHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "from":{"type":"string","description":"Base ref (default HEAD)"},
          "to":{"type":"string","description":"Target ref; omit both from/to to diff HEAD vs working tree"},
          "path":{"type":"string","description":"Limit the diff to this path"}
        },"required":["repository_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitDiffHandler(IGitService git) => _git = git;

    public string Name => "git_diff";
    public string Description =>
        "Returns a unified diff for a registered Git repository. With no from/to it shows HEAD vs " +
        "the working tree (what is about to be committed).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            return GitToolSupport.Ok(await _git.DiffAsync(
                repoId, GitToolSupport.Str(args, "from"), GitToolSupport.Str(args, "to"), GitToolSupport.Str(args, "path"), ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
