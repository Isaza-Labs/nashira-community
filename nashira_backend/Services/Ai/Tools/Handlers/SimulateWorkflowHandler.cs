using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Runs the structural simulation over a workflow (resolved by id or name), persisting a
// SimulationResult and pointing the workflow's LastSimulationId at it. Write → single_confirm.
public sealed class SimulateWorkflowHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","description":"Workflow id (or identify by name)"},
          "name":{"type":"string","description":"Workflow name if no id"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly WorkflowSimulationService _simulation;

    public SimulateWorkflowHandler(AppDbContext db, ICurrentUser user, WorkflowSimulationService simulation)
    {
        _db = db;
        _user = user;
        _simulation = simulation;
    }

    public string Name => "simulate_workflow";
    public string Description =>
        "Runs the structural simulation over a workflow (resolved by workflow_id or name) and records the " +
        "result. Returns ok plus node/issue/warning counts; the promotion gate consumes this record.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
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
            var sim = await _simulation.SimulateAsync(row, ct);
            return JsonSerializer.SerializeToElement(new
            {
                simulation_result_id = sim.SimulationResultId,
                workflow_id = sim.WorkflowId,
                ok = sim.Ok,
                node_count = sim.NodeCount,
                issue_count = sim.IssueCount,
                warning_count = sim.WarningCount,
                schema_hash = sim.SchemaHash,
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
