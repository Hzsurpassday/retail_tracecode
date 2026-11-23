using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
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
    }

    private static string Err(HttpContext ctx, string code, string message) =>
        JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier });

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
