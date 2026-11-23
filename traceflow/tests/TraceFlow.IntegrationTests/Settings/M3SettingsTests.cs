using System.Runtime.Versioning; // DpapiProtector 仅 Windows（与 SettingsServiceTests 相同标注）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using Xunit;

namespace TraceFlow.IntegrationTests.Settings;

/// <summary>
/// M3 只读设置键（销售核验/分片/回查/发送）的直测：四个 getter 的默认值、app_setting 行覆盖、
/// 以及非法值回落（非数字/非正数不得抛错、不得产出 0 或负退避）。
/// 每测试独立库（IAsyncLifetime）：本组会写入 app_setting 行，类共享库会产生顺序依赖。
/// 这些键不在 Catalog 内（设置页契约锁定条目数），覆盖途径为直写 app_setting 行。
/// </summary>
[SupportedOSPlatform("windows")]
public class M3SettingsTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private SettingsService NewSvc() => new(_fx.ConnectionString, new DpapiProtector());

    /// <summary>模拟运维直写 app_setting（不经 Catalog 的 UpdateAsync 校验）：先插后更，覆盖 INSERT/UPDATE 两条路径。</summary>
    private async Task SetRawAsync(string key, string? valueJson)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_setting WHERE setting_key=@k)
    INSERT INTO dbo.app_setting(setting_key,value_json,updated_by) VALUES(@k,@v,N'test');
ELSE UPDATE dbo.app_setting SET value_json=@v, updated_by=N'test' WHERE setting_key=@k",
            new { k = key, v = valueJson });
    }

    [Fact]
    public async Task M3_getters_return_mandated_defaults_when_no_rows()
    {
        var svc = NewSvc();
        var sale = await svc.GetSaleVerifySettingsAsync(CancellationToken.None);
        Assert.Equal("M3-1", sale.RuleVersion);
        Assert.Equal(200, sale.MaxCodesPerRun);
        Assert.Equal(200, await svc.GetPartMaxCodesAsync(CancellationToken.None));
        Assert.Equal(new QueryPolicy(30, 20, 24, 600, 120), await svc.GetQueryPolicyAsync(CancellationToken.None));
        Assert.False(await svc.GetRetryPathCertifiedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task App_setting_rows_override_defaults_on_insert_and_update()
    {
        // 插入行：int 键 + 布尔键
        await SetRawAsync("sale.verify.maxCodesPerRun", "50");
        await SetRawAsync("top.dispatch.leaseSeconds", "240");
        await SetRawAsync("top.retryPathCertified", "true");

        var svc = NewSvc();
        Assert.Equal(50, (await svc.GetSaleVerifySettingsAsync(CancellationToken.None)).MaxCodesPerRun);
        Assert.Equal("M3-1", (await svc.GetSaleVerifySettingsAsync(CancellationToken.None)).RuleVersion); // 未覆盖的字符串键保持默认
        var policy = await svc.GetQueryPolicyAsync(CancellationToken.None);
        Assert.Equal(240, policy.DispatchLeaseSeconds);
        Assert.Equal(30, policy.BackoffSeconds); // 未覆盖的 int 键保持默认
        Assert.True(await svc.GetRetryPathCertifiedAsync(CancellationToken.None));

        // 更新同一行（UQ_app_setting_key 唯一 → UPDATE 分支）后重读
        await SetRawAsync("sale.verify.maxCodesPerRun", "120");
        await SetRawAsync("top.retryPathCertified", "false");
        Assert.Equal(120, (await NewSvc().GetSaleVerifySettingsAsync(CancellationToken.None)).MaxCodesPerRun);
        Assert.False(await NewSvc().GetRetryPathCertifiedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Non_numeric_or_non_positive_values_fall_back_to_defaults()
    {
        var svc = NewSvc();
        var intKeys = new[]
        {
            "sale.verify.maxCodesPerRun", "upload.part.maxCodes", "top.query.backoffSeconds", "top.query.maxAttempts",
            "top.query.manualAfterHours", "top.query.consistencyWindowSeconds", "top.dispatch.leaseSeconds",
        };
        // 非数字 / 空 / 0 / 负数 / 小数 / int 越界（long 可解析，强转会溢出）/ JSON 引号字符串
        var bad = new[] { "abc", "", "0", "-5", "3.5", "2147483648", "\"200\"" };
        foreach (var value in bad)
        {
            foreach (var key in intKeys) await SetRawAsync(key, value);
            // 不抛错，且四个 getter 全部回落默认（负数不得被采信成负退避）
            Assert.Equal(200, (await svc.GetSaleVerifySettingsAsync(CancellationToken.None)).MaxCodesPerRun);
            Assert.Equal(200, await svc.GetPartMaxCodesAsync(CancellationToken.None));
            var policy = await svc.GetQueryPolicyAsync(CancellationToken.None);
            Assert.Equal(new QueryPolicy(30, 20, 24, 600, 120), policy);
            Assert.True(policy.BackoffSeconds > 0, $"值 {value} 不得产生非正退避");
        }
    }

    [Fact]
    public async Task Retry_path_certified_accepts_only_true_or_false_text()
    {
        var svc = NewSvc();
        // 仅 true/false（大小写不敏感）被采信；1/0、引号字符串、乱码一律 false（fail-closed）
        await SetRawAsync("top.retryPathCertified", "TRUE");
        Assert.True(await svc.GetRetryPathCertifiedAsync(CancellationToken.None));
        await SetRawAsync("top.retryPathCertified", "False");
        Assert.False(await svc.GetRetryPathCertifiedAsync(CancellationToken.None));
        foreach (var bad in new[] { "true;", "1", "0", "yes", "\"true\"", "not-json", "" })
        {
            await SetRawAsync("top.retryPathCertified", bad);
            Assert.False(await svc.GetRetryPathCertifiedAsync(CancellationToken.None), $"值 {bad} 应按 false 处理");
        }
    }
}
