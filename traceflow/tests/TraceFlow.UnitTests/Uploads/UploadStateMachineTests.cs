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
    };

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public void Illegal_edge_rejected(UploadState from, UploadEvent evt)
    {
        Assert.False(UploadStateMachine.TryGetNext(from, evt, out _));
    }

    [Fact]
    public void Succeeded_is_terminal_and_every_other_state_has_exit()
    {
        Assert.DoesNotContain(UploadStateMachine.AllTransitions, t => t.From == UploadState.Succeeded);
        foreach (UploadState s in Enum.GetValues<UploadState>())
            if (s != UploadState.Succeeded)
                Assert.Contains(UploadStateMachine.AllTransitions, t => t.From == s);
    }
}
