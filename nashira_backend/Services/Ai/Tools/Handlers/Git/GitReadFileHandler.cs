using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitReadFileHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "path":{"type":"string","description":"File path within the repo"},
          "ref":{"type":"string","description":"Branch, tag, or commit sha (default branch if omitted)"}
        },"required":["repository_id","path"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitReadFileHandler(IGitService git) => _git = git;

    public string Name => "git_read_file";
    public string Description =>
        "Reads a file from a registered Git repository at a ref. Returns UTF-8 text, or base64 " +
        "content with is_binary=true for binary files.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            var path = GitToolSupport.Str(args, "path") ?? "";
            return GitToolSupport.Ok(await _git.ReadFileAsync(repoId, path, GitToolSupport.Str(args, "ref"), ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
