using TraceFlow.Domain.Sales;

namespace TraceFlow.UnitTests.Sales;

/// <summary>
/// RET-02 前序解析纯规则：每码唯一未退货前序事件；0 条 MISSING、多条 AMBIGUOUS、已全退 ALREADY_RETURNED。
/// 不触库、不依赖行结构（SourceLineId 由调用方回填）。
/// </summary>
public class ReturnRulesTests
{
    private static (long CodeId, DateTime? OccurredAt, IReadOnlyList<long> SaleEventIds) Entry(long codeId,
        DateTime? occurredAt, params long[] eventIds)
        => (codeId, occurredAt, eventIds.ToList());

    [Fact]
    public void Unique_unreturned_sale_event_is_the_predecessor()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[] { Entry(11, new DateTime(2026, 11, 1), 101) }, Array.Empty<long>());

        var candidate = Assert.Single(result);
        Assert.Equal(11, candidate.CodeId);
        Assert.Equal(101, candidate.PredecessorEventId);
        Assert.Null(candidate.Error);
    }

    [Fact]
    public void No_sale_event_reports_PREDECESSOR_MISSING()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[] { Entry(12, null) }, Array.Empty<long>());

        var candidate = Assert.Single(result);
        Assert.Null(candidate.PredecessorEventId);
        Assert.Equal(ReturnRules.ErrorMissing, candidate.Error);
    }

    [Fact]
    public void All_sale_events_returned_reports_ALREADY_RETURNED()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[] { Entry(13, new DateTime(2026, 11, 1), 201, 202) }, new long[] { 201, 202 });

        var candidate = Assert.Single(result);
        Assert.Null(candidate.PredecessorEventId);
        Assert.Equal(ReturnRules.ErrorAlreadyReturned, candidate.Error);
    }

    [Fact]
    public void Multiple_unreturned_events_report_PREDECESSOR_AMBIGUOUS()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[] { Entry(14, new DateTime(2026, 11, 1), 301, 302, 303) }, new long[] { 301 });

        var candidate = Assert.Single(result);
        Assert.Null(candidate.PredecessorEventId);
        Assert.Equal(ReturnRules.ErrorAmbiguous, candidate.Error);
    }

    [Fact]
    public void One_returned_one_unreturned_picks_the_unreturned_event()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[] { Entry(15, new DateTime(2026, 11, 1), 401, 402) }, new long[] { 401 });

        var candidate = Assert.Single(result);
        Assert.Equal(402, candidate.PredecessorEventId);
        Assert.Null(candidate.Error);
    }

    [Fact]
    public void Duplicate_event_ids_and_duplicate_code_entries_are_collapsed()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[]
            {
                Entry(16, new DateTime(2026, 11, 1), 501, 501),
                Entry(16, new DateTime(2026, 11, 1), 501),
            }, Array.Empty<long>());

        var candidate = Assert.Single(result);
        Assert.Equal(501, candidate.PredecessorEventId);
        Assert.Null(candidate.Error);
    }

    [Fact]
    public void Occurred_at_is_reserved_and_never_disambiguates_multiple_unreturned()
    {
        // F2：OccurredAt 仅为调用方展示/排序预留；时间先后绝不能把 AMBIGUOUS 静默降级为「最近一条」。
        var result = ReturnRules.ResolvePredecessors(
            new[]
            {
                Entry(20, new DateTime(2026, 11, 1), 701),
                Entry(20, new DateTime(2026, 11, 5), 702),
            }, Array.Empty<long>());

        var candidate = Assert.Single(result);
        Assert.Null(candidate.PredecessorEventId);
        Assert.Equal(ReturnRules.ErrorAmbiguous, candidate.Error);
    }

    [Fact]
    public void Codes_are_resolved_independently()
    {
        var result = ReturnRules.ResolvePredecessors(
            new[]
            {
                Entry(17, new DateTime(2026, 11, 1), 601),
                Entry(18, null),
                Entry(19, new DateTime(2026, 11, 1), 602, 603),
            }, new long[] { 602 });

        Assert.Equal(3, result.Count);
        Assert.Equal(601, result.Single(c => c.CodeId == 17).PredecessorEventId);
        Assert.Equal(ReturnRules.ErrorMissing, result.Single(c => c.CodeId == 18).Error);
        Assert.Equal(603, result.Single(c => c.CodeId == 19).PredecessorEventId);
    }
}