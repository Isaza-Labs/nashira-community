using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Workflow;

public class CreateWorkflow
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("nodes")] public JsonElement Nodes { get; set; }
    [JsonPropertyName("edges")] public JsonElement Edges { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
    // Optional link to the AIConversation this definition came out of, for clients
    // that build a workflow from a chat and then POST it themselves.
    [JsonPropertyName("conversation_id")] public Guid? ConversationId { get; set; }
}

// Import of a compiled workflow artifact (the YAML produced by GET /{id}/yaml).
// JSON is accepted too, since YAML is a superset of it.
public class ImportWorkflow
{
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    // Overrides the name in the document. Useful when importing next to a workflow
    // that already carries that name — names are not unique, so without this an
    // import produces two indistinguishable rows in the list.
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
}

public class UpdateWorkflow
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("nodes")] public JsonElement? Nodes { get; set; }
    [JsonPropertyName("edges")] public JsonElement? Edges { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; set; }
    [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
}

public class WorkflowSummary
{
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("environment")] public string Environment { get; set; } = string.Empty;
    [JsonPropertyName("schema_hash")] public string SchemaHash { get; set; } = string.Empty;
    [JsonPropertyName("last_simulation_id")] public Guid? LastSimulationId { get; set; }
    [JsonPropertyName("conversation_id")] public Guid? ConversationId { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class WorkflowResponse : WorkflowSummary
{
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "v1";
    [JsonPropertyName("nodes")] public JsonElement Nodes { get; set; }
    [JsonPropertyName("edges")] public JsonElement Edges { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; set; }
    [JsonPropertyName("promoted_from")] public Guid? PromotedFrom { get; set; }
    [JsonPropertyName("change_summary")] public string ChangeSummary { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }

    // Bundle import only, and null everywhere else so the shape every other caller
    // sees is unchanged.
    //
    // What the translation between instances cost: a network flag dropped, a
    // target_mode with no local equivalent, a snippet matched by name rather than by
    // slug. None of those are failures — they are decisions the importer made on the
    // operator's behalf, and leaving them silent means the operator learns about them
    // from a run instead of from the import.
    [JsonPropertyName("import_notes")] public List<string>? ImportNotes { get; set; }

    // The snippets the bundle brought that this instance did not already have. Named
    // so a reviewer can open them before the workflow is promoted — they arrived
    // unverified, because this instance has neither reviewed nor run them.
    [JsonPropertyName("created_snippets")] public List<ImportedSnippet>? CreatedSnippets { get; set; }

    // The sub-workflows a bundle carried that were created here (not the ones reused
    // because an identical local graph already existed under the same name). Bundle
    // imports only.
    [JsonPropertyName("created_workflows")] public List<ImportedWorkflow>? CreatedWorkflows { get; set; }

    // The triggers a bundle carried, created DISABLED with a fresh secret and no
    // targets. Bundle imports only; the notes say what to set before enabling each.
    [JsonPropertyName("created_triggers")] public List<ImportedTrigger>? CreatedTriggers { get; set; }
}

public class ImportedWorkflow
{
    [JsonPropertyName("workflow_id")] public Guid WorkflowId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public class ImportedTrigger
{
    [JsonPropertyName("workflow_trigger_id")] public Guid WorkflowTriggerId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("route")] public string? Route { get; set; }
}

public class ImportedSnippet
{
    [JsonPropertyName("snippet_id")] public Guid SnippetId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
}
