using TraceFlow.Application.Ops;

namespace TraceFlow.UnitTests.Ops;

/// <summary>
/// 性能基准 / 试运行脚本的库名守卫：只放行 TraceFlow_Trial_* 与 TraceFlow_Perf_*；
/// 保护库、集成测试库、系统库与含特殊字符的名字一律拒绝（脚本与工具同此口径）。
/// </summary>
public class DatabaseNamePolicyTests
{
    [Theory]
    [InlineData("TraceFlow_Trial_Smoke_1")]
    [InlineData("TraceFlow_Perf_x")]
    [InlineData("TraceFlow_Perf_20261124120000")]
    [InlineData("TraceFlow_Trial_20261124")]
    public void Accepts_trial_and_perf_names(string name)
    {
        Assert.True(DatabaseNamePolicy.TryValidate(name, out var error));
        Assert.Equal("", error);
        Assert.False(DatabaseNamePolicy.IsProtected(name));
    }

    [Theory]
    [InlineData("TraceFlow_Dev")]
    [InlineData("TraceFlow_M2Manual")]
    [InlineData("TraceFlow_M2Gate")]
    [InlineData("TraceFlow_M3Ui")]
    [InlineData("TraceFlow_Test_abc")]
    [InlineData("master")]
    [InlineData("model")]
    [InlineData("tempdb")]
    [InlineData("traceflow_dev")] // 大小写不敏感命中保护库
    public void Rejects_protected_system_and_test_databases(string name)
    {
        Assert.False(DatabaseNamePolicy.TryValidate(name, out var error));
        Assert.NotEqual("", error);
        Assert.True(DatabaseNamePolicy.IsProtected(name));
    }

    [Theory]
    [InlineData("TraceFlow_Trial_bad]name")]
    [InlineData("TraceFlow_Trial_has space")]
    [InlineData("TraceFlow_Trial_semi;colon")]
    [InlineData("TraceFlow_Trial_")]
    [InlineData("TraceFlow_Prod_x")]
    [InlineData("TraceFlow_Trial_" + "0123456789012345678901234567890123456789012345678901234567890")] // 61 位
    [InlineData("")]
    [InlineData((string?)null)]
    public void Rejects_malformed_names(string? name)
    {
        Assert.False(DatabaseNamePolicy.TryValidate(name, out _));
    }

    [Fact]
    public void Drop_policy_only_allows_trial_and_perf_prefixes()
    {
        Assert.True(DatabaseNamePolicy.TryValidate("TraceFlow_Trial_Summary_Check", out _));
        Assert.True(DatabaseNamePolicy.TryValidate("TraceFlow_Perf_keep", out _));
        // drop 与 create 同一策略：非 Trial/Perf 前缀永不通过。
        Assert.False(DatabaseNamePolicy.TryValidate("TraceFlow_Test_drop_me", out _));
        Assert.False(DatabaseNamePolicy.TryValidate("TraceFlow_Dev", out _));
        Assert.False(DatabaseNamePolicy.TryValidate("tempdb", out _));
    }

    [Fact]
    public void Trial_or_perf_prefix_check_is_explicit()
    {
        Assert.True(DatabaseNamePolicy.IsTrialOrPerfName("TraceFlow_Trial_A"));
        Assert.True(DatabaseNamePolicy.IsTrialOrPerfName("TraceFlow_Perf_B"));
        Assert.False(DatabaseNamePolicy.IsTrialOrPerfName("TraceFlow_Test_A"));
        Assert.False(DatabaseNamePolicy.IsTrialOrPerfName("TraceFlow_Dev"));
    }

    [Fact]
    public void Protected_list_covers_four_development_databases()
    {
        Assert.Equal(4, DatabaseNamePolicy.ProtectedDatabases.Count);
        Assert.Contains("TraceFlow_M3Ui", DatabaseNamePolicy.ProtectedDatabases);
    }
}