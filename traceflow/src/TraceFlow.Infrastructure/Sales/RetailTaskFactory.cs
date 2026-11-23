using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Billing;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sales;

/// <summary>零售单据行：码集合为核验后的规范化码（CodeId 为 trace_code 主键）。</summary>
public sealed record RetailLine(long SourceLineId, string SourceLineKey, string ProductSourceId, string PlatformDrugId,
    decimal Quantity, string? Unit, string? BatchNo, IReadOnlyList<(long CodeId, string CodeNormalized)> Codes);

/// <summary>
/// 零售任务上下文（SALE-05）。TaskType ∈ {RETAIL_321, CONSUMER_RETURN_116, PURCHASE_RETURN_202}。
/// <see cref="SnapshotId"/> 可空：夹具播种的 202 无 KSOA 快照，落库保持 NULL（绝不写 0 假装有来源）。
/// MappingDigest 为调用方排序拼接后的映射 id/version 串（工厂再摘要化写入 mapping_version_snapshot，与 M2 同法）。
/// <see cref="PredecessorEventByCodeId"/>（可选，init）：116/202 的 codeId→前序 code_event id（116=SALE，202=PURCHASE_IN），
/// 由 Task 6 构造；321 为 null。退货事件与 predecessor_task_id 均由此派生，不额外增加方法参数。
/// </summary>
public sealed record RetailTaskContext(long OrgId, string Env, long? SnapshotId, string DocKey, DateTime? BusinessDateTimeUtc,
    string SourceHash, string MappingDigest, string RuleVersion, IReadOnlyList<RetailLine> Lines, string TaskType)
{
    /// <summary>退货前序事件（codeId → predecessor code_event.id）；321 保持 null。</summary>
    public IReadOnlyDictionary<long, long>? PredecessorEventByCodeId { get; init; }
}

/// <summary>
/// PartIds 按 part_index 升序，且仅含 state &lt;&gt; 'Cancelled' 的分片（分片收缩后遗留的取消分片不返回）。
/// Reused=true 表示复用既有任务（未新建/未复活/未提升）。
/// </summary>
public sealed record RetailTaskResult(long TaskId, IReadOnlyList<long> PartIds, bool Reused);

/// <summary>
/// 零售任务工厂（SALE-05/IDEM-03/DEF-01）：事件键为单据级 <c>KSOA:{docKey}:{taskType}</c>，同事务创建
/// upload_task + 多分片 + 每分片 outbox + 逐码 T2 占用（RETAIL:321 / RETURN:116 / RETURN:202）+ code_event。
/// 分片按行顺序装填且单行不可拆（行码数 &gt; upload.part.maxCodes → 422 PART_TOO_LARGE）；
/// bill_code = BillCodeGenerator(contract, taskId, partIndex)（P1/P2 后缀），Cancelled 复活不换单号。
/// Blocked + RETURN_CONFIRM_REQUIRED（116 人工确认）走同一绑定写路径，但任务保持 Blocked，
/// 由 Task 6 确认服务在确认事务内执行 Blocked→Ready；其他 Blocked 原因拒绝复用（REUSE_STATE_INVALID）。
/// 调用方负责事务边界与提交；本类只抛出异常，不做回滚。
/// </summary>
public sealed class RetailTaskFactory
{
    public const string Retail321 = "RETAIL_321";
    public const string ConsumerReturn116 = "CONSUMER_RETURN_116";
    /// <summary>采购退货 202（R-M3-4：无 KSOA 采集，只做服务/测试支持）。</summary>
    public const string PurchaseReturn202 = "PURCHASE_RETURN_202";
    private const string CancelledState = "Cancelled";
    private const string BlockedState = "Blocked";
    private const string ReturnConfirmRequired = "RETURN_CONFIRM_REQUIRED";
    private const int ClaimGateBatch = 500;

    private readonly SettingsService _settings;
    private readonly IJobQueue _queue;
    public RetailTaskFactory(SettingsService settings, IJobQueue queue)
    {
        _settings = settings;
        _queue = queue;
    }

    public async Task<RetailTaskResult> CreateAsync(SqlConnection conn, SqlTransaction tx, RetailTaskContext ctx, CancellationToken ct)
    {
        if (ctx.Lines.Count == 0)
            throw new AllocationValidationException("EMPTY_LINES", "任务至少须包含一条通过核验的销售行");
        if (ctx.TaskType is not (Retail321 or ConsumerReturn116 or PurchaseReturn202))
            throw new AllocationValidationException("TASK_TYPE_INVALID", $"不支持的零售任务类型 {ctx.TaskType}");
        if (ctx.Lines.Sum(l => l.Codes.Count) == 0)
            throw new AllocationValidationException("EMPTY_CODES", "任务至少须包含一个通过核验的追溯码");

        var eventKey = $"KSOA:{ctx.DocKey}:{ctx.TaskType}";
        var maxCodes = await _settings.GetPartMaxCodesAsync(ct);
        var buckets = Split(ctx.Lines, maxCodes); // 先分片：PART_TOO_LARGE 在任何写入前抛出
        await ValidatePredecessorsAsync(conn, tx, ctx, ct);
        var predecessorTaskId = await ResolvePredecessorTaskAsync(conn, tx, ctx, ct);
        var predecessorBillCodes = predecessorTaskId is null
            ? Array.Empty<string>()
            : (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT bill_code FROM dbo.upload_part WHERE task_id=@id ORDER BY part_index",
                new { id = predecessorTaskId.Value }, tx, cancellationToken: ct))).ToArray();

        var payloadJson = DocPayloadJson(eventKey, ctx, predecessorBillCodes);
        var payloadHash = Sha(payloadJson);
        var mappingSnapshot = Sha(ctx.MappingDigest);

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingRow>(new CommandDefinition(@"
SELECT id,state,state_reason,source_snapshot_id,source_hash
FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE org_id=@orgId AND env=@env AND event_key=@key",
            new { orgId = ctx.OrgId, env = ctx.Env, key = eventKey }, tx, cancellationToken: ct));

        // 复用既有活动任务：来源漂移即拒绝（载荷已发送/已接受，不可改写）。
        if (existing is not null && existing.state != CancelledState && existing.state != BlockedState)
        {
            if (existing.source_snapshot_id != ctx.SnapshotId
                || !string.Equals(existing.source_hash, ctx.SourceHash, StringComparison.Ordinal))
                throw new AllocationConflictException("SOURCE_CHANGED",
                    $"事件键 {eventKey} 已绑定其他来源快照/摘要，禁止复用改写载荷");
            var reusedPartIds = (await conn.QueryAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.upload_part WHERE task_id=@id AND state<>N'Cancelled' ORDER BY part_index",
                new { id = existing.id }, tx, cancellationToken: ct))).ToList();
            return new RetailTaskResult(existing.id, reusedPartIds, true);
        }

        var promote = false; // Blocked + RETURN_CONFIRM_REQUIRED：全量绑定写，但任务保持 Blocked
        long taskId;
        if (existing is not null)
        {
            var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
                new { id = existing.id }, tx, cancellationToken: ct));
            if (existing.state == CancelledState)
            {
                if (sent > 0)
                    throw new AllocationConflictException("REACTIVATE_REFUSED", "Cancelled 任务已有 UPLOAD 尝试，禁止同行复活");
            }
            else // Blocked：仅人工确认中的退货任务可提升，其余 Blocked 原因（WAIT_DEPENDENCY 等）拒绝改写
            {
                if (!string.Equals(existing.state_reason, ReturnConfirmRequired, StringComparison.Ordinal) || sent > 0)
                    throw new AllocationConflictException("REUSE_STATE_INVALID",
                        $"任务处于 {existing.state}/{existing.state_reason}，不可复用写入");
                promote = true;
            }
            taskId = existing.id;
        }
        else
        {
            taskId = 0;
        }

        // 失败先行：绑定前以 UPDLOCK/HOLDLOCK 读同范围 ACTIVE 占用；任意外来占用（含无任务占用）即冲突，零写入。
        await AssertClaimsAvailableAsync(conn, tx, ctx, ClaimScope(ctx.TaskType), existing?.id, ct);

        if (existing is null)
        {
            taskId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,mapping_version_snapshot,
    payload_json,payload_hash,state,source_integrity,match_state,verification_state,dependency_state,predecessor_task_id)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@key,@taskType,@snap,@sh,@mv,@pj,@ph,N'Ready',N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE',@pred)",
                new { orgId = ctx.OrgId, env = ctx.Env, key = eventKey, taskType = ctx.TaskType, snap = ctx.SnapshotId,
                      sh = ctx.SourceHash, mv = mappingSnapshot, pj = payloadJson, ph = payloadHash, pred = predecessorTaskId },
                tx, cancellationToken: ct));
        }
        else
        {
            // 提升保留 Blocked/原因（Task 6 确认后才 Blocked→Ready）；Cancelled 复活直接 Ready。
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=@state, state_reason=@reason,
       source_snapshot_id=@snap, source_hash=@sh, payload_json=@pj, payload_hash=@ph, mapping_version_snapshot=@mv,
       source_integrity=N'COMPLETE', match_state=N'COMPLETE', verification_state=N'PASSED', dependency_state=N'NONE',
       predecessor_task_id=@pred, version=version+1
WHERE id=@id",
                new
                {
                    id = taskId, state = promote ? BlockedState : "Ready",
                    reason = promote ? existing.state_reason : "Cancelled 任务复活（从未发送）",
                    snap = ctx.SnapshotId, sh = ctx.SourceHash, pj = payloadJson, ph = payloadHash,
                    mv = mappingSnapshot, pred = predecessorTaskId,
                }, tx, cancellationToken: ct));
        }

        var reuseExistingParts = existing is not null;
        var partIds = await UpsertPartsAsync(conn, tx, ctx, eventKey, taskId, buckets, maxCodes, reuseExistingParts,
            predecessorBillCodes, ct);
        await InsertClaimsAsync(conn, tx, ctx, taskId, ClaimScope(ctx.TaskType), ct);
        await InsertEventsAsync(conn, tx, ctx, taskId, ct);
        return new RetailTaskResult(taskId, partIds, false);
    }

    /// <summary>行码数上限内按行顺序装填；单行码数超限即 422 PART_TOO_LARGE（单行不可跨分片）。</summary>
    private static List<List<RetailLine>> Split(IReadOnlyList<RetailLine> lines, int maxCodes)
    {
        var buckets = new List<List<RetailLine>>();
        var current = new List<RetailLine>();
        var count = 0;
        foreach (var line in lines)
        {
            if (line.Codes.Count == 0) continue; // 无码行不进载荷（核验已保证 REQUIRED 行有码）
            if (line.Codes.Count > maxCodes)
                throw new AllocationValidationException("PART_TOO_LARGE",
                    $"来源行 {line.SourceLineKey} 含 {line.Codes.Count} 个码，超过分片上限 {maxCodes}（单行不可拆）");
            if (count > 0 && count + line.Codes.Count > maxCodes)
            {
                buckets.Add(current);
                current = new List<RetailLine>();
                count = 0;
            }
            current.Add(line);
            count += line.Codes.Count;
        }
        if (current.Count > 0) buckets.Add(current);
        return buckets;
    }

    /// <summary>
    /// 逐分片落库：Cancelled 复活 / Blocked 确认提升时复用既有 part_index → bill_code（不换单号），
    /// 分片数变少的多余分片行政性取消；每个分片一条 upload-dispatch（intent part:{partId}，trigger sale-verify），
    /// CANCELLED 队列行复位。
    /// </summary>
    private async Task<IReadOnlyList<long>> UpsertPartsAsync(SqlConnection conn, SqlTransaction tx, RetailTaskContext ctx,
        string eventKey, long taskId, IReadOnlyList<List<RetailLine>> buckets, int maxCodes, bool reuseExistingParts,
        IReadOnlyList<string> predecessorBillCodes, CancellationToken ct)
    {
        var existingParts = reuseExistingParts
            ? (await conn.QueryAsync<PartRow>(new CommandDefinition(
                "SELECT id, part_index, bill_code FROM dbo.upload_part WHERE task_id=@id ORDER BY part_index",
                new { id = taskId }, tx, cancellationToken: ct))).ToList()
            : new List<PartRow>();
        var contract = existingParts.Count < buckets.Count ? await _settings.GetBillContractAsync(ct) : null;
        var splitRule = $"maxCodes:{maxCodes}";
        var partIds = new List<long>(buckets.Count);
        for (var index = 0; index < buckets.Count; index++)
        {
            var partJson = PartPayloadJson(eventKey, ctx, buckets[index], index, predecessorBillCodes);
            var partHash = Sha(partJson);
            var codeSetHash = SourceHasher.CodeSetHash(
                buckets[index].SelectMany(l => l.Codes.Select(c => c.CodeNormalized)).ToList());
            long partId;
            if (index < existingParts.Count)
            {
                partId = existingParts[index].id;
                await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_part SET payload_json=@pj, payload_hash=@ph, code_set_hash=@csh, split_rule_version=@srv, state=N'Ready'
WHERE id=@id",
                    new { id = partId, pj = partJson, ph = partHash, csh = codeSetHash, srv = splitRule },
                    tx, cancellationToken: ct));
            }
            else
            {
                var billCode = BillCodeGenerator.Generate(contract!, taskId, index); // 超长抛 InvalidOperationException，禁截断
                partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,split_rule_version,state)
OUTPUT INSERTED.id
VALUES(@taskId,@orgId,@env,@idx,@bill,@pj,@ph,@csh,@srv,N'Ready')",
                    new { taskId, orgId = ctx.OrgId, env = ctx.Env, idx = index, bill = billCode, pj = partJson,
                          ph = partHash, csh = codeSetHash, srv = splitRule }, tx, cancellationToken: ct));
            }
            partIds.Add(partId);
            var jobId = await _queue.EnqueueAsync(conn, tx, ctx.OrgId, ctx.Env, "upload-dispatch", $"part:{partId}",
                JsonSerializer.Serialize(new { orgId = ctx.OrgId, env = ctx.Env, taskId, partId, trigger = "sale-verify" }),
                DateTime.UtcNow, ct);
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'PENDING', attempt_count=0, next_run_at=SYSUTCDATETIME(),
       finished_at=NULL, last_error=NULL, lease_owner=NULL, lease_until=NULL
WHERE id=@id AND job_state=N'CANCELLED'", new { id = jobId }, tx, cancellationToken: ct));
        }
        // 复活后分片数减少：多余旧分片不再承载任何码，行政性取消（含其未执行的发送作业），避免过期码集被发送。
        for (var index = buckets.Count; index < existingParts.Count; index++)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.upload_part SET state=N'Cancelled' WHERE id=@id AND state<>N'Cancelled'",
                new { id = existingParts[index].id }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'CANCELLED', finished_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state IN (N'PENDING',N'FAILED')",
                new { orgId = ctx.OrgId, env = ctx.Env, intent = $"part:{existingParts[index].id}" }, tx, cancellationToken: ct));
        }
        return partIds;
    }

    /// <summary>T2 占用：每码一条 ACTIVE（scope 与 M2 的 PURCHASE_IN 分开）；同任务已持有的占用不重复插入，其余冲突 2601/2627 → 409 CODE_CONTENDED。</summary>
    private static async Task InsertClaimsAsync(SqlConnection conn, SqlTransaction tx, RetailTaskContext ctx, long taskId,
        string scope, CancellationToken ct)
    {
        foreach (var line in ctx.Lines)
            foreach (var code in line.Codes)
            {
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.code_claim
               WHERE org_id=@orgId AND env=@env AND code_id=@codeId AND event_scope=@scope
                 AND claim_state=N'ACTIVE' AND upload_task_id=@taskId)
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,allocation_id,claim_state)
VALUES(@orgId,@env,@codeId,@scope,@taskId,NULL,N'ACTIVE')",
                        new { orgId = ctx.OrgId, env = ctx.Env, codeId = code.CodeId, scope, taskId },
                        tx, cancellationToken: ct));
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    // 唯一索引 UX_claim_active 争用（失败先行门禁之后仍可能并发插入）：由调用方回滚整个事务。
                    throw new AllocationConflictException("CODE_CONTENDED",
                        $"码 {code.CodeNormalized} 在 {scope} 范围已被其他有效任务占用");
                }
            }
    }

    /// <summary>
    /// 失败先行占用门禁：写入前以 UPDLOCK/HOLDLOCK 读同 scope/org/env 的 ACTIVE 占用。
    /// 同任务（taskId 非空）已持有的占用允许；upload_task_id 为空或属于其他任务 → 冲突，调用方回滚且零写入。
    /// 分批查询避免超过 2100 个参数上限。
    /// </summary>
    private static async Task AssertClaimsAvailableAsync(SqlConnection conn, SqlTransaction tx, RetailTaskContext ctx,
        string scope, long? taskId, CancellationToken ct)
    {
        var codeIds = ctx.Lines.SelectMany(l => l.Codes.Select(c => c.CodeId)).Distinct().ToList();
        for (var start = 0; start < codeIds.Count; start += ClaimGateBatch)
        {
            var batch = codeIds.Skip(start).Take(ClaimGateBatch).ToList();
            var contended = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.code_claim WITH (UPDLOCK, HOLDLOCK)
WHERE claim_state=N'ACTIVE' AND event_scope=@scope AND org_id=@orgId AND env=@env AND code_id IN @codes
  AND (upload_task_id IS NULL OR upload_task_id<>@taskId)",
                new { orgId = ctx.OrgId, env = ctx.Env, scope, codes = batch, taskId = taskId ?? -1L },
                tx, cancellationToken: ct));
            if (contended > 0)
                throw new AllocationConflictException("CODE_CONTENDED",
                    $"提交的码中有 {contended} 个已在 {scope} 范围被其他有效任务占用");
        }
    }

    /// <summary>
    /// 退货前序映射校验：每个码必须给出前序事件，且该 code_event 必须存在、code_id 与码一致。
    /// 任一缺失/错配 → PREDECESSOR_INVALID（在任何写入前抛出）。321 无前序要求。
    /// </summary>
    private static async Task ValidatePredecessorsAsync(SqlConnection conn, SqlTransaction tx,
        RetailTaskContext ctx, CancellationToken ct)
    {
        if (ctx.TaskType == Retail321) return;
        var map = ctx.PredecessorEventByCodeId;
        var codeIds = ctx.Lines.SelectMany(l => l.Codes.Select(c => c.CodeId)).Distinct().ToList();
        if (map is null || codeIds.Any(id => !map.ContainsKey(id)))
            throw new AllocationValidationException("PREDECESSOR_INVALID", "退货码缺少前序销售事件映射");

        var eventIds = map.Values.Distinct().ToList();
        var codeByEventId = new Dictionary<long, long>();
        for (var start = 0; start < eventIds.Count; start += ClaimGateBatch)
        {
            var batch = eventIds.Skip(start).Take(ClaimGateBatch).ToList();
            var rows = await conn.QueryAsync<EventCodeRow>(new CommandDefinition(@"
SELECT id, code_id FROM dbo.code_event WHERE org_id=@orgId AND env=@env AND id IN @ids",
                new { orgId = ctx.OrgId, env = ctx.Env, ids = batch }, tx, cancellationToken: ct));
            foreach (var row in rows) codeByEventId[row.id] = row.code_id;
        }
        foreach (var pair in map)
        {
            if (!codeByEventId.TryGetValue(pair.Value, out var eventCodeId) || eventCodeId != pair.Key)
                throw new AllocationValidationException("PREDECESSOR_INVALID",
                    $"码 {pair.Key} 的前序事件 {pair.Value} 不存在或归属其他码");
        }
    }

    /// <summary>321 → 每码 SALE/OUT；116 → CONSUMER_RETURN/IN；202 → PURCHASE_RETURN/OUT（predecessor_event_id 由调用方映射）。</summary>
    private static async Task InsertEventsAsync(SqlConnection conn, SqlTransaction tx, RetailTaskContext ctx, long taskId,
        CancellationToken ct)
    {
        var (eventType, direction) = ctx.TaskType switch
        {
            Retail321 => ("SALE", "OUT"),
            ConsumerReturn116 => ("CONSUMER_RETURN", "IN"),
            _ => ("PURCHASE_RETURN", "OUT"),
        };
        foreach (var line in ctx.Lines)
            foreach (var code in line.Codes)
            {
                long? predecessor = ctx.PredecessorEventByCodeId is not null
                    && ctx.PredecessorEventByCodeId.TryGetValue(code.CodeId, out var pred)
                    ? pred
                    : null;
                await conn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.code_event
               WHERE org_id=@orgId AND env=@env AND code_id=@codeId AND event_type=@eventType AND source_line_id=@lineId)
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_snapshot_id,source_line_id,
    upload_task_id,predecessor_event_id,business_datetime)
VALUES(@orgId,@env,@codeId,@eventType,@direction,@snap,@lineId,@taskId,@predecessor,@biz)",
                    new { orgId = ctx.OrgId, env = ctx.Env, codeId = code.CodeId, eventType, direction,
                          snap = ctx.SnapshotId, lineId = line.SourceLineId, taskId, predecessor = predecessor,
                          biz = ctx.BusinessDateTimeUtc }, tx, cancellationToken: ct));
            }
    }

    /// <summary>退货前序任务：前序事件所属任务唯一时回填 predecessor_task_id，否则留 NULL（不伪造血缘）。</summary>
    private static async Task<long?> ResolvePredecessorTaskAsync(SqlConnection conn, SqlTransaction tx,
        RetailTaskContext ctx, CancellationToken ct)
    {
        if (ctx.TaskType == Retail321 || ctx.PredecessorEventByCodeId is null || ctx.PredecessorEventByCodeId.Count == 0)
            return null;
        var ids = ctx.PredecessorEventByCodeId.Values.Distinct().ToList();
        var taskIds = (await conn.QueryAsync<long?>(new CommandDefinition(@"
SELECT upload_task_id FROM dbo.code_event WHERE id IN @ids AND upload_task_id IS NOT NULL",
            new { ids }, tx, cancellationToken: ct)))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        return taskIds.Count == 1 ? taskIds[0] : null;
    }

    private static string ClaimScope(string taskType) => taskType switch
    {
        Retail321 => "RETAIL:321",
        PurchaseReturn202 => "RETURN:202",
        _ => "RETURN:116",
    };

    private static string DocPayloadJson(string eventKey, RetailTaskContext ctx, IReadOnlyList<string> predecessorBillCodes)
    {
        var payload = new Dictionary<string, object?>
        {
            ["eventKey"] = eventKey,
            ["taskType"] = ctx.TaskType,
            ["businessDateTimeUtc"] = ctx.BusinessDateTimeUtc,
            ["ruleVersion"] = ctx.RuleVersion,
            ["lines"] = ctx.Lines.Select(LineProjection).ToArray(),
        };
        // 116：平台载荷需携带原销售单号（RET-04）；321 不含该字段（全单不可变载荷与 brief 形状一致）。
        if (predecessorBillCodes.Count > 0) payload["predecessorBillCodes"] = predecessorBillCodes;
        return JsonSerializer.Serialize(payload);
    }

    private static string PartPayloadJson(string eventKey, RetailTaskContext ctx, IReadOnlyList<RetailLine> lines,
        int partIndex, IReadOnlyList<string> predecessorBillCodes)
    {
        var payload = new Dictionary<string, object?>
        {
            ["eventKey"] = eventKey,
            ["taskType"] = ctx.TaskType,
            ["partIndex"] = partIndex,
            ["businessDateTimeUtc"] = ctx.BusinessDateTimeUtc,
            ["ruleVersion"] = ctx.RuleVersion,
            ["lines"] = lines.Select(LineProjection).ToArray(),
        };
        if (predecessorBillCodes.Count > 0) payload["predecessorBillCodes"] = predecessorBillCodes;
        return JsonSerializer.Serialize(payload);
    }

    private static object LineProjection(RetailLine line) => new
    {
        sourceLineKey = line.SourceLineKey,
        productSourceId = line.ProductSourceId,
        platformDrugId = line.PlatformDrugId,
        quantity = line.Quantity.ToString("0.000000", CultureInfo.InvariantCulture),
        unit = line.Unit,
        batchNo = line.BatchNo,
        codes = line.Codes.Select(c => c.CodeNormalized).ToArray(),
    };

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private sealed class ExistingRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
        public string? state_reason { get; set; }
        public long? source_snapshot_id { get; set; }
        public string source_hash { get; set; } = "";
    }

    private sealed class EventCodeRow
    {
        public long id { get; set; }
        public long code_id { get; set; }
    }

    private sealed class PartRow
    {
        public long id { get; set; }
        public int part_index { get; set; }
        public string bill_code { get; set; } = "";
    }
}
