using TraceFlow.Domain.Uploads;
using Xunit;

namespace TraceFlow.UnitTests.Uploads;

public class UploadStateMachineTests
{
    public static TheoryData<UploadState, UploadEvent, UploadState> LegalEdges => new()
    {
        { UploadState.Blocked, UploadEvent.EligibilitySatisfied, UploadState.Ready },
        { UploadState.Ready, UploadEvent.Claimed, UploadState.Dispatching },
        { UploadState.Ready, UploadEvent.EligibilityLost, UploadState.Blocked },
        { UploadState.Dispatching, UploadEvent.ReceiptAccepted, UploadState.Accepted },
        { UploadState.Dispatching, UploadEvent.PartialEvidence, UploadState.PartialSuccess },
        { UploadState.Dispatching, UploadEvent.Rejected, UploadState.Failed },
        { UploadState.Dispatching, UploadEvent.TransportUncertain, UploadState.AcceptanceUnknown },
        { UploadState.Dispatching, UploadEvent.DuplicateBillCode, UploadState.AcceptanceUnknown },
        { UploadState.Dispatching, UploadEvent.LeaseLost, UploadState.AcceptanceUnknown },
        { UploadState.Dispatching, UploadEvent.ObservationExpired, UploadState.ManualAction },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedAccepted, UploadState.Accepted },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedNotAccepted, UploadState.RetryWait },
        { UploadState.AcceptanceUnknown, UploadEvent.ManualEscalate, UploadState.ManualAction },
        { UploadState.Accepted, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.Accepted, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.Accepted, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.Accepted, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.Accepted, UploadEvent.QueryUnresolved, UploadState.ResultUnresolved },
        { UploadState.Accepted, UploadEvent.ObservationExpired, UploadState.ManualAction },
        { UploadState.Processing, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.Processing, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.Processing, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.Processing, UploadEvent.QueryUnresolved, UploadState.ResultUnresolved },
        { UploadState.Processing, UploadEvent.ObservationExpired, UploadState.ManualAction },
        { UploadState.RetryWait, UploadEvent.RetryDue, UploadState.Ready },
        { UploadState.PartialSuccess, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.PartialSuccess, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.PartialSuccess, UploadEvent.ManualEscalate, UploadState.ManualAction },
        { UploadState.ResultUnresolved, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.ResultUnresolved, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.ResultUnresolved, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.ResultUnresolved, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.ResultUnresolved, UploadEvent.ObservationExpired, UploadState.ManualAction },
        { UploadState.Failed, UploadEvent.ControlledRetryApproved, UploadState.RetryWait },
        { UploadState.Failed, UploadEvent.ManualEscalate, UploadState.ManualAction },
        { UploadState.ManualAction, UploadEvent.EvidenceVerifiedAccepted, UploadState.Accepted },
        { UploadState.ManualAction, UploadEvent.EvidenceVerifiedProcessing, UploadState.Processing },
        { UploadState.ManualAction, UploadEvent.QueryConfirmedNotAccepted, UploadState.RetryWait },
        { UploadState.ManualAction, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.ManualAction, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.ManualAction, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.ManualAction, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.Blocked, UploadEvent.AllocationCancelled, UploadState.Cancelled },
        { UploadState.Ready, UploadEvent.AllocationCancelled, UploadState.Cancelled },
        // 复核结论变化（VerificationChangedAfterSend）：允许来源集合与既有 SQL 守卫逐项一致。
        { UploadState.Blocked, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.Ready, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.Dispatching, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.AcceptanceUnknown, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.RetryWait, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.PartialSuccess, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.ResultUnresolved, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        { UploadState.ManualAction, UploadEvent.VerificationChangedAfterSend, UploadState.ManualAction },
        // 退货占位撤销：仅 Blocked 占位（零分片零尝试）。
        { UploadState.Blocked, UploadEvent.ReturnPlaceholderCancelled, UploadState.Cancelled },
    };

    [Theory]
    [MemberData(nameof(LegalEdges))]
    public void Legal_edge_transitions(UploadState from, UploadEvent evt, UploadState to)
    {
        Assert.True(UploadStateMachine.TryGetNext(from, evt, out var next));
        Assert.Equal(to, next);
        Assert.Contains((from, evt, to), UploadStateMachine.AllTransitions);
    }

    public static TheoryData<UploadState, UploadEvent> IllegalEdges => new()
    {
        { UploadState.Succeeded, UploadEvent.Claimed },
        { UploadState.Succeeded, UploadEvent.QueryAllSuccess },
        { UploadState.Succeeded, UploadEvent.ManualEscalate },
        { UploadState.Dispatching, UploadEvent.RetryDue },          // 超时不得直接回 READY
        { UploadState.Processing, UploadEvent.Claimed },            // 处理中不得重新上传
        { UploadState.AcceptanceUnknown, UploadEvent.Claimed },     // 受理未知不得盲目重发
        { UploadState.PartialSuccess, UploadEvent.RetryDue },       // 部分成功禁整单重排
        { UploadState.Accepted, UploadEvent.ReceiptAccepted },
        { UploadState.Blocked, UploadEvent.Claimed },
        { UploadState.ManualAction, UploadEvent.EligibilitySatisfied }, // 人工不能直接设成功/就绪
        { UploadState.Dispatching, UploadEvent.AllocationCancelled },   // 已发起发送不可行政取消（IN-09）
        { UploadState.Accepted, UploadEvent.AllocationCancelled },
        { UploadState.Cancelled, UploadEvent.Claimed },                 // Cancelled 是终态
        // 复核结论变化绝不降级受理态/终态（与既有 SQL 守卫一致：只保留，不转人工）。
        { UploadState.Accepted, UploadEvent.VerificationChangedAfterSend },
        { UploadState.Processing, UploadEvent.VerificationChangedAfterSend },
        { UploadState.Succeeded, UploadEvent.VerificationChangedAfterSend },
        { UploadState.Failed, UploadEvent.VerificationChangedAfterSend },
        { UploadState.Cancelled, UploadEvent.VerificationChangedAfterSend },
        // 退货占位撤销只允许 Blocked 占位，任何发送/受理相关状态都不可达 Cancelled。
        { UploadState.Ready, UploadEvent.ReturnPlaceholderCancelled },
        { UploadState.Dispatching, UploadEvent.ReturnPlaceholderCancelled },
        { UploadState.AcceptanceUnknown, UploadEvent.ReturnPlaceholderCancelled },
        { UploadState.ManualAction, UploadEvent.ReturnPlaceholderCancelled },
        { UploadState.Cancelled, UploadEvent.ReturnPlaceholderCancelled },
    };

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public void Illegal_edge_rejected(UploadState from, UploadEvent evt)
    {
        Assert.False(UploadStateMachine.TryGetNext(from, evt, out _));
    }

    [Fact]
    public void Terminal_states_have_no_exit_and_every_other_state_has_exit()
    {
        Assert.DoesNotContain(UploadStateMachine.AllTransitions, t => t.From == UploadState.Succeeded);
        Assert.DoesNotContain(UploadStateMachine.AllTransitions, t => t.From == UploadState.Cancelled);
        foreach (UploadState s in Enum.GetValues<UploadState>())
            if (s != UploadState.Succeeded && s != UploadState.Cancelled)
                Assert.Contains(UploadStateMachine.AllTransitions, t => t.From == s);
    }

    /// <summary>
    /// 已受理/已发送但始终拿不到结论的分片必须能升级人工（H1）：只经观察窗过期一条边，
    /// 不接受 ManualEscalate（该边只留给受理未知/部分成功等已取证的中间态），两条路都只通向 ManualAction。
    /// </summary>
    [Theory]
    [InlineData(UploadState.Accepted)]
    [InlineData(UploadState.Dispatching)]
    public void Pre_result_states_escalate_via_observation_expired_only(UploadState from)
    {
        Assert.True(UploadStateMachine.TryGetNext(from, UploadEvent.ObservationExpired, out var next));
        Assert.Equal(UploadState.ManualAction, next);
        Assert.False(UploadStateMachine.TryGetNext(from, UploadEvent.ManualEscalate, out _));
    }

    /// <summary>每个可回查状态都必须有到 ManualAction 的合法出口，杜绝"升级无路可走→永远重排"。</summary>
    [Theory]
    [InlineData(UploadState.Dispatching)]
    [InlineData(UploadState.Accepted)]
    [InlineData(UploadState.Processing)]
    [InlineData(UploadState.AcceptanceUnknown)]
    [InlineData(UploadState.PartialSuccess)]
    [InlineData(UploadState.ResultUnresolved)]
    public void Queryable_states_can_reach_manual_action(UploadState from)
        => Assert.Equal(UploadState.ManualAction, UploadStateMachine.AllTransitions
            .Where(t => t.From == from)
            .Select(t => (UploadState?)t.To)
            .FirstOrDefault(to => to == UploadState.ManualAction));

    /// <summary>
    /// Dispatching 不是回查状态（F1）：状态机不得有从 Dispatching 出发的 Query* 边，
    /// 否则回查服务会把平台的 AllSuccess/Partial/Failed 落进非法转换而静默丢弃。
    /// 在途/崩溃的发送只经 LeaseLost/TransportUncertain 收敛为 AcceptanceUnknown 后再回查。
    /// </summary>
    [Fact]
    public void Dispatching_has_no_query_edges()
    {
        var queryEvents = new[]
        {
            UploadEvent.QueryAllSuccess, UploadEvent.QueryPartial, UploadEvent.QueryFailed,
            UploadEvent.QueryProcessing, UploadEvent.QueryUnresolved,
            UploadEvent.QueryConfirmedAccepted, UploadEvent.QueryConfirmedNotAccepted,
        };
        Assert.All(queryEvents,
            e => Assert.False(UploadStateMachine.TryGetNext(UploadState.Dispatching, e, out _), $"Dispatching 不应有 {e} 边"));
        Assert.DoesNotContain(UploadStateMachine.AllTransitions,
            t => t.From == UploadState.Dispatching && t.Event.ToString().StartsWith("Query", StringComparison.Ordinal));
    }

    /// <summary>
    /// 转换表是唯一事实源：复核结论变化的允许来源集合必须与改造前 SQL 守卫（「非 Accepted/Processing/
    /// Succeeded/Failed/Cancelled」）逐项一致——不多不少，且目标恒为 ManualAction。
    /// </summary>
    [Fact]
    public void Verification_changed_sources_match_previous_guard_exactly()
    {
        var preserved = new[]
        {
            UploadState.Accepted, UploadState.Processing, UploadState.Succeeded, UploadState.Failed,
            UploadState.Cancelled,
        };
        var expected = Enum.GetValues<UploadState>().Except(preserved).OrderBy(s => s).ToList();
        var actual = UploadStateMachine.AllTransitions
            .Where(t => t.Event == UploadEvent.VerificationChangedAfterSend)
            .ToList();
        Assert.Equal(expected, actual.Select(t => t.From).OrderBy(s => s).ToList());
        Assert.All(actual, t => Assert.Equal(UploadState.ManualAction, t.To));
    }

    /// <summary>退货占位撤销的来源集合恒为单个 Blocked（不拓宽到 Ready/发送后状态），目标恒为 Cancelled。</summary>
    [Fact]
    public void Return_placeholder_cancel_source_is_blocked_only()
    {
        var edge = Assert.Single(UploadStateMachine.AllTransitions,
            t => t.Event == UploadEvent.ReturnPlaceholderCancelled);
        Assert.Equal((UploadState.Blocked, UploadState.Cancelled), (edge.From, edge.To));
    }
}
