using System.Text;
using nashira_backend.Services.Export;

namespace nashira_backend.Tests;

// The report path: markdown in, PDF/HTML out. The parser is where a report quietly
// loses its shape (a table read as a paragraph, a fence read as markup), so the block
// structure is pinned here rather than only the bytes that come out the far end.
public class DocumentExportTests
{
    static DocumentExportTests()
    {
        // QuestPDF requires a licence; Program.cs sets it at boot, tests don't run Program.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private const string Report = """
        # Workflow report

        The workflow **ping-8.8.8.8-hourly** runs every hour.

        ## Schedule

        | Field | Value |
        |---|---|
        | Cron | `0 * * * *` |
        | Timezone | UTC |

        ## Notes

        - Target `8.8.8.8`, port 53
        - Owner: net-ops
          continued on the next line

        ```bash
        ping -c 1 8.8.8.8
        ```

        > Retained for 30 days.
        """;

    [Fact]
    public void Parse_keeps_each_block_in_its_own_shape()
    {
        var blocks = MarkdownDoc.Parse(Report);

        Assert.Collection(blocks,
            b => Assert.Equal(1, Assert.IsType<MdHeading>(b).Level),
            b => Assert.IsType<MdParagraph>(b),
            b => Assert.Equal(2, Assert.IsType<MdHeading>(b).Level),
            b =>
            {
                var table = Assert.IsType<MdTable>(b);
                Assert.Equal(2, table.Headers.Count);
                Assert.Equal(2, table.Rows.Count);
                // The delimiter row is structure, never a data row.
                Assert.Equal("Cron", MarkdownDoc.PlainText(table.Rows[0][0]));
                Assert.Equal("0 * * * *", MarkdownDoc.PlainText(table.Rows[0][1]));
            },
            b => Assert.Equal(2, Assert.IsType<MdHeading>(b).Level),
            b =>
            {
                var list = Assert.IsType<MdList>(b);
                Assert.False(list.Ordered);
                Assert.Equal(2, list.Items.Count);
                // An indented line continues the item above it instead of starting a block.
                Assert.Contains("continued on the next line", MarkdownDoc.PlainText(list.Items[1].Inline));
            },
            b =>
            {
                var code = Assert.IsType<MdCode>(b);
                Assert.Equal("bash", code.Language);
                Assert.Equal("ping -c 1 8.8.8.8", code.Text);
            },
            b => Assert.Contains("Retained", MarkdownDoc.PlainText(Assert.IsType<MdQuote>(b).Inline)));
    }

    [Fact]
    public void Parse_marks_inline_runs_and_leaves_stray_markers_alone()
    {
        var spans = MarkdownDoc.ParseInline("**bold** and `code` and *soft* and snake_case_name and 2*3");

        Assert.Contains(spans, s => s is { Text: "bold", Bold: true });
        Assert.Contains(spans, s => s is { Text: "code", Code: true });
        Assert.Contains(spans, s => s is { Text: "soft", Italic: true });
        // An underscore inside a word and a lone asterisk are data, not emphasis.
        var plain = MarkdownDoc.PlainText(spans);
        Assert.Contains("snake_case_name", plain);
        Assert.Contains("2*3", plain);
    }

    [Theory]
    [InlineData("[docs](https://example.com/x)", "https://example.com/x")]
    [InlineData("[x](javascript:alert(1))", null)]
    [InlineData("[y](/api/secrets/1)", null)]
    public void Parse_only_keeps_http_links_live(string markdown, string? expected)
    {
        // `javascript:alert(1)` closes the destination on its own paren, so the tail can
        // spill into a second span. What matters is that no span comes out clickable.
        var spans = MarkdownDoc.ParseInline(markdown);

        Assert.Equal(expected, spans[0].Href);
        Assert.All(spans, s => Assert.True(s.Href is null || s.Href.StartsWith("http", StringComparison.Ordinal)));
    }

    [Fact]
    public void Parse_does_not_read_a_fenced_block_as_markup()
    {
        // CLI output is full of pipes, hashes and asterisks. Inside a fence they are text.
        var blocks = MarkdownDoc.Parse("```\n| not | a | table |\n# not a heading\n```");

        var code = Assert.IsType<MdCode>(Assert.Single(blocks));
        Assert.Contains("| not | a | table |", code.Text);
        Assert.Contains("# not a heading", code.Text);
    }

    [Fact]
    public void Pdf_is_a_real_pdf_and_carries_the_title()
    {
        var pdf = DocumentPdfBuilder.Build("Workflow report", MarkdownDoc.Parse(Report),
            new DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc));

        // %PDF- header and %%EOF trailer: what every reader checks before anything else.
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf[^32..]));
        Assert.True(pdf.Length > 2_000, $"suspiciously small PDF: {pdf.Length} bytes");
    }

    [Fact]
    public void Pdf_renders_an_empty_document_rather_than_throwing()
    {
        // A report whose body parsed to nothing still has to produce a file — an
        // exception here would surface to the user as "the export failed".
        var pdf = DocumentPdfBuilder.Build("Empty", MarkdownDoc.Parse("   "), DateTime.UtcNow);

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void Html_document_escapes_content_and_stays_self_contained()
    {
        var html = DocumentHtmlBuilder.Build(
            "Report", MarkdownDoc.Parse("<script>alert(1)</script>\n\n| a |\n|---|\n| <b>x</b> |"),
            DateTime.UtcNow);

        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
        // Self-contained: styles inline, nothing fetched when the file is opened.
        Assert.Contains("<style>", html);
        Assert.DoesNotContain("<link", html);
    }

    [Fact]
    public void Table_rows_become_a_document_table_in_column_order()
    {
        // export_table with format=pdf goes through here. A row missing a key must leave
        // an empty cell, not shift the remaining values one column to the left.
        var columns = new[] { "host", "ip" };
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["ip"] = "10.0.0.2" },
        };

        var table = ExportService.ToDocumentTable(columns, rows);

        Assert.Equal(["host", "ip"], table.Headers.Select(MarkdownDoc.PlainText));
        Assert.Equal("", MarkdownDoc.PlainText(table.Rows[0][0]));
        Assert.Equal("10.0.0.2", MarkdownDoc.PlainText(table.Rows[0][1]));
    }
}
