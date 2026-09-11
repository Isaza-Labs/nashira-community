using System.Text.Json;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Registers an inbound webhook on a repository. Write → single_confirm.
//
// The half of the setup this tool cannot do is the half that matters: somebody has to
// paste the URL and the secret into GitHub. The result is shaped so the agent has
// everything that step needs — path, header name, secret — and the description says
// the secret is shown once, because it is.
public sealed class GitCreateWebhookHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository uuid from git_list_repositories"},
          "name":{"type":"string","description":"Name for this webhook, unique within the repository"},
          "provider":{"type":"string","enum":["github","gitlab","generic"],"default":"github","description":"Decides which header the sender signs with. Not editable afterwards."},
          "on_push_workflow_id":{"type":"string","description":"Workflow to run on a matching push. Omit to only pull."},
          "on_push_branches":{"type":"array","items":{"type":"string"},"description":"Branch names that fire it, e.g. [\"main\"]. Omit or leave empty for every branch."},
          "auto_pull":{"type":"boolean","default":true,"description":"Pull the working copy before the run, so it sees the pushed commit"},
          "enabled":{"type":"boolean","default":true}
        },"required":["repository_id","name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitWebhookService _webhooks;
    public GitCreateWebhookHandler(IGitWebhookService webhooks) => _webhooks = webhooks;

    public string Name => "git_create_webhook";
    public string Description =>
        "Registers an inbound webhook so a push to a repository pulls the working copy and optionally "
        + "runs a workflow. Returns the path to paste into the provider, the header it must sign with, "
        + "and the shared secret — the secret is returned ONCE and nothing can read it back, so give it "
        + "to the user in the same reply. Creating it here does not configure anything on GitHub; the "
        + "user still adds the URL and secret on the provider side.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            var created = await _webhooks.CreateAsync(repoId, new CreateGitWebhook
            {
                Name = GitToolSupport.Str(args, "name") ?? string.Empty,
                Provider = GitToolSupport.Str(args, "provider"),
                OnPushWorkflowId = Guid.TryParse(GitToolSupport.Str(args, "on_push_workflow_id"), out var wf)
                    ? wf
                    : null,
                OnPushBranches = Branches(args),
                AutoPull = GitHubToolSupport.Bool(args, "auto_pull", true),
                Enabled = GitHubToolSupport.Bool(args, "enabled", true),
            }, ct);

            return GitToolSupport.Ok(new
            {
                git_webhook_id = created.GitWebhookId,
                name = created.Name,
                provider = created.Provider,
                ingest_path = created.IngestPath,
                signature_header = created.SignatureHeader,
                secret = created.Secret,
                on_push_workflow_id = created.OnPushWorkflowId,
                fires_for = created.OnPushBranches.Count == 0
                    ? "every branch"
                    : string.Join(", ", created.OnPushBranches),
                auto_pull = created.AutoPull,
                enabled = created.Enabled,
                next_step = "In the provider's repository settings add a webhook whose URL is this "
                    + "server's public origin + ingest_path, content type application/json, with the "
                    + "secret above. The secret cannot be retrieved later — only rotated.",
            });
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }

    private static List<string>? Branches(JsonElement args)
    {
        if (!args.TryGetProperty("on_push_branches", out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }
}
