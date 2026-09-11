using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitListRepositoriesHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":50}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitListRepositoriesHandler(IGitService git) => _git = git;

    public string Name => "git_list_repositories";
    public string Description =>
        "Lists the Git repositories registered for this tenant (id, name, url, default branch). " +
        "Use the returned id with the other git tools.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return GitToolSupport.Ok(await _git.ListAsync(GitToolSupport.Int(args, "limit", 50), 0, ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
