using TraceFlow.Domain.Sales;

namespace TraceFlow.UnitTests.Sales;

/// <summary>SALE-01..04 纯规则：错误码优先序、正负行分桶、EXEMPT 依据门禁（brief Step 1 用例 + 补充边界）。</summary>
public class SaleRulesTests
{
    private static SaleLineInput Line(long id, decimal qty, string? code, string cls = "REQUIRED", string? reason = null,
        string kind = "SALE")
        => new(id, $"L{id}", "SP0001", qty, code, kind, cls, reason);

    [Fact]
    public void Required_line_without_code_fails_with_MISSING_CODE()
        => Assert.Equal("MISSING_CODE", SaleRules.Evaluate(new[] { Line(1, 1, null) }).FailCode);

    [Fact]
    public void Same_code_twice_in_document_fails_with_DUPLICATE_CODE()
        => Assert.Equal("DUPLICATE_CODE",
            SaleRules.Evaluate(new[] { Line(1, 1, "81234567890123456789"), Line(2, 1, "81234567890123456789") }).FailCode);

    [Fact]
    public void Exempt_line_with_reason_is_skipped_and_not_uploaded()
    {
        var r = SaleRules.Evaluate(new[] { Line(1, 2, null, "EXEMPT", "OTC 不纳入追溯") });
        Assert.True(r.Ok);
        Assert.Null(r.FailCode);
        Assert.Empty(r.SaleLines);
        Assert.Empty(r.ReturnLines);
    }

    [Fact]
    public void Exempt_without_reason_is_UNKNOWN_CLASSIFICATION()
        => Assert.Equal("UNKNOWN_CLASSIFICATION", SaleRules.Evaluate(new[] { Line(1, 2, null, "EXEMPT") }).FailCode);

    [Fact]
    public void Unknown_classification_blocks()
        => Assert.Equal("UNKNOWN_CLASSIFICATION", SaleRules.Evaluate(new[] { Line(1, 1, "81234567890123456789", "UNKNOWN") }).FailCode);

    [Fact]
    public void Negative_line_goes_to_return_bucket_and_never_to_sale()
    {
        var r = SaleRules.Evaluate(new[]
        {
            Line(1, 1, "81234567890123456789"),
            Line(2, -1, "81234567890123456790", kind: "CONSUMER_RETURN"),
        });
        Assert.True(r.Ok);
        Assert.Single(r.SaleLines);
        Assert.Single(r.ReturnLines);
        Assert.Equal(2, r.ReturnLines[0].SourceLineId);
    }

    [Fact]
    public void Unsupported_code_format_fails_with_CODE_FORMAT()
        => Assert.Equal("CODE_FORMAT", SaleRules.Evaluate(new[] { Line(1, 1, "12345") }).FailCode);

    [Fact]
    public void Failure_carries_diagnostics_for_operators()
    {
        var r = SaleRules.Evaluate(new[] { Line(1, 1, null), Line(2, 1, "81234567890123456789", "UNKNOWN") });
        // 分类门禁先于码门禁：UNKNOWN 行优先暴露，缺失码留到下一轮修复后再报。
        Assert.Equal("UNKNOWN_CLASSIFICATION", r.FailCode);
        Assert.Single(r.UnknownClassLines);
        Assert.Equal(2, r.UnknownClassLines[0].SourceLineId);
    }

    [Fact]
    public void Zero_quantity_line_is_neither_sale_nor_return()
    {
        var r = SaleRules.Evaluate(new[] { Line(1, 0, "81234567890123456789", kind: "NO_EVENT") });
        Assert.True(r.Ok);
        Assert.Empty(r.SaleLines);
        Assert.Empty(r.ReturnLines);
    }

    [Fact]
    public void Duplicate_detected_across_sale_and_return_lines()
    {
        var r = SaleRules.Evaluate(new[]
        {
            Line(1, 1, "81234567890123456789"),
            Line(2, -1, "81234567890123456789", kind: "CONSUMER_RETURN"),
        });
        Assert.Equal("DUPLICATE_CODE", r.FailCode);
        Assert.Equal(new[] { "81234567890123456789" }, r.DuplicateCodes);
    }

    [Fact]
    public void Exempt_code_does_not_take_part_in_duplicate_detection()
    {
        var r = SaleRules.Evaluate(new[]
        {
            Line(1, 1, "81234567890123456789"),
            Line(2, 1, "81234567890123456789", "EXEMPT", "OTC 不纳入追溯"),
        });
        Assert.True(r.Ok);
        Assert.Single(r.SaleLines);
    }
}
