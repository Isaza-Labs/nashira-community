using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Reads a document's text back into the conversation. Read → autonomous.
//
// Takes the id of either a report or an export, because from where the user is
// standing they are the same object — "the report you generated" is an export
// artifact when export_document made it and a report artifact when a workflow saved
// it, and requiring the agent to know which store it landed in is a distinction that
// only exists inside this codebase.
//
// Long documents page rather than truncate silently: `offset` + `next_offset` walk a
// report of any size, and a body that stops mid-sentence with no marker is how an
// agent ends up confidently summarising the first third of an incident write-up.
public sealed class ReadReportHandler : IToolHandler
{
    private const int DefaultMaxChars = 20_000;
    private const int MaxMaxChars = 100_000;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "id":{"type":"string","description":"The report_artifact_id from list_reports, or the export_artifact_id returned by export_document / export_table"},
          "offset":{"type":"integer","minimum":0,"default":0,"description":"Character offset to start from; use next_offset from the previous call to continue"},
          "max_chars":{"type":"integer","minimum":500,"maximum":100000,"default":20000,"description":"Maximum characters to return in this call"}
        },"required":["id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;

    public ReadReportHandler(AppDbContext db) => _db = db;

    public string Name => "read_report";
    public string Description =>
        "Returns the text of a report or of a document this agent generated earlier, so it can be " +
        "quoted, summarised or continued. Accepts a report_artifact_id (see list_reports) or the " +
        "export_artifact_id returned by export_document / export_table. Markdown, html, csv, json and " +
        "plain text come back verbatim; a rendered pdf or xlsx cannot be read back and returns its " +
        "download link instead. Long documents page via offset / next_offset.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var raw = Str(args, "id");
        if (string.IsNullOrWhiteSpace(raw)) return Err("id is required");
        if (!Guid.TryParse(raw.Trim(), out var id))
            return Err($"'{raw}' is not a valid id — pass the report_artifact_id or export_artifact_id verbatim");

        var offset = args.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov) ? Math.Max(ov, 0) : 0;
        var maxChars = args.TryGetProperty("max_chars", out var m) && m.TryGetInt32(out var mv)
            ? Math.Clamp(mv, 500, MaxMaxChars)
            : DefaultMaxChars;

        var report = await _db.ReportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportArtifactId == id && r.IsActive, ct);
        if (report is not null)
        {
            // Expiry is a deletion that has not been swept yet, not a soft state to read
            // through. The message names the date because the next question is always
            // "since when".
            if (report.ExpiresAt is { } exp && exp <= DateTime.UtcNow)
                return Err($"report '{report.Title}' expired on {exp:yyyy-MM-dd} and is no longer readable");

            return Build(
                "report", id, report.Title, report.FileName, report.ContentType, report.Content,
                report.SizeBytes, report.WorkflowRunId, report.ExpiresAt, report.CreatedAt,
                $"/api/reports/{id}/download", offset, maxChars);
        }

        var export = await _db.ExportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ExportArtifactId == id && a.IsActive, ct);
        if (export is not null)
            return Build(
                "export", id, export.FileName, export.FileName, export.ContentType, export.Content,
                export.SizeBytes, null, null, export.CreatedAt,
                $"/api/export/{id}/download", offset, maxChars);

        return Err(
            $"no report or export with id {id}. Use list_reports to find it — an export is also deleted "
            + "when its retention lapses.");
    }

    private static JsonElement Build(
        string kind, Guid id, string title, string fileName, string contentType, byte[] content,
        long sizeBytes, Guid? workflowRunId, DateTime? expiresAt, DateTime createdAt,
        string downloadUrl, int offset, int maxChars)
    {
        if (!DocumentText.IsReadable(contentType, content))
            return JsonSerializer.SerializeToElement(new
            {
                kind,
                id,
                title,
                file_name = fileName,
                content_type = contentType,
                size_bytes = sizeBytes,
                created_at = createdAt,
                download_url = downloadUrl,
                readable = false,
                // Not an `error`: the call did what it could, and the dispatcher treats an
                // `error` field as a failed tool call worth self-correcting. Nothing here
                // is correctable — the format is what it is.
                note = DocumentText.BinaryExplanation(fileName, contentType),
            });

        var text = DocumentText.Decode(content);
        var start = Math.Min(offset, text.Length);
        var slice = text.Substring(start, Math.Min(maxChars, text.Length - start));
        var end = start + slice.Length;

        return JsonSerializer.SerializeToElement(new
        {
            kind,
            id,
            title,
            file_name = fileName,
            content_type = contentType,
            size_bytes = sizeBytes,
            workflow_run_id = workflowRunId,
            expires_at = expiresAt,
            created_at = createdAt,
            download_url = downloadUrl,
            readable = true,
            content = slice,
            offset = start,
            chars_returned = slice.Length,
            total_chars = text.Length,
            truncated = end < text.Length,
            next_offset = end < text.Length ? end : (int?)null,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
