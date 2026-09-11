using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Git;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitListWebhooksHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "repository_id":{"type":"string","description":"Repository uuid from git_list_repositories"}
        },"required":["repository_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IGitWebhookService _webhooks;
    public GitListWebhooksHandler(IGitWebhookService webhooks) => _webhooks = webhooks;

    public string Name => "git_list_webhooks";
    public string Description =>
        "Lists the inbound webhooks on a repository: which workflow each one runs, which branches it "
        + "fires for, whether it pulls first, and how the last delivery went. Read this before saying a "
        + "push 'did not trigger anything' — a branch filter is the usual reason.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var repoId = GitToolSupport.RepoId(args);
            var hooks = await _webhooks.ListAsync(repoId, ct);
            return GitToolSupport.Ok(new
            {
                webhooks = hooks.Items.Select(w => new
                {
                    git_webhook_id = w.GitWebhookId,
                    name = w.Name,
                    provider = w.Provider,
                    ingest_path = w.IngestPath,
                    signature_header = w.SignatureHeader,
                    has_secret = w.HasSecret,
                    allow_unsigned = w.AllowUnsigned,
                    on_push_workflow_id = w.OnPushWorkflowId,
                    on_push_workflow_name = w.OnPushWorkflowName,
                    // Empty means every branch fires it — say so rather than showing
                    // an empty list the reader has to interpret.
                    on_push_branches = w.OnPushBranches.Count == 0 ? null : w.OnPushBranches,
                    fires_for = w.OnPushBranches.Count == 0 ? "every branch" : string.Join(", ", w.OnPushBranches),
                    auto_pull = w.AutoPull,
                    enabled = w.Enabled,
                    last_delivery_at = w.LastDeliveryAt,
                    last_delivery_status = w.LastDeliveryStatus,
                    delivery_count = w.DeliveryCount,
                }),
                count = hooks.Total,
            });
        }
        catch (DomainException ex)
        {
            return GitToolSupport.Err(ex.Message);
        }
    }
}
