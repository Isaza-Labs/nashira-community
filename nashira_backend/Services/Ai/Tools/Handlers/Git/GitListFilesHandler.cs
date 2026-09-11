using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitListFilesHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "path":{"type":"string","description":"Directory within the repo (root if omitted)"},
          "ref":{"type":"string","description":"Branch, tag, or commit sha (default branch if omitted)"}
        },"required":["repository_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitListFilesHandler(IGitService git) => _git = git;

    public string Name => "git_list_files";
    public string Description => "Lists files and directories at a path and ref in a registered Git repository.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            return GitToolSupport.Ok(await _git.ListFilesAsync(repoId, GitToolSupport.Str(args, "path"), GitToolSupport.Str(args, "ref"), ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
