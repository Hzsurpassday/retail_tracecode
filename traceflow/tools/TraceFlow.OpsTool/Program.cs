using System.Globalization;
using System.Runtime.Versioning;

namespace TraceFlow.OpsTool;

/// <summary>
/// TraceFlow 运维工具（M4 收尾）：性能基准（NFR-03 / AT-075）与试运行库/管理员准备。
/// 只连 TraceFlow_Trial_* / TraceFlow_Perf_* 库；保护库与集成测试库一律拒绝；凭据只从环境变量读取且不回显。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int ExitUsageOrRejected = 2;
    private const int ExitFailure = 1;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var command = args[0];
        var rest = args[1..];
        try
        {
            return command.ToLowerInvariant() switch
            {
                "create-db" => await DbCommands.CreateAsync(rest),
                "drop-db" => await DbCommands.DropAsync(rest),
                "init-admin" => await DbCommands.InitAdminAsync(rest),
                "perf" => await PerfCommand.RunAsync(rest),
                _ => Unknown(command),
            };
        }
        catch (CommandException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            // 只输出异常类型与消息（不回显连接串/密码），便于定位且不泄露运行环境。
            Console.Error.WriteLine("失败(" + ex.GetType().Name + "): " + ex.Message);
            return ExitFailure;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令: {command}（用 --help 查看用法）");
        return ExitUsageOrRejected;
    }

    internal static void PrintUsage()
    {
        Console.WriteLine("""
用法: dotnet run --project traceflow/tools/TraceFlow.OpsTool -c Release -- <命令> [选项]

命令:
  create-db <dbName> [--base <conn>]
      创建 TraceFlow_Trial_* / TraceFlow_Perf_* 库（默认排序规则）；已存在则打印"已存在"并以 0 退出。
      连接串取 --base 或环境变量 TRACEFLOW_TRIAL_CONN（不含 Initial Catalog）；缺省 → 退出码 2。
      保护库（TraceFlow_Dev/M2Manual/M2Gate/M3Ui）、TraceFlow_Test_*、系统库 → 退出码 2。

  drop-db <dbName> --yes [--base <conn>]
      仅允许 TraceFlow_Trial_* / TraceFlow_Perf_*；无 --yes 拒绝；保护库永久拒绝。

  init-admin <username> [--db <dbName> | --conn <full conn>] [--base <conn>]
      密码取环境变量 TRACEFLOW_TRIAL_ADMIN_PASSWORD（≥10 位），缺失 → 退出码 2。
      授权 system:manage/trace:read/audit:read，写审计 admin.init；失败的密码永不回显。

  perf [--base <conn>] [--products 10000] [--sale-lines 100000] [--documents 2000]
       [--iterations 30] [--warmup 3] [--out <path>] [--keep]
      连接串取 --base 或环境变量 TRACEFLOW_PERF_DB（再缺省 TRACEFLOW_TRIAL_CONN）。
      建 TraceFlow_Perf_<yyyyMMddHHmmss> → 迁移 → 种子 → 登录后测 7 个只读端点 p95（nearest-rank），
      任一端点 p95 > 2000ms → 退出码 1（结果文件照常写出）。默认结束后删除基准库，--keep 保留。

说明:
  - 所有库名按 ^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$ 校验；脚本侧另有同规则自检。
  - 本工具不读取、不打印任何连接串密码/管理员密码/cookie/token。
""");
    }

    internal static string FormatMs(double ms) => ms.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>可预期的命令失败（退出码显式给出，消息已脱敏）。</summary>
internal sealed class CommandException : Exception
{
    public CommandException(string message, int exitCode) : base(message) => ExitCode = exitCode;

    public int ExitCode { get; }
}