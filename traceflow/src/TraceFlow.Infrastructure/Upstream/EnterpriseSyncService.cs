using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Upstream;

public sealed record EnterpriseSyncSummary(int PartnersSeen, int VersionsAdded);

/// <summary>ENT-02：往来单位/企业版本只增不改（UQ ref_ent_id+ent_id）；改名→新 ent_id 版本行；角色原样保留（AT-020/021）。</summary>
public sealed class EnterpriseSyncService
{
    private const int PageSize = 50; // 候选，待 G-TOP
    private const int MaxPages = 1000;
    private readonly string _cs;
    private readonly TopClientService _top;
    private readonly IAuditLog _audit;

    public EnterpriseSyncService(string connectionString, TopClientService top, IAuditLog audit)
    {
        _cs = connectionString; _top = top; _audit = audit;
    }

    public async Task<EnterpriseSyncSummary> RunAsync(long orgId, string env, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new TopNotCertifiedException("G-TOP 未取证：企业同步仅 MOCK 可用");
        string? orgRef;
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            orgRef = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT ref_ent_id FROM dbo.organization WHERE id=@org", new { org = orgId }, cancellationToken: ct));
        }
        var pageNo = 1; int? totalPages = null; var seen = 0; var added = 0;
        while (pageNo <= MaxPages)
        {
            var outcome = await _top.CallAsync(env, "query.listparts", new Dictionary<string, string>
            {
                ["page_no"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture),
            }, orgRef, "SYSTEM", ct);
            if (outcome.Kind != TopOutcomeKind.Success)
                throw new InvalidOperationException($"往来单位第 {pageNo} 页非成功：{outcome.Kind} {outcome.SubCode}");
            using var doc = JsonDocument.Parse(outcome.ResultJson ?? "{}");
            var r = doc.RootElement;
            totalPages = r.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : totalPages;
            await using (var c = new SqlConnection(_cs))
            {
                await c.OpenAsync(ct);
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
                if (r.TryGetProperty("parts", out var parts))
                    foreach (var p in parts.EnumerateArray())
                    {
                        seen++;
                        var refEntId = GetString(p, "ref_ent_id") ?? throw new InvalidOperationException("往来单位缺 ref_ent_id（候选合同不符）");
                        var entId = GetString(p, "ent_id") ?? throw new InvalidOperationException("往来单位缺 ent_id（候选合同不符）");
                        // brief 原为 "IF NOT EXISTS(...) INSERT"，跳过分支 ExecuteNonQuery 返回 -1（非 DML 批次）；
                        // 改单语句 INSERT..WHERE NOT EXISTS（语义相同，行数 0/1，2008 R2 兼容）
                        var rows = await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
SELECT @ref,@ent,@name,@cc,@audit,@role
WHERE NOT EXISTS (SELECT 1 FROM dbo.platform_enterprise_version WHERE ref_ent_id=@ref AND ent_id=@ent)",
                            new { @ref = refEntId, ent = entId, name = GetString(p, "name") ?? "", cc = GetString(p, "credit_code"),
                                  audit = GetString(p, "audit_status"), role = GetString(p, "role") },
                            tx, cancellationToken: ct));
                        added += rows;
                    }
                await tx.CommitAsync(ct);
            }
            if (totalPages is not int t || pageNo >= t) break;
            pageNo++;
        }
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            await _audit.AppendAsync(c, null, new AuditEntry("SYSTEM", "System", "enterprise.sync", "platform_enterprise",
                orgId.ToString(CultureInfo.InvariantCulture), $"往来单位 {seen}，新版本 {added}", null, null, null, null, env));
        }
        return new EnterpriseSyncSummary(seen, added);
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
