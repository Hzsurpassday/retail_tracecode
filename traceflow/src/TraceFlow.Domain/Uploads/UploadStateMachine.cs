namespace TraceFlow.Domain.Uploads;

/// <summary>
/// 唯一转换表。只判定合法性；证据条件（观察窗/合同证据/快照未变/token）由 Application 层前置校验。
/// QueryConfirmedNotAccepted 仅当满足"一致性延迟窗口+合同证据"后才允许由调用方触发（IDEM-05）。
/// </summary>
public static class UploadStateMachine
{
    private static readonly Dictionary<(UploadState, UploadEvent), UploadState> Table = new()
    {
        [(UploadState.Blocked, UploadEvent.EligibilitySatisfied)] = UploadState.Ready,
        [(UploadState.Ready, UploadEvent.Claimed)] = UploadState.Dispatching,
        [(UploadState.Ready, UploadEvent.EligibilityLost)] = UploadState.Blocked,
        [(UploadState.Dispatching, UploadEvent.ReceiptAccepted)] = UploadState.Accepted,
        [(UploadState.Dispatching, UploadEvent.PartialEvidence)] = UploadState.PartialSuccess,
        [(UploadState.Dispatching, UploadEvent.Rejected)] = UploadState.Failed,
        [(UploadState.Dispatching, UploadEvent.TransportUncertain)] = UploadState.AcceptanceUnknown,
        [(UploadState.Dispatching, UploadEvent.DuplicateBillCode)] = UploadState.AcceptanceUnknown,
        [(UploadState.Dispatching, UploadEvent.LeaseLost)] = UploadState.AcceptanceUnknown,
        // 回查观察窗过期但始终未取得可推进的结论：受理态（Dispatching/Accepted）必须有出口，
        // 否则人工升级无路可走、整单会永久卡在等待态（H1）。
        [(UploadState.Dispatching, UploadEvent.ObservationExpired)] = UploadState.ManualAction,
        [(UploadState.Accepted, UploadEvent.ObservationExpired)] = UploadState.ManualAction,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedAccepted)] = UploadState.Accepted,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedNotAccepted)] = UploadState.RetryWait,
        [(UploadState.AcceptanceUnknown, UploadEvent.ManualEscalate)] = UploadState.ManualAction,
        [(UploadState.Accepted, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.Accepted, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.Accepted, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.Accepted, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.Accepted, UploadEvent.QueryUnresolved)] = UploadState.ResultUnresolved,
        [(UploadState.Processing, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.Processing, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.Processing, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.Processing, UploadEvent.QueryUnresolved)] = UploadState.ResultUnresolved,
        [(UploadState.Processing, UploadEvent.ObservationExpired)] = UploadState.ManualAction,
        [(UploadState.RetryWait, UploadEvent.RetryDue)] = UploadState.Ready,
        [(UploadState.PartialSuccess, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.PartialSuccess, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.PartialSuccess, UploadEvent.ManualEscalate)] = UploadState.ManualAction,
        [(UploadState.ResultUnresolved, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.ResultUnresolved, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.ResultUnresolved, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.ResultUnresolved, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.ResultUnresolved, UploadEvent.ObservationExpired)] = UploadState.ManualAction,
        [(UploadState.Failed, UploadEvent.ControlledRetryApproved)] = UploadState.RetryWait,
        [(UploadState.Failed, UploadEvent.ManualEscalate)] = UploadState.ManualAction,
        [(UploadState.ManualAction, UploadEvent.EvidenceVerifiedAccepted)] = UploadState.Accepted,
        [(UploadState.ManualAction, UploadEvent.EvidenceVerifiedProcessing)] = UploadState.Processing,
        [(UploadState.ManualAction, UploadEvent.QueryConfirmedNotAccepted)] = UploadState.RetryWait,
        [(UploadState.ManualAction, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.ManualAction, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.ManualAction, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.ManualAction, UploadEvent.QueryFailed)] = UploadState.Failed,
        // IN-09：从未发起网络调用的冻结任务可原子取消；发送后任何状态不可达 Cancelled
        [(UploadState.Blocked, UploadEvent.AllocationCancelled)] = UploadState.Cancelled,
        [(UploadState.Ready, UploadEvent.AllocationCancelled)] = UploadState.Cancelled,
    };

    public static IReadOnlyList<(UploadState From, UploadEvent Event, UploadState To)> AllTransitions { get; } =
        Table.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();

    public static bool TryGetNext(UploadState current, UploadEvent evt, out UploadState next)
        => Table.TryGetValue((current, evt), out next);
}
