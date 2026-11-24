using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Ops;
using TraceFlow.Host.Security;

namespace TraceFlow.OpsTool;

/// <summary>
/// create-db / drop-db / init-admin：库名守卫复用 <see cref="DatabaseNamePolicy"/>（脚本侧同规则自检）；
/// SQL 一律 QUOTENAME + 正则白名单双重防护；保护库、TraceFlow_Test_*、系统库永久拒绝。
/// </summary>
internal static class DbCommands
{
    private const int ExitRejected = 2;

    public static async Task<int> CreateAsync(IReadOnlyList<string> args)
    {
        var cli = CliArgs.Parse(args);
        var name = RequireName(cli);
        var master = BuildMasterConnection(ResolveBaseConnection(cli, "TRACEFLOW_TRIAL_CONN"));

        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        var exists = await conn.ExecuteScalarAsync<int?>("SELECT DB_ID(@name)", new { name });
        if (exists is not null)
        {
            Console.WriteLine($"库 {name} 已存在（不重建、不删除）");
            return 0;
        }

        // 名称已正则白名单：QUOTENAME 仅作纵深防御。
        await conn.ExecuteAsync(@"
DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name) + N';';
EXEC (@sql);", new { name });
        Console.WriteLine($"已创建库 {name}（默认排序规则）");
        return 0;
    }

    public static async Task<int> DropAsync(IReadOnlyList<string> args)
    {
        var cli = CliArgs.Parse(args, "--yes");
        var name = RequireName(cli);
        if (!cli.Flag("yes"))
            throw new CommandException("drop-db 需要显式 --yes（拒绝误删）", ExitRejected);
        var master = BuildMasterConnection(ResolveBaseConnection(cli, "TRACEFLOW_TRIAL_CONN"));

        var dropped = await DropDatabaseAsync(master, name);
        if (dropped)
            Console.WriteLine($"已删除库 {name}");
        return 0;
    }

    /// <summary>内部删除（perf 收尾复用，不走进程外命令）。库名守卫在此再次执行。</summary>
    public static async Task<bool> DropDatabaseAsync(string masterConnectionString, string name)
    {
        if (!DatabaseNamePolicy.TryValidate(name, out var error))
            throw new CommandException(error, ExitRejected);

        await using var conn = new SqlConnection(masterConnectionString);
        await conn.OpenAsync();
        var exists = await conn.ExecuteScalarAsync<int?>("SELECT DB_ID(@name)", new { name });
        if (exists is null)
        {
            Console.WriteLine($"库 {name} 不存在（跳过删除）");
            return false;
        }

        await conn.ExecuteAsync(@"
DECLARE @sql nvarchar(max) =
    N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
EXEC (@sql);", new { name });
        return true;
    }

    public static async Task<int> InitAdminAsync(IReadOnlyList<string> args)
    {
        var cli = CliArgs.Parse(args);
        if (cli.Positionals.Count != 1)
            throw new CommandException("用法: init-admin <username> [--db <dbName> | --conn <full conn>] [--base <conn>]", ExitRejected);
        var username = cli.Positionals[0];

        // 密码只来自环境变量；缺失/过短 → 退出码 2，且绝不回显。
        var password = Environment.GetEnvironmentVariable("TRACEFLOW_TRIAL_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(password) || password.Length < 10)
            throw new CommandException("缺少环境变量 TRACEFLOW_TRIAL_ADMIN_PASSWORD（≥10 位）", ExitRejected);

        string conn;
        var full = cli.Get("conn");
        var db = cli.Get("db");
        if (!string.IsNullOrWhiteSpace(full))
        {
            var builder = new SqlConnectionStringBuilder(full);
            if (string.IsNullOrEmpty(builder.InitialCatalog))
                throw new CommandException("--conn 必须包含 Initial Catalog（目标试运行库）", ExitRejected);
            if (!DatabaseNamePolicy.TryValidate(builder.InitialCatalog, out var connError))
                throw new CommandException(connError, ExitRejected);
            conn = builder.ConnectionString;
        }
        else if (!string.IsNullOrWhiteSpace(db))
        {
            if (!DatabaseNamePolicy.TryValidate(db, out var error))
                throw new CommandException(error, ExitRejected);
            conn = BuildConnection(ResolveBaseConnection(cli, "TRACEFLOW_TRIAL_CONN"), db);
        }
        else
        {
            throw new CommandException("init-admin 需要 --db <dbName> 或 --conn <含 Initial Catalog 的连接串>", ExitRejected);
        }

        // 与 Host 交互式初始化同一实现（校验/权限/审计/错误码一致）。
        return await AdminInitializer.CreateAsync(conn, username, password);
    }

    // ---------------------------------------------------------------- 辅助

    private static string RequireName(CliArgs cli)
    {
        if (cli.Positionals.Count != 1)
            throw new CommandException("缺少库名（用法: <命令> <dbName>）", ExitRejected);
        var name = cli.Positionals[0];
        if (!DatabaseNamePolicy.TryValidate(name, out var error))
            throw new CommandException(error, ExitRejected);
        return name;
    }

    /// <summary>连接串来源：--base → 指定环境变量；缺失报错（绝不内置凭据）。</summary>
    public static string ResolveBaseConnection(CliArgs cli, string environmentVariable)
    {
        var baseConn = cli.Get("base");
        if (!string.IsNullOrWhiteSpace(baseConn)) return baseConn;
        var fromEnv = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        throw new CommandException($"缺少连接串：请传 --base 或设置环境变量 {environmentVariable}（不含 Initial Catalog）", ExitRejected);
    }

    /// <summary>强制连 master（库名由参数白名单决定，不接受连接串里夹带的目标库）。</summary>
    public static string BuildMasterConnection(string baseConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" };
        return builder.ConnectionString;
    }

    public static string BuildConnection(string baseConnectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }
}