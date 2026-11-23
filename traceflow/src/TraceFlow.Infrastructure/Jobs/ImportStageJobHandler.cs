using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Master;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>
/// import-stage：重解析 staged_content → import_row OK/ERROR + 计数。
/// 先删后插保证租约失效重跑幂等（行由 staged_content 确定性推导）。
/// ok==0（含零数据行）→ FAILED；否则 STAGED 等待人工提交。
/// </summary>
public sealed class ImportStageJobHandler : IJobHandler
{
    private readonly string _cs;
    public ImportStageJobHandler(string connectionString) => _cs = connectionString;
    public string Kind => "import-stage";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        var batchId = payload.RootElement.GetProperty("batchId").GetInt64();
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var batch = await conn.QuerySingleOrDefaultAsync<Batch>(new CommandDefinition(
            "SELECT file_kind,status,staged_content FROM dbo.import_batch WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
            new { id = batchId }, tx, cancellationToken: ct));
        if (batch is null || batch.status != "STAGED") { await tx.CommitAsync(ct); return; } // 已提交/失败：重跑无操作
        if (batch.staged_content is null) throw new InvalidOperationException($"批次 {batchId} 缺少 staged_content");

        var rows = CsvReader.Parse(batch.staged_content, ImportService.Templates[batch.file_kind]);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.import_row WHERE batch_id=@b", new { b = batchId }, tx, cancellationToken: ct));
        var ok = 0; var failed = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var error = ImportService.ValidateRow(batch.file_kind, rows[i]);
            if (error is null) ok++; else failed++;
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.import_row(batch_id,row_number,raw_json,status,error) VALUES(@b,@rn,@raw,@status,@error)",
                new { b = batchId, rn = i + 1, raw = JsonSerializer.Serialize(rows[i]),
                      status = error is null ? "OK" : "ERROR", error }, tx, cancellationToken: ct));
        }
        var status = ok == 0 ? "FAILED" : "STAGED";
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.import_batch SET total_rows=@total, success_rows=@ok, failed_rows=@failed, status=@status WHERE id=@id",
            new { id = batchId, total = rows.Count, ok, failed, status }, tx, cancellationToken: ct));
        await new AuditLog().AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "master.import.staged",
            "import_batch", batchId.ToString(), null, null,
            JsonSerializer.Serialize(new { totalRows = rows.Count, successRows = ok, failedRows = failed, status }), null, null, "LIVE"));
        await tx.CommitAsync(ct);
    }

    private sealed record Batch(string file_kind, string status, string? staged_content);
}
