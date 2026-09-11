using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Browses the snippet catalogue. Read → autonomous.
//
// Until this existed the catalogue had no tool at all: the snippets skill told the
// agent to reach it through `execute_operation` against the `na_snippets` spec, which
// is a self-API call over HTTP carrying a borrowed session bearer. Every one of those
// calls in this deployment failed on "spec 'na_snippets' has no base_url configured",
// and the observed consequence was not an error message to the user — it was a hundred
// stored workflows whose only nodes are __start__ and __end__, plus one that invented
// `__ping__`. An agent that cannot see the catalogue authors around it.
//
// So discovery is in-process, on the chat turn's own scope and the chatting user's own
// ICurrentUser. There is no base URL, no bearer to borrow and nothing to misconfigure.
public sealed class ListSnippetsHandler : IToolHandler
{
    // How many matching rows are examined before ordering. `proven` is resolved from
    // run history rather than from a column, so it cannot be an ORDER BY: the page has
    // to be chosen AFTER it is known. Scanning a bounded window is the compromise —
    // 500 rows is two queries against ids, and it is far past the point where a human
    // is reading a catalogue rather than searching it.
    private const int ProvenScanCap = 500;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "type":{"type":"string","description":"Filter by handler type (ping, transform, rest_call, integration_action, ssh, mcp_call, python_snippet)"},
          "q":{"type":"string","description":"Case-insensitive substring match on name and description"},
          "limit":{"type":"integer","description":"Max rows (default 50, max 200)"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ISnippetHandlerRegistry _registry;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ListSnippetsHandler> _logger;

    public ListSnippetsHandler(
        AppDbContext db, ISnippetHandlerRegistry registry, IServiceProvider sp,
        ILogger<ListSnippetsHandler> logger)
    {
        _db = db;
        _registry = registry;
        _sp = sp;
        _logger = logger;
    }

    public string Name => "list_snippets";

    // `proven` tells the model which of these have actually completed a step. Without
    // it the agent picks by name and will wire in a half-finished experiment when a
    // working equivalent sits in the same list. Advisory, not a filter: the agent
    // routinely creates a snippet and uses it in the same turn, and that one is
    // unproven by definition.
    public string Description =>
        "Lists the snippet catalogue — the reusable steps a workflow node invokes by snippet_id. " +
        "Returns snippet_id, name, type, description, target_mode, effective_idempotency and `proven`. " +
        "proven=true means a step using it has completed successfully on this instance at least once; " +
        "proven=false means it has never finished a run, so treat it as a draft. Results are ordered " +
        "proven-first. CALL THIS BEFORE create_workflow: a node's snippet_id must be a snippet UUID " +
        "from this list (or __start__ / __end__ / subflow). If nothing here fits, call create_snippet " +
        "and use the id it returns — never invent a placeholder like '__ping__'.";

    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var type = Str(args, "type")?.Trim();
        var q = Str(args, "q")?.Trim();
        var limit = Math.Clamp(Int(args, "limit") ?? 50, 1, 200);

        var availableTypes = _registry.KnownTypes.ToArray();
        var query = _db.Snippets.AsNoTracking()
            .Where(s => s.IsActive && availableTypes.Contains(s.Type));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(s => s.Type == type);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.Name, pattern)
                || (s.Description != null && EF.Functions.ILike(s.Description, pattern)));
        }

        var matched = await query.CountAsync(ct);
        if (matched == 0)
        {
            return JsonSerializer.SerializeToElement(new
            {
                items = Array.Empty<object>(),
                total = 0,
                known_types = _registry.KnownTypes.OrderBy(t => t, StringComparer.Ordinal),
                note = "the catalogue has no snippet matching that filter. Create one with "
                    + "create_snippet and use the id it returns; do not invent a snippet_id.",
            });
        }

        // Read the window, not the page. Taking `limit` here and sorting afterwards is
        // what the previous version did, and it made the promise in this tool's own
        // description false: with more snippets than fit on a page, "ordered
        // proven-first" only ever meant "first N by name, of which the proven ones came
        // first" — so the working snippet three pages down never surfaced and the agent
        // picked an unproven one that happened to sort early.
        var scan = Math.Max(limit, ProvenScanCap);
        var rows = await query.OrderBy(s => s.Name).Take(scan).ToListAsync(ct);

        // A snippet is "proven" when some step run that referenced it finished without
        // failing. Resolved from the workflows that name it rather than from a column,
        // so it stays true of history rather than of a flag someone set.
        var proven = await ProvenIdsAsync(rows.Select(r => r.SnippetId).ToList(), ct);

        var items = rows
            .Select(s =>
            {
                var handler = _registry.Resolve(s.Type, _sp);
                var floor = handler?.DefaultIdempotency ?? IdempotencyKind.RequiresCompensation;
                return new
                {
                    snippet_id = s.SnippetId,
                    name = s.Name,
                    slug = s.Slug,
                    type = s.Type,
                    description = s.Description,
                    target_mode = s.TargetMode,
                    effective_idempotency = Idempotency.ToWire(Idempotency.Effective(s, floor)),
                    network_enabled = s.NetworkEnabled,
                    verified = s.Verified,
                    proven = proven.Contains(s.SnippetId),
                };
            })
            .OrderByDescending(x => x.proven)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        _logger.LogInformation(
            "ai.tool.list_snippets.ok type={Type} matched={Matched} returned={Count} proven={Proven}",
            type, matched, items.Count, items.Count(i => i.proven));

        return JsonSerializer.SerializeToElement(new
        {
            items,
            // `total` is how many snippets match, not how many came back. Reporting the
            // page size as the total is how an agent concludes the catalogue is small
            // and stops looking.
            total = matched,
            returned = items.Count,
            known_types = _registry.KnownTypes.OrderBy(t => t, StringComparer.Ordinal),
            note = matched > rows.Count
                ? $"{matched} snippets match; the proven-first ordering was resolved over the "
                    + $"first {rows.Count} by name. Narrow with `q` or `type` if what you want is missing."
                : null,
        });
    }

    // Which of these snippets a completed step has actually referenced. One query for
    // the workflows naming any of them, then one for the runs of those workflows that
    // produced a non-failed step — enough to separate "has worked here" from "exists".
    private async Task<HashSet<Guid>> ProvenIdsAsync(List<Guid> ids, CancellationToken ct)
    {
        var proven = new HashSet<Guid>();
        if (ids.Count == 0) return proven;

        // NodesJson is text; matching the id substring is what the delete guard in
        // SnippetController already does, and it is exact enough for a UUID.
        var workflows = await _db.Workflows.AsNoTracking()
            .Where(ReferencesAny(ids))
            .Select(w => new { w.WorkflowId, w.NodesJson })
            .ToListAsync(ct);
        if (workflows.Count == 0) return proven;

        var workflowIds = workflows.Select(w => w.WorkflowId).ToList();
        var succeeded = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => workflowIds.Contains(r.WorkflowId))
            .Join(_db.StepRuns.AsNoTracking().Where(sr => sr.Result != NodeResultNames.Failed),
                r => r.WorkflowRunId, sr => sr.WorkflowRunId, (r, sr) => r.WorkflowId)
            .Distinct()
            .ToListAsync(ct);
        if (succeeded.Count == 0) return proven;

        var provenWorkflows = succeeded.ToHashSet();
        foreach (var w in workflows.Where(w => provenWorkflows.Contains(w.WorkflowId)))
            foreach (var id in ids)
                if (w.NodesJson.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))
                    proven.Add(id);

        return proven;
    }

    private static Expression<Func<WorkflowEntity, bool>> ReferencesAny(IEnumerable<Guid> ids)
    {
        var workflow = Expression.Parameter(typeof(WorkflowEntity), "workflow");
        var nodes = Expression.Property(workflow, nameof(WorkflowEntity.NodesJson));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression body = Expression.Constant(false);

        foreach (var id in ids)
            body = Expression.OrElse(
                body,
                Expression.Call(nodes, contains, Expression.Constant(id.ToString())));

        return Expression.Lambda<Func<WorkflowEntity, bool>>(body, workflow);
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}

// The one step-result spelling this handler needs. Kept next to its use rather than
// widened into the engine's vocabulary for a single comparison.
internal static class NodeResultNames
{
    internal const string Failed = "failed";
}
