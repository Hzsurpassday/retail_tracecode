namespace TraceFlow.Domain.Uploads;

public enum UploadEvent
{
    EligibilitySatisfied, EligibilityLost, Claimed,
    ReceiptAccepted, PartialEvidence, Rejected, TransportUncertain, DuplicateBillCode, LeaseLost,
    QueryProcessing, QueryAllSuccess, QueryPartial, QueryFailed, QueryUnresolved,
    QueryConfirmedAccepted, QueryConfirmedNotAccepted,
    ObservationExpired, RetryDue, ManualEscalate, ControlledRetryApproved,
    EvidenceVerifiedAccepted, EvidenceVerifiedProcessing
}