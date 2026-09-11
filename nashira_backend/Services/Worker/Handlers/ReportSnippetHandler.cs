using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Common;
using nashira_backend.Services.Export;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Lets a workflow step produce a report — the counterpart of FlowWeaver's `report`
// snippet, built on Nashira's own pieces instead of a ported engine: the body is
// markdown (the same contract as the agent's export_document / save_report tools),
// rendered by the shared document builders and persisted as a ReportArtifact, so a
// run's report lands in /reports next to everything else, stamped with the run and
// workflow that produced it.
//
// Input (the node's config_overrides, resolved before dispatch), per workflow.v1
// snippets/SPEC.md — either body form:
//   {
//     "title":       "Nightly config audit",     // required (or document.title)
//     "content":     "# markdown body …",        // Nashira's body: a markdown string
//     "document":    { title, subtitle?, badge?, stats?: [{label, value, hint?}],
//                      sections: [{ title|heading, description?, markdown?|text?,
//                                   tables?: [{caption?, headers, rows}], table?,
//                                   callouts?: [{title, body, tone?}] }] },
//                                                // FlowWeaver's structured document,
//                                                // rendered to markdown first
//     "format":      "markdown" | "html" | "pdf"  // default markdown: the whole
//                  | "csv" | "xlsx"                // document, prose included
//                                                  // csv/xlsx: the document's
//                                                  // markdown tables only — see
//                                                  // "Tabular formats" below
//     "description": "one line for /reports",    // optional
//     "file_name":   "audit.md",                 // optional; defaults to a slug of the title
//     "retain_days": 30                          // optional; omit to keep indefinitely
//   }
//
// Tabular formats. A report's body is prose; csv and xlsx are not. The contract
// lists both, so this handler exports the only genuinely tabular thing a report
// contains: the markdown tables in its body (which is also where FlowWeaver's
// `document.sections[].tables` and its `stats` block land, since RenderDocument
// writes them as markdown tables). One table becomes one sheet — named after the
// heading it sits under — and, in csv, one header row plus its rows; several
// tables become several sheets, and a csv that stacks them under their names with
// a blank line between. A report with no table at all is refused with
// `not_supported` naming the reason: a one-column sheet of prose lines would be a
// spreadsheet in name only, and an author who asked for xlsx and got that would
// send the wrong file to whoever reads it.
//
// Output (for {{ steps.X.output.Y }}):
//   { report_artifact_id, title, file_name, filename, content_type, format,
//     size_bytes, sha256, download_url, base64 }
//
// `base64` is the raw file bytes, so a downstream email_send node can attach the
// document via templating — the same relay pattern FlowWeaver uses.
public sealed class ReportSnippetHandler : ISnippetHandler
{
    // Matches save_report's cap; a step that wants more is generating data, not a report.
    private const int MaxBytes = 10 * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly ILogger<ReportSnippetHandler> _logger;

    public ReportSnippetHandler(AppDbContext db, ILogger<ReportSnippetHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeReport;

    // Persisting an artifact is a side effect, but retention reaps it and
    // re-running just writes another row — cheap to repeat, nothing to undo.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var input = request.Input;
        var document = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.Object
                ? d : (JsonElement?)null;

        var title = (Str(input, "title") ?? (document is { } doc ? Str(doc, "title") : null))?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return SnippetResult.Fail("report needs a `title` (on the payload or on `document`)", "bad_input");

        // `document` is the CANONICAL key — FlowWeaver's structured form, rendered to
        // markdown so one pipeline serves both — and `content` is the alias
        // (snippets/SPEC.md, `report`). The canonical one wins when a node carries
        // both. This read used to prefer `content`, so a node that had been given a
        // structured document and kept its old markdown string rendered the markdown
        // and silently discarded the document — which for `csv` and `xlsx` also meant
        // `document.stats` and `document.sections[].tables` never landed, and the step
        // could fail with `not_supported` for having no table when it had several.
        var content = document is { } structured ? RenderDocument(structured) : null;
        if (string.IsNullOrWhiteSpace(content)) content = Str(input, "content");
        if (string.IsNullOrWhiteSpace(content))
            return SnippetResult.Fail("report needs `content` (a markdown body) or `document` (a structured document)", "bad_input");

        var format = (Str(input, "format") ?? "markdown").Trim().ToLowerInvariant();
        var generatedAt = DateTime.UtcNow;

        byte[] bytes;
        string contentType;
        string ext;
        try
        {
            switch (format)
            {
                case "pdf":
                    bytes = DocumentPdfBuilder.Build(title!, MarkdownDoc.Parse(content!), generatedAt);
                    contentType = "application/pdf";
                    ext = "pdf";
                    break;
                case "html":
                    bytes = new UTF8Encoding(false).GetBytes(
                        DocumentHtmlBuilder.Build(title!, MarkdownDoc.Parse(content!), generatedAt));
                    contentType = "text/html; charset=utf-8";
                    ext = "html";
                    break;
                case "md":
                case "markdown":
                    // Passthrough: the content already is the document. Only the title
                    // is added, and only when the author did not open with one.
                    var body = content!.TrimStart().StartsWith("# ", StringComparison.Ordinal)
                        ? content!
                        : $"# {title}{Environment.NewLine}{Environment.NewLine}{content}";
                    bytes = new UTF8Encoding(false).GetBytes(body);
                    contentType = "text/markdown";
                    ext = "md";
                    break;
                case "csv":
                case "xlsx":
                {
                    // Only the tables travel: they are the tabular part of the
                    // document, and the part someone asking for a sheet wants.
                    var tables = ExtractTables(content!);
                    if (tables.Count == 0)
                        return SnippetResult.Fail(
                            $"report format '{format}' exports the report's tables, and this report has none — "
                            + "add a markdown table to `content` (or `document.sections[].tables` / "
                            + "`document.stats`), or ask for 'markdown', 'html' or 'pdf' to keep the prose",
                            "not_supported");

                    if (format == "csv")
                    {
                        bytes = BuildCsv(tables);
                        contentType = "text/csv";
                        ext = "csv";
                    }
                    else
                    {
                        bytes = BuildXlsx(tables);
                        contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                        ext = "xlsx";
                    }
                    break;
                }
                default:
                    return SnippetResult.Fail(
                        $"unknown format '{format}' — use 'markdown', 'html', 'pdf', 'csv' or 'xlsx'", "bad_input");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow.report.render_failed format={Format}", format);
            return SnippetResult.Fail($"could not render the {format} document: {ex.Message}", "render_failed");
        }

        if (bytes.Length > MaxBytes)
            return SnippetResult.Fail(
                $"the rendered report is {bytes.Length / (1024 * 1024)} MB; the limit is {MaxBytes / (1024 * 1024)} MB",
                "too_large");

        var retainDays = input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("retain_days", out var rd) && rd.TryGetInt32(out var rdv) && rdv > 0
                ? rdv
                : (int?)null;

        var fileName = Str(input, "file_name")?.Trim();
        var row = new ReportArtifact
        {
            ReportArtifactId = Guid.NewGuid(),
            Title = title!,
            Description = Str(input, "description")?.Trim(),
            ContentType = contentType,
            FileName = string.IsNullOrWhiteSpace(fileName) ? $"{Slug.From(title!)}.{ext}" : fileName!,
            Content = bytes,
            SizeBytes = bytes.Length,
            // The pre-allocated run identity — the whole reason the scope carries it.
            WorkflowRunId = request.WorkflowRunId,
            WorkflowId = request.WorkflowId == Guid.Empty ? null : request.WorkflowId,
            ExpiresAt = retainDays is { } days ? generatedAt.AddDays(days) : null,
            CreatedBy = request.TriggeredBy,
            IsActive = true,
            CreatedAt = generatedAt,
            UpdatedAt = generatedAt,
        };
        _db.ReportArtifacts.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "workflow.report.ok report_id={ReportId} format={Format} size_bytes={SizeBytes}",
            row.ReportArtifactId, format, row.SizeBytes);

        return SnippetResult.Ok(new
        {
            report_artifact_id = row.ReportArtifactId,
            title = row.Title,
            file_name = row.FileName,
            // The contract's spelling beside Nashira's.
            filename = row.FileName,
            content_type = row.ContentType,
            format,
            size_bytes = row.SizeBytes,
            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            download_url = $"/api/reports/{row.ReportArtifactId}/download",
            // Raw file bytes for a downstream email_send attachment. Bounded by the
            // 10 MB cap above, so the step output stays storable.
            base64 = Convert.ToBase64String(bytes),
        }, StepChange.Changed, logs: $"report '{row.Title}' → {row.FileName} ({format}, {row.SizeBytes} bytes)");
    }

    // ─── tabular formats ───────────────────────────────────────

    // One markdown table, flattened to text and named after the heading it sits
    // under — the sheet name in xlsx, the label above the block in a multi-table csv.
    private sealed record ReportTable(
        string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

    // Pulls the tables out of the report's markdown, in document order. Cells are
    // flattened to plain text: **bold** and `code` are presentation, and a
    // spreadsheet cell holds the value, not the markup around it.
    private static List<ReportTable> ExtractTables(string markdown)
    {
        var tables = new List<ReportTable>();
        string? heading = null;

        foreach (var block in MarkdownDoc.Parse(markdown))
        {
            switch (block)
            {
                case MdHeading h:
                    // The nearest heading above a table is what a reader would call
                    // it; the H1 is the report title, which names the first table
                    // only if nothing more specific comes between.
                    heading = MarkdownDoc.PlainText(h.Inline).Trim();
                    break;
                case MdTable t:
                    tables.Add(new ReportTable(
                        string.IsNullOrWhiteSpace(heading) ? $"Table {tables.Count + 1}" : heading!,
                        t.Headers.Select(c => MarkdownDoc.PlainText(c).Trim()).ToList(),
                        t.Rows
                            .Select(r => (IReadOnlyList<string>)r
                                .Select(c => MarkdownDoc.PlainText(c).Trim()).ToList())
                            .ToList()));
                    break;
            }
        }

        return tables;
    }

    // Quoting is ExportService's — the same rules an /api/export csv follows, so a
    // report's csv and an export's csv open identically. The rows are built here
    // rather than through ExportService.BuildCsv because that one keys cells by
    // column name, and a markdown table's headers are positional: two columns may
    // share a name, or have none, and a dictionary would silently drop one.
    private static byte[] BuildCsv(IReadOnlyList<ReportTable> tables)
    {
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            // A single table is a plain csv, nothing else in the file. Several get a
            // name line and a blank line between them: no csv dialect expresses more
            // than one table, so the honest thing is to say where each one starts.
            if (sb.Length > 0) sb.AppendLine();
            if (tables.Count > 1) sb.AppendLine(ExportService.CsvEscape(table.Name));

            sb.AppendLine(string.Join(',', table.Headers.Select(h => ExportService.CsvEscape(h))));
            foreach (var row in table.Rows)
                sb.AppendLine(string.Join(',', row.Select(c => ExportService.CsvEscape(c))));
        }
        // UTF-8 BOM so Excel opens accented text correctly — as ExportService does.
        return new UTF8Encoding(true).GetBytes(sb.ToString());
    }

    // One worksheet per table, through the same ClosedXML the export service uses.
    // ExportService.BuildXlsx writes a single sheet from one column set, which a
    // report with several tables does not have, so the sheet loop lives here.
    private static byte[] BuildXlsx(IReadOnlyList<ReportTable> tables)
    {
        using var wb = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            var ws = wb.Worksheets.Add(SheetName(table.Name, used));
            for (var c = 0; c < table.Headers.Count; c++)
                ws.Cell(1, c + 1).Value = table.Headers[c];
            for (var r = 0; r < table.Rows.Count; r++)
                for (var c = 0; c < table.Rows[r].Count; c++)
                    ws.Cell(r + 2, c + 1).Value = table.Rows[r][c];
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // Excel's own rules: at most 31 characters, none of []:*?/\, non-empty, unique
    // within the workbook. A heading that breaks any of them is trimmed rather than
    // rejected — the sheet is the report's content, not its title.
    private static string SheetName(string raw, HashSet<string> used)
    {
        var cleaned = new string(raw.Where(c => !"[]:*?/\\".Contains(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Table";
        if (cleaned.Length > 31) cleaned = cleaned[..31];

        var candidate = cleaned;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = cleaned[..Math.Min(cleaned.Length, 31 - suffix.Length)] + suffix;
        }
        return candidate;
    }

    // FlowWeaver's ReportDocument as markdown: the title becomes the H1 (the
    // markdown branch above adds it), subtitle and badge a lead line, stats a table,
    // each section an H2 with its prose, GFM tables and callouts as blockquotes.
    // Lenient on purpose about the section keys — `heading`/`title`, `markdown`/
    // `text`/`description`, one `table` or many `tables` — because the two products'
    // authors write both, and a report that renders slightly plainer beats one that
    // refuses.
    internal static string RenderDocument(JsonElement doc)
    {
        var sb = new StringBuilder();
        var title = Str(doc, "title");
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("# ").AppendLine(title!.Trim()).AppendLine();

        var badge = Str(doc, "badge");
        var subtitle = Str(doc, "subtitle");
        if (!string.IsNullOrWhiteSpace(badge)) sb.Append('`').Append(badge!.Trim()).AppendLine("`").AppendLine();
        if (!string.IsNullOrWhiteSpace(subtitle)) sb.Append('*').Append(subtitle!.Trim()).AppendLine("*").AppendLine();

        if (doc.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Array
            && stats.GetArrayLength() > 0)
        {
            sb.AppendLine("| Metric | Value | Note |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var s in stats.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                sb.Append("| ").Append(Cell(Text(s, "label"))).Append(" | ").Append(Cell(Text(s, "value")))
                  .Append(" | ").Append(Cell(Text(s, "hint"))).AppendLine(" |");
            }
            sb.AppendLine();
        }

        if (doc.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var section in sections.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object) continue;

                var heading = Str(section, "title") ?? Str(section, "heading");
                if (!string.IsNullOrWhiteSpace(heading)) sb.Append("## ").AppendLine(heading!.Trim()).AppendLine();

                foreach (var key in new[] { "description", "markdown", "text" })
                {
                    var prose = Str(section, key);
                    if (!string.IsNullOrWhiteSpace(prose)) sb.AppendLine(prose!.Trim()).AppendLine();
                }

                if (section.TryGetProperty("table", out var single) && single.ValueKind == JsonValueKind.Object)
                    AppendTable(sb, single);
                if (section.TryGetProperty("tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
                    foreach (var t in tables.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.Object) AppendTable(sb, t);

                if (section.TryGetProperty("callouts", out var callouts) && callouts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in callouts.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        var ctitle = Text(c, "title");
                        var body = Text(c, "body");
                        var tone = Str(c, "tone");
                        sb.Append("> ");
                        if (!string.IsNullOrWhiteSpace(tone) && !string.Equals(tone, "info", StringComparison.OrdinalIgnoreCase))
                            sb.Append('[').Append(tone!.Trim().ToUpperInvariant()).Append("] ");
                        if (ctitle.Length > 0) sb.Append("**").Append(ctitle).Append("**");
                        if (ctitle.Length > 0 && body.Length > 0) sb.Append(" — ");
                        sb.AppendLine(body.Replace("\r\n", "\n").Replace("\n", "\n> "));
                        sb.AppendLine();
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendTable(StringBuilder sb, JsonElement table)
    {
        var caption = Str(table, "caption");
        if (!string.IsNullOrWhiteSpace(caption)) sb.Append("**").Append(caption!.Trim()).AppendLine("**").AppendLine();

        var headers = new List<string>();
        if (table.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray()) headers.Add(Cell(Scalar(h)));

        var rows = new List<List<string>>();
        if (table.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rs.EnumerateArray())
            {
                var cells = new List<string>();
                if (r.ValueKind == JsonValueKind.Array)
                    foreach (var c in r.EnumerateArray()) cells.Add(Cell(Scalar(c)));
                else if (r.ValueKind == JsonValueKind.Object && headers.Count > 0)
                    // Rows as objects keyed by header, a shape agents produce.
                    foreach (var h in headers)
                        cells.Add(r.TryGetProperty(h, out var v) ? Cell(Scalar(v)) : string.Empty);
                else
                    cells.Add(Cell(Scalar(r)));
                rows.Add(cells);
            }
        }

        var width = Math.Max(headers.Count, rows.Count > 0 ? rows.Max(r => r.Count) : 0);
        if (width == 0) return;
        while (headers.Count < width) headers.Add(string.Empty);

        sb.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", width))).AppendLine(" |");
        foreach (var r in rows)
        {
            while (r.Count < width) r.Add(string.Empty);
            sb.Append("| ").Append(string.Join(" | ", r)).AppendLine(" |");
        }
        sb.AppendLine();
    }

    private static string Cell(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace("|", "\\|").Trim();

    private static string Text(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) ? Scalar(v) : string.Empty;

    private static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Object or JsonValueKind.Array => v.GetRawText(),
        _ => v.ToString(),
    };

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
