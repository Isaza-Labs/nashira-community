using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Export;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Export;

public sealed class ExportService : IExportService
{
    private const string CsvContentType = "text/csv";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string HtmlContentType = "text/html; charset=utf-8";
    private const string MarkdownContentType = "text/markdown; charset=utf-8";
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string JsonContentType = "application/json; charset=utf-8";

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ExportService(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<ExportArtifactResponse> CreateAsync(
        string format, string? fileName, IReadOnlyList<string>? columns,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, CancellationToken ct)
    {
        format = (format ?? "csv").Trim().ToLowerInvariant();
        var cols = (columns is { Count: > 0 } ? columns : InferColumns(rows)).ToList();

        byte[] bytes;
        string contentType;
        string ext;
        switch (format)
        {
            case "xlsx":
                bytes = BuildXlsx(cols, rows);
                contentType = XlsxContentType;
                ext = "xlsx";
                break;
            case "html":
                bytes = BuildHtml(cols, rows, TitleFrom(fileName));
                contentType = HtmlContentType;
                ext = "html";
                break;
            case "md":
            case "markdown":
                bytes = BuildMarkdown(cols, rows, TitleFrom(fileName));
                contentType = MarkdownContentType;
                ext = "md";
                break;
            case "pdf":
                // A table is a one-block document, rendered by the same builder a report
                // goes through so both come out looking like the same product.
                bytes = DocumentPdfBuilder.Build(TitleFrom(fileName), [ToDocumentTable(cols, rows)], DateTime.UtcNow);
                contentType = PdfContentType;
                ext = "pdf";
                break;
            case "docx":
                // Same route the PDF takes: a table is a one-block document, rendered by
                // the builder a report goes through, so both come out looking like the
                // same product.
                bytes = DocumentDocxBuilder.Build(TitleFrom(fileName), [ToDocumentTable(cols, rows)], DateTime.UtcNow);
                contentType = DocxContentType;
                ext = "docx";
                break;
            case "json":
                bytes = BuildJson(cols, rows);
                contentType = JsonContentType;
                ext = "json";
                break;
            case "csv":
                bytes = BuildCsv(cols, rows);
                contentType = CsvContentType;
                ext = "csv";
                break;
            default:
                throw new ValidationException(
                    "format must be 'csv', 'json', 'xlsx', 'docx', 'pdf', 'html' or 'markdown'");
        }

        return await PersistAsync(fileName, ext, contentType, bytes, ct);
    }

    public async Task<ExportArtifactResponse> CreateDocumentAsync(
        string format, string? fileName, string? title, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ValidationException("content must be a non-empty markdown document");

        format = (format ?? "pdf").Trim().ToLowerInvariant();
        var heading = string.IsNullOrWhiteSpace(title) ? TitleFrom(fileName) : title.Trim();
        var generatedAt = DateTime.UtcNow;

        byte[] bytes;
        string contentType;
        string ext;
        switch (format)
        {
            case "pdf":
                bytes = DocumentPdfBuilder.Build(heading, MarkdownDoc.Parse(content), generatedAt);
                contentType = PdfContentType;
                ext = "pdf";
                break;
            case "html":
                bytes = new UTF8Encoding(false).GetBytes(
                    DocumentHtmlBuilder.Build(heading, MarkdownDoc.Parse(content), generatedAt));
                contentType = HtmlContentType;
                ext = "html";
                break;
            case "docx":
                bytes = DocumentDocxBuilder.Build(heading, MarkdownDoc.Parse(content), generatedAt);
                contentType = DocxContentType;
                ext = "docx";
                break;
            case "md":
            case "markdown":
                // Passthrough: the input already is the document. Only the title is added,
                // and only when the author did not already open with one.
                bytes = new UTF8Encoding(false).GetBytes(WithTitle(heading, content));
                contentType = MarkdownContentType;
                ext = "md";
                break;
            default:
                // `json` is deliberately absent: a written report is prose, and there is no
                // honest JSON shape for it beyond wrapping the markdown in a string, which
                // would be a worse answer than saying so.
                throw new ValidationException(
                    "format must be 'pdf', 'docx', 'html' or 'markdown' — use export_table for csv/json/xlsx");
        }

        return await PersistAsync(fileName, ext, contentType, bytes, ct);
    }

    private async Task<ExportArtifactResponse> PersistAsync(
        string? fileName, string ext, string contentType, byte[] bytes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = new ExportArtifact
        {
            ExportArtifactId = Guid.NewGuid(),
            FileName = SanitizeFileName(fileName, ext),
            ContentType = contentType,
            SizeBytes = bytes.LongLength,
            Content = bytes,
            CreatedByUserId = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ExportArtifacts.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    private static string WithTitle(string title, string content) =>
        content.TrimStart().StartsWith("# ", StringComparison.Ordinal)
            ? content
            : $"# {title}{Environment.NewLine}{Environment.NewLine}{content}";

    public async Task<ListResponse<ExportArtifactResponse>> ListAsync(int limit, int offset, CancellationToken ct)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.ExportArtifacts.AsNoTracking().Where(a => a.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new ListResponse<ExportArtifactResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        };
    }

    public Task<ExportArtifact?> GetForDownloadAsync(Guid id, CancellationToken ct) =>
        _db.ExportArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ExportArtifactId == id && a.IsActive, ct);

    // ─── builders ───────────────────────────────────────────────────

    internal static MdTable ToDocumentTable(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows) =>
        new(columns.Select(c => (IReadOnlyList<MdSpan>)[new MdSpan(c)]).ToList(),
            rows.Select(r => (IReadOnlyList<IReadOnlyList<MdSpan>>)columns
                    .Select(c => (IReadOnlyList<MdSpan>)[new MdSpan(r.TryGetValue(c, out var v) ? v ?? string.Empty : string.Empty)])
                    .ToList())
                .ToList());

    private static List<string> InferColumns(IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            foreach (var k in r.Keys)
                if (set.Add(k)) seen.Add(k);
        return seen;
    }

    /// <summary>An array of objects, one per row, columns in the requested order.</summary>
    /// <remarks>
    /// Values stay strings. Every other format here renders what the caller gave it
    /// and JSON is the one where a renderer could be tempted to guess types instead —
    /// and guessing turns a leading-zero asset tag into a number, an IP octet into a
    /// float, and a version "1.10" into 1.1. A caller that wants numbers can send
    /// them as numbers once the rows carry a type, which they do not today.
    ///
    /// Columns are written in the requested order and a row missing one gets null, so
    /// the array is rectangular — a consumer can read it as a table without checking
    /// each object for the keys it expects.
    /// </remarks>
    internal static byte[] BuildJson(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                foreach (var col in columns)
                {
                    writer.WritePropertyName(col);
                    if (row.TryGetValue(col, out var v) && v is not null) writer.WriteStringValue(v);
                    else writer.WriteNullValue();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    internal static byte[] BuildCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columns.Select(CsvEscape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', columns.Select(c => CsvEscape(row.TryGetValue(c, out var v) ? v : null))));
        // UTF-8 BOM so Excel opens accented text correctly.
        return new UTF8Encoding(true).GetBytes(sb.ToString());
    }

    // Self-contained HTML: styles are inlined in a <style> block so the file can be
    // opened from disk or pasted into an email body without fetching anything.
    private static byte[] BuildHtml(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{HtmlEscape(title)}</title>");
        sb.AppendLine("""
            <style>
              body { font-family: ui-sans-serif, system-ui, "Segoe UI", Roboto, sans-serif; margin: 2rem; color: #12171f; }
              h1 { font-size: 1.1rem; margin: 0 0 1rem; }
              table { border-collapse: collapse; width: 100%; font-size: 0.85rem; }
              th, td { border: 1px solid #dce0e5; padding: 0.4rem 0.6rem; text-align: left; vertical-align: top; }
              th { background: #eef0f3; font-weight: 600; }
              tr:nth-child(even) td { background: #f9fafc; }
            </style>
            """);
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>{HtmlEscape(title)}</h1>");
        sb.AppendLine("<table><thead><tr>");
        foreach (var c in columns) sb.Append("<th>").Append(HtmlEscape(c)).Append("</th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var c in columns)
                sb.Append("<td>").Append(HtmlEscape(row.TryGetValue(c, out var v) ? v : null)).Append("</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    // GitHub-flavoured pipe table. Pipes inside cells are escaped and newlines
    // become <br> — a raw newline would silently break the row into two.
    private static byte[] BuildMarkdown(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.Append("| ").Append(string.Join(" | ", columns.Select(MarkdownEscape))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", columns.Select(_ => "---"))).AppendLine(" |");
        foreach (var row in rows)
            sb.Append("| ")
              .Append(string.Join(" | ", columns.Select(c => MarkdownEscape(row.TryGetValue(c, out var v) ? v : null))))
              .AppendLine(" |");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    internal static string HtmlEscape(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");

    internal static string MarkdownEscape(string? value) =>
        (value ?? string.Empty)
            .Replace("|", "\\|")
            .Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    private static byte[] BuildXlsx(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Export");
        for (var c = 0; c < columns.Count; c++)
            ws.Cell(1, c + 1).Value = columns[c];
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < columns.Count; c++)
                ws.Cell(r + 2, c + 1).Value = (rows[r].TryGetValue(columns[c], out var v) ? v : null) ?? string.Empty;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    internal static string CsvEscape(string? value)
    {
        var v = value ?? string.Empty;
        if (v.IndexOfAny(['"', ',', '\n', '\r']) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    // Human-readable heading for the document formats, derived from the requested
    // filename so an "interface-errors.html" export is titled accordingly.
    private static string TitleFrom(string? fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        return stem.Length == 0 ? "Export" : stem.Replace('_', ' ').Replace('-', ' ');
    }

    private static string SanitizeFileName(string? name, string ext)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "export" : name.Trim();
        foreach (var bad in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(bad, '_');
        if (baseName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^(ext.Length + 1)];
        return $"{baseName}.{ext}";
    }

    private static ExportArtifactResponse ToResponse(ExportArtifact a) => new()
    {
        ExportArtifactId = a.ExportArtifactId,
        FileName = a.FileName,
        ContentType = a.ContentType,
        SizeBytes = a.SizeBytes,
        DownloadUrl = $"/api/export/{a.ExportArtifactId}/download",
        CreatedAt = a.CreatedAt,
    };
}
