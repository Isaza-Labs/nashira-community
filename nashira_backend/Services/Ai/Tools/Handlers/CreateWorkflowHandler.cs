using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Creates a workflow in the draft environment: nodes/edges are validated against
// workflow.v1 + required acyclic and the canonical SchemaHash is recomputed. Write → single_confirm.
public sealed class CreateWorkflowHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Workflow name"},
          "description":{"type":"string","description":"Optional description"},
          "nodes":{"type":"array","items":{"type":"object"},"description":"workflow.v1 nodes (JSON array)"},
          "edges":{"type":"array","items":{"type":"object"},"description":"workflow.v1 edges (JSON array)"},
          "input_schema":{"type":"object","description":"Optional input JSON schema object"},
          "metadata":{"type":"object","description":"Optional metadata object"},
          "change_summary":{"type":"string","description":"Optional change summary"}
        },"required":["name","nodes","edges"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly WorkflowValidator _validator;
    private readonly WorkflowReferenceChecker _references;
    private readonly AgentTurnScope _turn;

    public CreateWorkflowHandler(
        AppDbContext db, ICurrentUser user, WorkflowValidator validator,
        WorkflowReferenceChecker references, AgentTurnScope turn)
    {
        _db = db;
        _user = user;
        _validator = validator;
        _references = references;
        _turn = turn;
    }

    public string Name => "create_workflow";
    public string Description =>
        "Creates a new workflow in the draft environment from workflow.v1 nodes/edges (both JSON arrays). " +
        "The definition is validated before it is stored: workflow.v1 schema, acyclic, AND every " +
        "node's snippet_id must resolve — a snippet UUID that exists, or one of __start__, __end__, " +
        "subflow. Invented placeholders are rejected here rather than silently doing nothing at run " +
        "time. Returns the new workflow id.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");

        if (!args.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return Err("nodes must be an array");
        if (!args.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            return Err("edges must be an array");

        string hash;
        try
        {
            hash = _validator.ValidateAndHash(nodes, edges);
        }
        catch (DomainException ex)
        {
            return Err(ex.Message);
        }

        // Shape and acyclicity say nothing about whether the nodes point at real
        // snippets, and this is the tool that stored one referencing `__ping__`.
        try
        {
            await _references.EnsureResolvableAsync(nodes, ct);
        }
        catch (DomainException ex)
        {
            return Err(ex.Message);
        }

        var now = DateTime.UtcNow;
        var row = new WorkflowEntity
        {
            WorkflowId = Guid.NewGuid(),
            Name = name,
            Description = Str(args, "description"),
            Version = 1,
            SchemaVersion = "v1",
            NodesJson = nodes.GetRawText(),
            EdgesJson = edges.GetRawText(),
            InputSchemaJson = args.TryGetProperty("input_schema", out var isch) && isch.ValueKind == JsonValueKind.Object ? isch.GetRawText() : null,
            MetadataJson = args.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object ? md.GetRawText() : null,
            Environment = WorkflowEntity.EnvDraft,
            SchemaHash = hash,
            ChangeSummary = Str(args, "change_summary") ?? string.Empty,
            CreatedByUserId = _user.IsAuthenticated ? _user.UserId : (Guid?)null,
            // The chat turn this workflow was authored in, so the row points back at
            // the conversation that produced it.
            ConversationId = _turn.ConversationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workflows.Add(row);
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = row.WorkflowId,
            name = row.Name,
            environment = row.Environment,
            version = row.Version,
            schema_hash = row.SchemaHash,
            conversation_id = row.ConversationId,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
