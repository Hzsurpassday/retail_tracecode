using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;

namespace TraceFlow.Infrastructure.Top;

/// <summary>TOP 调用编排：暂停门禁→预算→HTTP 事务外→解析→授权暂停/限流改期（§3.3、JOB-03、§2.9）。</summary>
public sealed class TopClientService
{
    private readonly string _connectionString;
    private readonly ITopGateway _gateway;
    private readonly RateBudgetService _budget;
    private readonly ChannelPauseService _pause;
    private readonly IAuditLog _audit;

    public TopClientService(string connectionString, ITopGateway gateway, RateBudgetService budget, ChannelPauseService pause, IAuditLog audit)
    {
        _connectionString = connectionString;
        _gateway = gateway;
        _budget = budget;
        _pause = pause;
        _audit = audit;
    }

    public async Task<TopOutcome> CallAsync(string env, string method, IReadOnlyDictionary<string, string> parameters,
        string? refEntId, string actor, CancellationToken ct)
    {
        var scopeKeys = ScopeKeys(method, refEntId);
        var budgetKeys = BudgetKeys(method, refEntId);

        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            if (await _pause.IsPausedAsync(conn, tx, env, scopeKeys, ct))
                throw new ChannelPausedException("method:" + method, $"TOP 通道已暂停（{method}）：授权错误须人工恢复");
            var wait = await _budget.TryAcquireAsync(conn, tx, env, budgetKeys, DateTime.UtcNow, ct);
            if (wait is TimeSpan w)
                throw new TopRateLimitedException(w);
            await tx.CommitAsync(ct);
        }

        var raw = await _gateway.CallAsync(new TopCall(method, parameters, env), ct); // HTTP 在事务外（§2.9）
        var outcome = TopResponseParser.Parse(raw.TransportOk, raw.ErrorJson, raw.BusinessJson, raw.RetryAfterSeconds);

        if (outcome.Kind is TopOutcomeKind.AuthError or TopOutcomeKind.RateLimited)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            if (outcome.Kind == TopOutcomeKind.AuthError)
            {
                // 授权错误只暂停相关管道（method/ent）；credential:top 保留为人工全局闸（门禁仍检查它），
                // 不随单方法授权错误自动暂停，否则任一方法出错会阻断全部 TOP 调用（§3.3"其余任务继续"）。
                var pauseKeys = scopeKeys.Where(k => k != "credential:top").ToArray();
                await _pause.PauseAsync(conn, tx, env, pauseKeys, "FAIL_BIZ_AUTH_ERROR: " + (outcome.Message ?? ""), actor, ct);
                await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "System", "top.channel.pause", "top_channel",
                    "method:" + method, outcome.Message, null, null, outcome.RequestId, null, env));
                await tx.CommitAsync(ct);
                throw new ChannelPausedException("method:" + method, $"TOP 授权错误，已按范围暂停（{method}）");
            }
            var seconds = raw.RetryAfterSeconds ?? (int)RateBudgetService.Backoff(0).TotalSeconds;
            await _budget.RegisterWaitAsync(conn, tx, env, budgetKeys, DateTime.UtcNow.AddSeconds(seconds), ct);
            await tx.CommitAsync(ct);
            throw new TopRateLimitedException(TimeSpan.FromSeconds(seconds));
        }
        return outcome;
    }

    public static IReadOnlyList<string> ScopeKeys(string method, string? refEntId)
        => refEntId is null
            ? new[] { "method:" + method, "credential:top" }
            : new[] { "method:" + method, "credential:top", "ent:" + refEntId };

    public static IReadOnlyList<string> BudgetKeys(string method, string? refEntId)
        => refEntId is null
            ? new[] { "appkey:total", "method:" + method }
            : new[] { "appkey:total", "method:" + method, $"ent:{refEntId}:method:{method}" };
}
