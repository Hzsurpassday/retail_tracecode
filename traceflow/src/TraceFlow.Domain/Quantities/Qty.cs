namespace TraceFlow.Domain.Quantities;

/// <summary>decimal(18,6) 域：12 位整数 + 6 位小数。禁 float；越界报错不舍入吞量（SRC-05）。</summary>
public readonly record struct Qty
{
    public const int Scale = 6;
    public static readonly Qty Zero = new(0m);
    public decimal Value { get; }

    public Qty(decimal value)
    {
        if (decimal.Round(value, Scale) != value)
            throw new ArgumentOutOfRangeException(nameof(value), "数量精度超过 6 位小数");
        if (value >= 1_000_000_000_000m || value <= -1_000_000_000_000m)
            throw new ArgumentOutOfRangeException(nameof(value), "数量超出 decimal(18,6) 范围");
        Value = value;
    }
}