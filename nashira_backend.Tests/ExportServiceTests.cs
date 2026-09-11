using System.Text;
using nashira_backend.Services.Export;

namespace nashira_backend.Tests;

public class ExportServiceTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("he \"q\"", "\"he \"\"q\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData(null, "")]
    public void CsvEscape_quotes_only_when_needed(string? input, string expected) =>
        Assert.Equal(expected, ExportService.CsvEscape(input));

    [Fact]
    public void BuildCsv_writes_header_and_orders_cells_by_column()
    {
        var columns = new[] { "host", "ip" };
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["host"] = "r1", ["ip"] = "10.0.0.1" },
            new Dictionary<string, string?> { ["ip"] = "10.0.0.2" }, // missing host -> empty cell
        };

        var text = Encoding.UTF8.GetString(ExportService.BuildCsv(columns, rows));

        Assert.Contains("host,ip", text);
        Assert.Contains("r1,10.0.0.1", text);
        Assert.Contains(",10.0.0.2", text);
    }
}
