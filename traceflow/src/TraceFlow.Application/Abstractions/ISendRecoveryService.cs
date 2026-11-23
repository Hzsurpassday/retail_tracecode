namespace TraceFlow.Application.Abstractions;

public interface ISendRecoveryService
{
    Task<int> RecoverAsync(CancellationToken ct);
    Task AppendStaleEvidenceAsync(long taskId, long partId, int attemptNo, string attemptKind,
        long fencingToken, string? rawResponse, CancellationToken ct);
}
