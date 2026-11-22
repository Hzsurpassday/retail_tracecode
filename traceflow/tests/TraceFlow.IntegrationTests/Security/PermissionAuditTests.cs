using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Security;

public class PermissionAuditTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public PermissionAuditTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Grant_check_revoke_and_duplicate_grant_idempotent()
    {
        await using var c = await _fx.OpenAsync();
        var userId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(N'u1',N'用户1',N'x')");
        var svc = new PermissionService(_fx.ConnectionString);
        Assert.False(await svc.HasAsync(userId, "master:match"));
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.GrantAsync(c, tx, userId, "master:match", "admin");
            await svc.GrantAsync(c, tx, userId, "master:match", "admin"); // 幂等
            await tx.CommitAsync();
        }
        Assert.True(await svc.HasAsync(userId, "master:match"));
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.RevokeAsync(c, tx, userId, "master:match", "admin");
            await tx.CommitAsync();
        }
        Assert.False(await svc.HasAsync(userId, "master:match"));
    }

    [Fact]
    public async Task Audit_append_stores_before_after_and_actor()
    {
        var log = new AuditLog();
        await using var c = await _fx.OpenAsync();
        await log.AppendAsync(c, null, new AuditEntry("admin", "User", "product-mapping.confirm",
            "product_mapping", "42", "证据充分", "{\"v\":1}", "{\"v\":2}", "req-1", null, "LIVE"));
        var row = await c.QuerySingleAsync<(string Actor, string Before, string After)>(
            "SELECT actor AS Actor, before_digest AS Before, after_digest AS After FROM dbo.audit_log WHERE object_id=N'42'");
        Assert.Equal("admin", row.Actor);
        Assert.Equal("{\"v\":1}", row.Before);
        Assert.Equal("{\"v\":2}", row.After);
    }
}