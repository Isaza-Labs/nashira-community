using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Git;

public class CreateGitWebhook
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("on_push_workflow_id")] public Guid? OnPushWorkflowId { get; set; }
    [JsonPropertyName("on_push_branches")] public List<string>? OnPushBranches { get; set; }
    [JsonPropertyName("auto_pull")] public bool? AutoPull { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool? AllowUnsigned { get; set; }
}

public class UpdateGitWebhook
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("on_push_workflow_id")] public Guid? OnPushWorkflowId { get; set; }
    // Explicit null clears the workflow binding; omitted leaves it alone. A nullable
    // Guid cannot express that on its own, so the caller says so out loud.
    [JsonPropertyName("clear_on_push_workflow")] public bool? ClearOnPushWorkflow { get; set; }
    [JsonPropertyName("on_push_branches")] public List<string>? OnPushBranches { get; set; }
    [JsonPropertyName("auto_pull")] public bool? AutoPull { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool? AllowUnsigned { get; set; }
}

public class GitWebhookResponse
{
    [JsonPropertyName("git_webhook_id")] public Guid GitWebhookId { get; set; }
    [JsonPropertyName("git_repository_id")] public Guid GitRepositoryId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;

    // The public path segment and the path built from it. The origin is the caller's
    // to supply — the server does not know which hostname it is reachable on from the
    // internet, and guessing one into a field an operator pastes into GitHub would be
    // worse than leaving it to the page that already knows.
    [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
    [JsonPropertyName("ingest_path")] public string IngestPath { get; set; } = string.Empty;

    [JsonPropertyName("signature_header")] public string SignatureHeader { get; set; } = string.Empty;
    [JsonPropertyName("has_secret")] public bool HasSecret { get; set; }
    [JsonPropertyName("on_push_workflow_id")] public Guid? OnPushWorkflowId { get; set; }
    [JsonPropertyName("on_push_workflow_name")] public string? OnPushWorkflowName { get; set; }
    [JsonPropertyName("on_push_branches")] public List<string> OnPushBranches { get; set; } = [];
    [JsonPropertyName("auto_pull")] public bool AutoPull { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("allow_unsigned")] public bool AllowUnsigned { get; set; }
    [JsonPropertyName("last_delivery_at")] public DateTime? LastDeliveryAt { get; set; }
    [JsonPropertyName("last_delivery_status")] public string? LastDeliveryStatus { get; set; }
    [JsonPropertyName("delivery_count")] public int DeliveryCount { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    // Present exactly once, on create and on rotate. Nothing returns it afterwards.
    [JsonPropertyName("secret")] public string? Secret { get; set; }
}

public class GitWebhookDeliveryResponse
{
    [JsonPropertyName("git_webhook_delivery_id")] public Guid GitWebhookDeliveryId { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("commit_sha")] public string? CommitSha { get; set; }
    [JsonPropertyName("delivery_key")] public string? DeliveryKey { get; set; }
    [JsonPropertyName("job_id")] public Guid? JobId { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

// What the public ingest endpoint answers with.
public class GitWebhookIngestResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("job_id")] public Guid? JobId { get; set; }
    [JsonPropertyName("deduplicated")] public bool Deduplicated { get; set; }
}

// Test-fire result: what a delivery would do, without a delivery.
public class GitWebhookTestResponse
{
    [JsonPropertyName("would_dispatch")] public bool WouldDispatch { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("workflow_id")] public Guid? WorkflowId { get; set; }
    [JsonPropertyName("input_preview")] public JsonElement InputPreview { get; set; }
}
