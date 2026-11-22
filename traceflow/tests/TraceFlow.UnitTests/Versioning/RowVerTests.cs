using TraceFlow.Domain.Versioning;

namespace TraceFlow.UnitTests.Versioning;

public class RowVerTests
{
    [Fact]
    public void Roundtrip_16_upper_hex()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x0a, 0xbc, 0x12, 0xff };
        var hex = RowVer.ToHex(bytes);
        Assert.Equal("000000000ABC12FF", hex);
        Assert.Equal(bytes, RowVer.FromHex(hex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000000ABC12F")]     // 15 位
    [InlineData("000000000ABC12FFF")]   // 17 位
    [InlineData("000000000ABC12GG")]    // 非 hex
    public void Invalid_hex_throws_FormatException(string bad)
        => Assert.Throws<FormatException>(() => RowVer.FromHex(bad));
}
