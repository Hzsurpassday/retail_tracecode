using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.DbMigrator;
using Xunit;

namespace TraceFlow.IntegrationTests;

public sealed class Sql2008Fixture : IAsyncLifetime
{
    private string _masterConn = "";
    private string _dbName = "";
    public string ConnectionString { get; private set; } = "";

    /// <summary>实际连接到的 SQL Server 引擎版本（SERVERPROPERTY('ProductVersion')）。</summary>
    public string ServerVersion { get; private set; } = "";

    /// <summary>true 表示经用户授权（TRACEFLOW_TEST_DB_ALLOW_NEWER=1）在高于 2008 R2 的引擎上运行，2008 R2 未验证。</summary>
    public bool IsDowngraded { get; private set; }

    public async Task InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("TRACEFLOW_TEST_DB")
            ?? throw new InvalidOperationException(
                "必须设置 TRACEFLOW_TEST_DB 指向真实 SQL Server 2008 R2 测试实例（不含 Initial Catalog）。AT-070 门禁不允许其他引擎替代。");
        var builder = new SqlConnectionStringBuilder(baseConn);
        builder.InitialCatalog = "master";
        _masterConn = builder.ConnectionString;
        _dbName = "TraceFlow_Test_" + Guid.NewGuid().ToString("N")[..12];

        await using (var master = new SqlConnection(_masterConn))
        {
            await master.OpenAsync();
            var ver = await master.ExecuteScalarAsync<string>(
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64))");
            if (ver is null)
                throw new InvalidOperationException("无法读取 SERVERPROPERTY('ProductVersion')：集成门禁要求真实 SQL Server 2008 R2（10.50.x）连接。");
            ServerVersion = ver;
            if (!ver.StartsWith("10.50."))
            {
                // 默认拒绝非 2008 R2；仅当用户显式授权降级（TRACEFLOW_TEST_DB_ALLOW_NEWER=1）时接受更新引擎，并如实标注。
                var allowNewer = Environment.GetEnvironmentVariable("TRACEFLOW_TEST_DB_ALLOW_NEWER") == "1";
                if (!allowNewer || !Version.TryParse(ver, out var parsed) || parsed < new Version(10, 50))
                    throw new InvalidOperationException($"集成门禁要求 SQL Server 2008 R2（10.50.x），实际 {ver}");
                IsDowngraded = true;
                // vstest 以 UTF-8 读取 testhost 输出；显式 UTF-8 避免中文横幅在默认代码页下乱码。
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine($"TRACEFLOW: 2008 R2 未验证 — 开发测试运行于 SQL Server {ver}（用户授权降级，交付前须真实 2008 R2 补验）");
            }
            // BIN2 变体（M0 携带 7）：opt-in 机械拦截大小写接缝（env/状态字面量）。默认 CI 排序不变。
            var bin2 = Environment.GetEnvironmentVariable("TRACEFLOW_TEST_DB_BIN2") == "1";
            await master.ExecuteAsync(bin2
                ? $"CREATE DATABASE [{_dbName}] COLLATE Latin1_General_BIN2"
                : $"CREATE DATABASE [{_dbName}]");
        }

        builder.InitialCatalog = _dbName;
        ConnectionString = builder.ConnectionString;
        var scripts = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        await new MigrationRunner(ConnectionString, scripts).RunAsync();
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task DisposeAsync()
    {
        if (_dbName.Length == 0) return;
        await using var master = new SqlConnection(_masterConn);
        await master.OpenAsync();
        await master.ExecuteAsync(
            $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];");
    }
}
