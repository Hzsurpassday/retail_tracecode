using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

/// <summary>
/// 人工补配与异常统一队列（spec §4.2）：聚合接入异常/对账差异/上传任务/映射冲突四类待处理项，
/// 提供统一行形状、类别详情与留证关闭。读权限 <c>trace:read</c>、解决权限 <c>system:manage</c>；
/// 匿名一律 401（授权中间件）。env 解析沿用 <c>ResolveEnv</c>（默认宿主 env，仅影响 RECON/TASK）。
/// 方言：SQL Server 2008 R2（UNION ALL + ROW_NUMBER 区间分页，无跳行取行子句）；Dapper 参数化。
/// 安全：任何原始平台响应/密钥不入响应；证据以可解析 JSON 或纯文本（≤4000）回显，截断不产生非法 JSON。
/// </summary>
public static class ExceptionsEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int SnippetChars = 200;
    private const int DetailMaxChars = 4000;
    private const int AuditReasonMaxChars = 200;
    private const int TaskAttemptLimit = 5;

    private static readonly string[] Categories = { "INGEST", "RECON", "TASK", "MAPPING" };

    public static void MapExceptionsEndpoints(this WebApplication app, string connectionString, string env)
    {
        // ---------------------------------------------------------------- 统一队列列表
        app.MapGet("/api/v1/exceptions", async (HttpContext ctx, string? category, string? status, int? page,
            int? pageSize, [FromQuery(Name = "env")] string? envFilter) =>
        {
            if (!TryPaging(page, pageSize, out var p, out var size, out var pagingError))
                return Err(ctx, 400, "VALIDATION", pagingError!);

            string? categoryFilter = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                categoryFilter = category.Trim().ToUpperInvariant();
                if (Array.IndexOf(Categories, categoryFilter) < 0)
                    return Err(ctx, 400, "VALIDATION", "category 只能为 INGEST/RECON/TASK/MAPPING");
            }
            var statusFilter = string.IsNullOrWhiteSpace(status) ? "OPEN" : status.Trim().ToUpperInvariant();
            if (statusFilter is not ("OPEN" or "RESOLVED"))
                return Err(ctx, 400, "VALIDATION", "status 只能为 OPEN/RESOLVED");
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");

            // 每类一行形状；TASK/MAPPING 只有 OPEN 语义，RESOLVED 过滤时以恒假条件自然置空。
            var inactive = statusFilter == "RESOLVED" ? " AND 1=0" : "";
            var branches = new List<string>();
            if (categoryFilter is null or "INGEST")
                branches.Add($@"
SELECT CAST(N'INGEST' AS nvarchar(16)) AS category,
       CAST(e.id AS nvarchar(64)) AS id,
       CAST(e.status AS nvarchar(16)) AS status,
       CAST(e.exception_type AS nvarchar(100)) AS title,
       CAST(ISNULL(LEFT(e.detail, {SnippetChars}), N'') AS nvarchar({SnippetChars})) AS detailSnippet,
       CAST(e.source_document_key COLLATE DATABASE_DEFAULT AS nvarchar(200)) AS objectRef,
       e.created_at AS createdAt,
       CAST(NULL AS char(4)) AS env,
       CAST(N'/imports' AS nvarchar(200)) AS deepLink
FROM dbo.ingest_exception e
WHERE e.org_id=@orgId AND e.status=@status");
            if (categoryFilter is null or "RECON")
                branches.Add($@"
SELECT CAST(N'RECON' AS nvarchar(16)) AS category,
       CAST(d.id AS nvarchar(64)) AS id,
       CAST(d.status AS nvarchar(16)) AS status,
       CAST(d.diff_type AS nvarchar(100)) AS title,
       CAST(ISNULL(LEFT(d.next_action, {SnippetChars}), N'') AS nvarchar({SnippetChars})) AS detailSnippet,
       CAST(d.object_ref AS nvarchar(200)) AS objectRef,
       d.found_at AS createdAt,
       CAST(r.env AS char(4)) AS env,
       CAST(NULL AS nvarchar(200)) AS deepLink
FROM dbo.reconciliation_difference d
JOIN dbo.reconciliation_run r ON r.id=d.run_id
WHERE r.org_id=@orgId AND r.env=@env AND d.status=@status");
            if (categoryFilter is null or "TASK")
                branches.Add($@"
SELECT CAST(N'TASK' AS nvarchar(16)) AS category,
       CAST(t.id AS nvarchar(64)) AS id,
       CAST(N'OPEN' AS nvarchar(16)) AS status,
       CAST(t.state AS nvarchar(100)) AS title,
       CAST(ISNULL(LEFT(t.state_reason, {SnippetChars}), N'') AS nvarchar({SnippetChars})) AS detailSnippet,
       CAST(t.event_key COLLATE DATABASE_DEFAULT AS nvarchar(200)) AS objectRef,
       t.created_at AS createdAt,
       CAST(t.env AS char(4)) AS env,
       CAST(N'/uploads?taskId=' + CAST(t.id AS nvarchar(20)) + N'&env=' + RTRIM(t.env) AS nvarchar(200)) AS deepLink
FROM dbo.upload_task t
WHERE t.org_id=@orgId AND t.env=@env
  AND t.state IN (N'ManualAction', N'ResultUnresolved', N'AcceptanceUnknown'){inactive}");
            if (categoryFilter is null or "MAPPING")
            {
                branches.Add($@"
SELECT CAST(N'MAPPING' AS nvarchar(16)) AS category,
       CAST(N'product:' + CAST(pm.id AS nvarchar(20)) AS nvarchar(64)) AS id,
       CAST(N'OPEN' AS nvarchar(16)) AS status,
       CAST(N'商品映射冲突' AS nvarchar(100)) AS title,
       CAST(ISNULL(LEFT(pr.name, {SnippetChars}), N'') AS nvarchar({SnippetChars})) AS detailSnippet,
       CAST(pr.spid COLLATE DATABASE_DEFAULT AS nvarchar(200)) AS objectRef,
       pr.updated_at AS createdAt,
       CAST(NULL AS char(4)) AS env,
       CAST(N'/products' AS nvarchar(200)) AS deepLink
FROM dbo.product_mapping pm
JOIN dbo.product pr ON pr.id=pm.product_id
WHERE pm.org_id=@orgId AND pm.state=N'CONFLICT'{inactive}");
                branches.Add($@"
SELECT CAST(N'MAPPING' AS nvarchar(16)) AS category,
       CAST(N'supplier:' + CAST(sm.id AS nvarchar(20)) AS nvarchar(64)) AS id,
       CAST(N'OPEN' AS nvarchar(16)) AS status,
       CAST(N'供应商映射冲突' AS nvarchar(100)) AS title,
       CAST(ISNULL(LEFT(su.name, {SnippetChars}), N'') AS nvarchar({SnippetChars})) AS detailSnippet,
       CAST(su.dwbh COLLATE DATABASE_DEFAULT AS nvarchar(200)) AS objectRef,
       CAST(NULL AS datetime2(3)) AS createdAt,
       CAST(NULL AS char(4)) AS env,
       CAST(N'/suppliers' AS nvarchar(200)) AS deepLink
FROM dbo.supplier_mapping sm
JOIN dbo.supplier su ON su.id=sm.supplier_id
WHERE sm.org_id=@orgId AND sm.state=N'CONFLICT'{inactive}");
            }
            var unionSql = string.Join("\nUNION ALL\n", branches);

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));

            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", targetEnv);
            args.Add("status", statusFilter);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) FROM ({unionSql}) u", args, cancellationToken: ctx.RequestAborted));

            // 64 位算区间：page 极大时 int 乘法会溢出为负、区间错位。
            args.Add("lo", ((long)p - 1) * size + 1);
            args.Add("hi", (long)p * size);
            // 2008 R2：两层派生表先算 ROW_NUMBER 再按区间取行（排序键含 NULL 置后）。
            var rows = (await conn.QueryAsync<QueueRow>(new CommandDefinition($@"
SELECT x.category, x.id, x.status, x.title, x.detailSnippet, x.objectRef, x.createdAt, x.env, x.deepLink
FROM (
    SELECT u.category, u.id, u.status, u.title, u.detailSnippet, u.objectRef, u.createdAt, u.env, u.deepLink,
           ROW_NUMBER() OVER (
               ORDER BY CASE WHEN u.createdAt IS NULL THEN 1 ELSE 0 END, u.createdAt DESC, u.category, u.id
           ) AS rn
    FROM ({unionSql}) u
) x
WHERE x.rn BETWEEN @lo AND @hi
ORDER BY x.rn", args, cancellationToken: ctx.RequestAborted))).ToList();

            // counts 为全量 OPEN 计数：不受分页与 status 过滤影响，但 RECON/TASK 受 env 定界。
            var counts = await conn.QuerySingleAsync<CountsRow>(new CommandDefinition(@"
SELECT
    (SELECT COUNT(*) FROM dbo.ingest_exception e
      WHERE e.org_id=@orgId AND e.status=N'OPEN') AS ingestOpen,
    (SELECT COUNT(*) FROM dbo.reconciliation_difference d
      JOIN dbo.reconciliation_run r ON r.id=d.run_id
      WHERE d.status=N'OPEN' AND r.org_id=@orgId AND r.env=@env) AS reconOpen,
    (SELECT COUNT(*) FROM dbo.upload_task t
      WHERE t.org_id=@orgId AND t.env=@env
        AND t.state IN (N'ManualAction', N'ResultUnresolved', N'AcceptanceUnknown')) AS taskOpen,
    (SELECT COUNT(*) FROM dbo.product_mapping pm
      WHERE pm.org_id=@orgId AND pm.state=N'CONFLICT')
  + (SELECT COUNT(*) FROM dbo.supplier_mapping sm
      WHERE sm.org_id=@orgId AND sm.state=N'CONFLICT') AS mappingOpen",
                new { orgId, env = targetEnv }, cancellationToken: ctx.RequestAborted));

            var items = rows.Select(r => new
            {
                r.Category, r.Id, r.Status, r.Title, r.DetailSnippet, r.ObjectRef, r.CreatedAt, r.Env, r.DeepLink,
            }).ToList();
            return Results.Ok(new
            {
                data = new
                {
                    items,
                    counts = new
                    {
                        ingestOpen = counts.IngestOpen,
                        reconOpen = counts.ReconOpen,
                        taskOpen = counts.TaskOpen,
                        mappingOpen = counts.MappingOpen,
                    },
                    page = p,
                    pageSize = size,
                    total,
                    env = targetEnv,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // ---------------------------------------------------------------- 类别详情
        app.MapGet("/api/v1/exceptions/{category}/{id}", async (HttpContext ctx, string category, string id,
            [FromQuery(Name = "env")] string? envFilter) =>
        {
            var cat = category.Trim().ToUpperInvariant();
            if (Array.IndexOf(Categories, cat) < 0)
                return Err(ctx, 400, "VALIDATION", "category 只能为 INGEST/RECON/TASK/MAPPING");

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            // RECON/TASK 详情一律按宿主 env 定界（跨 env 视为不存在）；INGEST/MAPPING 无 env。
            var targetEnv = ResolveEnv(envFilter, env);
            if (targetEnv is null) return Err(ctx, 400, "VALIDATION", "env 只能为 LIVE 或 MOCK");

            switch (cat)
            {
                case "INGEST":
                {
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var ingestId))
                        return Err(ctx, 404, "NOT_FOUND", "接入异常不存在");
                    var row = await conn.QuerySingleOrDefaultAsync<IngestRow>(new CommandDefinition(@"
SELECT e.id, e.exception_type AS exceptionType, e.source_document_key AS sourceDocumentKey,
       e.source_line_key AS sourceLineKey, e.detail, e.status,
       e.resolved_by AS resolvedBy, e.resolved_at AS resolvedAt,
       e.resolution_evidence AS resolutionEvidence, e.created_at AS createdAt
FROM dbo.ingest_exception e
WHERE e.id=@ingestId AND e.org_id=@orgId", new { ingestId, orgId }, cancellationToken: ctx.RequestAborted));
                    if (row is null) return Err(ctx, 404, "NOT_FOUND", "接入异常不存在");
                    return Results.Ok(new
                    {
                        data = new
                        {
                            item = new
                            {
                                category = "INGEST",
                                id = row.Id.ToString(CultureInfo.InvariantCulture),
                                row.Status,
                                title = row.ExceptionType,
                                detailSnippet = Cap(row.Detail, SnippetChars) ?? "",
                                objectRef = row.SourceDocumentKey,
                                createdAt = row.CreatedAt,
                                env = (string?)null,
                                deepLink = "/imports",
                            },
                            detail = new
                            {
                                row.ExceptionType,
                                row.SourceDocumentKey,
                                row.SourceLineKey,
                                detailText = Cap(row.Detail, DetailMaxChars),
                                row.Status,
                                row.ResolvedBy,
                                row.ResolvedAt,
                                resolutionEvidence = Cap(row.ResolutionEvidence, DetailMaxChars),
                            },
                        },
                        requestId = ctx.TraceIdentifier,
                    });
                }
                case "RECON":
                {
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var diffId))
                        return Err(ctx, 404, "NOT_FOUND", "对账差异不存在");
                    var row = await conn.QuerySingleOrDefaultAsync<ReconRow>(new CommandDefinition(@"
SELECT d.id, d.diff_type AS diffType, d.severity, d.object_ref AS objectRef, d.status,
       d.owner_role AS ownerRole, d.next_action AS nextAction,
       d.evidence_json AS evidenceJson, d.resolution_evidence AS resolutionEvidence,
       d.resolved_by AS resolvedBy, d.resolved_at AS resolvedAt, d.found_at AS foundAt,
       r.id AS runId, r.env, r.window_start AS windowStart, r.window_end AS windowEnd,
       r.business_type AS businessType, r.status AS runStatus, r.started_at AS startedAt,
       r.finished_at AS finishedAt
FROM dbo.reconciliation_difference d
JOIN dbo.reconciliation_run r ON r.id=d.run_id
WHERE d.id=@diffId AND r.org_id=@orgId AND r.env=@targetEnv",
                        new { diffId, orgId, targetEnv }, cancellationToken: ctx.RequestAborted));
                    if (row is null) return Err(ctx, 404, "NOT_FOUND", "对账差异不存在");
                    var evidence = ParseEvidence(row.EvidenceJson, DetailMaxChars);
                    var resolutionEvidence = ParseEvidence(row.ResolutionEvidence, DetailMaxChars);
                    return Results.Ok(new
                    {
                        data = new
                        {
                            item = new
                            {
                                category = "RECON",
                                id = row.Id.ToString(CultureInfo.InvariantCulture),
                                row.Status,
                                title = row.DiffType,
                                detailSnippet = Cap(row.NextAction, SnippetChars) ?? "",
                                objectRef = row.ObjectRef,
                                createdAt = row.FoundAt,
                                env = row.Env,
                                deepLink = (string?)null,
                            },
                            detail = new
                            {
                                row.DiffType,
                                row.Severity,
                                row.ObjectRef,
                                row.Status,
                                row.OwnerRole,
                                row.NextAction,
                                createdAt = row.FoundAt,
                                evidence = evidence.Json,
                                evidenceText = evidence.Text,
                                resolutionEvidence = resolutionEvidence.Json,
                                resolutionEvidenceText = resolutionEvidence.Text,
                                row.ResolvedBy,
                                row.ResolvedAt,
                                run = new
                                {
                                    runId = row.RunId.ToString(CultureInfo.InvariantCulture),
                                    row.Env,
                                    row.WindowStart,
                                    row.WindowEnd,
                                    row.BusinessType,
                                    status = row.RunStatus,
                                    row.StartedAt,
                                    row.FinishedAt,
                                },
                            },
                        },
                        requestId = ctx.TraceIdentifier,
                    });
                }
                case "TASK":
                {
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var taskId))
                        return Err(ctx, 404, "NOT_FOUND", "上传任务不存在");
                    var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT t.id, t.env, t.event_key AS eventKey, t.task_type AS taskType, t.state,
       t.state_reason AS stateReason, t.created_at AS createdAt, t.version
FROM dbo.upload_task t
WHERE t.id=@taskId AND t.org_id=@orgId AND t.env=@targetEnv",
                        new { taskId, orgId, targetEnv }, cancellationToken: ctx.RequestAborted));
                    if (row is null) return Err(ctx, 404, "NOT_FOUND", "上传任务不存在");
                    // 最近 N 次尝试：仅暴露诊断字段，raw_response 永不出网。
                    var attempts = (await conn.QueryAsync<AttemptRow>(new CommandDefinition($@"
SELECT TOP ({TaskAttemptLimit}) attempt_kind AS attemptKind, attempt_no AS attemptNo,
       http_status AS httpStatus, request_id AS requestId, started_at AS startedAt,
       finished_at AS finishedAt
FROM dbo.upload_attempt
WHERE task_id=@taskId
ORDER BY id DESC", new { taskId }, cancellationToken: ctx.RequestAborted))).ToList();
                    var deepLink = "/uploads?taskId=" + row.Id.ToString(CultureInfo.InvariantCulture)
                        + "&env=" + row.Env;
                    return Results.Ok(new
                    {
                        data = new
                        {
                            item = new
                            {
                                category = "TASK",
                                id = row.Id.ToString(CultureInfo.InvariantCulture),
                                status = "OPEN",
                                title = row.State,
                                detailSnippet = Cap(row.StateReason, SnippetChars) ?? "",
                                objectRef = row.EventKey,
                                createdAt = row.CreatedAt,
                                env = row.Env,
                                deepLink,
                            },
                            detail = new
                            {
                                row.State,
                                row.StateReason,
                                row.TaskType,
                                row.EventKey,
                                row.Env,
                                row.CreatedAt,
                                row.Version,
                                attempts = attempts.Select(a => new
                                {
                                    a.AttemptKind, a.AttemptNo, a.HttpStatus, a.RequestId, a.StartedAt, a.FinishedAt,
                                }),
                            },
                        },
                        requestId = ctx.TraceIdentifier,
                    });
                }
                default: // MAPPING
                {
                    if (id.StartsWith("product:", StringComparison.Ordinal))
                    {
                        if (!long.TryParse(id.Substring("product:".Length), NumberStyles.None,
                                CultureInfo.InvariantCulture, out var mappingId))
                            return Err(ctx, 404, "NOT_FOUND", "映射冲突不存在");
                        var row = await conn.QuerySingleOrDefaultAsync<ProductMappingRow>(new CommandDefinition(@"
SELECT pm.id, pm.state, pm.version, pm.evidence_json AS evidenceJson,
       pm.confirmed_by AS confirmedBy, pm.confirmed_at AS confirmedAt,
       pr.spid, pr.name AS localName, pr.display_code AS displayCode, pr.updated_at AS updatedAt,
       pd.platform_drug_id AS platformDrugId, pd.approval_no_norm AS approvalNoNorm,
       pd.manufacturer_identity AS manufacturerIdentity, pd.dosage_form AS dosageForm,
       pd.strength, pd.package_spec AS packageSpec
FROM dbo.product_mapping pm
JOIN dbo.product pr ON pr.id=pm.product_id
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE pm.id=@mappingId AND pm.org_id=@orgId AND pm.state=N'CONFLICT'",
                            new { mappingId, orgId }, cancellationToken: ctx.RequestAborted));
                        if (row is null) return Err(ctx, 404, "NOT_FOUND", "映射冲突不存在");
                        var evidence = ParseEvidence(row.EvidenceJson, DetailMaxChars);
                        return Results.Ok(new
                        {
                            data = new
                            {
                                item = new
                                {
                                    category = "MAPPING",
                                    id = "product:" + row.Id.ToString(CultureInfo.InvariantCulture),
                                    status = "OPEN",
                                    title = "商品映射冲突",
                                    detailSnippet = Cap(row.LocalName, SnippetChars) ?? "",
                                    objectRef = row.Spid,
                                    createdAt = row.UpdatedAt,
                                    env = (string?)null,
                                    deepLink = "/products",
                                },
                                detail = new
                                {
                                    side = "product",
                                    local = new { row.Spid, name = row.LocalName, row.DisplayCode },
                                    // platform_drug 无名称列：不臆造，名称返回 null。
                                    platform = new
                                    {
                                        row.PlatformDrugId,
                                        name = (string?)null,
                                        row.ApprovalNoNorm,
                                        row.ManufacturerIdentity,
                                        row.DosageForm,
                                        row.Strength,
                                        row.PackageSpec,
                                    },
                                    mapping = new { row.State, row.Version, row.ConfirmedBy, row.ConfirmedAt },
                                    evidence = evidence.Json,
                                    evidenceText = evidence.Text,
                                },
                            },
                            requestId = ctx.TraceIdentifier,
                        });
                    }
                    if (id.StartsWith("supplier:", StringComparison.Ordinal))
                    {
                        if (!long.TryParse(id.Substring("supplier:".Length), NumberStyles.None,
                                CultureInfo.InvariantCulture, out var mappingId))
                            return Err(ctx, 404, "NOT_FOUND", "映射冲突不存在");
                        var row = await conn.QuerySingleOrDefaultAsync<SupplierMappingRow>(new CommandDefinition(@"
SELECT sm.id, sm.state, sm.version, sm.evidence_json AS evidenceJson,
       su.dwbh, su.name AS localName,
       ev.ref_ent_id AS refEntId, ev.ent_id AS platformEntId, ev.name AS platformName,
       ev.credit_code AS platformCreditCode
FROM dbo.supplier_mapping sm
JOIN dbo.supplier su ON su.id=sm.supplier_id
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE sm.id=@mappingId AND sm.org_id=@orgId AND sm.state=N'CONFLICT'",
                            new { mappingId, orgId }, cancellationToken: ctx.RequestAborted));
                        if (row is null) return Err(ctx, 404, "NOT_FOUND", "映射冲突不存在");
                        var evidence = ParseEvidence(row.EvidenceJson, DetailMaxChars);
                        return Results.Ok(new
                        {
                            data = new
                            {
                                item = new
                                {
                                    category = "MAPPING",
                                    id = "supplier:" + row.Id.ToString(CultureInfo.InvariantCulture),
                                    status = "OPEN",
                                    title = "供应商映射冲突",
                                    detailSnippet = Cap(row.LocalName, SnippetChars) ?? "",
                                    objectRef = row.Dwbh,
                                    createdAt = (DateTime?)null,
                                    env = (string?)null,
                                    deepLink = "/suppliers",
                                },
                                detail = new
                                {
                                    side = "supplier",
                                    local = new { row.Dwbh, name = row.LocalName },
                                    platform = new
                                    {
                                        row.RefEntId,
                                        entId = row.PlatformEntId,
                                        name = row.PlatformName,
                                        creditCode = row.PlatformCreditCode,
                                    },
                                    mapping = new { row.State, row.Version },
                                    evidence = evidence.Json,
                                    evidenceText = evidence.Text,
                                },
                            },
                            requestId = ctx.TraceIdentifier,
                        });
                    }
                    return Err(ctx, 404, "NOT_FOUND", "映射冲突不存在");
                }
            }
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // ---------------------------------------------------------------- 留证关闭（仅 INGEST/RECON）
        app.MapPost("/api/v1/exceptions/{category}/{id}/resolve",
            (HttpContext ctx, string category, string id, IdempotencyExecutor idem, IAuditLog audit,
                [FromQuery(Name = "env")] string? envFilter) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var cat = category.Trim().ToUpperInvariant();
                if (Array.IndexOf(Categories, cat) < 0)
                    return ErrTuple(ctx, 400, "VALIDATION", "category 只能为 INGEST/RECON/TASK/MAPPING");
                // TASK/MAPPING 的处置仍在既有页面：422 并在响应中给出深链。
                if (cat is "TASK" or "MAPPING")
                {
                    var deepLink = cat == "TASK" ? TaskDeepLink(id, envFilter, env)
                        : SupplierLink(id) ? "/suppliers" : "/products";
                    return ErrTuple(ctx, 422, "NOT_RESOLVABLE_VIA_QUEUE",
                        $"该类别不支持在队列内解决，请前往 {deepLink} 处理", new[]
                        {
                            new { field = "deepLink", value = deepLink, message = "处置入口" },
                        });
                }

                var body = await ReadBodyAsync(ctx, ct);
                ResolveRequestBody? req;
                try
                {
                    req = string.IsNullOrWhiteSpace(body) ? null
                        : JsonSerializer.Deserialize<ResolveRequestBody>(body, Web);
                }
                catch (JsonException)
                {
                    return ErrTuple(ctx, 400, "BAD_REQUEST", "请求体不是合法 JSON");
                }
                var evidence = req?.ResolutionEvidence?.Trim();
                if (string.IsNullOrEmpty(evidence))
                    return ErrTuple(ctx, 422, "VALIDATION", "resolutionEvidence 必填");
                if (evidence.Length > DetailMaxChars)
                    return ErrTuple(ctx, 422, "VALIDATION", $"resolutionEvidence 不得超过 {DetailMaxChars} 字");

                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var actor = ctx.User.Identity!.Name!;
                var reason = evidence.Length <= AuditReasonMaxChars
                    ? evidence
                    : evidence.Substring(0, AuditReasonMaxChars);
                DateTime? resolvedAt;

                if (cat == "INGEST")
                {
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var ingestId))
                        return ErrTuple(ctx, 404, "NOT_FOUND", "接入异常不存在");
                    var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.ingest_exception
SET status=N'RESOLVED', resolved_by=@actor, resolved_at=SYSUTCDATETIME(), resolution_evidence=@evidence
WHERE id=@ingestId AND org_id=@orgId AND status=N'OPEN'",
                        new { ingestId, orgId, evidence, actor }, tx, cancellationToken: ct));
                    if (rows == 0)
                    {
                        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                            "SELECT COUNT(*) FROM dbo.ingest_exception WHERE id=@ingestId AND org_id=@orgId",
                            new { ingestId, orgId }, tx, cancellationToken: ct));
                        return exists == 0
                            ? ErrTuple(ctx, 404, "NOT_FOUND", "接入异常不存在")
                            : ErrTuple(ctx, 409, "ALREADY_RESOLVED", "接入异常已解决，不能重复处理");
                    }
                    resolvedAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                        "SELECT resolved_at FROM dbo.ingest_exception WHERE id=@ingestId",
                        new { ingestId }, tx, cancellationToken: ct));
                    // 控制面人工动作：审计 env 恒 LIVE；reason 为证据摘要（≤200）。
                    await audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "exception.resolved",
                        "ingest_exception", ingestId.ToString(CultureInfo.InvariantCulture), reason,
                        null, JsonSerializer.Serialize(new { status = "RESOLVED", category = "INGEST" }),
                        ctx.TraceIdentifier, null, "LIVE"));
                }
                else // RECON：与 Task 1 差异解决同语义（宿主 env 定界，跨 env 404）。
                {
                    if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var diffId))
                        return ErrTuple(ctx, 404, "NOT_FOUND", "对账差异不存在");
                    var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE d SET d.status=N'RESOLVED', d.resolution_evidence=@evidence, d.resolved_by=@actor,
       d.resolved_at=SYSUTCDATETIME()
FROM dbo.reconciliation_difference d
JOIN dbo.reconciliation_run r ON r.id=d.run_id
WHERE d.id=@diffId AND d.status=N'OPEN' AND r.org_id=@orgId AND r.env=@env",
                        new { diffId, orgId, env, evidence, actor }, tx, cancellationToken: ct));
                    if (rows == 0)
                    {
                        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.reconciliation_difference d
JOIN dbo.reconciliation_run r ON r.id=d.run_id
WHERE d.id=@diffId AND r.org_id=@orgId AND r.env=@env",
                            new { diffId, orgId, env }, tx, cancellationToken: ct));
                        return exists == 0
                            ? ErrTuple(ctx, 404, "NOT_FOUND", "对账差异不存在")
                            : ErrTuple(ctx, 409, "DIFFERENCE_ALREADY_RESOLVED", "差异已解决，不能重复处理");
                    }
                    resolvedAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                        "SELECT resolved_at FROM dbo.reconciliation_difference WHERE id=@diffId",
                        new { diffId }, tx, cancellationToken: ct));
                    await audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "exception.resolved",
                        "reconciliation_difference", diffId.ToString(CultureInfo.InvariantCulture), reason,
                        null, JsonSerializer.Serialize(new { status = "RESOLVED", category = "RECON" }),
                        ctx.TraceIdentifier, null, "LIVE"));
                }

                return (200, JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        category = cat,
                        id,
                        status = "RESOLVED",
                        resolvedAt,
                    },
                    requestId = ctx.TraceIdentifier,
                }, Web));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
    }

    // ---------------------------------------------------------------- 辅助

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static bool TryPaging(int? page, int? pageSize, out int p, out int size, out string? error)
    {
        p = 1;
        size = DefaultPageSize;
        error = null;
        if (page is int rawPage)
        {
            if (rawPage < 1) { error = "page 必须 ≥ 1"; return false; }
            p = rawPage;
        }
        if (pageSize is int rawSize)
        {
            if (rawSize < 1) { error = "pageSize 必须 ≥ 1"; return false; }
            size = Math.Min(rawSize, MaxPageSize);
        }
        return true;
    }

    private static string? ResolveEnv(string? requested, string current)
    {
        if (string.IsNullOrWhiteSpace(requested)) return current;
        var value = requested.Trim().ToUpperInvariant();
        return value is "LIVE" or "MOCK" ? value : null;
    }

    private static string? Cap(string? raw, int maxChars)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        return raw.Length <= maxChars ? raw : raw.Substring(0, maxChars);
    }

    /// <summary>
    /// 证据回显：可解析且未超长 → JSON 元素；否则以纯文本返回截断内容，
    /// 保证"截断"永远不产生非法 JSON（不臆造 JSON、不截断后再解析）。
    /// </summary>
    private static (JsonElement? Json, string? Text) ParseEvidence(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        if (raw.Length > maxChars) return (null, raw.Substring(0, maxChars));
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, raw);
        }
    }

    private static bool SupplierLink(string id) => id.StartsWith("supplier:", StringComparison.Ordinal);

    private static string TaskDeepLink(string id, string? envFilter, string hostEnv)
    {
        var targetEnv = ResolveEnv(envFilter, hostEnv) ?? hostEnv;
        return "/uploads?taskId=" + Uri.EscapeDataString(id) + "&env=" + targetEnv;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Request.EnableBuffering();
        ctx.Request.Body.Position = 0;
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        ctx.Request.Body.Position = 0;
        return body;
    }

    /// <summary>标准错误体（code/message/fieldErrors/retryable/retryAfter/requestId 六字段）。</summary>
    private static IResult Err(HttpContext ctx, int status, string code, string message)
        => Results.Json(new
        {
            code, message, fieldErrors = Array.Empty<object>(), retryable = false, retryAfter = (int?)null,
            requestId = ctx.TraceIdentifier,
        }, statusCode: status);

    private static (int Status, string Body) ErrTuple(HttpContext ctx, int status, string code, string message,
        object? fieldErrors = null)
        => (status, JsonSerializer.Serialize(new
        {
            code, message, fieldErrors = fieldErrors ?? Array.Empty<object>(), retryable = false,
            retryAfter = (int?)null, requestId = ctx.TraceIdentifier,
        }, Web));

    public sealed record ResolveRequestBody(string? ResolutionEvidence);

    private sealed class QueueRow
    {
        public string Category { get; set; } = "";
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public string Title { get; set; } = "";
        public string DetailSnippet { get; set; } = "";
        public string? ObjectRef { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? Env { get; set; }
        public string? DeepLink { get; set; }
    }

    private sealed class CountsRow
    {
        public int IngestOpen { get; set; }
        public int ReconOpen { get; set; }
        public int TaskOpen { get; set; }
        public int MappingOpen { get; set; }
    }

    private sealed class IngestRow
    {
        public long Id { get; set; }
        public string ExceptionType { get; set; } = "";
        public string SourceDocumentKey { get; set; } = "";
        public string? SourceLineKey { get; set; }
        public string? Detail { get; set; }
        public string Status { get; set; } = "";
        public string? ResolvedBy { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string? ResolutionEvidence { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ReconRow
    {
        public long Id { get; set; }
        public string DiffType { get; set; } = "";
        public string Severity { get; set; } = "";
        public string ObjectRef { get; set; } = "";
        public string Status { get; set; } = "";
        public string? OwnerRole { get; set; }
        public string? NextAction { get; set; }
        public string? EvidenceJson { get; set; }
        public string? ResolutionEvidence { get; set; }
        public string? ResolvedBy { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public DateTime FoundAt { get; set; }
        public long RunId { get; set; }
        public string Env { get; set; } = "";
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public string BusinessType { get; set; } = "";
        public string RunStatus { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    private sealed class TaskRow
    {
        public long Id { get; set; }
        public string Env { get; set; } = "";
        public string EventKey { get; set; } = "";
        public string TaskType { get; set; } = "";
        public string State { get; set; } = "";
        public string? StateReason { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Version { get; set; }
    }

    private sealed class AttemptRow
    {
        public string AttemptKind { get; set; } = "";
        public int AttemptNo { get; set; }
        public int? HttpStatus { get; set; }
        public string? RequestId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    private sealed class ProductMappingRow
    {
        public long Id { get; set; }
        public string State { get; set; } = "";
        public int Version { get; set; }
        public string? EvidenceJson { get; set; }
        public string? ConfirmedBy { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public string Spid { get; set; } = "";
        public string LocalName { get; set; } = "";
        public string? DisplayCode { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string PlatformDrugId { get; set; } = "";
        public string? ApprovalNoNorm { get; set; }
        public string? ManufacturerIdentity { get; set; }
        public string? DosageForm { get; set; }
        public string? Strength { get; set; }
        public string? PackageSpec { get; set; }
    }

    private sealed class SupplierMappingRow
    {
        public long Id { get; set; }
        public string State { get; set; } = "";
        public int Version { get; set; }
        public string? EvidenceJson { get; set; }
        public string Dwbh { get; set; } = "";
        public string LocalName { get; set; } = "";
        public string RefEntId { get; set; } = "";
        public string PlatformEntId { get; set; } = "";
        public string PlatformName { get; set; } = "";
        public string? PlatformCreditCode { get; set; }
    }
}