using TraceFlow.Domain.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

/// <summary>
/// billstatus 候选映射（TOP-02/03/04）：上传成功≠处理成功；逐码证据缺失一律 UNKNOWN；
/// 结构缺失/不可解析 → Unresolved（继续回查），绝不升级成功。fixture 读取 docs/contracts/fixtures/top。
/// </summary>
public class BillStatusParserTests
{
    /// <summary>
    /// 读 docs/contracts/fixtures/top 下的候选信封（bin 上溯 6 级到仓库根），并按网关同样方式拆出
    /// <c>*_response</c> 业务体——解析器入参是业务体，不是文件顶层包装。
    /// </summary>
    private static string Read(string name)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "docs", "contracts", "fixtures", "top", name));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Name.EndsWith("_response", StringComparison.Ordinal))
                return p.Value.GetRawText();
        return doc.RootElement.GetRawText();
    }

    [Fact]
    public void Result_type_1_is_processing()
        => Assert.Equal(BillStatusKind.Processing, BillStatusParser.Parse(Read("result-type-1.json")).Kind);

    [Fact]
    public void Result_type_3_or_flag_52_is_partial_with_per_code_evidence()
    {
        var r = BillStatusParser.Parse(Read("partial-flag-52.json"));
        Assert.Equal(BillStatusKind.Partial, r.Kind);
    }

    [Fact]
    public void Missing_code_status_is_UNKNOWN_not_success()
    {
        var r = BillStatusParser.Parse("""{"result_type":"2","code_list":[{"code":"8...1","status":"1"},{"code":"8...2"}]}""");
        Assert.Equal(new[] { "SUCCESS", "UNKNOWN" }, r.Codes.Select(c => c.Result));
    }

    [Fact]
    public void Missing_model_is_unresolved_not_success()
        => Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse("{\"foo\":1}").Kind);

    [Fact]
    public void Success_fixture_walks_all_codes_successful()
    {
        var r = BillStatusParser.Parse(Read("billstatus-success.json"));
        Assert.Equal(BillStatusKind.AllSuccess, r.Kind);
        Assert.Equal(new[] { "SUCCESS", "SUCCESS" }, r.Codes.Select(c => c.Result));
    }

    [Fact]
    public void Partial_fixture_keeps_failed_code_evidence()
    {
        var r = BillStatusParser.Parse(Read("billstatus-partial.json"));
        Assert.Equal(BillStatusKind.Partial, r.Kind);
        Assert.Equal(new[] { "SUCCESS", "FAILED" }, r.Codes.Select(c => c.Result));
        Assert.Equal("码已被其他企业绑定", r.Codes[1].Reason);
        Assert.Equal("81234567890123456782", r.Codes[1].Code);
    }

    [Fact]
    public void Result_type_4_is_failed()
    {
        var r = BillStatusParser.Parse("""{"result_type":"4","code_list":[{"code":"8...1","status":"2"}]}""");
        Assert.Equal(BillStatusKind.Failed, r.Kind);
        Assert.Equal("FAILED", Assert.Single(r.Codes).Result);
    }

    [Fact]
    public void Result_type_3_with_empty_code_list_is_partial_not_success()
    {
        var r = BillStatusParser.Parse("""{"result_type":"3","sub_process_flag":"52","code_list":[]}""");
        Assert.Equal(BillStatusKind.Partial, r.Kind);
        Assert.Empty(r.Codes);
        Assert.NotNull(r.Note); // 证据不足必须留下说明
    }

    [Fact]
    public void Result_type_2_with_failed_or_unknown_codes_never_upgrades_to_success()
    {
        var failed = BillStatusParser.Parse("""{"result_type":"2","code_list":[{"code":"a","status":"2"}]}""");
        Assert.Equal(BillStatusKind.Partial, failed.Kind);
        var unknown = BillStatusParser.Parse("""{"result_type":"2","code_list":[{"code":"a","status":"9"}]}""");
        Assert.Equal(BillStatusKind.Partial, unknown.Kind);
        Assert.Equal("UNKNOWN", Assert.Single(unknown.Codes).Result);
    }

    [Fact]
    public void Null_or_blank_payload_is_unresolved()
    {
        Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse(null).Kind);
        Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse("   ").Kind);
    }

    [Fact]
    public void Malformed_json_is_unresolved_not_success()
    {
        Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse("{not json").Kind);
        Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse("[]").Kind);
    }

    [Fact]
    public void Result_json_without_result_type_or_model_is_unresolved()
        => Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse("""{"code_list":[{"code":"a","status":"1"}]}""").Kind);

    [Fact]
    public void Sub_process_flag_52_alone_is_partial()
    {
        var r = BillStatusParser.Parse("""{"sub_process_flag":"52","code_list":[{"code":"a","status":"1"}]}""");
        Assert.Equal(BillStatusKind.Partial, r.Kind);
    }

    [Fact]
    public void Duplicate_code_entries_are_collapsed_to_first_evidence()
    {
        var r = BillStatusParser.Parse("""{"result_type":"3","code_list":[{"code":"a","status":"1"},{"code":"a","status":"2"}]}""");
        Assert.Equal("SUCCESS", Assert.Single(r.Codes).Result);
    }
}