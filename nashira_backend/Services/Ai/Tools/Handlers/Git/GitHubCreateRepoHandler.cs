using System.Text.Json;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Creates a repository on GitHub and — unless asked not to — registers it here in the
// same call. Write → single_confirm: it creates something outside this system that the
// platform cannot roll back.
//
// The registration is part of this tool on purpose. There is no other way for the agent
// to turn a clone URL into the repository_id that git_write_file / git_pull need, so
// creating without registering left the natural "create a repo and put a file in it"
// request dead one step in.
public sealed class GitHubCreateRepoHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "credential_id":{"type":"string","description":"credential_id of a credential with auth_method 'token' (from list_credentials). Pass this or credential_name."},
          "credential_name":{"type":"string","description":"Credential name as the user said it, resolved case-insensitively. Use when you have a name and no id."},
          "name":{"type":"string","description":"Repository name"},
          "description":{"type":"string"},
          "private":{"type":"boolean","default":true},
          "auto_init":{"type":"boolean","default":true,"description":"Create an initial commit with a README, so the repo has a default branch immediately"},
          "organization":{"type":"string","description":"Create under this org instead of the token owner's account"},
          "register":{"type":"boolean","default":true,"description":"Register the new repo here as well, so the git_* tools can operate on it"},
          "register_name":{"type":"string","description":"Name for the local registration. Defaults to the GitHub repository name."}
        },"required":["name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitHubService _github;
    private readonly IGitService _git;
    private readonly ILogger<GitHubCreateRepoHandler> _logger;

    public GitHubCreateRepoHandler(IGitHubService github, IGitService git, ILogger<GitHubCreateRepoHandler> logger)
    {
        _github = github;
        _git = git;
        _logger = logger;
    }

    public string Name => "github_create_repo";
    public string Description =>
        "Creates a repository on GitHub using a stored token credential, and registers it here so the " +
        "git_* tools can use it right away. Identify the credential with credential_id (from " +
        "list_credentials) or credential_name. Returns the GitHub URLs and the repository_id you pass " +
        "to git_write_file next.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var (credRefId, credRefName) = GitHubToolSupport.CredentialRef(args);
            var credentialId = await _github.ResolveCredentialAsync(credRefId, credRefName, ct);

            var repo = await _github.CreateRepoAsync(new GitHubCreateRepoRequest
            {
                CredentialId = credentialId,
                Name = GitToolSupport.Str(args, "name") ?? string.Empty,
                Description = GitToolSupport.Str(args, "description"),
                Private = GitHubToolSupport.Bool(args, "private", true),
                AutoInit = GitHubToolSupport.Bool(args, "auto_init", true),
                Organization = GitToolSupport.Str(args, "organization"),
            }, ct);

            var register = GitHubToolSupport.Bool(args, "register", true);
            Guid? repositoryId = null;
            string? registerError = null;

            if (register && !string.IsNullOrWhiteSpace(repo.CloneUrl))
            {
                try
                {
                    var registered = await _git.CreateAsync(new CreateGitRepository
                    {
                        Name = GitToolSupport.Str(args, "register_name")?.Trim() is { Length: > 0 } rn
                            ? rn
                            : GitToolSupport.Str(args, "name") ?? repo.FullName,
                        Url = repo.CloneUrl,
                        DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch,
                        AuthCredentialId = credentialId,
                        Description = GitToolSupport.Str(args, "description"),
                    }, ct);
                    repositoryId = registered.GitRepositoryId;
                }
                catch (DomainException ex)
                {
                    // The GitHub repository exists at this point; failing the whole call
                    // would report a rollback that did not happen. Surface the reason and
                    // let the user register it by hand.
                    registerError = ex.Message;
                    _logger.LogWarning("github.create_repo.register_failed full_name={FullName} reason={Reason}",
                        repo.FullName, ex.Message);
                }
            }

            return GitToolSupport.Ok(new
            {
                full_name = repo.FullName,
                html_url = repo.HtmlUrl,
                clone_url = repo.CloneUrl,
                @private = repo.Private,
                default_branch = repo.DefaultBranch,
                registered = repositoryId is not null,
                repository_id = repositoryId,
                register_error = registerError,
            });
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
