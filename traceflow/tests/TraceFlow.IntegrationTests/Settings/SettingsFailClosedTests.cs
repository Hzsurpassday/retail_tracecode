using System.Runtime.Versioning; // brief 缺失此 using（SupportedOSPlatform 所需）；与 SettingsServiceTests 同型（T6 偏差 1 先例）
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using Xunit;

namespace TraceFlow.IntegrationTests.Settings;

[SupportedOSPlatform("windows")] // DpapiProtector 仅 Windows（与 SettingsServiceTests 相同标注）
public class SettingsFailClosedTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public SettingsFailClosedTests(Sql2008Fixture fx) => _fx = fx;

    private async Task SetPausedRawAsync(string? valueJson) // null → value_json 置 NULL（存在行但值缺失的情形）
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_setting WHERE setting_key=N'sync.paused')
    INSERT INTO dbo.app_setting(setting_key,value_json,updated_by) VALUES(N'sync.paused',@v,N'test');
ELSE UPDATE dbo.app_setting SET value_json=@v WHERE setting_key=N'sync.paused'", new { v = valueJson });
    }

    [Fact]
    public async Task Garbage_pause_value_fails_closed()
    {
        await SetPausedRawAsync("not-json");
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        Assert.True(await svc.IsChannelPausedAsync(default));
        await SetPausedRawAsync("\"true\""); // JSON 字符串而非布尔→保守视为暂停
        Assert.True(await svc.IsChannelPausedAsync(default));
    }

    [Fact]
    public async Task Boolean_false_stays_open()
    {
        await SetPausedRawAsync("false");
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        Assert.False(await svc.IsChannelPausedAsync(default));
        await SetPausedRawAsync("true");
        Assert.True(await svc.IsChannelPausedAsync(default));
    }

    [Fact]
    public async Task Null_pause_value_fails_closed()
    {
        // R11a 仅豁免「确实没有设置行」；行存在而 value_json 为 NULL 属值缺失，保守视为暂停（fail-closed）。
        await SetPausedRawAsync(null);
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        Assert.True(await svc.IsChannelPausedAsync(default));
        await SetPausedRawAsync("false"); // 恢复常规值，避免污染同库后续断言
        Assert.False(await svc.IsChannelPausedAsync(default));
    }
}
