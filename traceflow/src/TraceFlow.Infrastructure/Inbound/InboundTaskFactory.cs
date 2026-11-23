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

namespace TraceFlow.Infrastructure.Inbound;

public sealed record StoredCodeRef(long CodeId, string CodeNormalized);

public sealed record AllocatedLine(long AllocationId, long SourceLineId, string SourceLineKey, string ProductSourceId,
    string PlatformDrugId, string BatchNo, decimal Quantity, string Unit, IReadOnlyList<StoredCodeRef> Codes);

public sealed record InboundTaskContext(long OrgId, string Env, long SnapshotId, string DocKey, string SourceHash,
    DateTime? BusinessDateTimeUtc, string SupplierDwbh, string SupplierRefEntId, string SupplierEntId,
    IReadOnlyList<AllocatedLine> Lines, IReadOnlyList<string> MappingVersions, string CreatedBy);

public sealed record TaskCreationResult(long TaskId, long PartId, string BillCode, bool Reused);

/// <summary>
/// T2 任务工厂（AT-033/IDEM-02）：同事务创建 upload_task+part0+outbox+code_event+占用绑定。
/// R7a：Cancelled 且从未发送的任务行政性复活同一行（bill_code 不变）；
/// R7b：mapping_version_snapshot 存映射版本对的 SHA256 摘要；
/// R7c：payload_json 为内部规范化 DTO，平台参数 M3 取证后构造。
/// </summary>
public sealed class InboundTaskFactory
{
    private readonly SettingsService _settings;
    private readonly IJobQueue _queue;
    public InboundTaskFactory(SettingsService settings, IJobQueue queue)
    {
        _settings = settings;
        _queue = queue;
    }

    public async Task<TaskCreationResult> CreateAsync(SqlConnection conn, SqlTransaction tx, InboundTaskContext ctx, CancellationToken ct)
    {
        if (ctx.Lines.Count == 0)
            throw new AllocationValidationException("EMPTY_LINES", "任务至少须包含一条已分配行");
        var eventKey = $"KSOA:{ctx.DocKey}:PURCHASE_IN";
        var payloadJson = JsonSerializer.Serialize(new
        {
            eventKey,
            taskType = "PURCHASE_IN_102",
            businessDateTimeUtc = ctx.BusinessDateTimeUtc,
            supplier = new { dwbh = ctx.SupplierDwbh, refEntId = ctx.SupplierRefEntId, entId = ctx.SupplierEntId },
            lines = ctx.Lines.Select(l => new
            {
                sourceLineKey = l.SourceLineKey, productSourceId = l.ProductSourceId, platformDrugId = l.PlatformDrugId,
                batchNo = l.BatchNo, quantity = l.Quantity.ToString("0.000000", CultureInfo.InvariantCulture), unit = l.Unit,
                codes = l.Codes.Select(c => c.CodeNormalized).ToArray()
            }).ToArray()
        });
        var payloadHash = Sha(payloadJson);
        var mappingDigest = Sha(string.Join(";", ctx.MappingVersions.OrderBy(v => v, StringComparer.Ordinal)));
        var codeSetHash = SourceHasher.CodeSetHash(ctx.Lines.SelectMany(l => l.Codes.Select(c => c.CodeNormalized)).ToList());

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingRow>(new CommandDefinition(@"
SELECT id,state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE org_id=@orgId AND env=@env AND event_key=@key",
            new { orgId = ctx.OrgId, env = ctx.Env, key = eventKey }, tx, cancellationToken: ct));
        long taskId, partId;
        string billCode;
        if (existing is not null && existing.state != "Cancelled")
        {
            partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.upload_part WHERE task_id=@id AND part_index=0", new { id = existing.id }, tx, cancellationToken: ct));
            billCode = (await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = partId }, tx, cancellationToken: ct)))!;
            return new TaskCreationResult(existing.id, partId, billCode, true);
        }
        if (existing is not null)
        {
            var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
                new { id = existing.id }, tx, cancellationToken: ct));
            if (sent > 0)
                throw new AllocationConflictException("REACTIVATE_REFUSED", "Cancelled 任务已有 UPLOAD 尝试，禁止同行复活");
            taskId = existing.id;
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Ready', state_reason=N'Cancelled 任务复活（从未发送）',
       source_snapshot_id=@snap, source_hash=@sh, payload_json=@pj, payload_hash=@ph, mapping_version_snapshot=@mv,
       source_integrity=N'COMPLETE', match_state=N'COMPLETE', verification_state=N'NOT_STARTED', dependency_state=N'NONE',
       version=version+1
WHERE id=@id",
                new { id = taskId, snap = ctx.SnapshotId, sh = ctx.SourceHash, pj = payloadJson, ph = payloadHash, mv = mappingDigest },
                tx, cancellationToken: ct));
            partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.upload_part WHERE task_id=@id AND part_index=0", new { id = taskId }, tx, cancellationToken: ct));
            billCode = (await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = partId }, tx, cancellationToken: ct)))!;
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_part SET payload_json=@pj, payload_hash=@ph, code_set_hash=@csh, state=N'Ready' WHERE id=@id",
                new { id = partId, pj = payloadJson, ph = payloadHash, csh = codeSetHash }, tx, cancellationToken: ct));
            var jobId = await EnqueueDispatchAsync(conn, tx, ctx, taskId, partId, ct);
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'PENDING', attempt_count=0, next_run_at=SYSUTCDATETIME(),
       finished_at=NULL, last_error=NULL, lease_owner=NULL, lease_until=NULL
WHERE id=@id AND job_state=N'CANCELLED'", new { id = jobId }, tx, cancellationToken: ct));
        }
        else
        {
            taskId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,mapping_version_snapshot,
    payload_json,payload_hash,state,source_integrity,match_state,verification_state,dependency_state,created_by)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@key,N'PURCHASE_IN_102',@snap,@sh,@mv,@pj,@ph,N'Ready',N'COMPLETE',N'COMPLETE',N'NOT_STARTED',N'NONE',@by)",
                new { orgId = ctx.OrgId, env = ctx.Env, key = eventKey, snap = ctx.SnapshotId, sh = ctx.SourceHash,
                      mv = mappingDigest, pj = payloadJson, ph = payloadHash, by = ctx.CreatedBy }, tx, cancellationToken: ct));
            var contract = await _settings.GetBillContractAsync(ct);
            billCode = BillCodeGenerator.Generate(contract, taskId, 0); // 超长抛 InvalidOperationException，禁截断
            partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT INSERTED.id
VALUES(@taskId,@orgId,@env,0,@bill,@pj,@ph,@csh,N'Ready')",
                new { taskId, orgId = ctx.OrgId, env = ctx.Env, bill = billCode, pj = payloadJson, ph = payloadHash, csh = codeSetHash },
                tx, cancellationToken: ct));
            await EnqueueDispatchAsync(conn, tx, ctx, taskId, partId, ct);
        }
        foreach (var line in ctx.Lines)
            foreach (var code in line.Codes)
                await conn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.code_event WHERE org_id=@orgId AND env=@env AND code_id=@codeId AND event_type=N'PURCHASE_IN' AND source_line_id=@lineId)
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_snapshot_id,source_line_id,upload_task_id,business_datetime)
VALUES(@orgId,@env,@codeId,N'PURCHASE_IN',N'IN',@snap,@lineId,@taskId,@biz)",
                    new { orgId = ctx.OrgId, env = ctx.Env, codeId = code.CodeId, lineId = line.SourceLineId,
                          snap = ctx.SnapshotId, taskId, biz = ctx.BusinessDateTimeUtc }, tx, cancellationToken: ct));
        foreach (var line in ctx.Lines)
            await CodeClaimWriter.BindTaskAsync(conn, tx, line.AllocationId, taskId, ct);
        return new TaskCreationResult(taskId, partId, billCode, false);
    }

    private Task<long> EnqueueDispatchAsync(SqlConnection conn, SqlTransaction tx, InboundTaskContext ctx, long taskId, long partId, CancellationToken ct)
        => _queue.EnqueueAsync(conn, tx, ctx.OrgId, ctx.Env, "upload-dispatch", $"part:{partId}",
            JsonSerializer.Serialize(new { orgId = ctx.OrgId, env = ctx.Env, taskId, partId, trigger = "allocation" }), DateTime.UtcNow, ct);

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private sealed class ExistingRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
    }
}
