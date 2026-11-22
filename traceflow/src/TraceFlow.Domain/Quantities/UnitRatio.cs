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

    public Qty Convert(Qty source)
    {
        decimal scaled = source.Value * Numerator;
        if (scaled % Denominator != 0m)
            throw new UnitConversionException($"换算不整除：{source.Value}×{Numerator}/{Denominator}，禁止舍入");
        decimal result = scaled / Denominator;
        try { return new Qty(result); }
        catch (ArgumentOutOfRangeException)
        { throw new UnitConversionException($"换算结果超精度/范围：{result}"); }
    }
}