namespace TraceFlow.Domain.Sales;

/// <summary>
/// 退货前序候选：<see cref="CodeId"/> 为码台账主键；<see cref="SourceLineId"/> 由调用方按码回填
/// （纯函数入参不含来源行，无法自行关联）；<see cref="PredecessorEventId"/> 仅在唯一可退回时非空；
/// <see cref="Error"/> 为 <see cref="ReturnRules"/> 的错误码（PREDECESSOR_MISSING / PREDECESSOR_AMBIGUOUS / ALREADY_RETURNED）。
/// </summary>
public sealed record ReturnCandidate(long CodeId, long? SourceLineId, long? PredecessorEventId, string? Error);

/// <summary>
/// 退货前序规则（RET-01/02/04 的纯函数部分，无 IO，可在单测内穷举）：
/// 每个退货码必须能唯一确定一条「尚未退回的最近 SALE 事件」作为前序。
/// <list type="bullet">
/// <item>无任何 SALE 事件 → <c>PREDECESSOR_MISSING</c>（未在该店销售过，不得退货上报）；</item>
/// <item>全部 SALE 事件已被退回（某 CONSUMER_RETURN 的 predecessor_event_id 指向它）→ <c>ALREADY_RETURNED</c>（一码仅一退）；</item>
/// <item>多于一条未退回 SALE 事件 → <c>PREDECESSOR_AMBIGUOUS</c>（操作员必须先在源系统澄清，绝不猜）；</item>
/// <item>恰好一条未退回 → 该事件即前序（唯一时自然也是最近一次）。</item>
/// </list>
/// 输入按码分组时允许同一码出现多次（调用方按事件逐条给出），事件 id 会先去重。
/// </summary>
public static class ReturnRules
{
    public const string ErrorMissing = "PREDECESSOR_MISSING";
    public const string ErrorAmbiguous = "PREDECESSOR_AMBIGUOUS";
    public const string ErrorAlreadyReturned = "ALREADY_RETURNED";

    /// <summary>同一退货码是否已有前序的确定性判定；返回顺序跟随输入中码的首次出现顺序。</summary>
    public static IReadOnlyList<ReturnCandidate> ResolvePredecessors(
        IReadOnlyList<(long CodeId, DateTime? OccurredAt, IReadOnlyList<long> SaleEventIds)> sales,
        IReadOnlyList<long> returnedEventIds)
    {
        ArgumentNullException.ThrowIfNull(sales);
        var returned = returnedEventIds is null
            ? new HashSet<long>()
            : new HashSet<long>(returnedEventIds);

        var candidates = new List<ReturnCandidate>(sales.Count);
        foreach (var group in sales.GroupBy(s => s.CodeId))
        {
            var eventIds = new List<long>();
            foreach (var entry in group)
            {
                if (entry.SaleEventIds is null) continue;
                foreach (var eventId in entry.SaleEventIds)
                    if (!eventIds.Contains(eventId)) eventIds.Add(eventId);
            }

            if (eventIds.Count == 0)
            {
                candidates.Add(new ReturnCandidate(group.Key, null, null, ErrorMissing));
                continue;
            }

            var unreturned = eventIds.Where(id => !returned.Contains(id)).ToList();
            if (unreturned.Count == 0)
                candidates.Add(new ReturnCandidate(group.Key, null, null, ErrorAlreadyReturned));
            else if (unreturned.Count > 1)
                candidates.Add(new ReturnCandidate(group.Key, null, null, ErrorAmbiguous));
            else
                candidates.Add(new ReturnCandidate(group.Key, null, unreturned[0], null));
        }
        return candidates;
    }
}