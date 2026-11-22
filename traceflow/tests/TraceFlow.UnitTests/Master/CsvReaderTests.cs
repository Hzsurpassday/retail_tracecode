using TraceFlow.Domain.Master;

namespace TraceFlow.UnitTests.Master;

public class CsvReaderTests
{
    private static readonly string[] Cols = { "spid", "name" };

    [Fact]
    public void Parses_header_and_rows_with_quotes()
    {
        var rows = CsvReader.Parse("spid,name\nSP0001,\"阿莫西林,胶囊\"\nSP0002,\"含\"\"引号\"\"\"\n", Cols);
        Assert.Equal(2, rows.Count);
        Assert.Equal("阿莫西林,胶囊", rows[0]["name"]);
        Assert.Equal("含\"引号\"", rows[1]["name"]);
    }

    [Fact]
    public void Bom_and_crlf_accepted()
    {
        var rows = CsvReader.Parse("\uFEFFspid,name\r\nSP0001,盒\r\n", Cols);
        Assert.Single(rows);
        Assert.Equal("SP0001", rows[0]["spid"]);
    }

    [Fact]
    public void Missing_column_throws_with_row_context()
    {
        var ex = Assert.Throws<CsvFormatException>(() => CsvReader.Parse("spid,other\nA,B\n", Cols));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Wrong_field_count_throws_with_row_number()
    {
        var ex = Assert.Throws<CsvFormatException>(() => CsvReader.Parse("spid,name\nA\n", Cols));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Empty_lines_skipped_values_trimmed()
    {
        var rows = CsvReader.Parse("spid,name\n\n SP0001 , 盒 \n\n", Cols);
        Assert.Single(rows);
        Assert.Equal("SP0001", rows[0]["spid"]);
        Assert.Equal("盒", rows[0]["name"]);
    }
}
