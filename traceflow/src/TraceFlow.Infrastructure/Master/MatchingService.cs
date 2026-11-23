using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Matching;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Master;

/// <summary>
/// MATCH-01..05 / ENT-01/02 自动匹配（单门店千级数据：一次事务全量加载+内存匹配，逐行 UPSERT）。
/// 缺字段≠通配；双向唯一才自动；is_locked 跳过；有效映射只做身份复核；写入竞态降级 CONFLICT。
/// </summary>
public sealed class MatchingService
{
    private readonly string _cs;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public MatchingService(string connectionString, SettingsService settings, IAuditLog audit)
    { _cs = connectionString; _settings = settings; _audit = audit; }

    public sealed record MatchResult(int ProductsAutoConfirmed, int ProductsCandidate, int ProductsConflict,
        int ProductsUnresolved, int ProductsReviewRequired, int SuppliersAutoConfirmed, int SuppliersCandidate,
        int SuppliersConflict, int SuppliersUnresolved, int SuppliersReviewRequired, int PrefixRows, int PrefixConflicts);

    public async Task<MatchResult> RunAsync(long orgId, string env, CancellationToken ct)
    {
        var passValues = await _settings.GetAuditPassValuesAsync(ct); // 独立连接，事务前读取
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var p = await MatchProductsAsync(conn, tx, orgId, ct);
        var s = await MatchSuppliersAsync(conn, tx, orgId, passValues, ct);
        var (prefixRows, prefixConflicts) = await PrefixIndexService.RebuildAsync(conn, tx, orgId, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "master.match.auto", "organization",
            orgId.ToString(), null, null,
            // 值元组被 System.Text.Json 序列化为 {}（字段非属性）→投影为具名匿名对象，保留计数信息
            JsonSerializer.Serialize(new
            {
                products = new { auto = p.auto, candidate = p.candidate, conflict = p.conflict, unresolved = p.unresolved, review = p.review },
                suppliers = new { auto = s.auto, candidate = s.candidate, conflict = s.conflict, unresolved = s.unresolved, review = s.review },
                prefixRows, prefixConflicts
            }),
            null, "match-1", env));
        await tx.CommitAsync(ct);
        return new MatchResult(p.auto, p.candidate, p.conflict, p.unresolved, p.review,
            s.auto, s.candidate, s.conflict, s.unresolved, s.review, prefixRows, prefixConflicts);
    }

    private static DrugIdentity Local(ProductRec r) => new(r.approval_no_norm, r.manufacturer_identity, r.dosage_form, r.strength, r.package_spec);
    private static DrugIdentity Platform(DrugRec r) => new(r.approval_no_norm, r.manufacturer_identity, r.dosage_form, r.strength, r.package_spec);
    private static string IdentityKey(DrugIdentity d) => string.Join("|",
        d.ApprovalNoNorm?.Trim() ?? "", d.ManufacturerIdentity?.Trim() ?? "", d.DosageForm?.Trim() ?? "",
        d.Strength?.Trim() ?? "", d.PackageSpec?.Trim() ?? "");

    private async Task<(int auto, int candidate, int conflict, int unresolved, int review)> MatchProductsAsync(
        SqlConnection conn, SqlTransaction tx, long orgId, CancellationToken ct)
    {
        var products = (await conn.QueryAsync<ProductRec>(new CommandDefinition(
            "SELECT id,spid,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec FROM dbo.product WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var drugs = (await conn.QueryAsync<DrugRec>(new CommandDefinition(
            "SELECT id,platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec FROM dbo.platform_drug",
            null, tx, cancellationToken: ct))).ToList();
        var maps = (await conn.QueryAsync<PMapRec>(new CommandDefinition(
            "SELECT id,product_id,platform_drug_id,state,is_locked,version FROM dbo.product_mapping WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var latest = maps.GroupBy(m => m.product_id).ToDictionary(g => g.Key,
            g => g.OrderBy(m => MappingStates.Active.Contains(m.state) ? 0 : 1).ThenByDescending(m => m.id).First());
        var localKeyCounts = products.GroupBy(x => IdentityKey(Local(x))).ToDictionary(g => g.Key, g => g.Count());
        int auto = 0, candidate = 0, conflict = 0, unresolved = 0, review = 0;

        foreach (var prod in products)
        {
            var local = Local(prod);
            latest.TryGetValue(prod.id, out var map);
            if (map is not null && map.is_locked) continue; // AT-016：人工锁定不可被自动改写
            if (map is not null && MappingStates.Active.Contains(map.state))
            {
                // AT-017：有效映射只复核五项身份；平台侧变化→REVIEW_REQUIRED
                var drug = drugs.FirstOrDefault(d => d.id == map.platform_drug_id);
                var m = drug is null ? null : DrugIdentityMatcher.Evaluate(local, Platform(drug));
                if (m is null || !m.IsFullMatch)
                {
                    var ev = JsonSerializer.Serialize(new { reason = "platform-identity-changed", equal = m?.Equal, missing = m?.Missing, conflicts = m?.Conflicts });
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.product_mapping SET state=N'REVIEW_REQUIRED', evidence_json=@ev, version=version+1 WHERE id=@id",
                        new { id = map.id, ev }, tx, cancellationToken: ct));
                    await WriteProductHistoryAsync(conn, tx, map.id, prod.id, map.platform_drug_id, MappingStates.ReviewRequired, ev, ct);
                    review++;
                }
                continue;
            }

            // brief 偏差 1（实证 CS1061/CS0019）：三元两分支元组名不一致（drug/match vs 推断 d/无名）→Roslyn 剥名，
            // 后续 .d/.match 全部编译失败。最小修复：两分支统一为 (DrugRec d, IdentityMatch match)，brief 的 .d/.match 访问不变。
            var candidates = prod.approval_no_norm is null
                ? new List<(DrugRec d, IdentityMatch match)>()
                : drugs.Where(d => d.approval_no_norm is not null
                        && string.Equals(d.approval_no_norm, prod.approval_no_norm, StringComparison.Ordinal))
                    .Select(d => (d, match: DrugIdentityMatcher.Evaluate(local, Platform(d)))).ToList();
            var full = candidates.Where(x => x.match.IsFullMatch).ToList();
            string state; long? target = null; var bidirectionalUnique = false;
            if (full.Count == 1)
            {
                var chosen = full[0].d;
                var drugTaken = maps.Any(m => MappingStates.Active.Contains(m.state) && m.platform_drug_id == chosen.id && m.product_id != prod.id);
                bidirectionalUnique = !drugTaken && localKeyCounts[IdentityKey(local)] == 1;
                target = chosen.id;
                state = bidirectionalUnique ? MappingStates.AutoConfirmed : MappingStates.Conflict;
            }
            else if (full.Count > 1) { state = MappingStates.Conflict; target = full[0].d.id; }
            else if (candidates.Any(x => x.match.HasConflict)) { state = MappingStates.Conflict; target = candidates.First(x => x.match.HasConflict).d.id; }
            else if (candidates.Any(x => x.match.HasOverlap)) { state = MappingStates.Candidate; target = candidates.First(x => x.match.HasOverlap).d.id; }
            else if (candidates.Count > 0) { state = MappingStates.Unresolved; target = candidates[0].d.id; }
            else
            {
                // 无候选：不建行；既有非有效行降级 UNRESOLVED（保留原 pair）
                if (map is not null && map.state != MappingStates.Unresolved)
                {
                    var ev0 = JsonSerializer.Serialize(new { reason = "no-candidates" });
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.product_mapping SET state=N'UNRESOLVED', evidence_json=@ev, version=version+1 WHERE id=@id",
                        new { id = map.id, ev = ev0 }, tx, cancellationToken: ct));
                    await WriteProductHistoryAsync(conn, tx, map.id, prod.id, map.platform_drug_id, MappingStates.Unresolved, ev0, ct);
                }
                unresolved++;
                continue;
            }

            var best = full.Count > 0 ? full[0].match
                : candidates.FirstOrDefault(x => x.match.HasConflict).match
                ?? candidates.FirstOrDefault(x => x.match.HasOverlap).match
                ?? candidates[0].match;
            var evidence = JsonSerializer.Serialize(new
            {
                equal = best.Equal, missing = best.Missing, conflicts = best.Conflicts, bidirectionalUnique,
                candidates = candidates.Select(x => new { platformDrugId = x.d.platform_drug_id, equal = x.match.Equal, missing = x.match.Missing, conflicts = x.match.Conflicts }),
            });
            var (mapId, finalState) = await UpsertProductMappingAsync(conn, tx, orgId, prod.id, target!.Value, state, evidence, map, ct);
            if (map is null || map.state != finalState)
                await WriteProductHistoryAsync(conn, tx, mapId, prod.id, target.Value, finalState, evidence, ct);
            switch (finalState)
            {
                case MappingStates.AutoConfirmed: auto++; break;
                case MappingStates.Candidate: candidate++; break;
                case MappingStates.Conflict: conflict++; break;
                default: unresolved++; break;
            }
        }
        return (auto, candidate, conflict, unresolved, review);
    }

    private static async Task<(long Id, string State)> UpsertProductMappingAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, long productId, long drugId, string state, string evidence, PMapRec? existing, CancellationToken ct)
    {
        try
        {
            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,rule_version,evidence_json)
OUTPUT inserted.id VALUES(@org,@pid,@drug,@state,N'match-1',@ev)",
                    new { org = orgId, pid = productId, drug = drugId, state, ev = evidence }, tx, cancellationToken: ct));
                return (id, state);
            }
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product_mapping SET platform_drug_id=@drug, state=@state, rule_version=N'match-1', evidence_json=@ev, version=version+1
WHERE id=@id",
                new { id = existing.id, drug = drugId, state, ev = evidence }, tx, cancellationToken: ct));
            return (existing.id, state);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // AT-015 并发兜底：有效唯一/对唯一竞态→降级 CONFLICT（语句级错误，事务仍可用）
            var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.product_mapping WHERE product_id=@pid ORDER BY id DESC",
                new { pid = productId }, tx, cancellationToken: ct));
            if (existingId is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.product_mapping SET state=N'CONFLICT', evidence_json=@ev, version=version+1 WHERE id=@id",
                    new { id = existingId.Value, ev = evidence }, tx, cancellationToken: ct));
                return (existingId.Value, MappingStates.Conflict);
            }
            var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,rule_version,evidence_json)
OUTPUT inserted.id VALUES(@org,@pid,@drug,N'CONFLICT',N'match-1',@ev)",
                new { org = orgId, pid = productId, drug = drugId, ev = evidence }, tx, cancellationToken: ct));
            return (newId, MappingStates.Conflict);
        }
    }

    private static async Task WriteProductHistoryAsync(SqlConnection conn, SqlTransaction tx, long mappingId,
        long productId, long drugId, string state, string evidence, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product_mapping_history(mapping_id,product_id,platform_drug_id,state,rule_version,evidence_json,changed_by,reason)
VALUES(@mid,@pid,@drug,@state,N'match-1',@ev,N'SYSTEM',N'auto-match')",
            new { mid = mappingId, pid = productId, drug = drugId, state, ev = evidence }, tx, cancellationToken: ct));

    private async Task<(int auto, int candidate, int conflict, int unresolved, int review)> MatchSuppliersAsync(
        SqlConnection conn, SqlTransaction tx, long orgId, IReadOnlyList<string> passValues, CancellationToken ct)
    {
        var suppliers = (await conn.QueryAsync<SupplierRec>(new CommandDefinition(
            "SELECT id,dwbh,name,credit_code,role FROM dbo.supplier WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var ents = (await conn.QueryAsync<EntRec>(new CommandDefinition(
            "SELECT id,ref_ent_id,ent_id,name,credit_code,audit_status,role FROM dbo.platform_enterprise_version",
            null, tx, cancellationToken: ct))).ToList();
        var maps = (await conn.QueryAsync<SMapRec>(new CommandDefinition(
            "SELECT id,supplier_id,enterprise_version_id,state,is_locked,version FROM dbo.supplier_mapping WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var latest = maps.GroupBy(m => m.supplier_id).ToDictionary(g => g.Key,
            g => g.OrderBy(m => MappingStates.Active.Contains(m.state) ? 0 : 1).ThenByDescending(m => m.id).First());
        var creditCounts = suppliers.Where(s => s.credit_code is not null)
            .GroupBy(s => s.credit_code!.Trim()).ToDictionary(g => g.Key, g => g.Count());
        int auto = 0, candidate = 0, conflict = 0, unresolved = 0, review = 0;

        foreach (var sup in suppliers)
        {
            latest.TryGetValue(sup.id, out var map);
            if (map is not null && map.is_locked) continue; // AT-016
            if (map is not null && MappingStates.Active.Contains(map.state))
            {
                // ENT-01 复核：信用代码不再精确相等→REVIEW_REQUIRED；改名新版本行不影响（AT-020 映射钉在版本 id）
                var ev = ents.FirstOrDefault(e => e.id == map.enterprise_version_id);
                var same = ev is not null && string.Equals(sup.credit_code?.Trim(), ev.credit_code?.Trim(), StringComparison.Ordinal);
                if (!same)
                {
                    var evJson = JsonSerializer.Serialize(new { reason = "credit-code-changed", local = sup.credit_code, platform = ev?.credit_code });
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.supplier_mapping SET state=N'REVIEW_REQUIRED', evidence_json=@ev, version=version+1 WHERE id=@id",
                        new { id = map.id, ev = evJson }, tx, cancellationToken: ct));
                    await WriteSupplierHistoryAsync(conn, tx, map.id, sup.id, map.enterprise_version_id, MappingStates.ReviewRequired, evJson, ct);
                    review++;
                }
                continue;
            }

            var creditCandidates = sup.credit_code is null
                ? new List<EntRec>()
                : ents.Where(e => e.credit_code is not null && string.Equals(e.credit_code.Trim(), sup.credit_code.Trim(), StringComparison.Ordinal)).ToList();
            string state; long? target = null;
            if (creditCandidates.Count >= 2) { state = MappingStates.Conflict; target = creditCandidates[0].id; } // AT-019
            else if (creditCandidates.Count == 1)
            {
                var ev = creditCandidates[0];
                var auditPassed = ev.audit_status is not null && passValues.Contains(ev.audit_status);
                var evTaken = maps.Any(m => MappingStates.Active.Contains(m.state) && m.enterprise_version_id == ev.id && m.supplier_id != sup.id);
                var unique = !evTaken && creditCounts[sup.credit_code!.Trim()] == 1;
                target = ev.id;
                if (!auditPassed) state = MappingStates.Candidate; // 默认空白名单→永不自动（G-TOP）
                else state = unique ? MappingStates.AutoConfirmed : MappingStates.Conflict;
            }
            else
            {
                // 名称仅候选（ENT-01），绝不自动
                var nameCandidates = ents.Where(e => e.name.Trim().Length > 0
                    && string.Equals(e.name.Trim(), sup.name.Trim(), StringComparison.Ordinal)).ToList();
                if (nameCandidates.Count > 0) { state = MappingStates.Candidate; target = nameCandidates[0].id; }
                else
                {
                    if (map is not null && map.state != MappingStates.Unresolved)
                    {
                        var ev0 = JsonSerializer.Serialize(new { reason = "no-candidates" });
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.supplier_mapping SET state=N'UNRESOLVED', evidence_json=@ev, version=version+1 WHERE id=@id",
                            new { id = map.id, ev = ev0 }, tx, cancellationToken: ct));
                        await WriteSupplierHistoryAsync(conn, tx, map.id, sup.id, map.enterprise_version_id, MappingStates.Unresolved, ev0, ct);
                    }
                    unresolved++;
                    continue;
                }
            }

            var evidence = JsonSerializer.Serialize(new
            {
                creditCode = sup.credit_code, targetEntId = ents.First(e => e.id == target!.Value).ent_id,
                auditStatus = ents.First(e => e.id == target.Value).audit_status,
                nameOnly = creditCandidates.Count == 0,
                candidates = creditCandidates.Select(e => new { entId = e.ent_id, refEntId = e.ref_ent_id, auditStatus = e.audit_status }),
            });
            var (mapId, finalState) = await UpsertSupplierMappingAsync(conn, tx, orgId, sup.id, target!.Value, state, evidence, map, ct);
            if (map is null || map.state != finalState)
                await WriteSupplierHistoryAsync(conn, tx, mapId, sup.id, target.Value, finalState, evidence, ct);
            switch (finalState)
            {
                case MappingStates.AutoConfirmed: auto++; break;
                case MappingStates.Candidate: candidate++; break;
                case MappingStates.Conflict: conflict++; break;
                default: unresolved++; break;
            }
        }
        return (auto, candidate, conflict, unresolved, review);
    }

    private static async Task<(long Id, string State)> UpsertSupplierMappingAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, long supplierId, long entVersionId, string state, string evidence, SMapRec? existing, CancellationToken ct)
    {
        try
        {
            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,evidence_json)
OUTPUT inserted.id VALUES(@org,@sid,@ev,@state,@evid)",
                    new { org = orgId, sid = supplierId, ev = entVersionId, state, evid = evidence }, tx, cancellationToken: ct));
                return (id, state);
            }
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier_mapping SET enterprise_version_id=@ev, state=@state, evidence_json=@evid, version=version+1
WHERE id=@id",
                new { id = existing.id, ev = entVersionId, state, evid = evidence }, tx, cancellationToken: ct));
            return (existing.id, state);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.supplier_mapping WHERE supplier_id=@sid ORDER BY id DESC",
                new { sid = supplierId }, tx, cancellationToken: ct));
            if (existingId is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.supplier_mapping SET state=N'CONFLICT', evidence_json=@ev, version=version+1 WHERE id=@id",
                    new { id = existingId.Value, ev = evidence }, tx, cancellationToken: ct));
                return (existingId.Value, MappingStates.Conflict);
            }
            var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,evidence_json)
OUTPUT inserted.id VALUES(@org,@sid,@ev,N'CONFLICT',@evid)",
                new { org = orgId, sid = supplierId, ev = entVersionId, evid = evidence }, tx, cancellationToken: ct));
            return (newId, MappingStates.Conflict);
        }
    }

    private static async Task WriteSupplierHistoryAsync(SqlConnection conn, SqlTransaction tx, long mappingId,
        long supplierId, long entVersionId, string state, string evidence, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping_history(mapping_id,supplier_id,enterprise_version_id,state,evidence_json,changed_by,reason)
VALUES(@mid,@sid,@ev,@state,@evid,N'SYSTEM',N'auto-match')",
            new { mid = mappingId, sid = supplierId, ev = entVersionId, state, evid = evidence }, tx, cancellationToken: ct));

    private sealed record ProductRec(long id, string spid, string? approval_no_norm, string? manufacturer_identity,
        string? dosage_form, string? strength, string? package_spec);
    private sealed record DrugRec(long id, string platform_drug_id, string? approval_no_norm, string? manufacturer_identity,
        string? dosage_form, string? strength, string? package_spec);
    private sealed record PMapRec(long id, long product_id, long platform_drug_id, string state, bool is_locked, int version);
    private sealed record SupplierRec(long id, string dwbh, string name, string? credit_code, string? role);
    private sealed record EntRec(long id, string ref_ent_id, string ent_id, string name, string? credit_code, string? audit_status, string? role);
    private sealed record SMapRec(long id, long supplier_id, long enterprise_version_id, string state, bool is_locked, int version);
}
