using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes MANY persisted reports in one confirmed call. Write → single_confirm;
// the batch spends one mutation-budget slot. Scope is ReportArtifacts only — the
// documents listed under /reports — never ExportArtifacts: an export is a file the
// user downloaded, and pulling it out from under them is not a cleanup. Selection is
// ids, filters, or both (filters restrict); empty selection refused. Writes its own
// audit event with the resolved titles, attributed by AuditLogger to whoever asked.
public sealed class BulkDeleteReportsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "report_ids":{"type":"array","items":{"type":"string"},"description":"Explicit reports to delete, by report_artifact_id"},
          "search":{"type":"string","description":"Only reports whose title or file name contains this (case-insensitive)"},
          "workflow_run_id":{"type":"string","description":"Only reports produced by this workflow run"},
          "expired_only":{"type":"boolean","description":"Only reports past their retention date"},
          "older_than_days":{"type":"integer","minimum":1,"description":"Only reports created more than this many days ago"},
          "expected_count":{"type":"integer","minimum":0,"description":"Refuse unless exactly this many reports match — pass the count the user confirmed"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly ILogger<BulkDeleteReportsHandler> _logger;

    public BulkDeleteReportsHandler(
        AppDbContext db, ICurrentUser user, IAuditLogger audit,
        ILogger<BulkDeleteReportsHandler> logger)
    {
        _db = db;
        _user = user;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "bulk_delete_reports";
    public string Description =>
        "Removes MANY persisted reports (/reports) in one call — one mutation, one confirmation. " +
        "Select by an explicit report_ids list, by filters (search, workflow_run_id, expired_only, " +
        "older_than_days), or both: filters restrict the list. At least one criterion is required. " +
        "Exports are never touched — only saved reports. Before calling, preview with list_reports, " +
        "show the user the titles and the count, and pass that count as expected_count.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rawIds = StrList(args, "report_ids");
        var ids = new List<Guid>();
        foreach (var raw in rawIds)
        {
            if (!Guid.TryParse(raw, out var id)) return Err($"'{raw}' is not a valid report_artifact_id");
            ids.Add(id);
        }

        var search = Str(args, "search")?.Trim();
        Guid? runId = null;
        if (Str(args, "workflow_run_id")?.Trim() is { Length: > 0 } rawRun)
        {
            if (!Guid.TryParse(rawRun, out var parsed)) return Err($"'{rawRun}' is not a valid workflow_run_id");
            runId = parsed;
        }
        var expiredOnly = args.TryGetProperty("expired_only", out var eo) && eo.ValueKind == JsonValueKind.True;
        var olderThanDays = args.TryGetProperty("older_than_days", out var od)
            && od.TryGetInt32(out var odv) && odv > 0 ? odv : (int?)null;

        var hasFilter = !string.IsNullOrEmpty(search) || runId is not null || expiredOnly || olderThanDays is not null;
        if (ids.Count == 0 && !hasFilter)
            return Err("pass report_ids and/or at least one filter — an empty selection would mean every report");

        var now = DateTime.UtcNow;
        List<Data.Models.ReportArtifact> candidates;
        var notFound = new List<string>();
        var excludedByFilter = new List<string>();

        if (ids.Count > 0)
        {
            var rows = await _db.ReportArtifacts
                .Where(r => r.IsActive && ids.Contains(r.ReportArtifactId))
                .ToListAsync(ct);
            var found = rows.Select(r => r.ReportArtifactId).ToHashSet();
            notFound = ids.Where(i => !found.Contains(i)).Select(i => i.ToString()).ToList();

            candidates = rows.Where(r => Matches(r, search, runId, expiredOnly, olderThanDays, now)).ToList();
            excludedByFilter = rows.Except(candidates).Select(r => r.Title).OrderBy(t => t).ToList();
        }
        else
        {
            var q = _db.ReportArtifacts.Where(r => r.IsActive);
            if (!string.IsNullOrEmpty(search))
            {
                var pattern = $"%{search}%";
                q = q.Where(r => EF.Functions.ILike(r.Title, pattern) || EF.Functions.ILike(r.FileName, pattern));
            }
            if (runId is { } rg) q = q.Where(r => r.WorkflowRunId == rg);
            if (expiredOnly) q = q.Where(r => r.ExpiresAt != null && r.ExpiresAt <= now);
            if (olderThanDays is { } days)
            {
                var cutoff = now.AddDays(-days);
                q = q.Where(r => r.CreatedAt < cutoff);
            }
            candidates = await q.ToListAsync(ct);
        }

        if (args.TryGetProperty("expected_count", out var ec) && ec.TryGetInt32(out var expected)
            && expected != candidates.Count)
            return Err($"expected_count is {expected} but {candidates.Count} reports match now — " +
                       "re-run list_reports, show the user the new list, and confirm again");

        var deleted = candidates
            .Select(r => new { report_artifact_id = r.ReportArtifactId, title = r.Title, file_name = r.FileName })
            .OrderBy(r => r.title).ToList();
        if (candidates.Count > 0)
        {
            foreach (var r in candidates)
            {
                r.IsActive = false;
                r.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync("agent.bulk", entityId: null, action: Name,
                before: new
                {
                    report_ids = rawIds, search, workflow_run_id = runId,
                    expired_only = expiredOnly, older_than_days = olderThanDays,
                },
                after: new { deleted_count = deleted.Count, deleted }, ct);
        }

        _logger.LogInformation(
            "ai.tool.bulk_delete_reports user={UserId} actor={Actor} deleted={Deleted} not_found={NotFound} excluded={Excluded}",
            _user.IsAuthenticated ? _user.UserId : null, _user.Username,
            deleted.Count, notFound.Count, excludedByFilter.Count);

        return JsonSerializer.SerializeToElement(new
        {
            deleted_count = deleted.Count,
            deleted,
            not_found = notFound.Count > 0 ? notFound : null,
            excluded_by_filter = excludedByFilter.Count > 0 ? excludedByFilter : null,
            note = deleted.Count == 0 ? "no reports matched the selection — nothing was deleted" : null,
        });
    }

    private static bool Matches(
        Data.Models.ReportArtifact r, string? search, Guid? runId, bool expiredOnly, int? olderThanDays, DateTime now) =>
        (string.IsNullOrEmpty(search)
            || r.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || r.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
        && (runId is null || r.WorkflowRunId == runId)
        && (!expiredOnly || (r.ExpiresAt is not null && r.ExpiresAt <= now))
        && (olderThanDays is not { } days || r.CreatedAt < now.AddDays(-days));

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
