using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Opens a pull request. Write → single_confirm: it is visible to the team and
// starts a review process, so it should not happen without the user saying so.
public sealed class GitHubCreatePrHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "credential_id":{"type":"string","description":"credential_id of a credential with auth_method 'token' (from list_credentials). Pass this or credential_name."},
          "credential_name":{"type":"string","description":"Credential name as the user said it, resolved case-insensitively. Use when you have a name and no id."},
          "owner":{"type":"string","description":"Repository owner (user or org)"},
          "repo":{"type":"string","description":"Repository name"},
          "title":{"type":"string"},
          "head":{"type":"string","description":"Branch containing the changes"},
          "base":{"type":"string","description":"Branch to merge into (e.g. main)"},
          "body":{"type":"string","description":"Pull-request description (markdown)"},
          "draft":{"type":"boolean","default":false}
        },"required":["owner","repo","title","head","base"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitHubService _github;
    public GitHubCreatePrHandler(IGitHubService github) => _github = github;

    public string Name => "github_create_pr";
    public string Description =>
        "Opens a pull request on GitHub from a head branch into a base branch. Push the branch " +
        "first (git_commit_push) so the head exists on the remote.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var (credRefId, credRefName) = GitHubToolSupport.CredentialRef(args);
            var credentialId = await _github.ResolveCredentialAsync(credRefId, credRefName, ct);
            return GitToolSupport.Ok(await _github.CreatePullRequestAsync(new GitHubCreatePrRequest
            {
                CredentialId = credentialId,
                Owner = GitToolSupport.Str(args, "owner") ?? string.Empty,
                Repo = GitToolSupport.Str(args, "repo") ?? string.Empty,
                Title = GitToolSupport.Str(args, "title") ?? string.Empty,
                Head = GitToolSupport.Str(args, "head") ?? string.Empty,
                Base = GitToolSupport.Str(args, "base") ?? string.Empty,
                Body = GitToolSupport.Str(args, "body"),
                Draft = GitHubToolSupport.Bool(args, "draft", false),
            }, ct));
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
