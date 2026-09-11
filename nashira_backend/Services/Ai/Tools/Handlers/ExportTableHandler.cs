using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Export;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class ExportTableHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "format":{"type":"string","enum":["csv","json","xlsx","docx","pdf","html","markdown"],"default":"csv"},
          "filename":{"type":"string","description":"Base file name (extension is added automatically)"},
          "columns":{"type":"array","items":{"type":"string"},"description":"Column order/allow-list; inferred from the rows if omitted"},
          "rows":{"type":"array","items":{"type":"object"},"description":"Array of flat objects, one per row"}
        },"required":["rows"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IExportService _export;
    public ExportTableHandler(IExportService export) => _export = export;

    public string Name => "export_table";
    public string Description =>
        "Exports tabular data (an array of flat objects) to a file and returns a download link. " +
        "Formats: csv, json (an array of objects, values as strings), xlsx (a spreadsheet to be " +
        "worked in), docx and pdf (a table meant to be read rather than edited), html " +
        "(self-contained, email-friendly) and markdown. Pick by what the user will DO with it: " +
        "xlsx to edit or pivot, csv or json to feed another system, docx to paste into a written " +
        "document, pdf to send or print. Use it to hand query results back to the user as a file. " +
        "For prose — a report, a summary, a write-up — use export_document.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var rows = ReadRows(args);
            if (rows.Count == 0) return Err("rows must be a non-empty array of objects");
            var res = await _export.CreateAsync(Str(args, "format") ?? "csv", Str(args, "filename"),
                ReadStringArray(args, "columns"), rows, ct);
            return JsonSerializer.SerializeToElement(res);
        }
        catch (DomainException ex)
        {
            return Err(ex.Message);
        }
    }

    private static List<IReadOnlyDictionary<string, string?>> ReadRows(JsonElement args)
    {
        var list = new List<IReadOnlyDictionary<string, string?>>();
        if (args.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var prop in row.EnumerateObject())
                    dict[prop.Name] = CellToString(prop.Value);
                list.Add(dict);
            }
        }
        return list;
    }

    private static string? CellToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(), // numbers, and arrays/objects as compact JSON
    };

    private static List<string>? ReadStringArray(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var el in a.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s) list.Add(s);
        return list.Count > 0 ? list : null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
