using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Master;

/// <summary>
/// MATCH-03/AT-018：S8P20 来源码前 7 位 × 有效映射平台药。前缀不是药品外键——
/// 同前缀多药仅标 CONFLICT 供人工排查，绝不据此自动绑定。单门店全局索引：全删全插。
/// </summary>
public static class PrefixIndexService
{
    public static async Task<(int Rows, int Conflicts)> RebuildAsync(SqlConnection conn, SqlTransaction tx, long orgId, CancellationToken ct)
    {
        var pairs = (await conn.QueryAsync<PrefixPair>(new CommandDefinition(@"
SELECT LEFT(sl.trace_code_normalized,7) AS prefix, pm.platform_drug_id
FROM dbo.source_line sl
JOIN dbo.source_snapshot ss ON ss.id=sl.snapshot_id
JOIN dbo.product p ON p.org_id=ss.org_id AND p.source_system_id=ss.source_system_id AND p.spid=sl.product_source_id
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
WHERE ss.org_id=@org AND sl.code_format=N'S8P20' AND sl.trace_code_normalized IS NOT NULL
GROUP BY LEFT(sl.trace_code_normalized,7), pm.platform_drug_id",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM dbo.drug_prefix_index", null, tx, cancellationToken: ct));
        var conflictPrefixes = pairs.GroupBy(x => x.prefix).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in pairs)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.drug_prefix_index(prefix,platform_drug_id,evidence_json,conflict_state)
VALUES(@prefix,@drug,@ev,@cs)",
                new { prefix = pair.prefix, drug = pair.platform_drug_id,
                      ev = JsonSerializer.Serialize(new { source = "source_line", orgId }),
                      cs = conflictPrefixes.Contains(pair.prefix) ? "CONFLICT" : "OK" }, tx, cancellationToken: ct));
        return (pairs.Count, conflictPrefixes.Count);
    }

    private sealed record PrefixPair(string prefix, long platform_drug_id);
}
