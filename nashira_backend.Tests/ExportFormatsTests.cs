using nashira_backend.Services.Export;

namespace nashira_backend.Tests;

// The html/markdown exporters were restored after the port from netora_agent
// dropped them. Both build documents by string concatenation, so escaping is the
// thing worth pinning: an unescaped cell corrupts the document silently.
public class ExportFormatsTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("say \"hi\"", "say &quot;hi&quot;")]
    [InlineData(null, "")]
    public void HtmlEscape_neutralizes_markup(string? input, string expected)
    {
        Assert.Equal(expected, ExportService.HtmlEscape(input));
    }

    [Fact]
    public void MarkdownEscape_protects_the_pipe_table()
    {
        // A raw pipe would add a phantom column; a raw newline would split the row.
        Assert.Equal(@"a\|b", ExportService.MarkdownEscape("a|b"));
        Assert.Equal("line1<br>line2", ExportService.MarkdownEscape("line1\nline2"));
        Assert.Equal("line1<br>line2", ExportService.MarkdownEscape("line1\r\nline2"));
        Assert.Equal("", ExportService.MarkdownEscape(null));
    }

    [Fact]
    public void CsvEscape_quotes_only_when_needed()
    {
        Assert.Equal("plain", ExportService.CsvEscape("plain"));
        Assert.Equal("\"a,b\"", ExportService.CsvEscape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ExportService.CsvEscape("say \"hi\""));
    }
}
