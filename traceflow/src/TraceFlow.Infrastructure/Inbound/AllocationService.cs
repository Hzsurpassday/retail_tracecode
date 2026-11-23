using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Quantities;

namespace TraceFlow.Infrastructure.Inbound;

public sealed record AllocationLineInput(long SourceLineId, long UpstreamLineId, decimal Quantity, string UnitRatio, string? ConversionEvidence, IReadOnlyList<string> Codes);
public sealed record AllocationRequest(string ExpectedMatchVersion, IReadOnlyList<AllocationLineInput> Lines);
public sealed record AllocationSaveResult(int MatchVersion, int AllocationsSaved, bool AllRequiredCovered);

/// <summary>
/// 人工分配保存/取消（IN-03/04/09，AT-027/028/029/032/067）：保存=全量替换（R9a），
/// 校验失败一次性 422 fieldErrors（R9c），版本闸/占用争用/分配对重复→409（R9d）。
/// </summary>
public sealed class AllocationService
{
    private readonly IAuditLog _audit;
    public AllocationService(IAuditLog audit) => _audit = audit;

    private sealed record SnapRow(long id, long source_system_id, string source_document_key, string document_type, string completeness_status, string? raw_header);
    private sealed record MatchRow(long id, int version, string match_state);
    private sealed record TaskRow(long id, string state, long source_snapshot_id);
    private sealed record LineRow(long id, string source_line_key, string product_source_id, decimal quantity_original, string? unit_original, string? batch_raw);
    private sealed record UpRow(long id, long bill_id, string? platform_drug_id, string? batch_no, string upstream_ref_ent_id, string completeness);
    private sealed record UpCodeRow(string code_normalized, string? package_level);
    private sealed record AllocRow(long id, string state, string source_document_key);
    private sealed record ParsedLine(AllocationLineInput Input, LineRow Line, Qty Qty, long Num, long Den, IReadOnlyList<long> CodeIds);

    public async Task<AllocationSaveResult> SaveAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, AllocationRequest req, string actor, CancellationToken ct)
    {
        var snap = await conn.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_system_id, source_document_key, document_type, completeness_status, raw_header
FROM dbo.source_snapshot WHERE id=@snapshotId AND org_id=@orgId", new { orgId, snapshotId }, tx, cancellationToken: ct));
        if (snap is null || snap.document_type != "PURCHASE_IN" || snap.completeness_status != "COMPLETE")
            throw new AllocationValidationException("VALIDATION", "快照不存在或非 COMPLETE 采购入库快照");

        var match = await conn.QuerySingleOrDefaultAsync<MatchRow>(new CommandDefinition(@"
SELECT id, version, match_state FROM dbo.inbound_match WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId", new { orgId, env, snapshotId }, tx, cancellationToken: ct));
        if (match is null)
            throw new AllocationValidationException("VALIDATION", "尚未生成匹配记录，请先触发匹配");
        if (match.match_state == "NOT_REQUIRED")
            throw new AllocationValidationException("VALIDATION", "全部行豁免，无需补配");
        if (!int.TryParse(req.ExpectedMatchVersion, out var expected) || expected != match.version)
            throw new AllocationConflictException("VERSION_CONFLICT", "匹配版本已变化，请刷新后重试");

        // R4/R8a：已发送或已进入发送流程的任务一律冻结，先于证据判定拒绝（只读，不改状态）。
        // 任务必须挂靠本次保存的快照：占用豁免与释放同用一个 snapshotId 作用域，跨快照任务直接 409。
        var task = await ResolveTaskAsync(conn, tx, orgId, env, snap.source_document_key, snapshotId, ct);
        var taskCancellable = false;
        if (task is not null)
        {
            var sent = await SentAttemptsAsync(conn, tx, task.id, ct);
            if (sent > 0 || task.state is not ("Blocked" or "Ready"))
                throw new AllocationConflictException("FROZEN", "任务已发送或已进入发送流程，禁止修改补配");
            taskCancellable = true;
        }

        var lines = (await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id, source_line_key, product_source_id, quantity_original, unit_original, batch_raw
FROM dbo.source_line WHERE snapshot_id=@snapshotId AND event_kind=N'PURCHASE_IN'",
            new { snapshotId }, tx, cancellationToken: ct))).ToDictionary(l => l.id);
        var required = await RequiredLineIdsAsync(conn, tx, orgId, snap.source_system_id, snapshotId, ct);

        var dwbh = Str(snap.raw_header, "dwbh");
        var supRef = string.IsNullOrEmpty(dwbh) ? null : await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT ev.ref_ent_id FROM dbo.supplier s
JOIN dbo.supplier_mapping sm ON sm.supplier_id=s.id AND sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE s.org_id=@orgId AND s.source_system_id=@sys AND s.dwbh=@dwbh",
            new { orgId, sys = snap.source_system_id, dwbh }, tx, cancellationToken: ct));
        if (supRef is null)
            throw new AllocationValidationException("SUPPLIER_UNRESOLVED", "供应商映射未确认，无法补配");

        var fieldErrors = new List<object>();
        var parsed = new List<ParsedLine>();
        var seenSource = new HashSet<long>();
        var seenUp = new HashSet<long>();
        foreach (var input in req.Lines)
        {
            void Err(string code, string msg) => fieldErrors.Add(new { sourceLineId = input.SourceLineId, code, message = msg });
            if (!lines.TryGetValue(input.SourceLineId, out var sl)) { Err("LINE_NOT_FOUND", "来源行不属于该快照"); continue; }
            if (!required.Contains(input.SourceLineId)) { Err("NOT_REQUIRED_LINE", "仅 REQUIRED 行可补配（EXEMPT/UNKNOWN 拒绝）"); continue; }
            if (!seenSource.Add(input.SourceLineId)) { Err("DUPLICATE_LINE", "同一来源行重复提交"); continue; }
            if (!seenUp.Add(input.UpstreamLineId)) { Err("UPSTREAM_LINE_REUSED", "同一上游明细被多行争用"); continue; }
            var up = await conn.QuerySingleOrDefaultAsync<UpRow>(new CommandDefinition(@"
SELECT l.id, l.bill_id, l.platform_drug_id, l.batch_no, b.upstream_ref_ent_id, b.completeness
FROM dbo.upstream_bill_line l JOIN dbo.upstream_bill b ON b.id=l.bill_id
WHERE l.id=@id AND b.org_id=@orgId AND b.env=@env",
                new { id = input.UpstreamLineId, orgId, env }, tx, cancellationToken: ct));
            if (up is null || up.completeness != "COMPLETE") { Err("UPSTREAM_NOT_FOUND", "上游明细不存在或单据未完成"); continue; }
            if (!string.Equals(up.upstream_ref_ent_id, supRef, StringComparison.Ordinal)) { Err("SUPPLIER_MISMATCH", "上游单据不属于已确认供应商"); continue; }
            var drugId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT pd.platform_drug_id FROM dbo.product p
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE p.org_id=@orgId AND p.source_system_id=@sys AND p.spid=@spid",
                new { orgId, sys = snap.source_system_id, spid = sl.product_source_id }, tx, cancellationToken: ct));
            if (drugId is null) { Err("PRODUCT_UNRESOLVED", "商品映射未确认"); continue; }
            if (!string.Equals(up.platform_drug_id, drugId, StringComparison.Ordinal)) { Err("DRUG_CONFLICT", "药品身份强冲突，禁止分配"); continue; }
            if (string.IsNullOrWhiteSpace(sl.batch_raw) || string.IsNullOrWhiteSpace(up.batch_no)
                || !string.Equals(sl.batch_raw.Trim(), up.batch_no.Trim(), StringComparison.Ordinal))
            { Err("BATCH_CONFLICT", "批号强冲突，禁止分配"); continue; }
            if (input.Codes.Count == 0) { Err("CODES_EMPTY", "至少选择一个码"); continue; }
            if (input.Codes.Distinct(StringComparer.Ordinal).Count() != input.Codes.Count) { Err("CODES_DUPLICATE", "码重复"); continue; }
            var upCodes = (await conn.QueryAsync<UpCodeRow>(new CommandDefinition(
                "SELECT code_normalized, package_level FROM dbo.upstream_bill_code WHERE line_id=@id",
                new { id = input.UpstreamLineId }, tx, cancellationToken: ct))).ToDictionary(k => k.code_normalized, StringComparer.Ordinal);
            if (input.Codes.Any(cn => !upCodes.ContainsKey(cn))) { Err("CODE_NOT_IN_LINE", "码不属于所选上游明细"); continue; }
            if (input.Codes.Any(cn => upCodes[cn].package_level != "MIN")) { Err("BIG_CODE_BLOCKED", "非最小包装码禁止入库分配（v1 无拆零，AT-028）"); continue; }
            if (input.Codes.Any(cn => TraceCodeNormalizer.Normalize(cn).Format != CodeFormat.Supported8Prefix20)) { Err("CODE_FORMAT", "码格式不支持"); continue; }
            var codeIds = new List<long>();
            var missing = false;
            foreach (var cn in input.Codes)
            {
                var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = cn }, tx, cancellationToken: ct));
                if (id is null) { Err("CODE_UNKNOWN", "码未入台账"); missing = true; break; }
                codeIds.Add(id!.Value);
            }
            if (missing) continue;
            var rel = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.code_relation WHERE parent_code_id IN @ids AND child_code_id IN @ids",
                new { ids = codeIds }, tx, cancellationToken: ct));
            if (rel > 0) { Err("CODE_RELATION", "码集存在父子关系，禁止重复计数"); continue; }
            Qty qty;
            try { qty = new Qty(input.Quantity); }
            catch (ArgumentOutOfRangeException) { Err("QTY_DOMAIN", "数量超出 decimal(18,6) 域"); continue; }
            if (qty.Value <= 0) { Err("QTY_NONPOSITIVE", "数量必须为正"); continue; }
            var parts = input.UnitRatio.Split('/');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var num) || !long.TryParse(parts[1], out var den) || num <= 0 || den <= 0)
            { Err("RATIO_FORMAT", "单位比例须为 n/d 正整数"); continue; }
            var ratio = new UnitRatio(num, den);
            Qty converted;
            try { converted = ratio.Convert(qty); }
            catch (UnitConversionException) { Err("CONVERSION_INEXACT", "换算不整除或超域（AT-029）"); continue; }
            if (converted.Value != input.Codes.Count)
            { Err("CONVERSION_INEXACT", $"换算结果 {converted.Value} 与码数 {input.Codes.Count} 不一致（AT-029）"); continue; }
            if ((num != 1 || den != 1) && string.IsNullOrWhiteSpace(input.ConversionEvidence))
            { Err("CONVERSION_EVIDENCE_REQUIRED", "非 1/1 换算必须提供换算依据"); continue; }
            parsed.Add(new ParsedLine(input, sl, qty, num, den, codeIds));
        }
        if (fieldErrors.Count > 0)
            throw new AllocationValidationException("ALLOCATION_INVALID", "补配校验失败", fieldErrors);
        if (parsed.Count == 0)
            throw new AllocationValidationException("EMPTY_LINES", "至少提交一条补配行");

        await AssertNoForeignClaimsAsync(conn, tx, orgId, env, snapshotId, parsed.SelectMany(p => p.CodeIds).Distinct().ToList(), ct);

        // R9a：先回退该快照旧占用（任务存在→发送前取消整单回退；否则撤销 RESERVED/TENTATIVE 分配并释放占用）。
        // 两条路径共用 snapshotId 作用域（与占用豁免一致）：发送前取消只覆盖任务挂靠的 RESERVED/COMMITTED，
        // 残留的 TENTATIVE（无占用但受 UX_alloc_pair_active 约束）由随后的撤销一并清理，避免同对重插被唯一索引阻断。
        if (taskCancellable)
            await InboundCancelService.CancelPreSendAsync(conn, tx, orgId, env, snapshotId, actor,
                "人工补配保存前撤销旧任务", "LIVE", ct);
        await CancelReservedForSnapshotAsync(conn, tx, orgId, env, snapshotId, actor, "人工补配保存替换", ct);

        foreach (var p in parsed)
        {
            long allocId;
            try
            {
                allocId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.inbound_allocation(org_id, env, source_line_id, upstream_line_id, quantity, unit_ratio_numerator, unit_ratio_denominator, conversion_evidence, package_level, state, created_by)
OUTPUT INSERTED.id
VALUES(@orgId, @env, @sourceLineId, @upstreamLineId, @qty, @num, @den, @evidence, N'MIN', N'RESERVED', @actor)",
                    new { orgId, env, sourceLineId = p.Input.SourceLineId, upstreamLineId = p.Input.UpstreamLineId,
                          qty = p.Qty.Value, num = p.Num, den = p.Den, evidence = p.Input.ConversionEvidence, actor },
                    tx, cancellationToken: ct));
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                throw new AllocationConflictException("ALLOCATION_EXISTS", "该来源行/上游明细对已存在有效分配");
            }
            for (var i = 0; i < p.Input.Codes.Count; i++)
            {
                var codeId = p.CodeIds[i];
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO dbo.allocation_code(allocation_id, code_id) VALUES(@allocId, @codeId)",
                    new { allocId, codeId }, tx, cancellationToken: ct));
                await CodeClaimWriter.InsertActiveAsync(conn, tx, orgId, env, codeId, "PURCHASE_IN", allocId, null, ct);
            }
        }

        // R9b：全部 REQUIRED 行覆盖→COMPLETE，否则 PARTIAL；不建任务（任务只在确认时创建）。
        var covered = required.SetEquals(parsed.Select(p => p.Input.SourceLineId));
        var newVersion = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=@state, reason=N'人工补配保存', version=version+1, matched_at=SYSUTCDATETIME()
OUTPUT INSERTED.version
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId",
            new { state = covered ? "COMPLETE" : "PARTIAL", orgId, env, snapshotId }, tx, cancellationToken: ct));
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.save", "source_snapshot", snapshotId.ToString(),
            null, null, null, null, null, "LIVE"));
        return new AllocationSaveResult(newVersion, parsed.Count, covered);
    }

    public async Task<int> CancelAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, long snapshotId,
        long? allocationId, string actor, string reason, CancellationToken ct)
    {
        if (allocationId is not null)
        {
            var alloc = await conn.QuerySingleOrDefaultAsync<AllocRow>(new CommandDefinition(@"
SELECT a.id, a.state, s.source_document_key
FROM dbo.inbound_allocation a
JOIN dbo.source_line l ON l.id=a.source_line_id
JOIN dbo.source_snapshot s ON s.id=l.snapshot_id
WHERE a.id=@id AND a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId",
                new { id = allocationId, orgId, env, snapshotId }, tx, cancellationToken: ct));
            if (alloc is null)
                throw new AllocationValidationException("VALIDATION", "分配不存在或不属于该快照");
            if (alloc.state == "CANCELLED") return 0;
            if (alloc.state == "COMMITTED")
            {
                // COMMITTED 已挂任务：单行取消=整单发送前取消（任务/分配/占用/匹配一并回退）。
                // 与 SaveAsync 同一冻结闸与快照作用域：已发送/发送中→FROZEN，任务属于其他快照→VERSION_CONFLICT。
                var committedTask = await ResolveTaskAsync(conn, tx, orgId, env, alloc.source_document_key, snapshotId, ct);
                if (committedTask is null) return 0;
                var committedSent = await SentAttemptsAsync(conn, tx, committedTask.id, ct);
                if (committedSent > 0 || committedTask.state is not ("Blocked" or "Ready"))
                    throw new AllocationConflictException("FROZEN", "任务已发送或已进入发送流程，禁止取消");
                var ok = await InboundCancelService.CancelPreSendAsync(conn, tx, orgId, env, snapshotId, actor, reason, "LIVE", ct);
                if (ok)
                    await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.cancel", "source_snapshot", snapshotId.ToString(), reason, null, null, null, null, "LIVE"));
                return ok ? 1 : 0;
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1 WHERE id=@id",
                new { id = alloc.id }, tx, cancellationToken: ct));
            await CodeClaimWriter.ReleaseByAllocationAsync(conn, tx, alloc.id, actor, reason, ct);
            await RecomputeMatchAsync(conn, tx, orgId, env, snapshotId, ct);
            await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.cancel", "inbound_allocation", alloc.id.ToString(), reason, null, null, null, null, "LIVE"));
            return 1;
        }

        var snap = await conn.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_system_id, source_document_key, document_type, completeness_status, raw_header
FROM dbo.source_snapshot WHERE id=@snapshotId AND org_id=@orgId", new { orgId, snapshotId }, tx, cancellationToken: ct));
        if (snap is null)
            throw new AllocationValidationException("VALIDATION", "快照不存在");
        var task = await ResolveTaskAsync(conn, tx, orgId, env, snap.source_document_key, snapshotId, ct);
        if (task is not null)
        {
            var sent = await SentAttemptsAsync(conn, tx, task.id, ct);
            if (sent > 0 || task.state is not ("Blocked" or "Ready"))
                throw new AllocationConflictException("FROZEN", "任务已发送或已进入发送流程，禁止取消");
            var ok = await InboundCancelService.CancelPreSendAsync(conn, tx, orgId, env, snapshotId, actor, reason, "LIVE", ct);
            return ok ? 1 : 0;
        }
        var n = await CancelReservedForSnapshotAsync(conn, tx, orgId, env, snapshotId, actor, reason, ct);
        if (n > 0)
        {
            await RecomputeMatchAsync(conn, tx, orgId, env, snapshotId, ct);
            await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.cancel", "source_snapshot", snapshotId.ToString(), reason, null, null, null, null, "LIVE"));
        }
        return n;
    }

    private static Task<TaskRow?> TaskForSnapshotAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, string docKey, CancellationToken ct)
        => conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id, state, source_snapshot_id FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND event_key=@eventKey AND source_snapshot_id IS NOT NULL AND state<>N'Cancelled'",
            new { orgId, env, eventKey = $"KSOA:{docKey}:PURCHASE_IN" }, tx, cancellationToken: ct));

    /// <summary>
    /// 任务路径的单一作用域：占用豁免（AssertNoForeignClaimsAsync）与释放（CancelPreSendAsync/CancelReservedForSnapshotAsync）
    /// 都必须锚定这次调用的 snapshotId。任务来自其他快照版本（陈旧 UI / 旧版本任务）时直接 409 VERSION_CONFLICT 拒绝且不写，
    /// 否则会释放错快照的占用：本快照自己的 RESERVED 行存活（→ ALLOCATION_EXISTS），另一版本的占用被误撤。
    /// </summary>
    private static async Task<TaskRow?> ResolveTaskAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        string docKey, long snapshotId, CancellationToken ct)
    {
        var task = await TaskForSnapshotAsync(conn, tx, orgId, env, docKey, ct);
        if (task is not null && task.source_snapshot_id != snapshotId)
            throw new AllocationConflictException("VERSION_CONFLICT", "发送前任务属于其他快照版本，请刷新后重试");
        return task;
    }

    private static Task<int> SentAttemptsAsync(SqlConnection conn, SqlTransaction tx, long taskId, CancellationToken ct)
        => conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
            new { taskId }, tx, cancellationToken: ct));

    /// <summary>
    /// DB-05 前置闸：跨文档 ACTIVE 占用先锁先判（UPDLOCK,HOLDLOCK 串行化并发保存），
    /// 避免插入分配后才撞 2627 而在调用方事务里留下“有分配无占用”的半成品。
    /// 本快照自身历史分配不计争用（与 T8 同 source_document_key 谱系豁免一致）。
    /// </summary>
    private static async Task AssertNoForeignClaimsAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, IReadOnlyList<long> codeIds, CancellationToken ct)
    {
        if (codeIds.Count == 0) return;
        var contended = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.code_claim cc WITH (UPDLOCK, HOLDLOCK)
WHERE cc.org_id=@orgId AND cc.env=@env AND cc.event_scope=N'PURCHASE_IN' AND cc.claim_state=N'ACTIVE'
  AND cc.code_id IN @codeIds
  AND NOT EXISTS (
      SELECT 1 FROM dbo.inbound_allocation a
      JOIN dbo.source_line l ON l.id=a.source_line_id
      WHERE a.id=cc.allocation_id AND a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId)",
            new { orgId, env, codeIds, snapshotId }, tx, cancellationToken: ct));
        if (contended > 0)
            throw new AllocationConflictException("CODE_CONTENDED", $"提交的码中有 {contended} 个已被其他有效入库占用");
    }

    private static async Task<int> CancelReservedForSnapshotAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, string actor, string reason, CancellationToken ct)
    {
        // TENTATIVE 行无占用但受 UX_alloc_pair_active 约束，同样须撤销，否则同对重插被唯一索引阻断。
        var ids = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT a.id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId AND a.state IN (N'RESERVED',N'TENTATIVE')",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct))).ToList();
        foreach (var id in ids)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1 WHERE id=@id",
                new { id }, tx, cancellationToken: ct));
            await CodeClaimWriter.ReleaseByAllocationAsync(conn, tx, id, actor, reason, ct);
        }
        return ids.Count;
    }

    private static async Task RecomputeMatchAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, long snapshotId, CancellationToken ct)
    {
        var sysId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT source_system_id FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId", new { id = snapshotId, orgId }, tx, cancellationToken: ct));
        var required = await RequiredLineIdsAsync(conn, tx, orgId, sysId, snapshotId, ct);
        var coveredIds = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT a.source_line_id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId AND a.state IN (N'RESERVED',N'COMMITTED')",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct))).ToHashSet();
        var state = required.Count == 0 ? "NOT_REQUIRED"
            : required.SetEquals(coveredIds) ? "COMPLETE"
            : coveredIds.Count > 0 ? "PARTIAL" : "UNMATCHED";
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=@state, reason=N'人工取消补配', version=version+1, matched_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId",
            new { state, orgId, env, snapshotId }, tx, cancellationToken: ct));
    }

    private static async Task<HashSet<long>> RequiredLineIdsAsync(SqlConnection conn, SqlTransaction tx, long orgId, long sysId,
        long snapshotId, CancellationToken ct)
        => (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT l.id FROM dbo.source_line l
JOIN dbo.product p ON p.org_id=@orgId AND p.source_system_id=@sysId AND p.spid=l.product_source_id
WHERE l.snapshot_id=@snapshotId AND l.event_kind=N'PURCHASE_IN' AND p.trace_classification=N'REQUIRED'",
            new { orgId, sysId, snapshotId }, tx, cancellationToken: ct))).ToHashSet();

    private static string? Str(string? json, string name)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }
}
