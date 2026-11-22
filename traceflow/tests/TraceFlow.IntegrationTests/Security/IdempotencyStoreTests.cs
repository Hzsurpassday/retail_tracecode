using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Security;

public class IdempotencyStoreTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public IdempotencyStoreTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Same_key_same_hash_replays_same_key_different_hash_conflicts()
    {
        var store = new IdempotencyStore();
        await using var c = await _fx.OpenAsync();
        var key = "k-" + Guid.NewGuid();
        var hash = new string('a', 64);
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            Assert.Null(await store.TryGetAsync(c, tx, key, hash, 1, default));
            await store.SaveAsync(c, tx, key, hash, 1, 200, "{\"ok\":true}", default);
            await tx.CommitAsync();
        }
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            var hit = await store.TryGetAsync(c, tx, key, hash, 1, default);
            Assert.NotNull(hit);
            Assert.Equal(200, hit!.StatusCode);
            await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
                store.TryGetAsync(c, tx, key, new string('b', 64), 1, default));
            await tx.CommitAsync();
        }
    }

    [Fact]
    public async Task Rollback_removes_idempotency_record_with_business()
    {
        var store = new IdempotencyStore();
        await using var c = await _fx.OpenAsync();
        var key = "k-" + Guid.NewGuid();
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await store.SaveAsync(c, tx, key, new string('a', 64), 1, 200, "{}", default);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.idempotency_record WHERE idem_key=@key", new { key }));
    }
}