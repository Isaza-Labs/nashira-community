using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Export;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// The written counterpart to export_table. Reports — an incident write-up, a workflow
// summary, a runbook — are prose with a table or two inside, and squeezing one through
// a rows-only tool produced either a one-column table or nothing at all: the agent
// would answer "PDF is not available" and hand over an HTML table instead.
public sealed class ExportDocumentHandler : IToolHandler
{
    private const int MaxContentChars = 400_000;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "format":{"type":"string","enum":["pdf","docx","html","markdown"],"default":"pdf"},
          "filename":{"type":"string","description":"Base file name (extension is added automatically)"},
          "title":{"type":"string","description":"Document title; defaults to the file name"},
          "content":{"type":"string","description":"The report body in markdown: #/##/### headings, paragraphs, - and 1. lists, | pipe | tables |, ``` fenced code, > quotes, **bold**, `code`"}
        },"required":["content"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IExportService _export;
    public ExportDocumentHandler(IExportService export) => _export = export;

    public string Name => "export_document";
    public string Description =>
        "Renders a written report from markdown into a document file and returns a download link. " +
        "Formats: pdf (default, paginated A4), docx (a Word document the user can keep editing), " +
        "html (self-contained) and markdown. Prefer docx when the user says they will edit, " +
        "review or hand the document on; pdf when it is final. Use it whenever " +
        "the user asks for a report, a summary or a document — anything that is prose, or prose with " +
        "tables inside. For a bare data table use export_table instead. The returned " +
        "export_artifact_id can be re-read later with read_report, but only when the format is " +
        "markdown or html — a pdf or a docx is a rendering and read_report does not recover its " +
        "text today, so use save_report for anything that has to be readable again. (The parser " +
        "behind parse_file can read both; wiring read_report to it is tracked separately.)";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var content = Str(args, "content");
            if (string.IsNullOrWhiteSpace(content))
                return Err("content must be a non-empty markdown document");
            if (content.Length > MaxContentChars)
                return Err($"content is too long ({content.Length} chars); the limit is {MaxContentChars}");

            var res = await _export.CreateDocumentAsync(
                Str(args, "format") ?? "pdf", Str(args, "filename"), Str(args, "title"), content, ct);
            return JsonSerializer.SerializeToElement(res);
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
