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

    // I-4：停用用户（is_active=0）即使 user_permission 授权行仍在，HasAsync/ForUserAsync 也必须 fail-closed。
    // 用户名带 Guid 后缀避免跨测试/跨运行碰撞；种子沿用 IF NOT EXISTS 幂等模式。
    [Fact]
    public async Task Deactivated_user_loses_permissions_immediately()
    {
        await using var c = await _fx.OpenAsync();
        var username = "deact-" + Guid.NewGuid().ToString("N");
        var userId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@u)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@u,N'停用测试',N'x');
SELECT id FROM dbo.app_user WHERE username=@u", new { u = username });
        var svc = new PermissionService(_fx.ConnectionString);
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.GrantAsync(c, tx, userId, "master:match", "admin");
            await tx.CommitAsync();
        }
        Assert.True(await svc.HasAsync(userId, "master:match"));
        Assert.Contains("master:match", await svc.ForUserAsync(userId));

        await c.ExecuteAsync("UPDATE dbo.app_user SET is_active=0 WHERE id=@userId", new { userId });

        Assert.False(await svc.HasAsync(userId, "master:match"));
        Assert.Empty(await svc.ForUserAsync(userId));
    }
}