using TraceFlow.Domain.Matching;

namespace TraceFlow.UnitTests.Matching;

public class ApprovalNoNormalizerTests
{
    [Theory]
    [InlineData("国药准字H20051234", "国药准字H20051234")]
    [InlineData(" 国药准字 h20051234 ", "国药准字H20051234")]
    [InlineData("国药准字H2005-1234", "国药准字H20051234")] // 分隔符去除，数字版本信息保留
    [InlineData("H20051234（再注册）", "H20051234再注册")]   // 有意义文字保留
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Normalize_keeps_letters_digits_version(string? raw, string? expected)
        => Assert.Equal(expected, ApprovalNoNormalizer.Normalize(raw));
}
