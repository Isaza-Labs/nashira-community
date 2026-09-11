using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Details of one workflow (resolved by id or name). Read → autonomous.
public sealed class GetWorkflowHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","description":"Workflow id (or identify by name)"},
          "name":{"type":"string","description":"Workflow name if no id"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public GetWorkflowHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "get_workflow";
    public string Description =>
        "Returns a workflow's details (name, description, version, environment, node/edge counts), " +
        "resolved by workflow_id or name.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("workflow not found (pass a valid workflow_id or name)");

        return JsonSerializer.SerializeToElement(new
        {
            workflow_id = row.WorkflowId,
            name = row.Name,
            description = row.Description,
            version = row.Version,
            environment = row.Environment,
            schema_hash = row.SchemaHash,
            node_count = Count(row.NodesJson),
            edge_count = Count(row.EdgesJson),
        });
    }

    private async Task<WorkflowEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "workflow_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(
                w => w.WorkflowId == id && w.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(
                w => w.Name == name && w.IsActive, ct);
        return null;
    }

    private static int Count(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
