using TraceFlow.Domain.Quantities;
using Xunit;

namespace TraceFlow.UnitTests.Quantities;

/// <summary>SRC-05/AT-029 极值回归：换算必须精确或抛 UnitConversionException，域内输入绝不泄漏原始 OverflowException，绝不静默舍入。</summary>
public class UnitRatioTests
{
    [Fact]
    public void Extreme_ratio_with_overflowing_intermediate_returns_exact_in_domain_result()
    {
        // 评审复现输入：10^17/10^17 = 1:1，中间积 ≈10^35 远超 decimal.MaxValue（旧实现泄漏原始 OverflowException）。
        // 精确算术下结果 = 源值本身，仍在域内 ⇒ 必须精确返回（与 1:1 大比例保持小数值的用例同理，SRC-05）。
        var ratio = new UnitRatio(100_000_000_000_000_000, 100_000_000_000_000_000);
        var source = new Qty(999999999999.999999m);
        Assert.Equal(source, ratio.Convert(source));
    }

    [Fact]
    public void Extreme_ratio_out_of_domain_result_throws_UnitConversionException_not_Overflow()
    {
        // 10^17:1 × 域内最大值 ⇒ 精确结果 ≈10^29 超出 decimal(18,6) 域 ⇒ UnitConversionException，而非原始 OverflowException。
        var ratio = new UnitRatio(100_000_000_000_000_000, 1);
        Assert.Throws<UnitConversionException>(() => ratio.Convert(new Qty(999999999999.999999m)));
    }

    [Fact]
    public void Large_exact_product_is_not_silently_rounded()
    {
        // 手算：333333333333.333333 × 3 = 999999999999.999999，精确且在域内。
        var ratio = new UnitRatio(3, 1);
        Assert.Equal(new Qty(999999999999.999999m), ratio.Convert(new Qty(333333333333.333333m)));
    }

    [Fact]
    public void Equal_large_ratio_preserves_fractional_value_exactly()
    {
        // 10^12/10^12 = 1:1：中间积超出旧实现的整除门（scaled % Denominator != 0 误判），精确结果必须保持 123.456。
        var ratio = new UnitRatio(1_000_000_000_000, 1_000_000_000_000);
        Assert.Equal(new Qty(123.456m), ratio.Convert(new Qty(123.456m)));
    }

    [Fact]
    public void Non_divisible_conversion_still_throws_UnitConversionException()
    {
        var ratio = new UnitRatio(1, 3);
        Assert.Throws<UnitConversionException>(() => ratio.Convert(new Qty(1m)));
    }
}