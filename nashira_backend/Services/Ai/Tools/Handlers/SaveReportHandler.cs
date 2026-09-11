using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Persists a report so it can be read again. Write → single_confirm.
//
// The counterpart to read_report, and the reason the pair is worth having at all: an
// export is handed over and forgotten, while a report stays in /reports under a title,
// attributable to whoever produced it, optionally tied to the run that generated it and
// optionally expiring. That is what makes "the report from last Tuesday's upgrade" a
// thing the agent can go and find rather than something it has to be told again.
//
// A markdown body is the default on purpose: it is the one format that survives the
// round trip, so a report saved today is quotable next month. A pdf handed to the user
// is export_document's job and stays that way.
public sealed class SaveReportHandler : IToolHandler
{
    private const int MaxBytes = 10 * 1024 * 1024;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string","description":"How the report is listed in /reports"},
          "content":{"type":"string","description":"The report body, markdown by default"},
          "description":{"type":"string","description":"One line on what it covers"},
          "content_type":{"type":"string","default":"text/markdown","description":"Only change it if the body is not markdown"},
          "file_name":{"type":"string","description":"Defaults to a slug of the title"},
          "workflow_run_id":{"type":"string","description":"The run this report documents, when there is one"},
          "retain_days":{"type":"integer","minimum":1,"description":"Delete it after this many days; omit to keep it indefinitely"}
        },"required":["title","content"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public SaveReportHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "save_report";
    public string Description =>
        "Saves a report so it stays available in /reports and can be read back later with read_report. " +
        "Use it for a document that should outlive the conversation — an incident write-up, a run " +
        "summary, an audit record — optionally tied to a workflow run and optionally expiring after " +
        "retain_days. For a file the user just wants to download now, use export_document instead.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var title = Str(args, "title")?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return Err("title is required");

        var content = Str(args, "content");
        if (string.IsNullOrWhiteSpace(content)) return Err("content is required");

        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaxBytes)
            return Err($"the report is {bytes.Length / (1024 * 1024)} MB; the limit is {MaxBytes / (1024 * 1024)} MB");

        Guid? runId = null;
        if (Str(args, "workflow_run_id")?.Trim() is { Length: > 0 } rawRun)
        {
            if (!Guid.TryParse(rawRun, out var parsed)) return Err($"'{rawRun}' is not a valid workflow_run_id");
            // Checked here although /api/reports does not: a run id the agent inferred
            // rather than read produces a report that is attributed to nothing and quietly
            // never appears under that run again.
            if (!await _db.WorkflowRuns.AsNoTracking().AnyAsync(r => r.WorkflowRunId == parsed, ct))
                return Err($"no workflow run with id {parsed} — omit workflow_run_id if this report documents no run");
            runId = parsed;
        }

        var retainDays = args.TryGetProperty("retain_days", out var rd) && rd.TryGetInt32(out var rdv) && rdv > 0
            ? rdv
            : (int?)null;

        var contentType = Str(args, "content_type")?.Trim();
        var fileName = Str(args, "file_name")?.Trim();
        var now = DateTime.UtcNow;

        var row = new ReportArtifact
        {
            ReportArtifactId = Guid.NewGuid(),
            Title = title,
            Description = Str(args, "description")?.Trim(),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "text/markdown" : contentType,
            FileName = string.IsNullOrWhiteSpace(fileName) ? $"{Slug.From(title)}.md" : fileName,
            Content = bytes,
            SizeBytes = bytes.Length,
            WorkflowRunId = runId,
            ExpiresAt = retainDays is { } days ? now.AddDays(days) : null,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ReportArtifacts.Add(row);
        await _db.SaveChangesAsync(ct);

        return JsonSerializer.SerializeToElement(new
        {
            report_artifact_id = row.ReportArtifactId,
            title = row.Title,
            file_name = row.FileName,
            content_type = row.ContentType,
            size_bytes = row.SizeBytes,
            workflow_run_id = row.WorkflowRunId,
            expires_at = row.ExpiresAt,
            created_at = row.CreatedAt,
            download_url = $"/api/reports/{row.ReportArtifactId}/download",
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
