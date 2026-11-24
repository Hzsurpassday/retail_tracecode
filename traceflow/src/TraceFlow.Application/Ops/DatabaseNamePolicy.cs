using System.Text.RegularExpressions;

namespace TraceFlow.Application.Ops;

/// <summary>
/// 数据库名守卫（性能基准 / 试运行脚本共用）：只允许 <c>TraceFlow_Trial_*</c> 与 <c>TraceFlow_Perf_*</c>，
/// 保护库（TraceFlow_Dev / M2Manual / M2Gate / M3Ui）、集成测试自有库（TraceFlow_Test_*）与系统库永久拒绝。
/// 纯函数、无 IO；正则字符集排除 <c>]</c>、空格、分号等，杜绝标识符注入。
/// </summary>
public static class DatabaseNamePolicy
{
    /// <summary>受保护库：任何脚本/工具不得创建冲突、不得删除（brief 指定的开发/门禁库）。</summary>
    public static readonly IReadOnlyList<string> ProtectedDatabases = new[]
    {
        "TraceFlow_Dev", "TraceFlow_M2Manual", "TraceFlow_M2Gate", "TraceFlow_M3Ui",
    };

    /// <summary>系统库：同样永久拒绝（避免 drop 误伤）。</summary>
    public static readonly IReadOnlyList<string> SystemDatabases = new[]
    {
        "master", "model", "msdb", "tempdb",
    };

    /// <summary>集成测试自有库前缀：本工具不创建、不删除。</summary>
    public const string IntegrationTestPrefix = "TraceFlow_Test_";

    private static readonly Regex TrialOrPerf =
        new(@"^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$", RegexOptions.CultureInvariant);

    /// <summary>是否为允许的 Trial/Perf 命名（不含保护/系统/Test 排除判断）。</summary>
    public static bool IsTrialOrPerfName(string? name)
        => !string.IsNullOrEmpty(name) && TrialOrPerf.IsMatch(name);

    /// <summary>是否命中保护库/系统库/集成测试库（大小写不敏感，与 SQL Server 默认排序规则一致）。</summary>
    public static bool IsProtected(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var p in ProtectedDatabases)
            if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var s in SystemDatabases)
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith(IntegrationTestPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验 create/drop 目标名：合法返回 true 且 <paramref name="error"/> 为空；
    /// 否则返回 false 并给出原因（调用方据此以退出码 2 拒绝）。
    /// </summary>
    public static bool TryValidate(string? name, out string error)
    {
        if (string.IsNullOrEmpty(name))
        {
            error = "库名不能为空";
            return false;
        }
        if (IsProtected(name))
        {
            error = $"拒绝操作保护库/系统库/测试库：{name}";
            return false;
        }
        if (!TrialOrPerf.IsMatch(name))
        {
            error = "库名必须匹配 ^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$";
            return false;
        }
        error = "";
        return true;
    }
}