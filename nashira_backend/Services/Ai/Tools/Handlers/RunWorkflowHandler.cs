using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Workflow;
using RunTrigger = nashira_backend.Data.Models.RunTrigger;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Executes a workflow end-to-end (resolved by id or name). Execute → elevated_confirm.
public sealed class RunWorkflowHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_id":{"type":"string","description":"Workflow id (or identify by name)"},
          "name":{"type":"string","description":"Workflow name if no id"},
          "input":{"type":"object","description":"Payload feeding {{ input.* }} in every node's config. Read the workflow's input_schema, or its nodes' {{ input.X }} references, to know which keys it needs."},
          "target_devices":{"type":"array","items":{"type":"string"},"description":"Device ids a per_device step fans out over. Omit when no step runs per device."}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly WorkflowRunService _runService;

    public RunWorkflowHandler(AppDbContext db, ICurrentUser user, WorkflowRunService runService)
    {
        _db = db;
        _user = user;
        _runService = runService;
    }

    public string Name => "run_workflow";
    public string Description =>
        "Executes a workflow end-to-end against its target environment, resolved by workflow_id or name. " +
        "Accepts the same input payload and target devices a manual run does — a workflow whose nodes " +
        "reference {{ input.X }} will run with those unresolved if you omit them, and a per_device " +
        "workflow with no targets resolves to nothing. " +
        "This performs real changes and is confirmed before running. Returns the run status and counts.";
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

        JsonElement? input = null;
        if (args.TryGetProperty("input", out var inputEl))
        {
            if (inputEl.ValueKind is JsonValueKind.Object) input = inputEl.Clone();
            else if (inputEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return Err("input must be a JSON object");
        }

        var targets = new List<Guid>();
        if (args.TryGetProperty("target_devices", out var targetsEl)
            && targetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in targetsEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out var did))
                    return Err("target_devices must be an array of device id strings");
                if (!targets.Contains(did)) targets.Add(did);
            }
        }

        // Reject an unknown id here rather than mid-run, exactly as the manual endpoint
        // does. Discovering it after the first step would leave a half-executed workflow
        // whose failure names a device instead of the request that was wrong.
        if (targets.Count > 0)
        {
            var known = await _db.Devices.AsNoTracking()
                .Where(d => d.IsActive && targets.Contains(d.DeviceId))
                .Select(d => d.DeviceId)
                .ToListAsync(ct);
            var missing = targets.Except(known).ToList();
            if (missing.Count > 0)
                return Err($"unknown target device(s): {string.Join(", ", missing)}");
        }

        try
        {
            var run = await _runService.RunAsync(row, input, targets, ct, RunTrigger.Agent);
            return JsonSerializer.SerializeToElement(new
            {
                workflow_run_id = run.WorkflowRunId,
                workflow_id = run.WorkflowId,
                environment = run.Environment,
                status = run.Status,
                final_state = run.FinalState,
                node_count = run.NodeCount,
                changed_count = run.ChangedCount,
                failed_count = run.FailedCount,
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
