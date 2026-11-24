using TraceFlow.Application.Export;

namespace TraceFlow.UnitTests.Export;

/// <summary>
/// CSV 单元测试（DEF-07 / AT-074）：公式注入防护、引号包裹/双写、CRLF、BOM 与数值列规则。
/// 这些断言直接锁定 <see cref="Csv"/> 的规则顺序，端点与导出一致性测试在集成层覆盖。
/// </summary>
public class CsvTests
{
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+86")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tTAB")]
    [InlineData("\rCRLF-start")]
    public void Cell_prefixes_formula_lead_chars_with_single_quote(string value)
    {
        // 规则 2：= + - @ TAB CR 开头一律加单引号前缀（在引号包裹前），如 =1+1 → '=1+1。
        Assert.Equal("\"'" + value + "\"", Csv.Cell(value));
    }

    [Fact]
    public void Cell_does_not_prefix_when_lead_char_is_safe()
    {
        // 仅首字符判定：值中间出现 = 不触发防护。
        Assert.Equal("\"a=b\"", Csv.Cell("a=b"));
        Assert.Equal("\"a-b@c\"", Csv.Cell("a-b@c"));
    }

    [Fact]
    public void Cell_wraps_plain_text_and_constants_in_quotes()
    {
        // 规则 3：简单常量也统一包裹，无歧义。
        Assert.Equal("\"TRACE-001\"", Csv.Cell("TRACE-001"));
        Assert.Equal("\"OK\"", Csv.Cell("OK"));
    }

    [Fact]
    public void Cell_keeps_comma_inside_quotes()
    {
        Assert.Equal("\"a,b\"", Csv.Cell("a,b"));
    }

    [Fact]
    public void Cell_doubles_internal_quotes()
    {
        // 规则 3：内部 " 双写为 ""。
        Assert.Equal("\"say \"\"hi\"\"\"", Csv.Cell("say \"hi\""));
        // 首字符为引号时仍需先按规则包裹（引号不是公式起始字符）。
        Assert.Equal("\"\"\"=x\"\"\"", Csv.Cell("\"=x\""));
    }

    [Fact]
    public void Cell_keeps_newlines_verbatim_inside_quotes()
    {
        // 规则 4：CR/LF 原样保留在引号内（只有行尾固定 CRLF）。
        Assert.Equal("\"line1\r\nline2\"", Csv.Cell("line1\r\nline2"));
        Assert.Equal("\"line1\nline2\"", Csv.Cell("line1\nline2"));
    }

    [Fact]
    public void Cell_null_or_empty_yield_empty_quoted_cell()
    {
        Assert.Equal("\"\"", Csv.Cell(null));
        Assert.Equal("\"\"", Csv.Cell(""));
    }

    [Fact]
    public void Number_uses_plain_decimal_without_scientific_notation()
    {
        // 数值列：InvariantCulture、定点最简写法；大/小数量都不得退化为科学计数法。
        Assert.Equal("0", Csv.Number(0m));
        Assert.Equal("12", Csv.Number(12.00m));
        Assert.Equal("12.345", Csv.Number(12.3450m));
        Assert.Equal("100000000000", Csv.Number(100_000_000_000m));
        Assert.Equal("0.00000001", Csv.Number(0.00000001m));
    }

    [Fact]
    public void Number_negative_value_is_not_formula_guarded()
    {
        // 负数由数值列合法输出，不加单引号前缀（不走 Cell）。
        Assert.Equal("-3", Csv.Number(-3m));
        Assert.Equal("-0.25", Csv.Number(-0.25m));
    }

    [Fact]
    public void Line_joins_cells_with_comma_and_terminates_with_crlf()
    {
        Assert.Equal("\"a\",\"b\"\r\n", Csv.Line(Csv.Cell("a"), Csv.Cell("b")));
        Assert.Equal("\r\n", Csv.Line());
        Assert.EndsWith("\r\n", Csv.Line("\"x\""));
    }

    [Fact]
    public void Bom_is_utf8_byte_order_mark_character()
    {
        Assert.Equal('\uFEFF', Assert.Single(Csv.Bom));
    }
}