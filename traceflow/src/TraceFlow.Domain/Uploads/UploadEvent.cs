namespace TraceFlow.Domain.Uploads;

public enum UploadEvent
{
    EligibilitySatisfied, EligibilityLost, Claimed,
    ReceiptAccepted, PartialEvidence, Rejected, TransportUncertain, DuplicateBillCode, LeaseLost,
    QueryProcessing, QueryAllSuccess, QueryPartial, QueryFailed, QueryUnresolved,
    QueryConfirmedAccepted, QueryConfirmedNotAccepted,
    ObservationExpired, RetryDue, ManualEscalate, ControlledRetryApproved,
    EvidenceVerifiedAccepted, EvidenceVerifiedProcessing,
    AllocationCancelled,
    // 复核结论变化（未发送 → 失去发送资格；已发送 → 只转人工，绝不降级终态/受理态）
    VerificationChangedAfterSend,
    // 退货占位撤销（从未发送、无分片、无尝试的 Blocked 占位行政取消）
    ReturnPlaceholderCancelled
}
