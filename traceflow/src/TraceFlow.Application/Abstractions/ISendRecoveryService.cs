namespace TraceFlow.Application.Abstractions;

public interface ISendRecoveryService
{
    /// <summary>
    /// 失租发送恢复：只处理 <paramref name="env"/> 环境的分片（MOCK worker 绝不动 LIVE 分片）。
    /// </summary>
    Task<int> RecoverAsync(string env, CancellationToken ct);
    Task AppendStaleEvidenceAsync(long taskId, long partId, int attemptNo, string attemptKind,
        long fencingToken, string? rawResponse, CancellationToken ct);
}
