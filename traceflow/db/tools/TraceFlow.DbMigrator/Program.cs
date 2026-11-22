using TraceFlow.DbMigrator;

var conn = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("TRACEFLOW_MIGRATE_CONN");
var dir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "db", "migrations");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("用法: TraceFlow.DbMigrator <连接串> [脚本目录]，或设置 TRACEFLOW_MIGRATE_CONN。只允许连接 TraceFlow 目标库。");
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
