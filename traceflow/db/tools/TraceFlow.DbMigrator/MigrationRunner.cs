using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.DbMigrator;

public sealed class MigrationException : Exception
{
    public MigrationException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record MigrationReport(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

public sealed class MigrationRunner
{
    private static readonly Regex FilePattern = new(@"^(\d{4})_.+\.sql$", RegexOptions.IgnoreCase);
    private static readonly Regex GoSplit = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private readonly string _conn;
    private readonly string _dir;

    public MigrationRunner(string connectionString, string scriptsDirectory)
    {
        _conn = connectionString;
        _dir = scriptsDirectory;
    }

    public async Task<MigrationReport> RunAsync(CancellationToken ct = default)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        await using (var conn = new SqlConnection(_conn))
        {
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(@"
IF OBJECT_ID(N'dbo.schema_version', N'U') IS NULL
CREATE TABLE dbo.schema_version (
  version int NOT NULL CONSTRAINT PK_schema_version PRIMARY KEY,
  name nvarchar(200) NOT NULL,
  checksum char(64) NOT NULL,
  applied_at datetime2(3) NOT NULL,
  success bit NOT NULL
);");
        }

        var files = Directory.GetFiles(_dir, "*.sql")
            .Where(f => FilePattern.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var version = int.Parse(name[..4]);
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));

            await using var conn = new SqlConnection(_conn);
            await conn.OpenAsync(ct);
            var existing = await conn.QuerySingleOrDefaultAsync<(string Checksum, bool Success)?>(
                "SELECT checksum AS Checksum, success AS Success FROM dbo.schema_version WHERE version=@version", new { version });
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.Checksum.Trim(), checksum, StringComparison.OrdinalIgnoreCase))
                    throw new MigrationException($"迁移 {name} 校验摘要不一致，停止启动（设计 §5.1：失败须停止，不得部分升级后继续）");
                if (!existing.Value.Success)
                    throw new MigrationException($"迁移 {name} 上次执行失败，需人工修复后重跑");
                skipped.Add(name);
                continue;
            }

            var sql = await File.ReadAllTextAsync(file, ct);
            var batches = GoSplit.Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var batch in batches)
                    await conn.ExecuteAsync(batch, transaction: tx, commandTimeout: 300);
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.schema_version(version,name,checksum,applied_at,success) VALUES(@version,@name,@checksum,SYSUTCDATETIME(),1)",
                    new { version, name, checksum }, tx);
                await tx.CommitAsync(ct);
                applied.Add(name);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.schema_version(version,name,checksum,applied_at,success) VALUES(@version,@name,@checksum,SYSUTCDATETIME(),0)",
                    new { version, name, checksum });
                throw new MigrationException($"迁移 {name} 执行失败，已回滚", ex);
            }
        }
        return new MigrationReport(applied, skipped);
    }
}
