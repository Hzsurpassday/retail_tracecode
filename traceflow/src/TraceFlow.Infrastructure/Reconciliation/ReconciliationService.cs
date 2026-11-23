using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;

namespace TraceFlow.Infrastructure.Reconciliation;

/// <summary>单次对账结果：run 主键 + 终态（COMPLETE/INCOMPLETE）+ 写入的差异条数。</summary>
public sealed record RunResult(long RunId, string Status, int DifferenceCount);

/// <summary>
/// 日对账（REC-01/02/03、AT-062/063/064）：本地上传结论 × 平台已持久化证据 × 上游完整性三方比对。
/// 平台侧只用 upload_task/upload_part/upload_attempt/code_result 的落库证据，<b>不调用 TOP</b>。
/// 差异只登记（11 类 CHECK 已由 0006 固定），不改任务终态、不触发重传；分片存在非终态时 run=INCOMPLETE，
/// 绝不把「平台统计不可得」呈现成「无差异」。
/// <para>
/// 环境口径：source_snapshot 无 env 列，文档经 tasks/verifications 归属到 org+env；RETURN 业务以任务锚定
/// 退货快照（不做无核验快照的 SOURCE_MISSING：消费退货来源尚未全量接入 G-ERP）。
/// </para>
/// </summary>
public sealed class ReconciliationService
{
    public const string JobKind = "reconciliation";
    public const string IntentPrefix = "recon:run:";

    public const string BusinessSale = "SALE";
    public const string BusinessReturn = "RETURN";

    public const string StatusRunning = "RUNNING";
    public const string StatusComplete = "COMPLETE";
    public const string StatusIncomplete = "INCOMPLETE";

    public const string DiffSourceMissing = "SOURCE_MISSING";
    public const string DiffUpstreamIncomplete = "UPSTREAM_INCOMPLETE";
    public const string DiffSourceChanged = "SOURCE_CHANGED";
    public const string DiffWaitVerify = "WAIT_VERIFY";
    public const string DiffMatchRequired = "MATCH_REQUIRED";
    public const string DiffMissingCode = "MISSING_CODE";
    public const string DiffWaitDependency = "WAIT_DEPENDENCY";
    public const string DiffAcceptanceUnknown = "ACCEPTANCE_UNKNOWN";
    public const string DiffPartialSuccess = "PARTIAL_SUCCESS";
    public const string DiffResultUnresolved = "RESULT_UNRESOLVED";
    public const string DiffPlatformMissing = "PLATFORM_MISSING";

    public const string UnavailableNonTerminalParts = "NON_TERMINAL_PARTS";

    private const int MaxWindowDays = 31;
    private const int ParamChunk = 500;
    private const int ObjectRefMaxChars = 200;
    private const int NextActionMaxChars = 200;
    private const int EvidenceMaxChars = 2000;
    private const int AuditReasonMaxChars = 200;

    /// <summary>平台统计无法定论的分片状态（PRD §9.2 非终态）：命中即 run=INCOMPLETE。</summary>
    private static readonly HashSet<string> NonTerminalPartStates = new(StringComparer.Ordinal)
    {
        "Blocked", "Ready", "Dispatching", "AcceptanceUnknown", "Processing", "RetryWait",
    };

    private static readonly HashSet<string> SnapshotIncompleteStates = new(StringComparer.Ordinal)
    {
        "DISCOVERED", "STABILIZING",
    };

    private static readonly HashSet<string> SnapshotChangedStates = new(StringComparer.Ordinal)
    {
        "SOURCE_CHANGED", "INVALID",
    };

    private static readonly HashSet<string> VerificationPendingStates = new(StringComparer.Ordinal)
    {
        "NOT_STARTED", "QUEUED", "RUNNING", "WAIT_RETRY",
    };

    private static readonly string[] TaskTypesSale = { "RETAIL_321" };
    private static readonly string[] TaskTypesReturn = { "CONSUMER_RETURN_116", "PURCHASE_RETURN_202" };

    private readonly string _cs;
    private readonly IAuditLog _audit;

    public ReconciliationService(string connectionString, IAuditLog audit)
    {
        _cs = connectionString;
        _audit = audit;
    }

    // ---------------------------------------------------------------- 触发（202）

    /// <summary>控制面触发：新连接 + 事务 Enqueue（同意图幂等）+ 人工审计（env 恒 LIVE）。</summary>
    public Task<long> EnqueueAsync(string env, DateTime windowStartUtc, DateTime windowEndUtc,
        string businessType, CancellationToken ct)
        => EnqueueAsync(env, windowStartUtc, windowEndUtc, businessType, "USER", null, ct);

    /// <summary>
    /// 带操作者/请求号的重载（brief 契约之外的最小增量：审计要能定位到具体用户与请求，
    /// 契约中的四参+ct 版本委托到这里，actor 缺省为 <c>USER</c>）。
    /// </summary>
    public async Task<long> EnqueueAsync(string env, DateTime windowStartUtc, DateTime windowEndUtc,
        string businessType, string actor, string? requestId, CancellationToken ct)
    {
        var (scope, type) = ValidateInput(env, windowStartUtc, windowEndUtc, businessType);
        var start = NormalizeUtc(windowStartUtc);
        var end = NormalizeUtc(windowEndUtc);

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var orgId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
        if (orgId is null)
            throw new AllocationValidationException("VALIDATION", "无活动机构，无法触发对账");

        var payload = JsonSerializer.Serialize(new
        {
            windowStart = Iso(start),
            windowEnd = Iso(end),
            businessType = type,
            env = scope,
        });
        var queue = new JobQueue(_cs, _audit);
        var jobId = await queue.EnqueueAsync(conn, tx, orgId.Value, scope, JobKind,
            BuildIntent(start, type), payload, null, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "recon.triggered", "outbox_job",
            jobId.ToString(CultureInfo.InvariantCulture),
            Truncate($"对账窗口 {Iso(start)} ~ {Iso(end)} 类型 {type}", AuditReasonMaxChars),
            null, payload, requestId, null, "LIVE"));
        await tx.CommitAsync(ct);
        return jobId;
    }

    // ---------------------------------------------------------------- 执行（作业）

    public async Task<RunResult> RunAsync(string env, DateTime windowStartUtc, DateTime windowEndUtc,
        string businessType, CancellationToken ct)
    {
        var (scope, type) = ValidateInput(env, windowStartUtc, windowEndUtc, businessType);
        var start = NormalizeUtc(windowStartUtc);
        var end = NormalizeUtc(windowEndUtc);

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var orgId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
        if (orgId is null)
            throw new AllocationValidationException("VALIDATION", "无活动机构，无法执行对账");

        // 1) run 先落 RUNNING（同一事务），崩溃即回滚，不留下半成品 run。
        var runId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.reconciliation_run(org_id,env,window_start,window_end,business_type,status)
VALUES(@orgId,@env,@start,@end,@businessType,N'RUNNING');
SELECT CAST(SCOPE_IDENTITY() AS bigint);",
            new { orgId = orgId.Value, env = scope, start, end, businessType = type }, tx, cancellationToken: ct));

        // 2) 本地侧集合（近纯读，全部在本事务内以一致性视图取）。
        var documents = await LoadDocumentsAsync(conn, tx, orgId.Value, scope, start, end, type, ct);
        var documentIds = documents.Select(d => d.SnapshotId).ToList();
        var verifications = await LoadVerificationsAsync(conn, tx, orgId.Value, scope, documentIds, ct);
        var tasks = await LoadTasksAsync(conn, tx, orgId.Value, scope, type, documentIds, ct);
        var taskIds = tasks.Select(t => t.TaskId).ToList();
        var parts = await LoadPartsAsync(conn, tx, taskIds, ct);
        var partIds = parts.Select(p => p.PartId).ToList();
        var queries = await LoadQueryAttemptsAsync(conn, tx, partIds, ct);
        var codeCounts = await LoadCodeCountsAsync(conn, tx, partIds, ct);
        var missingLines = await LoadMissingCodeLinesAsync(conn, tx, documentIds, ct);
        var lineCount = await CountDocumentLinesAsync(conn, tx, documentIds, ct);
        var upstreamIncomplete = await LoadIncompleteUpstreamBillsAsync(conn, tx, orgId.Value, scope, start, end, ct);

        // 3) 11 类差异。
        var diffs = new List<DifferenceRow>();
        Evaluate(diffs, type, documents, verifications, tasks, parts, queries, codeCounts, missingLines, upstreamIncomplete);

        // 4) 平台统计可得性：任一分片非终态 → INCOMPLETE（绝不显示"无差异"）。
        var unavailable = parts.Any(p => NonTerminalPartStates.Contains(p.State));
        var status = unavailable ? StatusIncomplete : StatusComplete;

        var partsByState = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            partsByState.TryGetValue(part.State, out var n);
            partsByState[part.State] = n + 1;
        }
        var succeededCodes = codeCounts.Values.Sum(c => c.Success);
        var failedCodes = codeCounts.Values.Sum(c => c.Failed);
        var unknownCodes = codeCounts.Values.Sum(c => c.Unknown);
        var localSummary = new
        {
            documents = documents.Count,
            saleLines = lineCount,
            codes = succeededCodes + failedCodes + unknownCodes,
            tasks = tasks.Count,
            parts = parts.Count,
            succeeded = succeededCodes,
            failed = failedCodes,
            partial = parts.Count(p => string.Equals(p.State, "PartialSuccess", StringComparison.Ordinal)),
            unknown = unknownCodes,
        };
        var platformSummary = new
        {
            partsByState,
            succeededParts = parts.Count(p => string.Equals(p.State, "Succeeded", StringComparison.Ordinal)),
            failedParts = parts.Count(p => string.Equals(p.State, "Failed", StringComparison.Ordinal)),
            partialParts = parts.Count(p => string.Equals(p.State, "PartialSuccess", StringComparison.Ordinal)),
            unresolvedParts = parts.Count(p => string.Equals(p.State, "ResultUnresolved", StringComparison.Ordinal)),
            unavailable,
            unavailableReason = unavailable ? UnavailableNonTerminalParts : null,
        };

        // 5) 差异写入 + run 完成 + 审计：同一事务（差异与 run 终态同生共死）。
        foreach (var diff in diffs)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.reconciliation_difference(run_id,diff_type,severity,object_ref,evidence_json,status,owner_role,next_action)
VALUES(@runId,@diffType,@severity,@objectRef,@evidenceJson,N'OPEN',@ownerRole,@nextAction)",
                new
                {
                    runId, diffType = diff.DiffType, severity = diff.Severity, objectRef = diff.ObjectRef,
                    evidenceJson = diff.EvidenceJson, ownerRole = diff.OwnerRole, nextAction = diff.NextAction,
                }, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.reconciliation_run
SET status=@status, local_summary=@localSummary, platform_summary=@platformSummary, finished_at=SYSUTCDATETIME()
WHERE id=@runId",
            new
            {
                status, localSummary = JsonSerializer.Serialize(localSummary),
                platformSummary = JsonSerializer.Serialize(platformSummary), runId,
            }, tx, cancellationToken: ct));

        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "recon.run.completed",
            "reconciliation_run", runId.ToString(CultureInfo.InvariantCulture),
            $"对账完成 status={status} differences={diffs.Count}", null,
            JsonSerializer.Serialize(new { status, differenceCount = diffs.Count }), null, null, scope));
        await tx.CommitAsync(ct);
        return new RunResult(runId, status, diffs.Count);
    }

    // ---------------------------------------------------------------- 差异规则

    private static void Evaluate(List<DifferenceRow> diffs, string businessType,
        IReadOnlyList<SnapshotRow> documents,
        IReadOnlyList<VerificationRow> verifications, IReadOnlyList<TaskRow> tasks, IReadOnlyList<PartRow> parts,
        IReadOnlyDictionary<long, QueryAggregate> queries, IReadOnlyDictionary<long, CodeAggregate> codeCounts,
        IReadOnlyList<LineRow> missingLines, IReadOnlyList<UpstreamBillRow> upstreamIncomplete)
    {
        var verificationBySnapshot = new Dictionary<long, VerificationRow>();
        foreach (var v in verifications) verificationBySnapshot[v.SnapshotId] = v;
        var failedVerificationSnapshots = new HashSet<long>(
            verifications.Where(v => string.Equals(v.State, "FAILED", StringComparison.Ordinal))
                .Select(v => v.SnapshotId));

        // SOURCE_MISSING（仅 SALE 语义：文档无当前核验）。RETURN 文档以任务锚定，返回单来源未全量接入
        // （G-ERP），无核验快照不构成本地缺失，故 RETURN 运行不产出该类型。
        if (businessType == BusinessSale)
        {
            foreach (var doc in documents)
            {
                if (verificationBySnapshot.ContainsKey(doc.SnapshotId)) continue;
                Add(diffs, DiffSourceMissing, doc.DocumentKey, new
                {
                    source = "source_snapshot", snapshotId = doc.SnapshotId, documentKey = doc.DocumentKey,
                    version = doc.Version, completeness = doc.Completeness, businessDatetime = doc.BusinessDateTime,
                });
            }
        }

        // UPSTREAM_INCOMPLETE：快照完整性未定 + 窗口内上游单据明细不完整。
        foreach (var doc in documents.Where(d => SnapshotIncompleteStates.Contains(d.Completeness)))
        {
            Add(diffs, DiffUpstreamIncomplete, doc.DocumentKey, new
            {
                source = "source_snapshot", snapshotId = doc.SnapshotId, documentKey = doc.DocumentKey,
                version = doc.Version, completeness = doc.Completeness,
            });
        }
        foreach (var bill in upstreamIncomplete)
        {
            Add(diffs, DiffUpstreamIncomplete, bill.BillNo ?? bill.PlatformBillId, new
            {
                source = "upstream_bill", billId = bill.BillId, platformBillId = bill.PlatformBillId,
                billNo = bill.BillNo, billDate = bill.BillDate, completeness = bill.Completeness,
            });
        }

        // SOURCE_CHANGED：快照标记来源已变/非法，或任务原因含 SOURCE_CHANGED。
        foreach (var doc in documents.Where(d => SnapshotChangedStates.Contains(d.Completeness)))
        {
            Add(diffs, DiffSourceChanged, doc.DocumentKey, new
            {
                source = "source_snapshot", snapshotId = doc.SnapshotId, documentKey = doc.DocumentKey,
                version = doc.Version, completeness = doc.Completeness,
            });
        }
        foreach (var task in tasks.Where(t => ContainsOrdinal(t.StateReason, "SOURCE_CHANGED")))
        {
            Add(diffs, DiffSourceChanged, task.EventKey, new
            {
                source = "upload_task", taskId = task.TaskId, taskType = task.TaskType, state = task.State,
                stateReason = task.StateReason, eventKey = task.EventKey,
            });
        }

        // WAIT_VERIFY：当前核验尚未出结论。
        foreach (var v in verifications.Where(v => VerificationPendingStates.Contains(v.State)))
        {
            var doc = documents.FirstOrDefault(d => d.SnapshotId == v.SnapshotId);
            Add(diffs, DiffWaitVerify, doc?.DocumentKey ?? SnapshotRef(v.SnapshotId), new
            {
                source = "verification", verificationId = v.VerificationId, snapshotId = v.SnapshotId,
                state = v.State, uploadTaskId = v.UploadTaskId,
            });
        }

        // MATCH_REQUIRED：核验失败，或任务未匹配且非 Blocked（同快照已有失败核验时只记一条，避免重复信号）。
        foreach (var v in verifications.Where(v => string.Equals(v.State, "FAILED", StringComparison.Ordinal)))
        {
            var doc = documents.FirstOrDefault(d => d.SnapshotId == v.SnapshotId);
            Add(diffs, DiffMatchRequired, doc?.DocumentKey ?? SnapshotRef(v.SnapshotId), new
            {
                source = "verification", verificationId = v.VerificationId, snapshotId = v.SnapshotId,
                state = v.State, uploadTaskId = v.UploadTaskId,
            });
        }
        foreach (var task in tasks.Where(t => !string.Equals(t.MatchState, "MATCHED", StringComparison.Ordinal)
                                              && !string.Equals(t.State, "Blocked", StringComparison.Ordinal)))
        {
            if (task.SnapshotId is long snapshotId && failedVerificationSnapshots.Contains(snapshotId)) continue;
            Add(diffs, DiffMatchRequired, task.EventKey, new
            {
                source = "upload_task", taskId = task.TaskId, taskType = task.TaskType, state = task.State,
                matchState = task.MatchState, eventKey = task.EventKey,
            });
        }

        // MISSING_CODE：本地销售/消费退货行缺追溯码（每行一条）。
        foreach (var line in missingLines)
        {
            Add(diffs, DiffMissingCode, line.LineKey, new
            {
                source = "source_line", lineId = line.LineId, snapshotId = line.SnapshotId,
                lineKey = line.LineKey, eventKind = line.EventKind,
            });
        }

        // WAIT_DEPENDENCY：任务在等待前序依赖。
        foreach (var task in tasks.Where(t => !string.Equals(t.DependencyState, "NONE", StringComparison.Ordinal)))
        {
            Add(diffs, DiffWaitDependency, task.EventKey, new
            {
                source = "upload_task", taskId = task.TaskId, taskType = task.TaskType, state = task.State,
                dependencyState = task.DependencyState, eventKey = task.EventKey,
            });
        }

        // ACCEPTANCE_UNKNOWN / PARTIAL_SUCCESS / RESULT_UNRESOLVED：任务级与分片级分别登记（evidence.level 注明层级）。
        foreach (var task in tasks.Where(t => string.Equals(t.State, "AcceptanceUnknown", StringComparison.Ordinal)))
        {
            Add(diffs, DiffAcceptanceUnknown, task.EventKey, new
            {
                source = "upload_task", level = "task", taskId = task.TaskId, taskType = task.TaskType,
                state = task.State, eventKey = task.EventKey,
            });
        }
        foreach (var part in parts.Where(p => string.Equals(p.State, "AcceptanceUnknown", StringComparison.Ordinal)))
        {
            Add(diffs, DiffAcceptanceUnknown, part.BillCode, new
            {
                source = "upload_part", level = "part", partId = part.PartId, taskId = part.TaskId,
                billCode = part.BillCode, state = part.State,
            });
        }
        foreach (var task in tasks.Where(t => string.Equals(t.State, "PartialSuccess", StringComparison.Ordinal)))
        {
            Add(diffs, DiffPartialSuccess, task.EventKey, new
            {
                source = "upload_task", level = "task", taskId = task.TaskId, taskType = task.TaskType,
                state = task.State, eventKey = task.EventKey,
            });
        }
        foreach (var part in parts.Where(p => string.Equals(p.State, "PartialSuccess", StringComparison.Ordinal)))
        {
            Add(diffs, DiffPartialSuccess, part.BillCode, new
            {
                source = "upload_part", level = "part", partId = part.PartId, taskId = part.TaskId,
                billCode = part.BillCode, state = part.State,
            });
        }
        foreach (var task in tasks.Where(t => string.Equals(t.State, "ResultUnresolved", StringComparison.Ordinal)))
        {
            Add(diffs, DiffResultUnresolved, task.EventKey, new
            {
                source = "upload_task", level = "task", taskId = task.TaskId, taskType = task.TaskType,
                state = task.State, eventKey = task.EventKey,
            });
        }
        foreach (var part in parts.Where(p => string.Equals(p.State, "ResultUnresolved", StringComparison.Ordinal)))
        {
            Add(diffs, DiffResultUnresolved, part.BillCode, new
            {
                source = "upload_part", level = "part", partId = part.PartId, taskId = part.TaskId,
                billCode = part.BillCode, state = part.State,
            });
        }
        foreach (var v in verifications.Where(v => string.Equals(v.State, "UNRESOLVED", StringComparison.Ordinal)))
        {
            var doc = documents.FirstOrDefault(d => d.SnapshotId == v.SnapshotId);
            Add(diffs, DiffResultUnresolved, doc?.DocumentKey ?? SnapshotRef(v.SnapshotId), new
            {
                source = "verification", level = "verification", verificationId = v.VerificationId,
                snapshotId = v.SnapshotId, state = v.State, uploadTaskId = v.UploadTaskId,
            });
        }

        // PLATFORM_MISSING：分片 Failed，但有 200 的 QUERY 尝试且该分片没有任何 SUCCESS 逐码结果。
        foreach (var part in parts.Where(p => string.Equals(p.State, "Failed", StringComparison.Ordinal)))
        {
            if (!queries.TryGetValue(part.PartId, out var query) || !query.AnyHttp200) continue;
            codeCounts.TryGetValue(part.PartId, out var codes);
            if (codes is not null && codes.Success > 0) continue;
            Add(diffs, DiffPlatformMissing, part.BillCode, new
            {
                source = "upload_part", partId = part.PartId, taskId = part.TaskId, billCode = part.BillCode,
                state = part.State, lastQueryAttemptId = query.LastAttemptId,
                lastQueryRequestId = query.LastRequestId, lastQueryHttpStatus = query.LastHttpStatus,
                successResults = codes?.Success ?? 0,
            });
        }
    }

    private static void Add(List<DifferenceRow> diffs, string diffType, string objectRef, object evidence)
    {
        var (severity, owner, nextAction) = Describe(diffType);
        var reference = string.IsNullOrWhiteSpace(objectRef) ? "(unknown)" : objectRef.Trim();
        diffs.Add(new DifferenceRow(diffType, severity, Truncate(reference, ObjectRefMaxChars),
            owner, nextAction, Truncate(JsonSerializer.Serialize(evidence), EvidenceMaxChars)));
    }

    private static (string Severity, string OwnerRole, string NextAction) Describe(string diffType) => diffType switch
    {
        DiffSourceMissing => ("HIGH", "operator", "补采/核验该销售单"),
        DiffUpstreamIncomplete => ("HIGH", "operator", "补全上游单据明细或重拉后复对"),
        DiffSourceChanged => ("HIGH", "operator", "重采最新来源版本并重新核验"),
        DiffWaitVerify => ("MEDIUM", "operator", "等待核验完成"),
        DiffMatchRequired => ("MEDIUM", "operator", "人工匹配/复核"),
        DiffMissingCode => ("HIGH", "operator", "补录追溯码"),
        DiffWaitDependency => ("MEDIUM", "operator", "等待前序采购入库核验通过"),
        DiffAcceptanceUnknown => ("HIGH", "ops", "按证据回查平台受理结论"),
        DiffPartialSuccess => ("MEDIUM", "ops", "核查部分成功分片并补齐"),
        DiffResultUnresolved => ("MEDIUM", "ops", "人工核对平台逐码结果"),
        DiffPlatformMissing => ("HIGH", "ops", "核查平台受理/回查证据，必要时人工重提"),
        _ => ("MEDIUM", "operator", "人工核对"),
    };

    // ---------------------------------------------------------------- 读取

    private const string SaleDocumentsSql = @"
;WITH latest AS (
    SELECT s.id, s.source_document_key, s.version, s.business_datetime, s.completeness_status,
           ROW_NUMBER() OVER (PARTITION BY s.source_system_id, s.source_document_key ORDER BY s.version DESC) AS rn
    FROM dbo.source_snapshot s
    WHERE s.org_id=@orgId AND s.document_type=N'SALE'
)
SELECT id AS SnapshotId, source_document_key AS DocumentKey, version AS Version,
       business_datetime AS BusinessDateTime, completeness_status AS Completeness
FROM latest
WHERE rn=1 AND business_datetime IS NOT NULL AND business_datetime>=@start AND business_datetime<@end
ORDER BY id";

    private const string ReturnDocumentsSql = @"
;WITH latest AS (
    SELECT s.id, s.source_document_key, s.version, s.business_datetime, s.completeness_status,
           ROW_NUMBER() OVER (PARTITION BY s.source_system_id, s.source_document_key ORDER BY s.version DESC) AS rn
    FROM dbo.source_snapshot s
    WHERE s.org_id=@orgId AND s.document_type=N'SALE'
), anchored AS (
    SELECT DISTINCT t.source_snapshot_id AS snapshot_id
    FROM dbo.upload_task t
    JOIN dbo.source_snapshot sn ON sn.id=t.source_snapshot_id AND sn.org_id=t.org_id
    WHERE t.org_id=@orgId AND t.env=@env
      AND t.task_type IN (N'CONSUMER_RETURN_116',N'PURCHASE_RETURN_202')
      AND sn.business_datetime IS NOT NULL AND sn.business_datetime>=@start AND sn.business_datetime<@end
)
SELECT l.id AS SnapshotId, l.source_document_key AS DocumentKey, l.version AS Version,
       l.business_datetime AS BusinessDateTime, l.completeness_status AS Completeness
FROM latest l
JOIN anchored a ON a.snapshot_id=l.id
WHERE l.rn=1
ORDER BY l.id";

    private static async Task<List<SnapshotRow>> LoadDocumentsAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, string env, DateTime start, DateTime end, string businessType, CancellationToken ct)
    {
        var sql = businessType == BusinessReturn ? ReturnDocumentsSql : SaleDocumentsSql;
        var rows = await conn.QueryAsync<SnapshotRow>(new CommandDefinition(sql,
            new { orgId, env, start, end }, tx, cancellationToken: ct));
        return rows.ToList();
    }

    private static async Task<List<VerificationRow>> LoadVerificationsAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, string env, IReadOnlyList<long> snapshotIds, CancellationToken ct)
    {
        var rows = new List<VerificationRow>();
        foreach (var chunk in Chunks(snapshotIds))
        {
            rows.AddRange(await conn.QueryAsync<VerificationRow>(new CommandDefinition(@"
SELECT id AS VerificationId, source_snapshot_id AS SnapshotId, state AS State, upload_task_id AS UploadTaskId
FROM dbo.verification
WHERE org_id=@orgId AND env=@env AND is_current=1 AND source_snapshot_id IN @ids",
                new { orgId, env, ids = chunk }, tx, cancellationToken: ct)));
        }
        return rows;
    }

    private static async Task<List<TaskRow>> LoadTasksAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, string env, string businessType, IReadOnlyList<long> snapshotIds, CancellationToken ct)
    {
        var rows = new List<TaskRow>();
        var taskTypes = businessType == BusinessReturn ? TaskTypesReturn : TaskTypesSale;
        foreach (var chunk in Chunks(snapshotIds))
        {
            rows.AddRange(await conn.QueryAsync<TaskRow>(new CommandDefinition(@"
SELECT id AS TaskId, event_key AS EventKey, task_type AS TaskType, state AS State,
       state_reason AS StateReason, match_state AS MatchState, dependency_state AS DependencyState,
       source_snapshot_id AS SnapshotId
FROM dbo.upload_task
WHERE org_id=@orgId AND env=@env AND task_type IN @taskTypes AND source_snapshot_id IN @ids",
                new { orgId, env, taskTypes, ids = chunk }, tx, cancellationToken: ct)));
        }
        return rows;
    }

    private static async Task<List<PartRow>> LoadPartsAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyList<long> taskIds, CancellationToken ct)
    {
        var rows = new List<PartRow>();
        foreach (var chunk in Chunks(taskIds))
        {
            rows.AddRange(await conn.QueryAsync<PartRow>(new CommandDefinition(@"
SELECT id AS PartId, task_id AS TaskId, part_index AS PartIndex, bill_code AS BillCode, state AS State
FROM dbo.upload_part WHERE task_id IN @ids ORDER BY id",
                new { ids = chunk }, tx, cancellationToken: ct)));
        }
        return rows;
    }

    private static async Task<Dictionary<long, QueryAggregate>> LoadQueryAttemptsAsync(SqlConnection conn,
        SqlTransaction tx, IReadOnlyList<long> partIds, CancellationToken ct)
    {
        var result = new Dictionary<long, QueryAggregate>();
        foreach (var chunk in Chunks(partIds))
        {
            var rows = await conn.QueryAsync<QueryAttemptRow>(new CommandDefinition(@"
SELECT id AS AttemptId, part_id AS PartId, attempt_no AS AttemptNo, http_status AS HttpStatus, request_id AS RequestId
FROM dbo.upload_attempt
WHERE part_id IN @ids AND attempt_kind=N'QUERY'
ORDER BY part_id, attempt_no, id",
                new { ids = chunk }, tx, cancellationToken: ct));
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.PartId, out var aggregate))
                {
                    aggregate = new QueryAggregate();
                    result[row.PartId] = aggregate;
                }
                if (row.HttpStatus == 200) aggregate.AnyHttp200 = true;
                // 行按 (part_id, attempt_no, id) 升序，最后写入的即"最后一次查询"。
                aggregate.LastAttemptId = row.AttemptId;
                aggregate.LastRequestId = row.RequestId;
                aggregate.LastHttpStatus = row.HttpStatus;
            }
        }
        return result;
    }

    private static async Task<Dictionary<long, CodeAggregate>> LoadCodeCountsAsync(SqlConnection conn,
        SqlTransaction tx, IReadOnlyList<long> partIds, CancellationToken ct)
    {
        var result = new Dictionary<long, CodeAggregate>();
        foreach (var chunk in Chunks(partIds))
        {
            var rows = await conn.QueryAsync<CodeCountRow>(new CommandDefinition(@"
SELECT part_id AS PartId, result AS Result, COUNT(*) AS Rows FROM dbo.code_result
WHERE part_id IN @ids GROUP BY part_id, result",
                new { ids = chunk }, tx, cancellationToken: ct));
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.PartId, out var aggregate))
                {
                    aggregate = new CodeAggregate();
                    result[row.PartId] = aggregate;
                }
                if (string.Equals(row.Result, "SUCCESS", StringComparison.Ordinal)) aggregate.Success += row.Rows;
                else if (string.Equals(row.Result, "FAILED", StringComparison.Ordinal)) aggregate.Failed += row.Rows;
                else if (string.Equals(row.Result, "UNKNOWN", StringComparison.Ordinal)) aggregate.Unknown += row.Rows;
            }
        }
        return result;
    }

    private static async Task<List<LineRow>> LoadMissingCodeLinesAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyList<long> snapshotIds, CancellationToken ct)
    {
        var rows = new List<LineRow>();
        foreach (var chunk in Chunks(snapshotIds))
        {
            rows.AddRange(await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id AS LineId, snapshot_id AS SnapshotId, source_line_key AS LineKey, event_kind AS EventKind
FROM dbo.source_line
WHERE snapshot_id IN @ids
  AND event_kind IN (N'SALE',N'CONSUMER_RETURN')
  AND (trace_code_normalized IS NULL OR LTRIM(RTRIM(trace_code_normalized))=N'')
ORDER BY id",
                new { ids = chunk }, tx, cancellationToken: ct)));
        }
        return rows;
    }

    private static async Task<int> CountDocumentLinesAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyList<long> snapshotIds, CancellationToken ct)
    {
        var total = 0;
        foreach (var chunk in Chunks(snapshotIds))
        {
            total += await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.source_line WHERE snapshot_id IN @ids",
                new { ids = chunk }, tx, cancellationToken: ct));
        }
        return total;
    }

    private static async Task<List<UpstreamBillRow>> LoadIncompleteUpstreamBillsAsync(SqlConnection conn,
        SqlTransaction tx, long orgId, string env, DateTime start, DateTime end, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<UpstreamBillRow>(new CommandDefinition(@"
SELECT id AS BillId, platform_bill_id AS PlatformBillId, bill_no AS BillNo, bill_date AS BillDate,
       completeness AS Completeness
FROM dbo.upstream_bill
WHERE org_id=@orgId AND env=@env AND completeness=N'INCOMPLETE'
  AND bill_date IS NOT NULL AND bill_date>=@start AND bill_date<@end
ORDER BY id", new { orgId, env, start, end }, tx, cancellationToken: ct));
        return rows.ToList();
    }

    // ---------------------------------------------------------------- 辅助

    private static (string Env, string BusinessType) ValidateInput(string env, DateTime windowStartUtc,
        DateTime windowEndUtc, string businessType)
    {
        var scope = (env ?? "").Trim().ToUpperInvariant();
        if (scope is not ("MOCK" or "LIVE"))
            throw new AllocationValidationException("VALIDATION", "env 只能为 LIVE 或 MOCK");
        var type = (businessType ?? "").Trim().ToUpperInvariant();
        if (type is not (BusinessSale or BusinessReturn))
            throw new AllocationValidationException("VALIDATION", "businessType 只能为 SALE 或 RETURN");
        if (windowEndUtc <= windowStartUtc)
            throw new AllocationValidationException("VALIDATION", "windowEnd 必须晚于 windowStart");
        if (windowEndUtc - windowStartUtc > TimeSpan.FromDays(MaxWindowDays))
            throw new AllocationValidationException("VALIDATION", "对账窗口不得超过 31 天");
        return (scope, type);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string BuildIntent(DateTime startUtc, string businessType)
        => IntentPrefix + startUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + ":" + businessType;

    private static string Iso(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string SnapshotRef(long snapshotId)
        => "snapshot:" + snapshotId.ToString(CultureInfo.InvariantCulture);

    private static bool ContainsOrdinal(string? text, string token)
        => text is not null && text.IndexOf(token, StringComparison.Ordinal) >= 0;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max);

    /// <summary>IN 列表每批 ≤500 个参数（2008 R2 参数上限 2100）；空集不产生批次。</summary>
    private static IEnumerable<long[]> Chunks(IReadOnlyList<long> ids)
    {
        for (var i = 0; i < ids.Count; i += ParamChunk)
        {
            var take = Math.Min(ParamChunk, ids.Count - i);
            var chunk = new long[take];
            for (var j = 0; j < take; j++) chunk[j] = ids[i + j];
            yield return chunk;
        }
    }

    // ---------------------------------------------------------------- 行模型

    private sealed record DifferenceRow(string DiffType, string Severity, string ObjectRef, string OwnerRole,
        string NextAction, string EvidenceJson);

    private sealed class SnapshotRow
    {
        public long SnapshotId { get; set; }
        public string DocumentKey { get; set; } = "";
        public int Version { get; set; }
        public DateTime? BusinessDateTime { get; set; }
        public string Completeness { get; set; } = "";
    }

    private sealed class VerificationRow
    {
        public long VerificationId { get; set; }
        public long SnapshotId { get; set; }
        public string State { get; set; } = "";
        public long? UploadTaskId { get; set; }
    }

    private sealed class TaskRow
    {
        public long TaskId { get; set; }
        public string EventKey { get; set; } = "";
        public string TaskType { get; set; } = "";
        public string State { get; set; } = "";
        public string? StateReason { get; set; }
        public string MatchState { get; set; } = "";
        public string DependencyState { get; set; } = "";
        public long? SnapshotId { get; set; }
    }

    private sealed class PartRow
    {
        public long PartId { get; set; }
        public long TaskId { get; set; }
        public int PartIndex { get; set; }
        public string BillCode { get; set; } = "";
        public string State { get; set; } = "";
    }

    private sealed class QueryAttemptRow
    {
        public long AttemptId { get; set; }
        public long PartId { get; set; }
        public int AttemptNo { get; set; }
        public int? HttpStatus { get; set; }
        public string? RequestId { get; set; }
    }

    private sealed class CodeCountRow
    {
        public long PartId { get; set; }
        public string Result { get; set; } = "";
        public int Rows { get; set; }
    }

    private sealed class LineRow
    {
        public long LineId { get; set; }
        public long SnapshotId { get; set; }
        public string LineKey { get; set; } = "";
        public string EventKind { get; set; } = "";
    }

    private sealed class UpstreamBillRow
    {
        public long BillId { get; set; }
        public string PlatformBillId { get; set; } = "";
        public string? BillNo { get; set; }
        public DateTime? BillDate { get; set; }
        public string Completeness { get; set; } = "";
    }

    private sealed class QueryAggregate
    {
        public bool AnyHttp200 { get; set; }
        public long LastAttemptId { get; set; }
        public string? LastRequestId { get; set; }
        public int? LastHttpStatus { get; set; }
    }

    private sealed class CodeAggregate
    {
        public int Success { get; set; }
        public int Failed { get; set; }
        public int Unknown { get; set; }
    }
}