using TraceFlow.Domain.Codes;

namespace TraceFlow.UnitTests.Codes;

public class CodeFormatStorageTests
{
    [Theory]
    [InlineData(CodeFormat.Empty, "EMPTY")]
    [InlineData(CodeFormat.Supported8Prefix20, "S8P20")]
    [InlineData(CodeFormat.Unsupported, "UNSUP")]
    public void Token_roundtrip(CodeFormat format, string token)
    {
        Assert.Equal(token, CodeFormatStorage.ToToken(format));
        Assert.Equal(format, CodeFormatStorage.FromToken(token));
    }

    [Fact]
    public void All_tokens_fit_legacy_nvarchar16_and_widened_column()
    {
        // M0 携带项 1：Supported8Prefix20 枚举名 18 字符放不下 nvarchar(16)；token 必须 ≤16。
        foreach (CodeFormat f in Enum.GetValues<CodeFormat>())
            Assert.True(CodeFormatStorage.ToToken(f).Length <= 16);
    }

    [Fact]
    public void Unknown_token_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => CodeFormatStorage.FromToken("Supported8Prefix20"));

    [Fact]
    public void Legacy_empty_default_token_accepted()
        => Assert.Equal(CodeFormat.Empty, CodeFormatStorage.FromToken("EMPTY"));
}
