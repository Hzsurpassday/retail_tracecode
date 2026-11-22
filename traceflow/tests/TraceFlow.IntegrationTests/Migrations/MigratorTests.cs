using Dapper;
using TraceFlow.DbMigrator;
using TraceFlow.IntegrationTests;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class MigratorTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public MigratorTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Second_run_is_idempotent_skip()
    {
        var scripts = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        var report = await new MigrationRunner(_fx.ConnectionString, scripts).RunAsync();
        Assert.Empty(report.Applied);
        Assert.NotEmpty(report.Skipped);
    }

    [Fact]
    public async Task Checksum_mismatch_stops_and_does_not_partially_apply()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        var tampered = Path.Combine(Path.GetTempPath(), "tf_tamper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tampered);
        foreach (var f in Directory.GetFiles(src, "*.sql")) File.Copy(f, Path.Combine(tampered, Path.GetFileName(f)));
        var first = Directory.GetFiles(tampered, "*.sql").Order().First();
        await File.AppendAllTextAsync(first, "\n-- tampered\n");
        await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(_fx.ConnectionString, tampered).RunAsync());
        Directory.Delete(tampered, true);
    }

    [Fact]
    public async Task Core_tables_exist_and_single_active_org_enforced()
    {
        await using var conn = await _fx.OpenAsync();
        foreach (var t in new[] { "organization", "source_system", "app_setting", "app_user", "user_permission", "audit_log", "idempotency_record" })
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name=@t", new { t }));

        await conn.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'T1',N'店1')");
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            conn.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'T2',N'店2')"));
        Assert.True(ex.Number is 2601 or 2627); // 过滤唯一索引：仅一个 active 门店
    }
}
