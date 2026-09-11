using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Promotes a workflow along draft -> qa -> production (resolved by id or name). draft->qa
// runs the simulation gate; qa->production requires approved_by (different from the promoter).
// Promotion creates an immutable copy in the target environment. Write → single_confirm.
public sealed class PromoteWorkflowHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","description":"Workflow id (or identify by name)"},
          "name":{"type":"string","description":"Workflow name if no id"},
          "target":{"type":"string","description":"Target environment: qa or production"},
          "approved_by":{"type":"string","description":"Required for qa->production; must differ from the promoter"},
          "change_summary":{"type":"string","description":"Optional change summary for the promoted copy"}
        },"required":["target"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly PromotionService _promotion;

    public PromoteWorkflowHandler(AppDbContext db, ICurrentUser user, PromotionService promotion)
    {
        _db = db;
        _user = user;
        _promotion = promotion;
    }

    public string Name => "promote_workflow";
    public string Description =>
        "Promotes a workflow (resolved by workflow_id or name) to the target environment (qa or production). " +
        "draft->qa runs the simulation gate; qa->production requires approved_by different from the promoter. " +
        "Creates an immutable copy in the target environment.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var target = Str(args, "target")?.Trim();
        if (string.IsNullOrWhiteSpace(target)) return Err("target is required (qa or production)");

        WorkflowEntity? row = null;
        if (Str(args, "workflow_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            row = await _db.Workflows.FirstOrDefaultAsync(
                w => w.WorkflowId == id && w.IsActive, ct);
        else if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            row = await _db.Workflows.FirstOrDefaultAsync(
                w => w.Name == name && w.IsActive, ct);
        if (row is null) return Err("workflow not found (pass a valid workflow_id or name)");

        try
        {
            var promoted = await _promotion.PromoteAsync(row, target, Str(args, "approved_by"), Str(args, "change_summary"), ct);
            return JsonSerializer.SerializeToElement(new
            {
                workflow_id = promoted.WorkflowId,
                name = promoted.Name,
                version = promoted.Version,
                environment = promoted.Environment,
                schema_hash = promoted.SchemaHash,
                promoted_from = promoted.PromotedFrom,
                change_summary = promoted.ChangeSummary,
            });
        }
        catch (DomainException ex)
        {
            return Err(ex.Message);
        }
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
