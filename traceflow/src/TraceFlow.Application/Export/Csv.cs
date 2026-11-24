using System.Globalization;

namespace TraceFlow.Application.Export;

/// <summary>
/// UTF-8 CSV 单元格格式化（DEF-07 / AT-074），无第三方依赖，规则顺序固定：
/// 1) null → 空串；
/// 2) 公式注入防护：首字符为 <c>= + - @</c>、TAB 或 CR 时，在引号包裹前加单引号前缀（<c>=1+1</c> → <c>'=1+1</c>）；
///    以 <c>-</c> 开头的文本一律按文本防护——金额/数量列必须走 <see cref="Number"/>，不经过 <see cref="Cell"/>；
/// 3) 引号包裹：所有文本单元格（含含逗号/引号/CR/LF 的）一律用 <c>"</c> 包裹，内部 <c>"</c> 双写；
/// 4) 换行符原样保留在引号内；行尾 <c>\r\n</c>。
/// </summary>
public static class Csv
{
    /// <summary>UTF-8 BOM（Excel 中文兼容）；正文以此开头。</summary>
    public const string Bom = "\uFEFF";

    /// <summary>文本单元格：先做公式注入防护，再统一加引号并双写内部引号。</summary>
    public static string Cell(string? value)
    {
        if (value is null) return "\"\"";
        var guarded = value.Length > 0 && IsFormulaLead(value[0]) ? "'" + value : value;
        return "\"" + guarded.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// 数量/金额单元格：InvariantCulture、定点最简写法（无科学计数法）。
    /// 负数由数值列合法输出，不加单引号前缀（Excel 不会把数值列解析为公式）。
    /// </summary>
    public static string Number(decimal value)
        => value.ToString("0.################", CultureInfo.InvariantCulture);

    /// <summary>一行 CSV：单元格以逗号连接，CRLF 结尾。</summary>
    public static string Line(params string[] cells) => string.Concat(string.Join(",", cells), "\r\n");

    /// <summary>公式注入起始字符：= + - @ TAB CR。</summary>
    private static bool IsFormulaLead(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';
}