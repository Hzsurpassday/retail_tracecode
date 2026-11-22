using TraceFlow.Domain.Codes;
using Xunit;

namespace TraceFlow.UnitTests.Codes;

public class TraceCodeNormalizerTests
{
    [Fact]
    public void Valid_8_prefix_20_digit_supported()
    {
        var r = TraceCodeNormalizer.Normalize("81234567890123456789");
        Assert.Equal(CodeFormat.Supported8Prefix20, r.Format);
        Assert.Equal("81234567890123456789", r.Normalized);
    }

    [Fact]
    public void Leading_zeros_preserved_as_string()
    {
        var r = TraceCodeNormalizer.Normalize("80000000000000000001");
        Assert.Equal("80000000000000000001", r.Normalized);
    }

    [Fact]
    public void Edge_whitespace_and_scanner_control_chars_removed()
    {
        var r = TraceCodeNormalizer.Normalize("\r\n 81234567890123456789\u001d\t ");
        Assert.Equal(CodeFormat.Supported8Prefix20, r.Format);
        Assert.Equal("\r\n 81234567890123456789\u001d\t ", r.Raw); // raw 永不改写
    }

    [Fact]
    public void Overlong_code_not_truncated()
    {
        var raw = "8123456789012345678901234"; // 25 位
        var r = TraceCodeNormalizer.Normalize(raw);
        Assert.Equal(raw, r.Normalized);
        Assert.Equal(CodeFormat.Unsupported, r.Format); // 异常保存，不截 20 位"修复"
    }

    [Fact]
    public void Non_8_prefix_is_unsupported_not_absent()
    {
        var r = TraceCodeNormalizer.Normalize("91234567890123456789");
        Assert.Equal(CodeFormat.Unsupported, r.Format);
    }

    [Fact]
    public void Internal_control_char_unsupported_not_silently_stripped()
    {
        var r = TraceCodeNormalizer.Normalize("8123456789\u00000123456789");
        Assert.Equal(CodeFormat.Unsupported, r.Format);
    }

    [Fact]
    public void Empty_is_empty_not_exempt()
    {
        Assert.Equal(CodeFormat.Empty, TraceCodeNormalizer.Normalize(null).Format);
        Assert.Equal(CodeFormat.Empty, TraceCodeNormalizer.Normalize("").Format);
        Assert.Equal(CodeFormat.Empty, TraceCodeNormalizer.Normalize("   ").Format);
    }
}