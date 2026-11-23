using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Codes;

namespace TraceFlow.Infrastructure.Codes;

public sealed record StoredCode(long Id, string Raw, string Normalized, CodeFormat Format);

public static class TraceCodeStore
{
    public static async Task<StoredCode> EnsureAsync(SqlConnection conn, SqlTransaction tx, string? raw, CancellationToken ct)
    {
        var n = TraceCodeNormalizer.Normalize(raw);
        if (n.Format == CodeFormat.Empty)
            throw new ArgumentException("空码不得入台账", nameof(raw));
        var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = n.Normalized }, tx, cancellationToken: ct));
        if (id is null)
        {
            try
            {
                id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id VALUES(@n,@raw,@fmt)",
                    new { n = n.Normalized, raw = n.Raw, fmt = CodeFormatStorage.ToToken(n.Format) }, tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627)
            {
                id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = n.Normalized }, tx, cancellationToken: ct));
            }
        }
        // Empty 已在上方抛异常；非 Empty 时 Normalized 必非 null（Normalize 契约），! 消除 CS8604（T5 偏差 2）
        return new StoredCode(id!.Value, n.Raw, n.Normalized!, n.Format);
    }
}
