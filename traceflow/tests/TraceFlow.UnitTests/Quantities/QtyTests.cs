using TraceFlow.Domain.Quantities;
using Xunit;

namespace TraceFlow.UnitTests.Quantities;

public class QtyTests
{
    [Fact]
    public void Accepts_six_decimal_places()
        => Assert.Equal(1.234567m, new Qty(1.234567m).Value);

    [Fact]
    public void Rejects_more_than_six_decimals()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Qty(0.0000001m));

    [Fact]
    public void Rejects_overflow_of_decimal_18_6()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Qty(1_000_000_000_000m)); // 13 位整数超限

    [Fact]
    public void Exact_conversion_succeeds()
    {
        var ratio = new UnitRatio(10, 1); // 1 大包装 = 10 最小单位
        Assert.Equal(new Qty(30m), ratio.Convert(new Qty(3m)));
    }

    [Fact]
    public void Inexact_conversion_throws_not_rounds()
    {
        var ratio = new UnitRatio(1, 3);
        Assert.Throws<UnitConversionException>(() => ratio.Convert(new Qty(1m)));
    }

    [Fact]
    public void Conversion_scale_overflow_throws()
    {
        var ratio = new UnitRatio(1, 1_000_000_0); // 结果需要 >6 位小数
        Assert.Throws<UnitConversionException>(() => ratio.Convert(new Qty(1m)));
    }

    [Fact]
    public void Ratio_direction_matters()
    {
        var bigToSmall = new UnitRatio(10, 1);
        var smallToBig = new UnitRatio(1, 10);
        Assert.Equal(new Qty(100m), bigToSmall.Convert(new Qty(10m)));
        Assert.Equal(new Qty(1m), smallToBig.Convert(new Qty(10m)));
    }

    [Fact]
    public void Invalid_ratio_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitRatio(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitRatio(1, -2));
    }
}
