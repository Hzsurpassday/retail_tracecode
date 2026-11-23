namespace TraceFlow.Domain.Uploads;

/// <summary>
/// 任务级状态聚合（DEF-01 / §9.2）：多分片任务的状态由分片状态按严重度取最高者。
/// 优先级（高→低）：
/// <c>AcceptanceUnknown &gt; ManualAction &gt; Dispatching &gt; Processing &gt; ResultUnresolved &gt; PartialSuccess
/// &gt; Failed &gt; Ready &gt; RetryWait &gt; Succeeded &gt; Blocked &gt; Accepted</c>。
/// <para>
/// <c>ManualAction</c> 覆盖除 <c>AcceptanceUnknown</c> 之外的一切（需继续回查/人工核对），位置固定在第二位。
/// <c>Ready</c>/<c>RetryWait</c> 表示仍有待发送/待重试的分片，必须高于已完成态（<c>Succeeded</c>/<c>Accepted</c>），
/// 否则任务级一旦提前落定就会阻断后续分片的发送前置条件。
/// </para>
/// <para><c>Cancelled</c> 是本地行政终态（IN-09），不参与聚合：全部分片 Cancelled 时任务同为 Cancelled。</para>
/// 空集合抛 <see cref="ArgumentException"/>（调用方缺陷，不得静默返回默认值）。
/// </summary>
public static class TaskStateAggregator
{
    /// <summary>严重度表：索引越小越严重。枚举全集必须出现在此表（Cancelled 单独处理）。</summary>
    private static readonly UploadState[] Severity =
    {
        UploadState.AcceptanceUnknown,
        UploadState.ManualAction,
        UploadState.Dispatching,
        UploadState.Processing,
        UploadState.ResultUnresolved,
        UploadState.PartialSuccess,
        UploadState.Failed,
        UploadState.Ready,
        UploadState.RetryWait,
        UploadState.Succeeded,
        UploadState.Blocked,
        UploadState.Accepted,
    };

    public static UploadState Aggregate(IReadOnlyCollection<UploadState> partStates)
    {
        ArgumentNullException.ThrowIfNull(partStates);
        if (partStates.Count == 0)
            throw new ArgumentException("分片状态集合不能为空", nameof(partStates));

        UploadState? winner = null;
        var winnerRank = int.MaxValue;
        foreach (var state in partStates)
        {
            if (state == UploadState.Cancelled) continue; // 取消分片不参与，也不阻断其余分片
            var rank = Array.IndexOf(Severity, state);
            if (rank >= 0 && rank < winnerRank)
            {
                winnerRank = rank;
                winner = state;
            }
        }

        // 全为 Cancelled：任务随分片一同取消（无活动分片可推进）。
        return winner ?? UploadState.Cancelled;
    }
}