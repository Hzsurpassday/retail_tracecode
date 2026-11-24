using TraceFlow.DbMigrator;

// 连接串可以走环境变量 TRACEFLOW_MIGRATE_CONN（试运行脚本用它，避免密码出现在命令行/进程表），
// 脚本目录用 --dir 指定；旧的 `TraceFlow.DbMigrator <连接串> [脚本目录]` 位置参数仍然有效。
string? dirArg = null;
var positional = new List<string>(args.Length);
for (var i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--dir", StringComparison.Ordinal))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("--dir 缺少路径。");
            return 2;
        }
        dirArg = args[++i];
        continue;
    }
    positional.Add(args[i]);
}

var conn = positional.Count > 0 ? positional[0] : Environment.GetEnvironmentVariable("TRACEFLOW_MIGRATE_CONN");
var dir = dirArg ?? (positional.Count > 1 ? positional[1] : Path.Combine(AppContext.BaseDirectory, "db", "migrations"));
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("用法: TraceFlow.DbMigrator <连接串> [脚本目录]，或设置 TRACEFLOW_MIGRATE_CONN（可配 --dir <脚本目录>）。只允许连接 TraceFlow 目标库。");
    return 2;
}
try
{
    var report = await new MigrationRunner(conn, dir).RunAsync();
    Console.WriteLine($"Applied: {string.Join(", ", report.Applied)}");
    Console.WriteLine($"Skipped: {string.Join(", ", report.Skipped)}");
    return 0;
}
catch (MigrationException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ex.InnerException?.Message);
    return 1;
}
