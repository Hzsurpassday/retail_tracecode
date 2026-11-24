using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Ops;
using TraceFlow.DbMigrator;

namespace TraceFlow.OpsTool;

/// <summary>
/// NFR-03 / AT-075 性能基准：10000 商品 / 100000 销售行，核心只读端点 p95（nearest-rank）≤ 2000ms。
/// 流程：建 TraceFlow_Perf_* 库 → 迁移 → 种子 → 进程内宿主（WebApplicationFactory，Mock env，无后台作业）→
/// 登录 → 顺序 warmup/计时 → JSON 结果 + PASS/FAIL 表 → （默认）删库。
/// 不达标一律保持 FAIL 并退出码 1；不缩小数据量、不裁剪端点。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PerfCommand
{
    private const double P95GateMs = 2000d;
    private const string DefaultOut = "traceflow/tools/TraceFlow.OpsTool/perf-results.json";
    private const string PerfUserName = "perf-reader";

    /// <summary>计时端点（顺序固定）。codes 前缀 6900：端点 MinPrefixLength=4，q=69 会被拒（见 deviations）。</summary>
    private static readonly string[] Endpoints =
    {
        "/api/v1/pos-sales?page=1",
        "/api/v1/pos-sales?page=20",
        "/api/v1/returns?page=1",
        "/api/v1/upload-tasks?page=1",
        "/api/v1/codes?q=6900",
        "/api/v1/ops/metrics",
        "/api/v1/dashboard",
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var cli = CliArgs.Parse(args, "--keep");
        var products = cli.GetInt("products", 10_000);
        var documents = cli.GetInt("documents", 2_000);
        var saleLines = cli.GetInt("sale-lines", 100_000);
        var iterations = cli.GetInt("iterations", 30);
        var warmup = cli.GetInt("warmup", 3, min: 0);
        var keep = cli.Flag("keep");

        // --out 缺省解析到仓库根（从程序集位置/当前目录向上找含 traceflow/db/migrations 的目录），
        // 始终按绝对路径落盘与打印：不随 shell 工作目录漂移，日志里也不会出现含糊的相对路径。
        var outArg = cli.Get("out");
        string fullOut;
        if (!string.IsNullOrWhiteSpace(outArg))
        {
            fullOut = Path.GetFullPath(outArg);
        }
        else
        {
            var root = FindRepositoryRoot();
            if (root is null)
                throw new CommandException(
                    "找不到仓库根（应包含 traceflow/db/migrations）；请用 --out 显式指定输出文件", 2);
            fullOut = Path.GetFullPath(Path.Combine(root, DefaultOut));
        }
        Console.WriteLine($"[perf] 结果文件（绝对路径）{fullOut}");

        if (saleLines % documents != 0)
            throw new CommandException("--sale-lines 必须能被 --documents 整除（每单行数一致）", 2);

        var baseConn = cli.Get("base");
        if (string.IsNullOrWhiteSpace(baseConn)) baseConn = Environment.GetEnvironmentVariable("TRACEFLOW_PERF_DB");
        if (string.IsNullOrWhiteSpace(baseConn)) baseConn = Environment.GetEnvironmentVariable("TRACEFLOW_TRIAL_CONN");
        if (string.IsNullOrWhiteSpace(baseConn))
            throw new CommandException("缺少连接串：请传 --base 或设置环境变量 TRACEFLOW_PERF_DB（不含 Initial Catalog）", 2);

        var dbName = "TraceFlow_Perf_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        if (!DatabaseNamePolicy.TryValidate(dbName, out var nameError))
            throw new CommandException(nameError, 2);
        var masterConn = DbCommands.BuildMasterConnection(baseConn);
        var targetConn = DbCommands.BuildConnection(baseConn, dbName);

        WebApplicationFactory<global::Program>? factory = null;
        var seedCommitted = false;
        try
        {
            Console.WriteLine($"[perf] 基准库 {dbName}（{dbName.Length} 字符）");

            await using (var master = new SqlConnection(masterConn))
            {
                await master.OpenAsync();
                var productVersion = await master.ExecuteScalarAsync<string>(
                    "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64))") ?? "";
                var engineVerified = productVersion.StartsWith("10.50.", StringComparison.Ordinal);
                if (!engineVerified)
                    Console.WriteLine($"[perf] 注意：引擎 {productVersion} 不是 SQL Server 2008 R2（10.50.x）；照常运行并记录 engineVerified2008R2=false");

                if (await master.ExecuteScalarAsync<int?>("SELECT DB_ID(@dbName)", new { dbName }) is not null)
                    throw new CommandException($"基准库 {dbName} 已存在，拒绝复用（避免污染既有数据）", 1);
                await master.ExecuteAsync("DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@dbName) + N';'; EXEC (@sql);",
                    new { dbName });

                var scripts = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
                if (!Directory.Exists(scripts))
                    throw new CommandException($"迁移脚本目录不存在：{scripts}", 1);
                var report = await new MigrationRunner(targetConn, scripts).RunAsync();
                Console.WriteLine($"[perf] 迁移 applied={report.Applied.Count} skipped={report.Skipped.Count}");

                var seedWatch = Stopwatch.StartNew();
                var seed = await PerfSeeder.SeedAsync(targetConn, products, documents, saleLines, CancellationToken.None);
                seedCommitted = true; // 种子已在单事务内提交；--keep 只在这之后才生效
                seedWatch.Stop();
                Console.WriteLine($"[perf] 种子完成 products={seed.Products} documents={seed.Documents} " +
                                  $"saleLines={seed.SaleLines} codes={seed.SaleLines} returnTasks={seed.ReturnTasks} " +
                                  $"用时 {seedWatch.Elapsed.TotalSeconds:0.0}s");
                Console.WriteLine($"[perf] 业务时间窗 {seed.OldestBusinessUtc:O} ~ {seed.NewestBusinessUtc:O}");

                var password = "Perf-" + Guid.NewGuid().ToString("N");
                await SeedPerfUserAsync(targetConn, PerfUserName, password);

                factory = CreateFactory(targetConn);
                var client = await LoginAsync(factory, PerfUserName, password);

                var results = new List<EndpointResult>(Endpoints.Length);
                foreach (var endpoint in Endpoints)
                {
                    var result = await MeasureAsync(client, endpoint, warmup, iterations);
                    results.Add(result);
                    Console.WriteLine($"[perf] {result.Endpoint} p50={result.P50Ms:0.0}ms p95={result.P95Ms:0.0}ms " +
                                      $"max={result.MaxMs:0.0}ms status={string.Join(",", result.StatusCodes)} " +
                                      $"{(result.Pass ? "PASS" : "FAIL")}");
                }

                // 一次对账触发（含种子的日期窗口）；只报耗时，不设 p95 门禁。
                var windowStart = seed.NewestBusinessUtc.Date.AddDays(-1);
                var recon = await MeasureReconciliationAsync(client, windowStart, windowStart.AddDays(1));
                Console.WriteLine($"[perf] POST /api/v1/reconciliation-runs {recon.Ms:0.0}ms status={recon.Status}");

                var payload = new
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    ProductVersion = productVersion,
                    EngineVerified2008R2 = engineVerified,
                    DbName = dbName,
                    Volumes = new
                    {
                        Products = seed.Products,
                        Documents = seed.Documents,
                        SaleLines = seed.SaleLines,
                        Codes = seed.SaleLines,
                        // 退货负载（CONSUMER_RETURN_116 任务 + 分片 + 确认行）：/returns 不再空表。
                        ReturnTasks = seed.ReturnTasks,
                    },
                    Iterations = iterations,
                    Warmup = warmup,
                    Results = results.Select(r => new
                    {
                        r.Endpoint,
                        r.P50Ms,
                        r.P95Ms,
                        r.MaxMs,
                        r.Pass,
                        r.StatusCodes,
                        r.Samples,
                    }).ToArray(),
                    ReconciliationRunMs = Math.Round(recon.Ms, 1),
                    ReconciliationStatus = recon.Status,
                    Environment = new
                    {
                        ProcessorCount = System.Environment.ProcessorCount,
                        OsDescription = RuntimeInformation.OSDescription,
                        DotnetVersion = RuntimeInformation.FrameworkDescription,
                    },
                    Deviations = new[]
                    {
                        "端点实际路由为 POST /api/v1/reconciliation-runs（brief 写作 /api/v1/reconciliation/runs）。",
                        "GET /api/v1/codes 的 q 至少 4 字符（CodesEndpoints.MinPrefixLength=4），故用前缀 6900（命中全部 100000 码）而非 q=69（会被 400 拒绝）。",
                        "pass = 所有样本 HTTP 2xx 且 p95 ≤ 2000ms（状态码非 2xx 也判 FAIL，避免错误响应被误判为快）。",
                        "种子额外写入 200 个 CONSUMER_RETURN_116 任务（每个 1 个分片；约 2/3 带 return_confirmation），/api/v1/returns 不再空表。",
                    },
                };

                var json = JsonSerializer.Serialize(payload, JsonOptions());
                Directory.CreateDirectory(Path.GetDirectoryName(fullOut)!);
                await File.WriteAllTextAsync(fullOut, json + System.Environment.NewLine);

                PrintTable(results, recon.Ms, productVersion, engineVerified, dbName);
                Console.WriteLine($"[perf] 结果已写出（绝对路径）{fullOut}");
                Console.WriteLine($"[perf] 基准库 {dbName}（keep={keep}）");

                var failed = results.Any(r => !r.Pass);
                if (failed)
                    Console.WriteLine("[perf] 结论 FAIL：存在 p95 > 2000ms 或非 2xx 样本（结果已如实写出，未调整数据量/端点）");
                else
                    Console.WriteLine("[perf] 结论 PASS：全部端点 p95 ≤ 2000ms 且样本均为 2xx");
                return failed ? 1 : 0;
            }
        }
        finally
        {
            factory?.Dispose();
            SqlConnection.ClearAllPools();
            // --keep 只在种子成功提交后生效：种子失败或回滚时必须删库，避免留下半套数据被后续复用。
            if (!keep || !seedCommitted)
            {
                try
                {
                    await DbCommands.DropDatabaseAsync(masterConn, dbName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[perf] 清理基准库失败: " + ex.Message);
                }
            }
        }
    }

    /// <summary>从程序集目录与当前目录向上找含 traceflow/db/migrations 的仓库根；找不到返回 null。</summary>
    private static string? FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "traceflow", "db", "migrations")))
                    return dir.FullName;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- 计测

    private sealed record EndpointResult(string Endpoint, double P50Ms, double P95Ms, double MaxMs, bool Pass,
        string[] StatusCodes, int Samples);

    private static async Task<EndpointResult> MeasureAsync(HttpClient client, string endpoint, int warmup, int iterations)
    {
        for (var i = 0; i < warmup; i++)
        {
            using var warm = await client.GetAsync(endpoint);
            await warm.Content.ReadAsStringAsync();
        }

        var samples = new List<double>(iterations);
        var statuses = new List<int>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(endpoint);
            await response.Content.ReadAsStringAsync();
            watch.Stop();
            statuses.Add((int)response.StatusCode);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        var p95 = Percentile.P95(samples);
        var allOk = statuses.All(s => s is >= 200 and <= 299);
        return new EndpointResult(endpoint, Math.Round(Percentile.P50(samples), 1), Math.Round(p95, 1),
            Math.Round(samples.Max(), 1), p95 <= P95GateMs && allOk,
            statuses.Distinct().OrderBy(s => s).Select(s => s.ToString()).ToArray(), samples.Count);
    }

    private static async Task<(double Ms, int Status)> MeasureReconciliationAsync(HttpClient client, DateTime windowStartUtc,
        DateTime windowEndUtc)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reconciliation-runs")
        {
            Content = JsonContent.Create(new
            {
                windowStart = windowStartUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                windowEnd = windowEndUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                businessType = "SALE",
            }),
        };
        request.Headers.Add("Idempotency-Key", "perf-recon-" + Guid.NewGuid().ToString("N"));
        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, (int)response.StatusCode);
    }

    private static void PrintTable(IReadOnlyList<EndpointResult> results, double reconMs, string productVersion,
        bool engineVerified, string dbName)
    {
        Console.WriteLine();
        Console.WriteLine("端点 | p50(ms) | p95(ms) | max(ms) | 样本 | 状态码 | 结论");
        foreach (var r in results)
            Console.WriteLine($"{r.Endpoint} | {r.P50Ms:0.0} | {r.P95Ms:0.0} | {r.MaxMs:0.0} | {r.Samples} | " +
                              $"{string.Join(",", r.StatusCodes)} | {(r.Pass ? "PASS" : "FAIL")}");
        Console.WriteLine($"POST /api/v1/reconciliation-runs | {reconMs:0.0} | — | — | 1 | — | 仅报告");
        Console.WriteLine($"SQL ProductVersion={productVersion} engineVerified2008R2={engineVerified} db={dbName}");
    }

    // ---------------------------------------------------------------- 宿主与登录

    private static WebApplicationFactory<global::Program> CreateFactory(string connectionString)
        => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            // 显式内容根：控制台宿主无法像测试项目一样由 Mvc.Testing 推断项目目录，否则会回退到
            // <解决方案目录>\TraceFlow.Host 而抛 DirectoryNotFoundException。内容根仅影响静态文件/配置定位。
            builder.UseContentRoot(ResolveHostContentRoot());
            builder.UseSetting("ConnectionStrings:TraceFlow", connectionString);
            builder.UseSetting("TraceFlow:Env", "Mock");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });

    /// <summary>优先取 MvcTestingAppManifest.json 中的 Host 项目目录（构建期生成），缺失时退回工具输出目录。</summary>
    private static string ResolveHostContentRoot()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "MvcTestingAppManifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!entry.Name.StartsWith("TraceFlow.Host,", StringComparison.Ordinal)) continue;
                    var root = entry.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) return root!;
                }
            }
            catch (JsonException)
            {
                // 清单损坏：退回输出目录，不掩盖真实错误。
            }
        }
        return AppContext.BaseDirectory;
    }

    private sealed record SessionResp(SessionData Data);

    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<global::Program> factory, string user,
        string password)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = password });
        login.EnsureSuccessStatusCode();
        var refreshed = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", refreshed!.Data.CsrfToken);
        return client;
    }

    private static async Task SeedPerfUserAsync(string connectionString, string username, string password)
    {
        var hasher = new PasswordHasher<string>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        var id = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.app_user(username, display_name, password_hash) VALUES(@u, N'性能基准只读', @h);
SELECT CAST(SCOPE_IDENTITY() AS bigint);",
            new { u = username, h = hasher.HashPassword(username, password) });
        foreach (var permission in new[] { "trace:read", "audit:read", "system:manage" })
            await conn.ExecuteAsync(
                "INSERT INTO dbo.user_permission(user_id, permission, granted_by) VALUES(@id, @p, N'perf')",
                new { id, p = permission });
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}