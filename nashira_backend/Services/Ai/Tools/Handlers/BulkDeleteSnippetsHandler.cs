using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes MANY snippets in one confirmed call. Write → single_confirm; the
// batch spends one mutation-budget slot. Same selection contract as the other bulk
// deletes (explicit ids/names, filters, or both; empty selection refused), plus the
// guard SnippetController.Delete applies per item: a snippet a live workflow still
// names is SKIPPED and reported with the workflows that reference it, instead of
// failing the whole batch or leaving a node that resolves to nothing at run time.
// Writes its own audit event with the resolved names, attributed to whoever asked.
public sealed class BulkDeleteSnippetsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "snippet_ids":{"type":"array","items":{"type":"string"},"description":"Explicit snippets to delete, by snippet_id"},
          "names":{"type":"array","items":{"type":"string"},"description":"Explicit snippets to delete, by exact name"},
          "name_contains":{"type":"string","description":"Only snippets whose name contains this (case-insensitive)"},
          "type":{"type":"string","description":"Only snippets of this handler type, e.g. ping, ssh, python_snippet"},
          "expected_count":{"type":"integer","minimum":0,"description":"Refuse unless exactly this many snippets match — pass the count the user confirmed"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly ILogger<BulkDeleteSnippetsHandler> _logger;

    public BulkDeleteSnippetsHandler(
        AppDbContext db, ICurrentUser user, IAuditLogger audit,
        ILogger<BulkDeleteSnippetsHandler> logger)
    {
        _db = db;
        _user = user;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "bulk_delete_snippets";
    public string Description =>
        "Removes MANY snippets from the catalogue in one call — one mutation, one confirmation. " +
        "Select by snippet_ids and/or names, by filters (name_contains, type), or both: filters " +
        "restrict the explicit list. At least one criterion is required. Snippets still referenced " +
        "by an active workflow are skipped and reported with the referencing workflow names — delete " +
        "or update those workflows first. Before calling, preview with list_snippets, show the user " +
        "the names and the count, and pass that count as expected_count.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawIds = StrList(args, "snippet_ids");
        var ids = new List<Guid>();
        foreach (var raw in rawIds)
        {
            if (!Guid.TryParse(raw, out var id)) return Err($"'{raw}' is not a valid snippet_id");
            ids.Add(id);
        }
        var names = StrList(args, "names");
        var nameContains = Str(args, "name_contains")?.Trim();
        var type = Str(args, "type")?.Trim();

        var hasExplicit = ids.Count > 0 || names.Count > 0;
        var hasFilter = !string.IsNullOrEmpty(nameContains) || !string.IsNullOrEmpty(type);
        if (!hasExplicit && !hasFilter)
            return Err("pass snippet_ids/names and/or at least one filter — an empty selection would mean the whole catalogue");

        List<SnippetEntity> candidates;
        var notFound = new List<string>();
        var excludedByFilter = new List<string>();

        if (hasExplicit)
        {
            var rows = await _db.Snippets
                .Where(s => s.IsActive && (ids.Contains(s.SnippetId) || names.Contains(s.Name)))
                .ToListAsync(ct);
            var foundIds = rows.Select(r => r.SnippetId).ToHashSet();
            var foundNames = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            notFound.AddRange(ids.Where(i => !foundIds.Contains(i)).Select(i => i.ToString()));
            notFound.AddRange(names.Where(n => !foundNames.Contains(n)));

            candidates = rows.Where(s => Matches(s, nameContains, type)).ToList();
            excludedByFilter = rows.Except(candidates).Select(s => s.Name).OrderBy(n => n).ToList();
        }
        else
        {
            var q = _db.Snippets.Where(s => s.IsActive);
            if (!string.IsNullOrEmpty(nameContains))
                q = q.Where(s => EF.Functions.ILike(s.Name, $"%{nameContains}%"));
            if (!string.IsNullOrEmpty(type)) q = q.Where(s => s.Type == type);
            candidates = await q.ToListAsync(ct);
        }

        if (args.TryGetProperty("expected_count", out var ec) && ec.TryGetInt32(out var expected)
            && expected != candidates.Count)
            return Err($"expected_count is {expected} but {candidates.Count} snippets match now — " +
                       "re-run list_snippets, show the user the new list, and confirm again");

        // One pass over the live workflows' node JSON finds every reference in the
        // batch — the per-item query SnippetController runs would be N round trips.
        var inUse = new List<object>();
        var deletable = new List<SnippetEntity>();
        if (candidates.Count > 0)
        {
            var workflows = await _db.Workflows.AsNoTracking()
                .Where(w => w.IsActive)
                .Select(w => new { w.Name, w.NodesJson })
                .ToListAsync(ct);
            foreach (var s in candidates)
            {
                var sid = s.SnippetId.ToString();
                var referencing = workflows
                    .Where(w => w.NodesJson.Contains(sid, StringComparison.OrdinalIgnoreCase))
                    .Select(w => w.Name).OrderBy(n => n).Take(5).ToList();
                if (referencing.Count > 0)
                    inUse.Add(new { snippet_id = s.SnippetId, name = s.Name, referenced_by = referencing });
                else
                    deletable.Add(s);
            }
        }

        var deleted = deletable
            .Select(s => new { snippet_id = s.SnippetId, name = s.Name, type = s.Type })
            .OrderBy(s => s.name).ToList();
        if (deletable.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var s in deletable)
            {
                s.IsActive = false;
                s.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync("agent.bulk", entityId: null, action: Name,
                before: new { snippet_ids = rawIds, names, name_contains = nameContains, type },
                after: new { deleted_count = deleted.Count, deleted, skipped_in_use = inUse.Count }, ct);
        }

        _logger.LogInformation(
            "ai.tool.bulk_delete_snippets user={UserId} actor={Actor} deleted={Deleted} in_use={InUse} not_found={NotFound} excluded={Excluded}",
            _user.IsAuthenticated ? _user.UserId : null, _user.Username,
            deleted.Count, inUse.Count, notFound.Count, excludedByFilter.Count);

        return JsonSerializer.SerializeToElement(new
        {
            deleted_count = deleted.Count,
            deleted,
            skipped_in_use = inUse.Count > 0 ? inUse : null,
            not_found = notFound.Count > 0 ? notFound : null,
            excluded_by_filter = excludedByFilter.Count > 0 ? excludedByFilter : null,
            note = deleted.Count == 0 ? "no snippets were deleted — check skipped_in_use / not_found" : null,
        });
    }

    private static bool Matches(SnippetEntity s, string? nameContains, string? type) =>
        (string.IsNullOrEmpty(nameContains)
            || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrEmpty(type) || s.Type == type);

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
