using System.Globalization;

namespace TraceFlow.Domain.Billing;

public static class BillCodeGenerator
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Generate(BillCodeContract contract, long taskId, int partIndex)
    {
        if (contract.MaxLength <= 0 || string.IsNullOrEmpty(contract.Prefix) ||
            !contract.Prefix.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            throw new ArgumentException("合同前缀必须为大写字母数字且 MaxLength>0", nameof(contract));
        if (taskId <= 0) throw new ArgumentOutOfRangeException(nameof(taskId));
        if (partIndex < 0) throw new ArgumentOutOfRangeException(nameof(partIndex));

        var body = Base36(taskId);
        var code = partIndex == 0
            ? contract.Prefix + body
            : contract.Prefix + body + "P" + partIndex.ToString(CultureInfo.InvariantCulture);
        if (code.Length > contract.MaxLength)
            throw new InvalidOperationException($"bill_code 长度 {code.Length} 超合同上限 {contract.MaxLength}，禁止截断");
        return code;
    }

    private static string Base36(long value)
    {
        Span<char> buf = stackalloc char[13];
        var i = buf.Length;
        while (value > 0) { buf[--i] = Digits[(int)(value % 36)]; value /= 36; }
        return new string(buf[i..]);
    }
}