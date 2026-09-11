using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Switches the working tree to another branch (creating it locally from origin
// when it only exists remotely). Write → single_confirm: it changes what every
// later read/write tool in the turn sees, so it should not happen silently.
public sealed class GitCheckoutHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository id from git_list_repositories"},
          "branch":{"type":"string","description":"Branch name to check out (from git_list_branches)"}
        },"required":["repository_id","branch"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitService _git;
    public GitCheckoutHandler(IGitService git) => _git = git;

    public string Name => "git_checkout";
    public string Description =>
        "Checks out a branch in a registered Git repository. Subsequent file reads/writes and " +
        "commits operate on this branch.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var branch = GitToolSupport.Str(args, "branch")?.Trim();
            if (string.IsNullOrWhiteSpace(branch)) return GitToolSupport.Err("branch is required");

            return GitToolSupport.Ok(await _git.CheckoutAsync(GitToolSupport.RepoId(args), branch, ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
