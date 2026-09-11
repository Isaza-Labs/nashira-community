using nashira_backend.Services.Files;

namespace nashira_backend.Tests;

public class FileParsingTests
{
    [Fact]
    public void ParseCsvRecords_handles_quotes_commas_and_embedded_newlines()
    {
        var csv = "name,note\n" +
                  "\"R1\",\"a, b\"\n" +
                  "\"R2\",\"line1\nline2\"\n" +
                  "\"R3\",\"he said \"\"hi\"\"\"";

        var records = FileParsingService.ParseCsvRecords(csv);

        Assert.Equal(4, records.Count);
        Assert.Equal(new[] { "name", "note" }, records[0]);
        Assert.Equal(new[] { "R1", "a, b" }, records[1]);
        Assert.Equal(new[] { "R2", "line1\nline2" }, records[2]);
        Assert.Equal(new[] { "R3", "he said \"hi\"" }, records[3]);
    }

    [Fact]
    public void ParseCsv_with_header_maps_rows_to_objects()
    {
        var table = new FileParsingService().ParseCsv("host,ip\nr1,10.0.0.1\nr2,10.0.0.2\n", hasHeader: true);

        Assert.Equal(new[] { "host", "ip" }, table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("r1", table.Rows[0]["host"]);
        Assert.Equal("10.0.0.2", table.Rows[1]["ip"]);
    }

    [Fact]
    public void ParseCsv_without_header_synthesizes_columns()
    {
        var table = new FileParsingService().ParseCsv("a,b,c\nd,e,f", hasHeader: false);

        Assert.Equal(new[] { "col1", "col2", "col3" }, table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("a", table.Rows[0]["col1"]);
        Assert.Equal("f", table.Rows[1]["col3"]);
    }
}
