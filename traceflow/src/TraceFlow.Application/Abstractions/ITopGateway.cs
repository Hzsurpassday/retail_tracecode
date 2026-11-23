namespace TraceFlow.Application.Abstractions;

public sealed record TopCall(string Method, IReadOnlyDictionary<string, string> Parameters, string Env);

/// <summary>ErrorJson=平台 error_response 节点；BusinessJson=业务 *_response 节点（TOP-01 分离解析）。</summary>
public sealed record TopRawResponse(bool TransportOk, int? HttpStatus, string? ErrorJson, string? BusinessJson, int? RetryAfterSeconds);

public interface ITopGateway
{
    Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct);
}
