using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;

namespace TraceFlow.Infrastructure.Inbound;

public sealed record MatchSummary(int SnapshotsSeen, int Complete, int NotRequired, int Partial, int Ambiguous, int Conflict, int Unmatched, int TasksCreated);

/// <summary>
/// 入库自动匹配（IN-01..05/AT-024..026/030）：分类门槛→供应商身份→单据候选→行级证据唯一全解→自动建任务。
/// 多解 AMBIGUOUS 留人工补配；既有任务处置/触发语义/证据门槛见计划裁定 R8a..R8f。
/// </summary>
public sealed class InboundMatchService
{
    private readonly string _cs;
    private readonly InboundTaskFactory _factory;
    private readonly IAuditLog _audit;
    public InboundMatchService(string connectionString, InboundTaskFactory factory, IAuditLog audit)
    {
        _cs = connectionString;
        _factory = factory;
        _audit = audit;
    }

    public async Task<MatchSummary> RunAsync(long orgId, string env, long? snapshotId, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        List<long> ids;
        if (snapshotId is not null) ids = new List<long> { snapshotId.Value };
        // 只扫描每个 (org,source_system,document_key) 的最新版本：被取代的快照在重建取消时会被置 UNMATCHED，
        // 若仍可被扫描则会与新版本互相取消重建（≤5 分钟无限循环）。定向触发（snapshotId 非空）不受此限。
        else ids = (await c.QueryAsync<long>(new CommandDefinition(@"
SELECT s.id FROM dbo.source_snapshot s
LEFT JOIN dbo.inbound_match m ON m.org_id=s.org_id AND m.env=@env AND m.source_snapshot_id=s.id
WHERE s.org_id=@orgId AND s.document_type=N'PURCHASE_IN' AND s.completeness_status=N'COMPLETE'
  AND (m.id IS NULL OR m.match_state<>N'COMPLETE')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.source_snapshot n
      WHERE n.org_id=s.org_id AND n.source_system_id=s.source_system_id
        AND n.source_document_key=s.source_document_key AND n.version>s.version)
ORDER BY s.id", new { orgId, env }, cancellationToken: ct))).ToList();
        int seen = 0, complete = 0, notRequired = 0, partial = 0, ambiguous = 0, conflict = 0, unmatched = 0, tasks = 0;
        foreach (var id in ids)
        {
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            var (state, created) = await MatchOneAsync(c, tx, orgId, env, id, ct);
            await tx.CommitAsync(ct);
            seen++;
            switch (state)
            {
                case "COMPLETE": complete++; break;
                case "NOT_REQUIRED": notRequired++; break;
                case "PARTIAL": partial++; break;
                case "AMBIGUOUS": ambiguous++; break;
                case "CONFLICT": conflict++; break;
                case "UNMATCHED": unmatched++; break;
            }
            if (created) tasks++;
        }
        return new MatchSummary(seen, complete, notRequired, partial, ambiguous, conflict, unmatched, tasks);
    }

    private async Task<(string State, bool TaskCreated)> MatchOneAsync(SqlConnection c, SqlTransaction tx, long orgId, string env, long snapshotId, CancellationToken ct)
    {
        var snap = await c.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id,source_system_id,source_document_key,document_type,completeness_status,business_datetime,content_hash,raw_header
FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId", new { id = snapshotId, orgId }, tx, cancellationToken: ct));
        if (snap is null || snap.document_type != "PURCHASE_IN" || snap.completeness_status != "COMPLETE")
            return ("SKIPPED", false);
        var lines = (await c.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw
FROM dbo.source_line WHERE snapshot_id=@id AND event_kind=N'PURCHASE_IN' ORDER BY id", new { id = snapshotId }, tx, cancellationToken: ct))).ToList();

        async Task<(string, bool)> Save(string state, string? reason, string? dwbh, object evidence, bool created = false)
        {
            await SaveMatchAsync(c, tx, orgId, env, snapshotId, dwbh, state, reason, JsonSerializer.Serialize(evidence), ct);
            await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "System", "inbound.match", "source_snapshot",
                snapshotId.ToString(), reason ?? state, null, null, null, null, env));
            return (state, created);
        }

        if (lines.Count == 0)
            return await Save("UNMATCHED", "采购入库快照无明细行", null, new { });
        var spids = lines.Select(l => l.product_source_id).Distinct().ToList();
        var classes = (await c.QueryAsync<ClassRow>(new CommandDefinition(
            "SELECT spid,trace_classification FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sys AND spid IN @spids",
            new { orgId, sys = snap.source_system_id, spids }, tx, cancellationToken: ct)))
            .ToDictionary(r => r.spid, r => r.trace_classification, StringComparer.Ordinal);
        if (lines.Any(l => !classes.TryGetValue(l.product_source_id, out var cl) || cl == "UNKNOWN"))
            return await Save("UNMATCHED", "存在 UNKNOWN 分类或档案缺失的来源行", null, new { });
        var required = lines.Where(l => classes[l.product_source_id] == "REQUIRED").ToList();
        if (required.Count == 0)
            return await Save("NOT_REQUIRED", "全部行 EXEMPT，无需上传", null, new { });
        foreach (var l in required.Where(l => l.quantity_original <= 0))
            await c.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.ingest_exception WHERE org_id=@orgId AND source_document_key=@key AND source_line_key=@lk AND exception_type=N'QTY_NONPOSITIVE' AND status=N'OPEN')
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,detail)
VALUES(@orgId,@sys,@key,@lk,N'QTY_NONPOSITIVE',N'REQUIRED 行数量≤0')",
                new { orgId, sys = snap.source_system_id, key = snap.source_document_key, lk = l.source_line_key }, tx, cancellationToken: ct));
        if (required.Any(l => l.quantity_original <= 0))
            return await Save("UNMATCHED", "REQUIRED 行数量≤0，已记录接入异常", null, new { });

        // R8a：既有任务先于证据判定处置——已发送/非发送前状态一律冻结；发送前且来源未变则无需重算（防周期扫描空转）。
        var existing = await c.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(
            "SELECT id,state,source_hash,source_snapshot_id FROM dbo.upload_task WHERE org_id=@orgId AND env=@env AND event_key=@key",
            new { orgId, env, key = $"KSOA:{snap.source_document_key}:PURCHASE_IN" }, tx, cancellationToken: ct));
        if (existing is not null && existing.state != "Cancelled")
        {
            var sent = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = existing.id }, tx, cancellationToken: ct));
            if (sent > 0 || existing.state is not ("Blocked" or "Ready"))
                return await Save("COMPLETE", "既有已发送任务冻结，来源变化由 M3 处置", null, new { });
            if (existing.source_hash == snap.content_hash)
                return await Save("COMPLETE", "既有发送前任务与来源一致，无需重建", null, new { });
        }

        string? dwbh = null, ywdjbh = null;
        if (snap.raw_header is not null)
        {
            using var hd = JsonDocument.Parse(snap.raw_header);
            dwbh = Str(hd.RootElement, "dwbh");
            ywdjbh = Str(hd.RootElement, "ywdjbh");
        }
        if (string.IsNullOrEmpty(dwbh))
            return await Save("UNMATCHED", "来源单头缺少供应商 dwbh", null, new { });
        var org = await c.QuerySingleAsync<OrgRow>(new CommandDefinition(
            "SELECT ent_id FROM dbo.organization WHERE id=@orgId", new { orgId }, tx, cancellationToken: ct));
        if (string.IsNullOrEmpty(org.ent_id))
            return await Save("UNMATCHED", "门店平台身份 ent_id 未配置", dwbh, new { });
        var sup = await c.QuerySingleOrDefaultAsync<SupRow>(new CommandDefinition(@"
SELECT sm.version AS map_version, ev.ref_ent_id, ev.ent_id
FROM dbo.supplier s
JOIN dbo.supplier_mapping sm ON sm.supplier_id=s.id AND sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE s.org_id=@orgId AND s.source_system_id=@sys AND s.dwbh=@dwbh",
            new { orgId, sys = snap.source_system_id, dwbh }, tx, cancellationToken: ct));
        if (sup is null)
            return await Save("UNMATCHED", "供应商映射未确认，无法定位上游身份", dwbh, new { });

        var bills = (await c.QueryAsync<BillRow>(new CommandDefinition(@"
SELECT id,bill_no,bill_date,to_ent_id FROM dbo.upstream_bill
WHERE org_id=@orgId AND env=@env AND upstream_ref_ent_id=@ref AND completeness=N'COMPLETE'",
            new { orgId, env, @ref = sup.ref_ent_id }, tx, cancellationToken: ct)))
            .Where(b => b.to_ent_id is null || string.Equals(b.to_ent_id, org.ent_id, StringComparison.Ordinal)).ToList();
        if (ywdjbh is not null)
        {
            bills = bills.Where(b => string.Equals(b.bill_no, ywdjbh, StringComparison.Ordinal)).ToList();
            if (bills.Count == 0)
                return await Save("UNMATCHED", "外部单号未命中上游出库单", dwbh, new { ywdjbh });
        }
        else
        {
            if (snap.business_datetime is null)
                return await Save("UNMATCHED", "来源单缺少业务时间，无法窗口匹配", dwbh, new { });
            var lo = snap.business_datetime.Value.AddDays(-7);
            var hi = snap.business_datetime.Value.AddDays(7);
            bills = bills.Where(b => b.bill_date is not null && b.bill_date >= lo && b.bill_date <= hi).ToList();
        }

        var maps = (await c.QueryAsync<MapRow>(new CommandDefinition(@"
SELECT p.spid, pd.platform_drug_id, pm.version
FROM dbo.product p
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE p.org_id=@orgId AND p.source_system_id=@sys AND p.spid IN @spids",
            new { orgId, sys = snap.source_system_id, spids = required.Select(l => l.product_source_id).Distinct().ToList() }, tx, cancellationToken: ct)))
            .ToDictionary(r => r.spid, StringComparer.Ordinal);
        if (required.Any(l => !maps.ContainsKey(l.product_source_id)))
            return await Save("UNMATCHED", "商品映射未确认，无法定位平台药品身份", dwbh, new { });

        var billIds = bills.Select(b => b.id).ToList();
        var upLines = billIds.Count == 0 ? new List<UpLineRow>() : (await c.QueryAsync<UpLineRow>(new CommandDefinition(
            "SELECT id,bill_id,platform_drug_id,batch_no,quantity,unit FROM dbo.upstream_bill_line WHERE bill_id IN @billIds",
            new { billIds }, tx, cancellationToken: ct))).ToList();
        var upCodes = billIds.Count == 0 ? new List<UpCodeRow>() : (await c.QueryAsync<UpCodeRow>(new CommandDefinition(
            "SELECT line_id,code_normalized,package_level FROM dbo.upstream_bill_code WHERE bill_id IN @billIds",
            new { billIds }, tx, cancellationToken: ct))).ToList();
        var codesByLine = upCodes.GroupBy(k => k.line_id).ToDictionary(g => g.Key, g => g.ToList());
        var allCodes = upCodes.Select(k => k.code_normalized).Distinct().ToList();
        // 本单谱系（同 source_document_key 的历史分配）占用的码不算争用，否则重匹配/版本重建会把自身占用误判为争用。
        var claimed = allCodes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            new HashSet<string>(await c.QueryAsync<string>(new CommandDefinition(@"
SELECT tc.code_normalized FROM dbo.code_claim cc JOIN dbo.trace_code tc ON tc.id=cc.code_id
WHERE cc.org_id=@orgId AND cc.env=@env AND cc.event_scope=N'PURCHASE_IN' AND cc.claim_state=N'ACTIVE'
  AND tc.code_normalized IN @allCodes
  AND NOT EXISTS (
      SELECT 1 FROM dbo.inbound_allocation a
      JOIN dbo.source_line sl ON sl.id=a.source_line_id
      JOIN dbo.source_snapshot ss ON ss.id=sl.snapshot_id
      WHERE a.id=cc.allocation_id AND ss.source_document_key=@docKey)",
                new { orgId, env, allCodes, docKey = snap.source_document_key }, tx, cancellationToken: ct)), StringComparer.Ordinal);
        var relations = allCodes.Count == 0 ? new List<RelRow>() : (await c.QueryAsync<RelRow>(new CommandDefinition(@"
SELECT p.code_normalized AS parent_norm, ch.code_normalized AS child_norm
FROM dbo.code_relation r JOIN dbo.trace_code p ON p.id=r.parent_code_id JOIN dbo.trace_code ch ON ch.id=r.child_code_id
WHERE p.code_normalized IN @allCodes AND ch.code_normalized IN @allCodes",
            new { allCodes }, tx, cancellationToken: ct))).ToList();

        bool CodesEligible(UpLineRow u)
        {
            if (u.quantity is null || u.quantity != decimal.Truncate(u.quantity.Value)) return false;
            if (!codesByLine.TryGetValue(u.id, out var ks) || ks.Count == 0 || ks.Count != (int)u.quantity.Value) return false;
            if (ks.Any(k => k.package_level != "MIN")) return false;
            if (ks.Any(k => TraceCodeNormalizer.Normalize(k.code_normalized).Format != CodeFormat.Supported8Prefix20)) return false;
            if (ks.Any(k => claimed.Contains(k.code_normalized))) return false;
            var set = new HashSet<string>(ks.Select(k => k.code_normalized), StringComparer.Ordinal);
            if (relations.Any(r => set.Contains(r.parent_norm) && set.Contains(r.child_norm))) return false;
            return true;
        }

        var candidates = new Dictionary<long, List<UpLineRow>>();
        foreach (var l in required)
        {
            var drugId = maps[l.product_source_id].platform_drug_id;
            candidates[l.id] = upLines.Where(u =>
                string.Equals(u.platform_drug_id, drugId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(l.batch_raw) && !string.IsNullOrWhiteSpace(u.batch_no)
                && string.Equals(l.batch_raw.Trim(), u.batch_no.Trim(), StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(l.unit_original) && !string.IsNullOrWhiteSpace(u.unit)
                && string.Equals(l.unit_original.Trim(), u.unit.Trim(), StringComparison.Ordinal)
                && u.quantity == l.quantity_original
                && CodesEligible(u)).ToList();
        }
        var evidence = new { lines = required.Select(l => new { sourceLineKey = l.source_line_key, candidates = candidates[l.id].Select(u => u.id).ToArray() }).ToArray() };
        if (required.Any(l => candidates[l.id].Count == 0))
        {
            // R8e：外部单号已命中但同药同量仅批号不同→CONFLICT（AT-025），优先于 UNMATCHED/PARTIAL 判定。
            if (ywdjbh is not null && required.Where(l => candidates[l.id].Count == 0).Any(l =>
                upLines.Any(u => string.Equals(u.platform_drug_id, maps[l.product_source_id].platform_drug_id, StringComparison.Ordinal)
                    && u.quantity == l.quantity_original
                    && !string.Equals(u.batch_no?.Trim() ?? "", l.batch_raw?.Trim() ?? "", StringComparison.Ordinal))))
                return await Save("CONFLICT", "外部单号命中但批号冲突，须人工核对", dwbh, evidence);
            if (required.All(l => candidates[l.id].Count == 0))
                return await Save("UNMATCHED", "无符合证据门槛的上游明细", dwbh, evidence);
            return await Save("PARTIAL", "部分来源行无匹配上游明细", dwbh, evidence);
        }
        if (required.Any(l => candidates[l.id].Count > 1)
            || required.Select(l => candidates[l.id][0].id).Distinct().Count() != required.Count)
            return await Save("AMBIGUOUS", "存在多解或上游明细争用，须人工补配", dwbh, evidence);

        // 来源版本变化且发送前：按旧任务自己的 source_snapshot_id 取消（释放旧占用/占用码），再走 R7a 复活重建（bill_code 不变）。
        if (existing is not null && existing.state != "Cancelled")
            await InboundCancelService.CancelPreSendAsync(c, tx, orgId, env, existing.source_snapshot_id, "SYSTEM", "来源版本变化，重建补配", env, ct);

        // 版本推进回收：同 docKey 旧快照上残留的 RESERVED/TENTATIVE 分配会永久占用码（UI 只列最新版，旧版无取消入口），
        // 不先回收则下面插入占用必撞 UX_claim_active → CODE_CONTENDED。无旧版本残留时零写入。
        await InboundCancelService.RevokeStaleDocKeyAllocationsAsync(c, tx, orgId, env, snap.source_document_key, snapshotId, ct);

        var allocLines = new List<AllocatedLine>();
        var mappingVersions = new List<string> { $"supplier:{dwbh}:{sup.map_version}" };
        foreach (var l in required)
        {
            var u = candidates[l.id][0];
            var ks = codesByLine[u.id];
            var stored = (await c.QueryAsync<CodeRow>(new CommandDefinition(
                "SELECT id,code_normalized FROM dbo.trace_code WHERE code_normalized IN @codes",
                new { codes = ks.Select(k => k.code_normalized).ToList() }, tx, cancellationToken: ct))).ToList();
            var allocId = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,package_level,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,@env,@lineId,@upLineId,@qty,1,1,N'MIN',N'RESERVED',N'SYSTEM')",
                new { orgId, env, lineId = l.id, upLineId = u.id, qty = l.quantity_original }, tx, cancellationToken: ct));
            foreach (var sc in stored)
            {
                await c.ExecuteAsync(new CommandDefinition("INSERT INTO dbo.allocation_code(allocation_id,code_id) VALUES(@allocId,@codeId)",
                    new { allocId, codeId = sc.id }, tx, cancellationToken: ct));
                await CodeClaimWriter.InsertActiveAsync(c, tx, orgId, env, sc.id, "PURCHASE_IN", allocId, null, ct);
            }
            allocLines.Add(new AllocatedLine(allocId, l.id, l.source_line_key, l.product_source_id,
                maps[l.product_source_id].platform_drug_id, u.batch_no!, l.quantity_original, l.unit_original!,
                stored.Select(sc => new StoredCodeRef(sc.id, sc.code_normalized)).ToList()));
            mappingVersions.Add($"product:{l.product_source_id}:{maps[l.product_source_id].version}");
        }
        var ctx = new InboundTaskContext(orgId, env, snapshotId, snap.source_document_key, snap.content_hash,
            snap.business_datetime, dwbh, sup.ref_ent_id, sup.ent_id, allocLines, mappingVersions.Distinct().ToList(), "SYSTEM");
        await _factory.CreateAsync(c, tx, ctx, ct);
        return await Save("COMPLETE", null, dwbh, evidence, created: true);
    }

    private static async Task SaveMatchAsync(SqlConnection c, SqlTransaction tx, long orgId, string env, long snapshotId,
        string? dwbh, string state, string? reason, string evidenceJson, CancellationToken ct)
    {
        var n = await c.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET supplier_dwbh=@dwbh, match_state=@state, reason=@reason, evidence_json=@ev, version=version+1, matched_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snap",
            new { orgId, env, snap = snapshotId, dwbh, state, reason, ev = evidenceJson }, tx, cancellationToken: ct));
        if (n == 0)
            await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state,reason,evidence_json,matched_at)
VALUES(@orgId,@env,@snap,@dwbh,@state,@reason,@ev,SYSUTCDATETIME())",
                new { orgId, env, snap = snapshotId, dwbh, state, reason, ev = evidenceJson }, tx, cancellationToken: ct));
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class SnapRow
    {
        public long id { get; set; }
        public long source_system_id { get; set; }
        public string source_document_key { get; set; } = "";
        public string document_type { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public DateTime? business_datetime { get; set; }
        public string content_hash { get; set; } = "";
        public string? raw_header { get; set; }
    }
    private sealed class LineRow
    {
        public long id { get; set; }
        public string source_line_key { get; set; } = "";
        public string product_source_id { get; set; } = "";
        public decimal quantity_original { get; set; }
        public string? unit_original { get; set; }
        public string? batch_raw { get; set; }
    }
    private sealed class ClassRow { public string spid { get; set; } = ""; public string trace_classification { get; set; } = ""; }
    private sealed class OrgRow { public string? ent_id { get; set; } }
    private sealed class SupRow { public int map_version { get; set; } public string ref_ent_id { get; set; } = ""; public string ent_id { get; set; } = ""; }
    private sealed class BillRow { public long id { get; set; } public string? bill_no { get; set; } public DateTime? bill_date { get; set; } public string? to_ent_id { get; set; } }
    private sealed class MapRow { public string spid { get; set; } = ""; public string platform_drug_id { get; set; } = ""; public int version { get; set; } }
    private sealed class UpLineRow
    {
        public long id { get; set; }
        public long bill_id { get; set; }
        public string? platform_drug_id { get; set; }
        public string? batch_no { get; set; }
        public decimal? quantity { get; set; }
        public string? unit { get; set; }
    }
    private sealed class UpCodeRow { public long line_id { get; set; } public string code_normalized { get; set; } = ""; public string? package_level { get; set; } }
    private sealed class RelRow { public string parent_norm { get; set; } = ""; public string child_norm { get; set; } = ""; }
    private sealed class CodeRow { public long id { get; set; } public string code_normalized { get; set; } = ""; }
    private sealed class TaskRow { public long id { get; set; } public string state { get; set; } = ""; public string source_hash { get; set; } = ""; public long source_snapshot_id { get; set; } }
}
