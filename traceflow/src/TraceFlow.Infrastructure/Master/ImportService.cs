using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Master;
using TraceFlow.Domain.Matching;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Master;

/// <summary>
/// MD-02/03：CSV 导入暂存/预览/提交。暂存按 (org,fileKind,fileHash) 幂等；提交仅应用 OK 行。
/// MD-01：PRODUCT 更新永不触碰 trace_classification/classification_reason（分类走专用端点）。
/// ENT-02：ENTERPRISE 仅插入——企业新版本只能来自平台同步，导入不覆盖既有版本行。
/// </summary>
public sealed class ImportService
{
    private readonly SettingsService _settings;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;

    // connectionString 参数保留（DI 形态一致）但不存储：所有读写走调用方传入的 conn/tx，
    // 存储未使用的私有字段会触发 CS0414（TreatWarningsAsErrors=true）。
    public ImportService(string connectionString, SettingsService settings, IJobQueue queue, IAuditLog audit)
    { _settings = settings; _queue = queue; _audit = audit; }

    /// <summary>四种导入模板（表头须恰好覆盖这些列，顺序不限；值可空，行校验另行把关）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Templates =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["PRODUCT"] = new[] { "spid", "display_code", "barcode", "name", "approval_no", "manufacturer",
                "dosage_form", "strength", "package_spec", "unit", "trace_classification", "classification_reason", "manufacturer_identity" },
            ["SUPPLIER"] = new[] { "dwbh", "name", "credit_code", "role" },
            ["PLATFORM_DRUG"] = new[] { "platform_drug_id", "approval_no", "manufacturer_identity",
                "dosage_form", "strength", "package_spec", "source_kind" },
            ["ENTERPRISE"] = new[] { "ref_ent_id", "ent_id", "name", "credit_code", "audit_status", "role" },
        };

    public async Task<(long BatchId, long JobId)> StageAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        string fileKind, string? fileName, string fileHash, string contentBase64, string actor, CancellationToken ct)
    {
        if (!Templates.TryGetValue(fileKind, out var columns))
            throw new SettingValidationException($"未知导入类型: {fileKind}");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { throw new SettingValidationException("content 不是合法 Base64"); }

        // 上限读取走独立连接（app_setting 与本事务无锁交集，安全）
        var (maxBytes, maxRows) = await _settings.GetImportBoundsAsync(ct);
        if (bytes.Length > maxBytes)
            throw new ImportBoundsException($"文件 {bytes.Length} 字节超过上限 {maxBytes}");
        var text = Encoding.UTF8.GetString(bytes);
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try { rows = CsvReader.Parse(text, columns); }
        catch (CsvFormatException ex) { throw new SettingValidationException(ex.Message); }
        if (rows.Count > maxRows)
            throw new ImportBoundsException($"数据行 {rows.Count} 超过上限 {maxRows}");
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, fileHash, StringComparison.OrdinalIgnoreCase))
            throw new SettingValidationException("fileHash 与内容 SHA-256 不一致");
        if (fileName is not null && fileName.Length > 260)
            throw new SettingValidationException("fileName 超过 260 字符");

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingBatch>(new CommandDefinition(
            "SELECT id,status FROM dbo.import_batch WHERE org_id=@org AND file_kind=@kind AND file_hash=@hash",
            new { org = orgId, kind = fileKind, hash = actualHash }, tx, cancellationToken: ct));
        long batchId;
        if (existing is not null)
        {
            batchId = existing.id;
            if (existing.status != "STAGED") return (batchId, 0); // 已提交/失败批次不再产生作业
        }
        else
        {
            batchId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.import_batch(org_id,file_kind,file_name,file_hash,total_rows,status,staged_by,staged_content)
OUTPUT inserted.id
VALUES(@org,@kind,@name,@hash,@total,N'STAGED',@by,@content)",
                new { org = orgId, kind = fileKind, name = fileName ?? "upload.csv", hash = actualHash,
                      total = rows.Count, by = actor, content = text }, tx, cancellationToken: ct));
        }
        var jobId = await _queue.EnqueueAsync(conn, tx, orgId, env, "import-stage", $"import-stage:{batchId}",
            JsonSerializer.Serialize(new { orgId, env, batchId }), null, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.import.stage", "import_batch",
            batchId.ToString(), null, null,
            JsonSerializer.Serialize(new { fileKind, totalRows = rows.Count, fileHash = actualHash }), null, null, "LIVE"));
        return (batchId, jobId);
    }

    /// <summary>行校验（列宽与 0003 DDL 逐列一致）；null=OK。超长记 ERROR 行而非 SQL 截断异常。</summary>
    public static string? ValidateRow(string fileKind, IReadOnlyDictionary<string, string> r)
    {
        string? Req(string col, int max) =>
            r[col].Length == 0 ? $"{col} 必填" : r[col].Length > max ? $"{col} 超过 {max} 字符" : null;
        string? Opt(string col, int max) => r[col].Length > max ? $"{col} 超过 {max} 字符" : null;
        string? First(params string?[] errors) => errors.FirstOrDefault(e => e is not null);
        switch (fileKind)
        {
            case "PRODUCT":
                var cls = r["trace_classification"].Length == 0 ? "UNKNOWN" : r["trace_classification"];
                if (cls is not ("UNKNOWN" or "REQUIRED" or "EXEMPT")) return $"trace_classification 非法: {cls}";
                if (cls == "EXEMPT" && r["classification_reason"].Length == 0) return "EXEMPT 必须填写 classification_reason";
                return First(Req("spid", 64), Req("name", 200), Opt("display_code", 64), Opt("barcode", 64),
                    Opt("approval_no", 100), Opt("manufacturer", 200), Opt("manufacturer_identity", 128),
                    Opt("dosage_form", 64), Opt("strength", 100), Opt("package_spec", 100), Opt("unit", 32),
                    Opt("classification_reason", 500));
            case "SUPPLIER":
                if (r["role"].Length > 0 && r["role"] is not ("SUPPLIER" or "SHIPPER" or "OWNER" or "DELIVERER"))
                    return $"role 非法: {r["role"]}";
                return First(Req("dwbh", 64), Req("name", 200), Opt("credit_code", 64));
            case "PLATFORM_DRUG":
                return First(Req("platform_drug_id", 64), Req("source_kind", 32), Opt("approval_no", 100),
                    Opt("manufacturer_identity", 128), Opt("dosage_form", 64), Opt("strength", 100), Opt("package_spec", 100));
            case "ENTERPRISE":
                return First(Req("ref_ent_id", 64), Req("ent_id", 64), Req("name", 200),
                    Opt("credit_code", 64), Opt("audit_status", 32), Opt("role", 32));
            default:
                return $"未知导入类型: {fileKind}";
        }
    }

    public async Task<int> CommitAsync(SqlConnection conn, SqlTransaction tx, long batchId, string env,
        string expectedVersionHex, string? reason, string actor, CancellationToken ct)
    {
        var batch = await conn.QuerySingleOrDefaultAsync<BatchRow>(new CommandDefinition(
            "SELECT id,org_id,file_kind,status,ver FROM dbo.import_batch WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
            new { id = batchId }, tx, cancellationToken: ct));
        if (batch is null) throw new SettingValidationException("批次不存在");
        if (!batch.ver.SequenceEqual(RowVer.FromHex(expectedVersionHex)))
            throw new SettingVersionException("批次版本冲突，请刷新后重试");
        if (batch.status != "STAGED") throw new SettingValidationException("批次状态不是 STAGED，无法提交");

        var rows = (await conn.QueryAsync<ImportRow>(new CommandDefinition(
            "SELECT id,row_number,raw_json FROM dbo.import_row WHERE batch_id=@b AND status=N'OK' ORDER BY row_number",
            new { b = batchId }, tx, cancellationToken: ct))).ToList();
        var sysId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", null, tx, cancellationToken: ct));

        var applied = 0;
        foreach (var row in rows)
        {
            var r = JsonSerializer.Deserialize<Dictionary<string, string>>(row.raw_json)!;
            switch (batch.file_kind)
            {
                case "PRODUCT": await ApplyProductAsync(conn, tx, batch.org_id, sysId, r, ct); break;
                case "SUPPLIER": await ApplySupplierAsync(conn, tx, batch.org_id, sysId, r, ct); break;
                case "PLATFORM_DRUG": await ApplyPlatformDrugAsync(conn, tx, r, ct); break;
                case "ENTERPRISE": await ApplyEnterpriseAsync(conn, tx, r, ct); break;
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.import_row SET status=N'APPLIED' WHERE id=@id", new { id = row.id }, tx, cancellationToken: ct));
            applied++;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.import_batch SET status=N'COMMITTED', committed_by=@by, committed_at=SYSUTCDATETIME() WHERE id=@id",
            new { id = batchId, by = actor }, tx, cancellationToken: ct));
        // 提交即触发自动匹配（Task 12 消费 master-match）；同事务保证不丢
        await _queue.EnqueueAsync(conn, tx, batch.org_id, env, "master-match", $"match:import:{batchId}",
            JsonSerializer.Serialize(new { orgId = batch.org_id, env, entity = (string?)null, trigger = "import" }), null, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.import.commit", "import_batch",
            batchId.ToString(), reason, JsonSerializer.Serialize(new { status = "STAGED" }),
            JsonSerializer.Serialize(new { status = "COMMITTED", appliedRows = applied }), null, null, "LIVE"));
        return applied;
    }

    private static string? N(string v) => v.Length == 0 ? null : v;

    private static async Task ApplyProductAsync(SqlConnection conn, SqlTransaction tx, long orgId, long sysId,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        var cls = r["trace_classification"].Length == 0 ? "UNKNOWN" : r["trace_classification"];
        var rawJson = JsonSerializer.Serialize(r);
        // MD-01：UPDATE 刻意不含 trace_classification/classification_reason——分类只能走 Task 11 专用端点
        var updated = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product SET display_code=@dc, barcode=@bc, name=@name, approval_no_raw=@ar, approval_no_norm=@an,
       manufacturer_raw=@mr, manufacturer_identity=@mi, dosage_form=@df, strength=@st, package_spec=@ps, unit=@unit,
       raw_json=@raw, updated_at=SYSUTCDATETIME()
WHERE org_id=@org AND source_system_id=@sys AND spid=@spid",
            new { org = orgId, sys = sysId, spid = r["spid"], dc = N(r["display_code"]), bc = N(r["barcode"]),
                  name = r["name"], ar = N(r["approval_no"]), an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                  mr = N(r["manufacturer"]), mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]),
                  st = N(r["strength"]), ps = N(r["package_spec"]), unit = N(r["unit"]), raw = rawJson },
            tx, cancellationToken: ct));
        if (updated == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,display_code,barcode,name,approval_no_raw,approval_no_norm,
       manufacturer_raw,manufacturer_identity,dosage_form,strength,package_spec,unit,trace_classification,classification_reason,raw_json)
VALUES(@org,@sys,@spid,@dc,@bc,@name,@ar,@an,@mr,@mi,@df,@st,@ps,@unit,@cls,@cr,@raw)",
                new { org = orgId, sys = sysId, spid = r["spid"], dc = N(r["display_code"]), bc = N(r["barcode"]),
                      name = r["name"], ar = N(r["approval_no"]), an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                      mr = N(r["manufacturer"]), mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]),
                      st = N(r["strength"]), ps = N(r["package_spec"]), unit = N(r["unit"]),
                      cls, cr = N(r["classification_reason"]), raw = rawJson }, tx, cancellationToken: ct));
    }

    private static async Task ApplySupplierAsync(SqlConnection conn, SqlTransaction tx, long orgId, long sysId,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        var rawJson = JsonSerializer.Serialize(r);
        var updated = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier SET name=@name, credit_code=@cc, role=@role, raw_json=@raw
WHERE org_id=@org AND source_system_id=@sys AND dwbh=@dwbh",
            new { org = orgId, sys = sysId, dwbh = r["dwbh"], name = r["name"],
                  cc = N(r["credit_code"]), role = N(r["role"]), raw = rawJson }, tx, cancellationToken: ct));
        if (updated == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code,role,raw_json)
VALUES(@org,@sys,@dwbh,@name,@cc,@role,@raw)",
                new { org = orgId, sys = sysId, dwbh = r["dwbh"], name = r["name"],
                      cc = N(r["credit_code"]), role = N(r["role"]), raw = rawJson }, tx, cancellationToken: ct));
    }

    private static async Task ApplyPlatformDrugAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        var identityJson = JsonSerializer.Serialize(r);
        var updated = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.platform_drug SET approval_no_norm=@an, manufacturer_identity=@mi, dosage_form=@df,
       strength=@st, package_spec=@ps, identity_json=@ij, source_kind=@sk
WHERE platform_drug_id=@pdid",
            new { pdid = r["platform_drug_id"], an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                  mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]), st = N(r["strength"]),
                  ps = N(r["package_spec"]), ij = identityJson, sk = r["source_kind"] }, tx, cancellationToken: ct));
        if (updated == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.platform_drug(platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec,identity_json,source_kind)
VALUES(@pdid,@an,@mi,@df,@st,@ps,@ij,@sk)",
                new { pdid = r["platform_drug_id"], an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                      mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]), st = N(r["strength"]),
                      ps = N(r["package_spec"]), ij = identityJson, sk = r["source_kind"] }, tx, cancellationToken: ct));
    }

    private static async Task ApplyEnterpriseAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        // ENT-02：仅插入。既有 (ref_ent_id,ent_id) 版本行永不更新（改名→平台同步新版本行，M2）
        await conn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.platform_enterprise_version WHERE ref_ent_id=@ref AND ent_id=@ent)
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
VALUES(@ref,@ent,@name,@cc,@audit,@role)",
            new { @ref = r["ref_ent_id"], ent = r["ent_id"], name = r["name"],
                  cc = N(r["credit_code"]), audit = N(r["audit_status"]), role = N(r["role"]) }, tx, cancellationToken: ct));
    }

    private sealed record ExistingBatch(long id, string status);
    private sealed record BatchRow(long id, long org_id, string file_kind, string status, byte[] ver);
    private sealed record ImportRow(long id, int row_number, string raw_json);
}
