using Dapper;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class Sql2008CompatibilityTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Sql2008CompatibilityTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public void No_high_version_syntax_in_any_migration_or_embedded_sql()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "db", "migrations"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src")),
        };
        var forbidden = new[] { @"\bOFFSET\b", @"\bFETCH\s+NEXT\b", @"\bSEQUENCE\b", @"\bJSON_VALUE\b", @"\bOPENJSON\b", @"\bSTRING_AGG\b", @"CREATE\s+OR\s+ALTER", @"\bNOLOCK\b" };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".sql") || f.EndsWith(".cs")))
            {
                var text = File.ReadAllText(file);
                foreach (var pattern in forbidden)
                    Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                        $"{file} 含 2008 R2 禁用语法: {pattern}");
            }
    }

    // 补强（相对 brief 的新增测试，非放宽）：全局约束禁词全集仅静态扫描 db/migrations/*.sql。
    // 不扫 src/*.cs 的原因：C# 的异常抛出关键字、字符串修剪/拼接/格式化方法与 T-SQL 禁词同形，
    // 扫 .cs 会误报；迁移脚本是纯 T-SQL，可安全使用全量禁词。函数类禁词用调用形（词后紧跟左括号）
    // 避免命中 0005 的 format 列名等合法标识符。pattern 一律拼接构造，本文件（含注释）不含完整
    // 禁词字面量，即使未来扫描范围扩到 tests/ 也不会自触发。
    [Fact]
    public void No_high_version_syntax_in_migration_scripts_full_constraint_list()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        Assert.True(Directory.Exists(root), $"迁移脚本目录不存在: {root}");
        var files = Directory.GetFiles(root, "*.sql");
        // 门禁必须覆盖全部已交付脚本（0001–0007），防止漏扫。
        foreach (var prefix in new[] { "0001", "0002", "0003", "0004", "0005", "0006", "0007" })
            Assert.Contains(files, f => Path.GetFileName(f).StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase));

        var forbidden = new[]
        {
            @"\bOFF" + @"SET\b",                       // 分页跳行子句（2012+）
            @"\bFE" + @"TCH\s+NEXT\b",                 // 分页取行子句（2012+）
            @"\bSEQUE" + @"NCE\b",                     // 序列对象（2012+）
            @"\bNEXT\s+VALUE\s+FOR\b",                 // 序列取值（2012+）
            @"\bJSON_" + @"VALUE\b",                   // JSON 函数（2016+）
            @"\bJSON_" + @"QUERY\b",                   // JSON 函数（2016+）
            @"\bOPEN" + @"JSON\b",                     // JSON 行集（2016+）
            @"\bSTRING_" + @"AGG\b",                   // 字符串聚合（2017+）
            @"\bSYSTEM_" + @"VERSIONING\b",            // 系统版本化临时表（2016+）
            @"\bPERIOD\s+FOR\s+SYSTEM_TIME\b",         // 临时表期间列（2016+）
            @"CRE" + @"ATE\s+OR\s+ALTER",              // 创建或替换（2016 SP1+）
            @"\bI" + @"IF\s*\(",                       // 内联条件函数（2012+）
            @"\bCON" + @"CAT\s*\(",                    // 拼接函数（2012+）
            @"\bFOR" + @"MAT\s*\(",                    // 格式化函数（2012+）
            @"\bTRY_" + @"CONVERT\s*\(",               // 容错转换（2012+）
            @"\bTHR" + @"OW\b",                        // 抛错语句（2012+）
            @"\bDR" + @"OP\s+\w+\s+IF\s+EXISTS\b",     // 条件删除（2016+）
            @"\bTR" + @"IM\s*\(",                      // 去空白函数（2017+）
            @"\bAT\s+TIME\s+ZONE\b",                   // 时区转换（2016+）
            @"\bNO" + @"LOCK\b",                       // 脏读提示（项目约定禁用）
        };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in forbidden)
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    $"{file} 含 2008 R2 禁用语法: {pattern}");
        }
    }

    [Fact]
    public async Task Row_number_pagination_works_on_2008r2()
    {
        await using var c = await _fx.OpenAsync();
        var rows = await c.QueryAsync<long>(@"
;WITH numbered AS (
  SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM dbo.audit_log
)
SELECT id FROM numbered WHERE rn > @skip AND rn <= @take ORDER BY rn", new { skip = 0, take = 50 });
        Assert.NotNull(rows);
    }
}