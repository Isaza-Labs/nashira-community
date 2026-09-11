using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Tools.Handlers;
using nashira_backend.Services.Export;
using nashira_backend.Services.Files;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace nashira_backend.Tests;

// parse_file's document formats: pdf (text per page, via PdfPig) and docx (body text
// plus tables as grids, via OpenXml). Fixtures are generated with the same libraries
// the product already ships — QuestPDF writes the PDF, OpenXml writes the DOCX — so
// the tests are true round-trips, not hand-crafted byte blobs.
public class ParseDocumentTests
{
    static ParseDocumentTests()
    {
        // QuestPDF requires a licence; Program.cs sets it at boot, tests don't run Program.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static (ParseFileHandler Handler, AgentTurnScope Scope) Build()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"parse-doc-{Guid.NewGuid()}")
            .Options);
        var scope = new AgentTurnScope();
        return (new ParseFileHandler(new FileParsingService(), scope, db), scope);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static byte[] Pdf(string markdown) =>
        DocumentPdfBuilder.Build("Fixture", MarkdownDoc.Parse(markdown), DateTime.UtcNow);

    private static byte[] Docx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Test matrix overview"))),
                new W.Table(
                    new W.TableRow(Cell("feature"), Cell("tested")),
                    new W.TableRow(Cell("netbox sync"), Cell("no"))),
                new W.Paragraph(new W.Run(new W.Text("End of document")))));
        }
        return ms.ToArray();

        static W.TableCell Cell(string t) => new(new W.Paragraph(new W.Run(new W.Text(t))));
    }

    [Fact]
    public async Task Parses_a_pdf_attachment_into_page_text()
    {
        var (handler, scope) = Build();
        scope.AddAttachment("report.pdf", Pdf("The quick brown fox audits the inventory."));

        var result = await handler.ExecuteAsync(
            Args("""{"format":"pdf","attachment":"report.pdf"}"""), default);

        Assert.Equal("pdf", result.GetProperty("format").GetString());
        Assert.True(result.GetProperty("page_count").GetInt32() >= 1);
        var text = result.GetProperty("pages")[0].GetProperty("text").GetString();
        Assert.Contains("quick brown fox", text);
    }

    [Fact]
    public async Task Pdf_page_parameter_fetches_one_page_and_rejects_out_of_range()
    {
        var (handler, scope) = Build();
        scope.AddAttachment("report.pdf", Pdf("Single page content."));

        var one = await handler.ExecuteAsync(
            Args("""{"format":"pdf","attachment":"report.pdf","page":1}"""), default);
        Assert.Equal(1, one.GetProperty("page").GetInt32());
        Assert.Contains("Single page", one.GetProperty("text").GetString());

        var bad = await handler.ExecuteAsync(
            Args("""{"format":"pdf","attachment":"report.pdf","page":99}"""), default);
        Assert.Contains("between 1 and", bad.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Parses_a_docx_attachment_into_text_and_tables()
    {
        var (handler, scope) = Build();
        scope.AddAttachment("matrix.docx", Docx());

        var result = await handler.ExecuteAsync(
            Args("""{"format":"docx","attachment":"matrix.docx"}"""), default);

        Assert.Equal("docx", result.GetProperty("format").GetString());
        var text = result.GetProperty("text").GetString();
        Assert.Contains("Test matrix overview", text);
        Assert.Contains("[table 1]", text);
        Assert.Contains("End of document", text);

        Assert.Equal(1, result.GetProperty("table_count").GetInt32());
        var table = result.GetProperty("tables")[0];
        Assert.Equal("feature", table.GetProperty("columns")[0].GetString());
        Assert.Equal(1, table.GetProperty("row_count").GetInt32());
        Assert.Equal("no", table.GetProperty("rows")[0].GetProperty("tested").GetString());
    }

    [Fact]
    public async Task A_legacy_doc_file_fails_with_a_parse_error_not_a_crash()
    {
        var (handler, scope) = Build();
        // OLE2 magic bytes — what a Word 97-2003 .doc actually starts with.
        scope.AddAttachment("old.doc", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        var result = await handler.ExecuteAsync(
            Args("""{"format":"docx","attachment":"old.doc"}"""), default);

        Assert.Contains("failed to parse docx", result.GetProperty("error").GetString());
    }
}
