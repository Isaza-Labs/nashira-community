using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates a workflow (resolved by id or name). Only draft workflows are editable; a
// structural edit re-validates nodes/edges against workflow.v1 and recomputes the
// SchemaHash. Only provided fields change; use new_name to rename. Write → single_confirm.
public sealed class UpdateWorkflowHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","description":"Workflow id (or identify by name)"},
          "name":{"type":"string","description":"Identify the workflow by name if workflow_id is omitted"},
          "new_name":{"type":"string","description":"Rename the workflow"},
          "description":{"type":"string"},
          "nodes":{"type":"array","items":{"type":"object"},"description":"Replacement workflow.v1 nodes (JSON array)"},
          "edges":{"type":"array","items":{"type":"object"},"description":"Replacement workflow.v1 edges (JSON array)"},
          "input_schema":{"type":"object"},
          "metadata":{"type":"object"},
          "change_summary":{"type":"string"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly WorkflowValidator _validator;
    private readonly WorkflowReferenceChecker _references;

    public UpdateWorkflowHandler(
        AppDbContext db, ICurrentUser user, WorkflowValidator validator,
        WorkflowReferenceChecker references)
    {
        _db = db;
        _user = user;
        _validator = validator;
        _references = references;
    }

    public string Name => "update_workflow";
    public string Description =>
        "Updates a workflow identified by workflow_id or name. Only draft workflows are editable. " +
        "Only provided fields change; pass nodes/edges to replace the definition (re-validated), or new_name to rename.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("workflow not found (pass a valid workflow_id or name)");

        if (row.Environment != WorkflowEntity.EnvDraft)
            return Err("only draft workflows are editable; promote a copy or create a new draft");

        var hasNodes = args.TryGetProperty("nodes", out var nodesArg) && nodesArg.ValueKind != JsonValueKind.Null;
        var hasEdges = args.TryGetProperty("edges", out var edgesArg) && edgesArg.ValueKind != JsonValueKind.Null;
        if (hasNodes || hasEdges)
        {
            var nodes = hasNodes ? nodesArg : Parse(row.NodesJson);
            var edges = hasEdges ? edgesArg : Parse(row.EdgesJson);
            if (nodes.ValueKind != JsonValueKind.Array) return Err("nodes must be an array");
            if (edges.ValueKind != JsonValueKind.Array) return Err("edges must be an array");

            string hash;
            try
            {
                hash = _validator.ValidateAndHash(nodes, edges);
                // Same gate as create: an edit must not be the way an unresolvable
                // reference gets in after the fact.
                await _references.EnsureResolvableAsync(nodes, ct);
            }
            catch (DomainException ex)
            {
                return Err(ex.Message);
            }
            row.NodesJson = nodes.GetRawText();
            row.EdgesJson = edges.GetRawText();
            // The simulation link is kept even when the hash changes: the promotion gate
            // recomputes and reports simulation_stale (oracle semantics), not _missing.
            row.SchemaHash = hash;
        }

        if (Str(args, "new_name")?.Trim() is { Length: > 0 } nn) row.Name = nn;
        if (args.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) row.Description = d.GetString();
        if (args.TryGetProperty("input_schema", out var isch) && isch.ValueKind == JsonValueKind.Object) row.InputSchemaJson = isch.GetRawText();
        if (args.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object) row.MetadataJson = md.GetRawText();
        if (args.TryGetProperty("change_summary", out var cs) && cs.ValueKind == JsonValueKind.String) row.ChangeSummary = cs.GetString()!;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = row.WorkflowId,
            name = row.Name,
            environment = row.Environment,
            version = row.Version,
            schema_hash = row.SchemaHash,
            updated = true,
        });
    }

    private async Task<WorkflowEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "workflow_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.Workflows.FirstOrDefaultAsync(
                w => w.WorkflowId == id && w.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.Workflows.FirstOrDefaultAsync(
                w => w.Name == name && w.IsActive, ct);
        return null;
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
