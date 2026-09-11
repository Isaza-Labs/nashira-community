using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Merges a pull request. elevated_confirm: this lands code on a shared branch —
// it is the most consequential GitHub action the agent can take, and undoing it
// means a revert commit, not a rollback.
public sealed class GitHubMergePrHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "credential_id":{"type":"string","description":"credential_id of a credential with auth_method 'token' (from list_credentials). Pass this or credential_name."},
          "credential_name":{"type":"string","description":"Credential name as the user said it, resolved case-insensitively. Use when you have a name and no id."},
          "owner":{"type":"string","description":"Repository owner (user or org)"},
          "repo":{"type":"string","description":"Repository name"},
          "number":{"type":"integer","description":"Pull-request number"},
          "method":{"type":"string","enum":["merge","squash","rebase"],"default":"merge"},
          "commit_title":{"type":"string","description":"Override the merge commit title"}
        },"required":["owner","repo","number"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitHubService _github;
    public GitHubMergePrHandler(IGitHubService github) => _github = github;

    public string Name => "github_merge_pr";
    public string Description =>
        "Merges an open pull request on GitHub (merge, squash or rebase). This lands code on the " +
        "base branch — confirm the PR is reviewed and checks pass before calling it.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var (credRefId, credRefName) = GitHubToolSupport.CredentialRef(args);
            var credentialId = await _github.ResolveCredentialAsync(credRefId, credRefName, ct);
            return GitToolSupport.Ok(await _github.MergePullRequestAsync(new GitHubMergePrRequest
            {
                CredentialId = credentialId,
                Owner = GitToolSupport.Str(args, "owner") ?? string.Empty,
                Repo = GitToolSupport.Str(args, "repo") ?? string.Empty,
                Number = GitHubToolSupport.Int(args, "number"),
                Method = GitToolSupport.Str(args, "method") ?? "merge",
                CommitTitle = GitToolSupport.Str(args, "commit_title"),
            }, ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
