using System.Numerics;

namespace TraceFlow.Domain.Quantities;

public sealed class UnitConversionException : Exception
{
    public UnitConversionException(string message) : base(message) { }
}

/// <summary>有向精确比例：1 来源单位 = Numerator/Denominator 目标单位。换算必须精确相等（SRC-05/AT-029）。</summary>
public sealed record UnitRatio
{
    public long Numerator { get; }
    public long Denominator { get; }

    public UnitRatio(long numerator, long denominator)
    {
        if (numerator <= 0 || denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "比例分子分母必须为正");
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>|result| 必须 < 10^12（Qty 域），即 |result×10^6| < 10^18。</summary>
    private static readonly BigInteger MaxAbsScaledResult = new BigInteger(1_000_000_000_000m) * 1_000_000;

    public Qty Convert(Qty source)
    {
        // Qty 构造保证小数位 ≤6 且 |Value| < 10^12，故 Value × 10^6 必为绝对值 < 10^18 的整数：
        // 该 decimal 乘法仅把 scale 降为 0，尾数不变（< 2^96），不溢出、不舍入，转 BigInteger 精确。
        BigInteger scaledValue = new(source.Value * 1_000_000m);

        // BigInteger 无界：中间积可达 ~10^35，绝不溢出、绝不静默舍入（SRC-05/AT-029）。
        BigInteger product = scaledValue * Numerator;
        BigInteger scaledResult = BigInteger.DivRem(product, Denominator, out BigInteger remainder);
        if (!remainder.IsZero)
            throw new UnitConversionException($"换算不整除：{source.Value}×{Numerator}/{Denominator}，禁止舍入");

        // scaledResult = 精确结果 × 10^6（整数）。超出 Qty 域 ⇒ 换算失败，绝不泄漏原始 OverflowException。
        if (BigInteger.Abs(scaledResult) >= MaxAbsScaledResult)
            throw new UnitConversionException($"换算结果超精度/范围：{source.Value}×{Numerator}/{Denominator}");

        // |scaledResult| < 10^18 < 2^63：以 96 位尾数 + scale=6 直接重建 decimal，
        // 全程不做 decimal 乘除，结果绝对精确（小数位恰为 6，无舍入路径）。
        long abs = (long)BigInteger.Abs(scaledResult);
        decimal result = new((int)(abs & 0xFFFFFFFF), (int)(abs >> 32), 0, scaledResult.Sign < 0, Qty.Scale);
        try { return new Qty(result); }
        catch (ArgumentOutOfRangeException)
        { throw new UnitConversionException($"换算结果超精度/范围：{result}"); }
    }
}