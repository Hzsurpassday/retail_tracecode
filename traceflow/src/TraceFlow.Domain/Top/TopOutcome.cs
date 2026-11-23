namespace TraceFlow.Domain.Top;

public enum TopOutcomeKind
{
    Success, PartialEvidence, AuthError, RateLimited, SystemError, BusinessRejected, TransportUncertain, DuplicateBillCode
}

public sealed record TopOutcome(
    TopOutcomeKind Kind,
    string? Code,
    string? SubCode,
    string? Message,
    string? ResultJson,
    string? RequestId,
    int? RetryAfterSeconds);
