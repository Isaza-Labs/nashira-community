using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using nashira_backend.Services.Export;
using nashira_backend.Services.Files;

namespace nashira_backend.Tests;

// Word and JSON were the two formats this product could READ and not WRITE:
// parse_file handles csv, xlsx, pdf, docx, json and text, while the export side
// stopped at csv/xlsx/pdf/html/markdown.
//
// The docx assertions read the generated file back with the same parser
// parse_file uses. Asserting that a byte array is non-empty proves the writer
// ran, not that anything can open what it wrote.
public class ExportDocxAndJsonTests
{
    private static readonly List<string> Columns = ["device", "ip", "status"];

    private static readonly List<IReadOnlyDictionary<string, string?>> Rows =
    [
        new Dictionary<string, string?> { ["device"] = "core-1", ["ip"] = "10.0.0.1", ["status"] = "up" },
        new Dictionary<string, string?> { ["device"] = "edge-2", ["ip"] = "10.0.0.2", ["status"] = null },
    ];

    // ── JSON ────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_is_an_array_of_objects_one_per_row()
    {
        var json = Encoding.UTF8.GetString(ExportService.BuildJson(Columns, Rows));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("core-1", doc.RootElement[0].GetProperty("device").GetString());
    }

    [Fact]
    public void Json_keeps_every_column_on_every_row()
    {
        // Rectangular on purpose: a consumer reads it as a table without testing
        // each object for the keys it expects. The second row has no status.
        var json = Encoding.UTF8.GetString(ExportService.BuildJson(Columns, Rows));
        using var doc = JsonDocument.Parse(json);
        var second = doc.RootElement[1];

        Assert.Equal(JsonValueKind.Null, second.GetProperty("status").ValueKind);
        Assert.Equal(Columns, second.EnumerateObject().Select(p => p.Name).ToList());
    }

    [Fact]
    public void Json_does_not_guess_types()
    {
        // The failure this avoids: a leading-zero asset tag becoming a number, an
        // octet becoming a float, a version "1.10" becoming 1.1.
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["tag"] = "007", ["version"] = "1.10" },
        };

        var json = Encoding.UTF8.GetString(ExportService.BuildJson(["tag", "version"], rows));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("007", doc.RootElement[0].GetProperty("tag").GetString());
        Assert.Equal("1.10", doc.RootElement[0].GetProperty("version").GetString());
    }

    [Fact]
    public void Json_of_no_rows_is_an_empty_array_not_nothing()
    {
        var json = Encoding.UTF8.GetString(ExportService.BuildJson(Columns, []));

        Assert.Equal("[]", json.Trim());
    }

    // ── DOCX ────────────────────────────────────────────────────────────────

    private static string DocxText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        return doc.MainDocumentPart!.Document.Body!.InnerText;
    }

    [Fact]
    public void A_docx_opens_as_a_word_document()
    {
        var bytes = DocumentDocxBuilder.Build(
            "Informe", MarkdownDoc.Parse("# Title\n\nBody text."), DateTime.UtcNow);

        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        Assert.NotNull(doc.MainDocumentPart?.Document?.Body);
    }

    [Fact]
    public void A_docx_carries_the_prose_it_was_given()
    {
        var bytes = DocumentDocxBuilder.Build(
            "Informe", MarkdownDoc.Parse("# Resumen\n\nCinco equipos respondieron."), DateTime.UtcNow);

        var text = DocxText(bytes);

        Assert.Contains("Informe", text);
        Assert.Contains("Resumen", text);
        Assert.Contains("Cinco equipos respondieron.", text);
    }

    [Fact]
    public void A_docx_carries_a_table_as_a_table_not_as_text()
    {
        // A generated report is mostly tables, and a table flattened into a
        // paragraph is the difference between a document and a transcript.
        var md = "| device | status |\n| --- | --- |\n| core-1 | up |\n";
        var bytes = DocumentDocxBuilder.Build("Informe", MarkdownDoc.Parse(md), DateTime.UtcNow);

        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var tables = doc.MainDocumentPart!.Document.Body!
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().ToList();

        Assert.Single(tables);
        Assert.Contains("core-1", tables[0].InnerText);
        Assert.Contains("device", tables[0].InnerText);
    }

    [Fact]
    public void The_parser_this_product_already_has_can_read_the_docx_it_now_writes()
    {
        // The point of the whole change in one assertion: the format it can read is
        // the format it can write, and the two agree.
        var md = "# Informe\n\nTexto.\n\n| device | status |\n| --- | --- |\n| core-1 | up |\n";
        var bytes = DocumentDocxBuilder.Build("Informe", MarkdownDoc.Parse(md), DateTime.UtcNow);

        var parsed = new FileParsingService().ParseDocx(bytes, hasHeader: true);

        Assert.Contains(parsed.Pages, p => p.Contains("Texto."));
        Assert.Single(parsed.Tables);
        Assert.Contains("device", parsed.Tables[0].Columns);
        Assert.Equal("core-1", parsed.Tables[0].Rows[0]["device"]);
    }
}
