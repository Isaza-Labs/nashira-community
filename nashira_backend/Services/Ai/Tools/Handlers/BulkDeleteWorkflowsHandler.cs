using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes MANY workflows in one confirmed call. Write → single_confirm; the
// batch spends one mutation-budget slot. Same selection contract as the other bulk
// deletes: explicit ids/names, filters, or both (filters restrict), empty selection
// refused. Writes its own audit event with the resolved list, attributed by
// AuditLogger to whoever asked.
public sealed class BulkDeleteWorkflowsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "workflow_ids":{"type":"array","items":{"type":"string"},"description":"Explicit workflows to delete, by id"},
          "names":{"type":"array","items":{"type":"string"},"description":"Explicit workflows to delete, by exact name"},
          "environment":{"type":"string","enum":["draft","qa","production"],"description":"Only workflows in this environment"},
          "name_contains":{"type":"string","description":"Only workflows whose name contains this (case-insensitive)"},
          "expected_count":{"type":"integer","minimum":0,"description":"Refuse unless exactly this many workflows match — pass the count the user confirmed"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly ILogger<BulkDeleteWorkflowsHandler> _logger;

    public BulkDeleteWorkflowsHandler(
        AppDbContext db, ICurrentUser user, IAuditLogger audit,
        ILogger<BulkDeleteWorkflowsHandler> logger)
    {
        _db = db;
        _user = user;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "bulk_delete_workflows";
    public string Description =>
        "Removes MANY workflows in one call — one mutation, one confirmation — instead of one " +
        "delete_workflow per item. Select by workflow_ids and/or names, by filters (environment, " +
        "name_contains), or both: filters restrict the explicit list. At least one criterion is " +
        "required. Before calling, preview with list_workflows, show the user the names and the " +
        "count, and pass that count as expected_count. Deleting a production workflow is still a " +
        "production change — say so explicitly when asking for confirmation.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawIds = StrList(args, "workflow_ids");
        var ids = new List<Guid>();
        foreach (var raw in rawIds)
        {
            if (!Guid.TryParse(raw, out var id)) return Err($"'{raw}' is not a valid workflow_id");
            ids.Add(id);
        }
        var names = StrList(args, "names");
        var environment = Str(args, "environment")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(environment)
            && environment is not (WorkflowEntity.EnvDraft or WorkflowEntity.EnvQa or WorkflowEntity.EnvProduction))
            return Err("environment must be 'draft', 'qa' or 'production'");
        var nameContains = Str(args, "name_contains")?.Trim();

        var hasExplicit = ids.Count > 0 || names.Count > 0;
        var hasFilter = !string.IsNullOrEmpty(environment) || !string.IsNullOrEmpty(nameContains);
        if (!hasExplicit && !hasFilter)
            return Err("pass workflow_ids/names and/or at least one filter — an empty selection would mean every workflow");

        List<WorkflowEntity> candidates;
        var notFound = new List<string>();
        var excludedByFilter = new List<string>();

        if (hasExplicit)
        {
            var rows = await _db.Workflows
                .Where(w => w.IsActive && (ids.Contains(w.WorkflowId) || names.Contains(w.Name)))
                .ToListAsync(ct);
            var foundIds = rows.Select(r => r.WorkflowId).ToHashSet();
            var foundNames = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            notFound.AddRange(ids.Where(i => !foundIds.Contains(i)).Select(i => i.ToString()));
            notFound.AddRange(names.Where(n => !foundNames.Contains(n)));

            candidates = rows.Where(w => Matches(w, environment, nameContains)).ToList();
            excludedByFilter = rows.Except(candidates).Select(w => w.Name).OrderBy(n => n).ToList();
        }
        else
        {
            var q = _db.Workflows.Where(w => w.IsActive);
            if (!string.IsNullOrEmpty(environment)) q = q.Where(w => w.Environment == environment);
            if (!string.IsNullOrEmpty(nameContains))
                q = q.Where(w => EF.Functions.ILike(w.Name, $"%{nameContains}%"));
            candidates = await q.ToListAsync(ct);
        }

        if (args.TryGetProperty("expected_count", out var ec) && ec.TryGetInt32(out var expected)
            && expected != candidates.Count)
            return Err($"expected_count is {expected} but {candidates.Count} workflows match now — " +
                       "re-run list_workflows, show the user the new list, and confirm again");

        var deleted = candidates
            .Select(w => new { workflow_id = w.WorkflowId, name = w.Name, environment = w.Environment })
            .OrderBy(w => w.name).ToList();
        if (candidates.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var w in candidates)
            {
                w.IsActive = false;
                w.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync("agent.bulk", entityId: null, action: Name,
                before: new { workflow_ids = rawIds, names, environment, name_contains = nameContains },
                after: new { deleted_count = deleted.Count, deleted }, ct);
        }

        _logger.LogInformation(
            "ai.tool.bulk_delete_workflows user={UserId} actor={Actor} deleted={Deleted} not_found={NotFound} excluded={Excluded}",
            _user.IsAuthenticated ? _user.UserId : null, _user.Username,
            deleted.Count, notFound.Count, excludedByFilter.Count);

        return JsonSerializer.SerializeToElement(new
        {
            deleted_count = deleted.Count,
            deleted,
            not_found = notFound.Count > 0 ? notFound : null,
            excluded_by_filter = excludedByFilter.Count > 0 ? excludedByFilter : null,
            note = deleted.Count == 0 ? "no workflows matched the selection — nothing was deleted" : null,
        });
    }

    private static bool Matches(WorkflowEntity w, string? environment, string? nameContains) =>
        (string.IsNullOrEmpty(environment) || w.Environment == environment)
        && (string.IsNullOrEmpty(nameContains)
            || w.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    private static List<string> StrList(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
