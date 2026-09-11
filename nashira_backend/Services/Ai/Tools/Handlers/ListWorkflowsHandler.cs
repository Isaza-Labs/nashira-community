using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists workflows. Read → autonomous.
public sealed class ListWorkflowsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "environment":{"type":"string","description":"Filter by environment (draft/qa/production)"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListWorkflowsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_workflows";
    public string Description =>
        "Lists workflows (id, name, version, environment, last update). Use get_workflow for details or " +
        "run_workflow to execute one.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var env = Str(args, "environment");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 100) : 25;

        var q = _db.Workflows.AsNoTracking().Where(w => w.IsActive);
        if (!string.IsNullOrWhiteSpace(env)) q = q.Where(w => w.Environment == env);

        var rows = await q.OrderByDescending(w => w.UpdatedAt).Take(limit)
            .Select(w => new
            {
                workflow_id = w.WorkflowId,
                name = w.Name,
                version = w.Version,
                environment = w.Environment,
                updated_at = w.UpdatedAt,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { workflows = rows, count = rows.Count });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
