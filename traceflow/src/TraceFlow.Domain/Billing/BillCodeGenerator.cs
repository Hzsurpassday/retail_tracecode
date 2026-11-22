using System.Globalization;

namespace TraceFlow.Domain.Billing;

public static class BillCodeGenerator
{
    // Base35：字母表剔除分隔符 'P'，保证 body 永不含 'P'，与分部后缀 "P{n}" 无碰撞（I-1）
    private const string Digits = "0123456789ABCDEFGHIJKLMNOQRSTUVWXYZ";

    public static string Generate(BillCodeContract contract, long taskId, int partIndex)
    {
        if (contract.MaxLength <= 0 || string.IsNullOrEmpty(contract.Prefix) ||
            !contract.Prefix.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            throw new ArgumentException("合同前缀必须为大写字母数字且 MaxLength>0", nameof(contract));
        if (taskId <= 0) throw new ArgumentOutOfRangeException(nameof(taskId));
        if (partIndex < 0) throw new ArgumentOutOfRangeException(nameof(partIndex));

        var body = Base35(taskId);
        var code = partIndex == 0
            ? contract.Prefix + body
            : contract.Prefix + body + "P" + partIndex.ToString(CultureInfo.InvariantCulture);
        if (code.Length > contract.MaxLength)
            throw new InvalidOperationException($"bill_code 长度 {code.Length} 超合同上限 {contract.MaxLength}，禁止截断");
        return code;
    }

    private static string Base35(long value)
    {
        Span<char> buf = stackalloc char[13]; // 35^12 < long.MaxValue < 35^13，13 位足够
        var i = buf.Length;
        while (value > 0) { buf[--i] = Digits[(int)(value % 35)]; value /= 35; }
        return new string(buf[i..]);
    }
}
