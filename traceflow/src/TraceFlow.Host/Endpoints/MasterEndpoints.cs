using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Domain.Matching;
using TraceFlow.Domain.Versioning;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Host.Endpoints;

public static class MasterEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapMasterEndpoints(this WebApplication app, string connectionString)
    {
        app.MapGet("/api/v1/products", async (HttpContext ctx, string? keyword, string? classification, string? mappingState, int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var where = new StringBuilder("WHERE p.org_id=@org");
            var par = new DynamicParameters();
            par.Add("org", orgId);
            if (!string.IsNullOrWhiteSpace(keyword))
            { where.Append(" AND (p.name LIKE @kw OR p.spid LIKE @kw OR p.display_code LIKE @kw OR p.barcode LIKE @kw)"); par.Add("kw", $"%{keyword}%"); }
            if (!string.IsNullOrWhiteSpace(classification))
            { where.Append(" AND p.trace_classification=@cls"); par.Add("cls", classification); }
            if (!string.IsNullOrWhiteSpace(mappingState))
            { where.Append(" AND ISNULL(pm.state,N'NONE')=@ms"); par.Add("ms", mappingState); }
            // 优先有效映射，否则最新一条（CANDIDATE/CONFLICT 也要可见，UI-11 确认入口）
            const string from = @"
FROM dbo.product p
OUTER APPLY (
  SELECT TOP 1 pm.id, pm.state, pm.is_locked, pm.version, pm.ver, pm.platform_drug_id
  FROM dbo.product_mapping pm WHERE pm.product_id=p.id
  ORDER BY CASE WHEN pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED') THEN 0 ELSE 1 END, pm.id DESC
) pm
LEFT JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {from} {where}", par, cancellationToken: ctx.RequestAborted));
            par.Add("lo", (p - 1) * ps + 1); par.Add("hi", p * ps);
            // brief 原文 SELECT * 会带出 rn 列，Dapper 构造器物化要求签名与结果列完全一致（InvalidOperationException）；
            // 改为显式列投影排除 rn（与 OpsEndpoints/ImportEndpoints 既有分页模式一致）
            var rows = await conn.QueryAsync<ProductRow>(new CommandDefinition($@"
SELECT id,spid,display_code,barcode,name,approval_no_raw,approval_no_norm,manufacturer_raw,manufacturer_identity,
       dosage_form,strength,package_spec,unit,trace_classification,classification_reason,updated_at,ver,
       mapping_id,mapping_state,is_locked,mapping_version,mapping_ver,platform_drug_key,p_approval_no_norm,
       p_manufacturer_identity,p_dosage_form,p_strength,p_package_spec FROM (
  SELECT p.id, p.spid, p.display_code, p.barcode, p.name, p.approval_no_raw, p.approval_no_norm,
         p.manufacturer_raw, p.manufacturer_identity, p.dosage_form, p.strength, p.package_spec, p.unit,
         p.trace_classification, p.classification_reason, p.updated_at, p.ver,
         pm.id AS mapping_id, pm.state AS mapping_state, pm.is_locked, pm.version AS mapping_version, pm.ver AS mapping_ver,
         pd.platform_drug_id AS platform_drug_key, pd.approval_no_norm AS p_approval_no_norm,
         pd.manufacturer_identity AS p_manufacturer_identity, pd.dosage_form AS p_dosage_form,
         pd.strength AS p_strength, pd.package_spec AS p_package_spec,
         ROW_NUMBER() OVER (ORDER BY p.id) AS rn
  {from} {where}) t WHERE rn BETWEEN @lo AND @hi", par, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new
            {
                data = new
                {
                    records = rows.Select(r => new
                    {
                        productId = r.id.ToString(), spid = r.spid, displayCode = r.display_code, barcode = r.barcode,
                        name = r.name, approvalNoRaw = r.approval_no_raw, approvalNoNorm = r.approval_no_norm,
                        manufacturerRaw = r.manufacturer_raw, manufacturerIdentity = r.manufacturer_identity,
                        dosageForm = r.dosage_form, strength = r.strength, packageSpec = r.package_spec, unit = r.unit,
                        traceClassification = r.trace_classification, classificationReason = r.classification_reason,
                        updatedAt = r.updated_at, version = RowVer.ToHex(r.ver),
                        mappingState = r.mapping_state ?? "NONE",
                        mapping = r.mapping_id is null ? null : new
                        {
                            mappingId = r.mapping_id.Value.ToString(), state = r.mapping_state, isLocked = r.is_locked!.Value,
                            mappingVersion = r.mapping_version!.Value, version = RowVer.ToHex(r.mapping_ver!),
                            platformDrugId = r.platform_drug_key, approvalNoNorm = r.p_approval_no_norm,
                            manufacturerIdentity = r.p_manufacturer_identity, dosageForm = r.p_dosage_form,
                            strength = r.p_strength, packageSpec = r.p_package_spec,
                        },
                    }),
                    total, page = p, pageSize = ps,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/suppliers", async (HttpContext ctx, string? keyword, string? mappingState, int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var where = new StringBuilder("WHERE s.org_id=@org");
            var par = new DynamicParameters();
            par.Add("org", orgId);
            if (!string.IsNullOrWhiteSpace(keyword))
            { where.Append(" AND (s.name LIKE @kw OR s.dwbh LIKE @kw OR s.credit_code LIKE @kw)"); par.Add("kw", $"%{keyword}%"); }
            if (!string.IsNullOrWhiteSpace(mappingState))
            { where.Append(" AND ISNULL(sm.state,N'NONE')=@ms"); par.Add("ms", mappingState); }
            const string from = @"
FROM dbo.supplier s
OUTER APPLY (
  SELECT TOP 1 sm.id, sm.state, sm.is_locked, sm.version, sm.ver, sm.enterprise_version_id
  FROM dbo.supplier_mapping sm WHERE sm.supplier_id=s.id
  ORDER BY CASE WHEN sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED') THEN 0 ELSE 1 END, sm.id DESC
) sm
LEFT JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {from} {where}", par, cancellationToken: ctx.RequestAborted));
            par.Add("lo", (p - 1) * ps + 1); par.Add("hi", p * ps);
            // 显式列投影排除 rn（同 products，Dapper 构造器物化签名约束）
            var rows = await conn.QueryAsync<SupplierRow>(new CommandDefinition($@"
SELECT id,dwbh,name,credit_code,role,ver,mapping_id,mapping_state,is_locked,mapping_version,mapping_ver,
       ref_ent_id,ent_id,platform_name,platform_credit_code,audit_status,platform_role FROM (
  SELECT s.id, s.dwbh, s.name, s.credit_code, s.role, s.ver,
         sm.id AS mapping_id, sm.state AS mapping_state, sm.is_locked, sm.version AS mapping_version, sm.ver AS mapping_ver,
         ev.ref_ent_id, ev.ent_id, ev.name AS platform_name, ev.credit_code AS platform_credit_code,
         ev.audit_status, ev.role AS platform_role,
         ROW_NUMBER() OVER (ORDER BY s.id) AS rn
  {from} {where}) t WHERE rn BETWEEN @lo AND @hi", par, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new
            {
                data = new
                {
                    records = rows.Select(r => new
                    {
                        supplierId = r.id.ToString(), dwbh = r.dwbh, name = r.name, creditCode = r.credit_code,
                        role = r.role, version = RowVer.ToHex(r.ver),
                        mappingState = r.mapping_state ?? "NONE",
                        mapping = r.mapping_id is null ? null : new
                        {
                            mappingId = r.mapping_id.Value.ToString(), state = r.mapping_state, isLocked = r.is_locked!.Value,
                            mappingVersion = r.mapping_version!.Value, version = RowVer.ToHex(r.mapping_ver!),
                            refEntId = r.ref_ent_id, entId = r.ent_id, platformName = r.platform_name,
                            creditCode = r.platform_credit_code, auditStatus = r.audit_status, role = r.platform_role,
                        },
                    }),
                    total, page = p, pageSize = ps,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // MD-01 分类专用端点：reason 必填；UPDATE 只动分类两列。
        // 诚实声明：affectedNonTerminalTasks 仅统计（M1 无任务产生路径，恒 0）；
        // 分类变更对已生成任务的失效/重阻断接线在 M2 上传管线实现。
        app.MapPost("/api/v1/products/{id:long}/trace-policy", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<TracePolicyRequest>(ctx.Request.Body, Web, ct);
                if (req is null || req.Classification is not ("REQUIRED" or "EXEMPT")
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500
                    || string.IsNullOrEmpty(req.ExpectedVersion))
                    return (422, Err(ctx, "VALIDATION", "classification 须为 REQUIRED/EXEMPT，reason 必填且不超过 500 字，expectedVersion 必填"));
                var prod = await conn.QuerySingleOrDefaultAsync<ProductPolicyRow>(new CommandDefinition(
                    "SELECT id,org_id,source_system_id,spid,trace_classification,classification_reason,ver FROM dbo.product WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
                    new { id }, tx, cancellationToken: ct));
                if (prod is null) return (404, Err(ctx, "NOT_FOUND", "商品不存在"));
                if (!prod.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("商品版本冲突，请刷新后重试");
                var before = JsonSerializer.Serialize(new { classification = prod.trace_classification, reason = prod.classification_reason });
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.product SET trace_classification=@cls, classification_reason=@reason, updated_at=SYSUTCDATETIME() WHERE id=@id",
                    new { id, cls = req.Classification, reason = req.Reason }, tx, cancellationToken: ct));
                // 经快照→来源行 spid 关联的非终态任务数（终态=Succeeded/Failed，0006 CK_task_state）
                var affected = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(DISTINCT t.id)
FROM dbo.upload_task t
JOIN dbo.source_snapshot ss ON ss.id=t.source_snapshot_id
JOIN dbo.source_line sl ON sl.snapshot_id=ss.id
WHERE ss.org_id=@org AND ss.source_system_id=@sys AND sl.product_source_id=@spid
  AND t.state NOT IN (N'Succeeded',N'Failed')",
                    new { org = prod.org_id, sys = prod.source_system_id, spid = prod.spid }, tx, cancellationToken: ct));
                var newVer = await conn.ExecuteScalarAsync<byte[]>(new CommandDefinition(
                    "SELECT ver FROM dbo.product WHERE id=@id", new { id }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "master.classify",
                    "product", id.ToString(), req.Reason, before,
                    JsonSerializer.Serialize(new { classification = req.Classification, reason = req.Reason }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        productId = id.ToString(), traceClassification = req.Classification,
                        classificationReason = req.Reason, version = RowVer.ToHex(newVer!), // brief 原文 CS8604（Dapper ExecuteScalarAsync<T> 返回 T?）；同事务 UPDLOCK 后行必存在，null 包容
                        affectedNonTerminalTasks = affected,
                    },
                    requestId = ctx.TraceIdentifier,
                }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterClassify));

        // T13 人工确认/解除（MATCH-05/AT-015）：UPDLOCK+HOLDLOCK 读-检-写；ver 不符→409 VERSION_CONFLICT；
        // 强冲突（CONFLICT）须先解决，不能直接确认→409 STRONG_CONFLICT；targetId 必须等于既有平台标识（确认不允许改绑，改绑走重新匹配/导入）→422；
        // 有效唯一竞态 2601/2627→409 MAPPING_ACTIVE_CONFLICT；解除守卫非终态上传任务→409 TASKS_IN_FLIGHT。
        app.MapPost("/api/v1/product-mappings/{id:long}/confirm", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.TargetId) || string.IsNullOrEmpty(req.ExpectedVersion)
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "targetId/expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<PMapFull>(new CommandDefinition(@"
SELECT pm.id, pm.state, pm.version, pm.ver, pd.platform_drug_id AS drug_key
FROM dbo.product_mapping pm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE pm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                if (map.state == MappingStates.Conflict)
                    return (409, Err(ctx, "STRONG_CONFLICT", "强冲突映射须先解决冲突，不能直接确认"));
                if (!string.Equals(map.drug_key, req.TargetId, StringComparison.Ordinal))
                    return (422, Err(ctx, "VALIDATION", "targetId 与既有平台药不一致——确认不允许改绑"));
                var actor = ctx.User.Identity!.Name!;
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product_mapping SET state=N'MANUAL_CONFIRMED', is_locked=1, confirmed_by=@by, confirmed_at=SYSUTCDATETIME(), version=version+1
WHERE id=@id", new { id, by = actor }, tx, cancellationToken: ct));
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    return (409, Err(ctx, "MAPPING_ACTIVE_CONFLICT", "该平台药已被其他有效映射占用（双向唯一）"));
                }
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "product_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product_mapping_history(mapping_id,product_id,platform_drug_id,state,rule_version,evidence_json,changed_by,reason)
SELECT @id,product_id,platform_drug_id,N'MANUAL_CONFIRMED',rule_version,evidence_json,@by,@reason FROM dbo.product_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.confirm", "product_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "MANUAL_CONFIRMED", targetId = req.TargetId }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "MANUAL_CONFIRMED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/supplier-mappings/{id:long}/confirm", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.TargetId) || string.IsNullOrEmpty(req.ExpectedVersion)
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "targetId/expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<SMapFull>(new CommandDefinition(@"
SELECT sm.id, sm.state, sm.version, sm.ver, ev.ent_id AS ent_key
FROM dbo.supplier_mapping sm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE sm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                if (map.state == MappingStates.Conflict)
                    return (409, Err(ctx, "STRONG_CONFLICT", "强冲突映射须先解决冲突，不能直接确认"));
                if (!string.Equals(map.ent_key, req.TargetId, StringComparison.Ordinal))
                    return (422, Err(ctx, "VALIDATION", "targetId 与既有企业版本 ent_id 不一致——确认不允许改绑"));
                var actor = ctx.User.Identity!.Name!;
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier_mapping SET state=N'MANUAL_CONFIRMED', is_locked=1, version=version+1
WHERE id=@id", new { id }, tx, cancellationToken: ct));
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    return (409, Err(ctx, "MAPPING_ACTIVE_CONFLICT", "该企业版本已被其他有效映射占用（双向唯一）"));
                }
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "supplier_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping_history(mapping_id,supplier_id,enterprise_version_id,state,evidence_json,changed_by,reason)
SELECT @id,supplier_id,enterprise_version_id,N'MANUAL_CONFIRMED',evidence_json,@by,@reason FROM dbo.supplier_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.confirm", "supplier_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "MANUAL_CONFIRMED", targetId = req.TargetId }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "MANUAL_CONFIRMED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/product-mappings/{id:long}/disable", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<DisableMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion) || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<PMapDisable>(new CommandDefinition(@"
SELECT pm.id, pm.org_id, pm.state, pm.ver, p.spid, p.source_system_id
FROM dbo.product_mapping pm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.product p ON p.id=pm.product_id
WHERE pm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                var inflight = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(DISTINCT t.id) FROM dbo.upload_task t
JOIN dbo.source_snapshot ss ON ss.id=t.source_snapshot_id
JOIN dbo.source_line sl ON sl.snapshot_id=ss.id
WHERE ss.org_id=@org AND ss.source_system_id=@sys AND sl.product_source_id=@spid AND t.state NOT IN (N'Succeeded',N'Failed')",
                    new { org = map.org_id, sys = map.source_system_id, spid = map.spid }, tx, cancellationToken: ct));
                if (inflight > 0) return (409, Err(ctx, "TASKS_IN_FLIGHT", $"存在 {inflight} 个非终态上传任务，禁止解除"));
                var actor = ctx.User.Identity!.Name!;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.product_mapping SET state=N'DISABLED', is_locked=0, version=version+1 WHERE id=@id", new { id }, tx, cancellationToken: ct));
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "product_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product_mapping_history(mapping_id,product_id,platform_drug_id,state,rule_version,evidence_json,changed_by,reason)
SELECT @id,product_id,platform_drug_id,N'DISABLED',rule_version,evidence_json,@by,@reason FROM dbo.product_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.disable", "product_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "DISABLED" }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "DISABLED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/supplier-mappings/{id:long}/disable", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<DisableMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion) || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<SMapDisable>(new CommandDefinition(
                    "SELECT id,org_id,state,ver FROM dbo.supplier_mapping WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                // M1 保守守卫：org 下任何在途采购类任务都阻断供应商解除（M2 细化到 dwbh 级关联）
                var inflight = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.upload_task WHERE org_id=@org AND task_type IN (N'PURCHASE_IN_102',N'PURCHASE_RETURN_202') AND state NOT IN (N'Succeeded',N'Failed')",
                    new { org = map.org_id }, tx, cancellationToken: ct));
                if (inflight > 0) return (409, Err(ctx, "TASKS_IN_FLIGHT", $"存在 {inflight} 个非终态采购任务，禁止解除"));
                var actor = ctx.User.Identity!.Name!;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.supplier_mapping SET state=N'DISABLED', is_locked=0, version=version+1 WHERE id=@id", new { id }, tx, cancellationToken: ct));
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "supplier_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping_history(mapping_id,supplier_id,enterprise_version_id,state,evidence_json,changed_by,reason)
SELECT @id,supplier_id,enterprise_version_id,N'DISABLED',evidence_json,@by,@reason FROM dbo.supplier_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.disable", "supplier_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "DISABLED" }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "DISABLED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));
    }

    private static string Err(HttpContext ctx, string code, string message) =>
        JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier });

    private static async Task<(string Ver, int Version)> ReadMappingVersionAsync(SqlConnection conn, SqlTransaction tx, string table, long id, CancellationToken ct)
    {
        // table 仅取本文件内两个常量字面量（product_mapping/supplier_mapping），无注入面
        var row = await conn.QuerySingleAsync<VerRow>(new CommandDefinition(
            $"SELECT ver,version FROM dbo.{table} WHERE id=@id", new { id }, tx, cancellationToken: ct));
        return (RowVer.ToHex(row.ver), row.version);
    }

    public sealed record ConfirmMappingRequest(string? TargetId, string? ExpectedVersion, string? Reason, string[]? EvidenceIds);
    public sealed record DisableMappingRequest(string? ExpectedVersion, string? Reason);
    public sealed record PMapFull(long id, string state, int version, byte[] ver, string drug_key);
    public sealed record SMapFull(long id, string state, int version, byte[] ver, string ent_key);
    public sealed record PMapDisable(long id, long org_id, string state, byte[] ver, string spid, long source_system_id);
    public sealed record SMapDisable(long id, long org_id, string state, byte[] ver);
    private sealed record VerRow(byte[] ver, int version);

    public sealed record TracePolicyRequest(string? Classification, string? Reason, string? ExpectedVersion);

    public sealed record ProductRow(long id, string spid, string? display_code, string? barcode, string name,
        string? approval_no_raw, string? approval_no_norm, string? manufacturer_raw, string? manufacturer_identity,
        string? dosage_form, string? strength, string? package_spec, string? unit,
        string trace_classification, string? classification_reason, DateTime updated_at, byte[] ver,
        long? mapping_id, string? mapping_state, bool? is_locked, int? mapping_version, byte[]? mapping_ver,
        string? platform_drug_key, string? p_approval_no_norm, string? p_manufacturer_identity,
        string? p_dosage_form, string? p_strength, string? p_package_spec);

    public sealed record SupplierRow(long id, string dwbh, string name, string? credit_code, string? role, byte[] ver,
        long? mapping_id, string? mapping_state, bool? is_locked, int? mapping_version, byte[]? mapping_ver,
        string? ref_ent_id, string? ent_id, string? platform_name, string? platform_credit_code,
        string? audit_status, string? platform_role);

    private sealed record ProductPolicyRow(long id, long org_id, long source_system_id, string spid,
        string trace_classification, string? classification_reason, byte[] ver);
}
