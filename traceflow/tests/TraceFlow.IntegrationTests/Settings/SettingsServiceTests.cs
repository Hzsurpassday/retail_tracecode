using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 DpapiProtectorTests/ApiGateTests 标注模式一致（T6 偏差 1）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.IntegrationTests.Settings;

[SupportedOSPlatform("windows")] // DpapiProtector 仅 Windows（与 M0 SecretProtectorTests 相同标注）
// 每测试独立库：sync.pollSeconds 会被多个测试以 "0" 新建，类共享库会产生顺序依赖（与 SourceSyncTests 同模式）
public class SettingsServiceTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();
    private SettingsService NewSvc() => new(_fx.ConnectionString, new DpapiProtector());

    private async Task UpdateAsync(string key, string? valueJson, string? secretValue, string ver, string reason = "测试")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await NewSvc().UpdateAsync(c, tx, key, valueJson, secretValue, ver, reason, "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Update_new_int_setting_creates_row_and_audit()
    {
        await UpdateAsync("sync.pollSeconds", "45", null, "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.pollSeconds");
        Assert.Equal("45", view.ValueJson);
        Assert.False(view.IsSecret);
        Assert.NotEqual("0", view.Version);
        Assert.Equal("tester", view.UpdatedBy);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'settings.update' AND object_id=N'sync.pollSeconds'"));
    }

    [Theory]
    [InlineData("4")]      // 下界外
    [InlineData("3601")]   // 上界外
    [InlineData("3.5")]    // 非整数
    [InlineData("\"30\"")] // 类型错
    public async Task Int_bounds_and_type_violations_throw_validation(string bad)
    {
        await Assert.ThrowsAsync<SettingValidationException>(() => UpdateAsync("sync.pollSeconds", bad, null, "0"));
    }

    [Fact]
    public async Task Stale_version_throws_conflict_and_correct_version_succeeds()
    {
        await UpdateAsync("sync.rescanHours", "72", null, "0");
        var v1 = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.rescanHours").Version;
        await UpdateAsync("sync.rescanHours", "48", null, v1);
        await Assert.ThrowsAsync<SettingVersionException>(() => UpdateAsync("sync.rescanHours", "24", null, v1)); // 旧 ver
        var v2 = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.rescanHours").Version;
        // brief 原文断言 "24"（被拒绝写入的值）与其自身乐观并发语义矛盾：旧 ver 写入抛 409 且事务回滚，
        // 库中应保持最后一次成功写入 "48"（实测 Expected "24" / Actual "48"，T6 偏差 4）。断言改为验证拒绝写入未落库。
        Assert.Equal("48", (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.rescanHours").ValueJson);
        await UpdateAsync("sync.rescanHours", "72", null, v2); // 新 ver 成功
    }

    [Fact]
    public async Task Secret_is_write_only_never_echoed_and_audit_has_no_plaintext()
    {
        await UpdateAsync("top.appsecret", null, "super-secret-value-123", "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "top.appsecret");
        Assert.True(view.IsSecret);
        Assert.True(view.HasSecretValue);
        Assert.Null(view.ValueJson); // 永不回显
        Assert.Equal("super-secret-value-123", await NewSvc().GetSecretAsync("top.appsecret", CancellationToken.None));
        await using var c = new SqlConnection(_fx.ConnectionString);
        var audits = await c.QueryAsync<string>(
            "SELECT ISNULL(before_digest,N'') + ISNULL(after_digest,N'') + ISNULL(reason,N'') FROM dbo.audit_log WHERE object_id=N'top.appsecret'");
        Assert.All(audits, a => Assert.DoesNotContain("super-secret-value-123", a));
        // 密钥键禁止明文 valueJson
        await Assert.ThrowsAsync<SettingValidationException>(() => UpdateAsync("top.appsecret", "\"plain\"", null, "0"));
    }

    [Fact]
    public async Task Sync_policy_defaults_then_reflects_updates()
    {
        var p0 = await NewSvc().GetSyncPolicyAsync(CancellationToken.None);
        Assert.Equal(SyncPolicy.Default, p0);
        await UpdateAsync("sync.stabilizeSeconds", "5", null, "0");
        await UpdateAsync("sync.roundIntervalSeconds", "5", null, "0");
        var p1 = await NewSvc().GetSyncPolicyAsync(CancellationToken.None);
        Assert.Equal(5, p1.StabilizeSeconds);
        Assert.Equal(5, p1.RoundIntervalSeconds);
        Assert.Equal(30, p1.PollSeconds); // 未改键保持默认
    }

    [Fact]
    public async Task Import_bounds_defaults_and_pause_toggle()
    {
        var (bytes, rows) = await NewSvc().GetImportBoundsAsync(CancellationToken.None);
        Assert.Equal(20971520L, bytes);
        Assert.Equal(10000, rows);
        Assert.False(await NewSvc().IsChannelPausedAsync(CancellationToken.None));
        await UpdateAsync("sync.paused", "true", null, "0");
        Assert.True(await NewSvc().IsChannelPausedAsync(CancellationToken.None));
        Assert.Empty(await NewSvc().GetAuditPassValuesAsync(CancellationToken.None)); // 默认空→供应商不自动确认（G-TOP）
    }

    [Fact]
    public async Task Unknown_key_throws_validation()
    {
        await Assert.ThrowsAsync<SettingValidationException>(() => UpdateAsync("no.such.key", "1", null, "0"));
    }

    [Fact]
    public async Task Connection_test_rejects_non_erp_config_and_missing_id()
    {
        await UpdateAsync("sync.pollSeconds", "30", null, "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.pollSeconds");
        var svc = NewSvc();
        await Assert.ThrowsAsync<SettingValidationException>(() => svc.TestErpConnectionAsync(view.Id, CancellationToken.None));
        await Assert.ThrowsAsync<SettingValidationException>(() => svc.TestErpConnectionAsync(999999, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_test_with_stored_local_connection_succeeds()
    {
        // 用本机测试库连接串充当 ERP 连接（仅验证 SELECT 1 通路与 5 秒超时设置，不代表真实 KSOA 取证）
        await UpdateAsync("erp.connection", null, _fx.ConnectionString, "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "erp.connection");
        var r = await NewSvc().TestErpConnectionAsync(view.Id, CancellationToken.None);
        Assert.True(r.Ok);
    }
}
