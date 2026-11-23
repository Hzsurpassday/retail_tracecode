using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Top;

public sealed record PauseView(long Id, string ScopeKey, string Reason, bool IsActive, DateTime PausedAtUtc, string PausedBy, DateTime? ResumedAtUtc, string? ResumedBy);

/// <summary>§3.3/JOB-05：FAIL_BIZ_AUTH_ERROR 按接口/主体/凭证范围暂停；只影响相关管道，恢复须人工动作。</summary>
public sealed class ChannelPauseService
{
    public async Task PauseAsync(SqlConnection conn, SqlTransaction tx, string env,
        IReadOnlyList<string> scopeKeys, string reason, string actor, CancellationToken ct)
    {
        foreach (var key in scopeKeys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var active = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.top_channel_pause WITH (UPDLOCK, HOLDLOCK) WHERE env=@env AND scope_key=@key AND is_active=1",
                new { env, key }, tx, cancellationToken: ct));
            if (active > 0) continue;
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(@env,@key,@reason,@actor)",
                    new { env, key, reason, actor }, tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627) { } // 并发暂停幂等
        }
    }

    public async Task<bool> IsPausedAsync(SqlConnection conn, SqlTransaction? tx, string env,
        IReadOnlyList<string> scopeKeys, CancellationToken ct)
    {
        if (scopeKeys.Count == 0) return false;
        var n = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.top_channel_pause
WHERE env=@env AND is_active=1 AND scope_key IN @keys",
            new { env, keys = scopeKeys.ToArray() }, tx, cancellationToken: ct));
        return n > 0;
    }

    public async Task<int> ResumeAsync(SqlConnection conn, SqlTransaction tx, string env, string scopeKey, string actor, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.top_channel_pause SET is_active=0, resumed_at=SYSUTCDATETIME(), resumed_by=@actor
WHERE env=@env AND scope_key=@key AND is_active=1",
            new { env, key = scopeKey, actor }, tx, cancellationToken: ct));

    public async Task<IReadOnlyList<PauseView>> ListAsync(SqlConnection conn, string env, CancellationToken ct)
        => (await conn.QueryAsync<PauseView>(new CommandDefinition(@"
SELECT id AS Id, scope_key AS ScopeKey, reason AS Reason, is_active AS IsActive,
       paused_at AS PausedAtUtc, paused_by AS PausedBy, resumed_at AS ResumedAtUtc, resumed_by AS ResumedBy
FROM dbo.top_channel_pause WHERE env=@env ORDER BY paused_at DESC, id DESC",
            new { env }, cancellationToken: ct))).ToList();
}
