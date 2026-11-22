using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TraceFlow.Application.Abstractions;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Security;

public class IdempotencyHardeningTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public IdempotencyHardeningTests(Sql2008Fixture fx) => _fx = fx;

    private static DefaultHttpContext MakeCtx(string key, string body, long userId)
    {
        var ctx = new DefaultHttpContext();
        // 执行裁定（brief MakeCtx 缺 RequestServices）：ContentHttpResult.ExecuteAsync 需 ILoggerFactory，
        // 裸 DefaultHttpContext 会抛 ArgumentNullException(provider)。补最小日志容器，不改任何断言语义。
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/v1/test";
        ctx.Request.Headers["Idempotency-Key"] = key;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "u" + userId),
        }, "test"));
        return ctx;
    }

    private static string HashOf(string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("POST|/api/v1/test||" + body)));

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Same_key_different_users_are_independent()
    {
        var store = new IdempotencyStore();
        var key = "k-" + Guid.NewGuid();
        var hash = new string('a', 64);
        await using var c = await _fx.OpenAsync();
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await store.SaveAsync(c, tx, key, hash, 1, 200, "{\"u\":1}", default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            // 用户 2 同名键：不得命中用户 1 的记录（M-1 跨用户重放关闭）
            Assert.Null(await store.TryGetAsync(c, tx, key, hash, 2, default));
            await store.SaveAsync(c, tx, key, hash, 2, 200, "{\"u\":2}", default);
            await tx.CommitAsync();
        }
        Assert.Equal(2, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.idempotency_record WHERE idem_key=@key", new { key }));
    }

    [Fact]
    public async Task Query_string_participates_in_hash()
    {
        var executor = new IdempotencyExecutor(_fx.ConnectionString, new IdempotencyStore());
        var key = "k-" + Guid.NewGuid();
        var body = "{\"x\":1}";
        var ctx1 = MakeCtx(key, body, 1);
        var r1 = await executor.RunAsync(ctx1, (conn, tx, ct) => Task.FromResult((200, "{\"first\":true}")));
        Assert.Equal(200, (await ExecuteAsync(r1, ctx1)).Status);

        var ctx2 = MakeCtx(key, body, 1);
        ctx2.Request.QueryString = new QueryString("?variant=2"); // 同键同 body 不同 query
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            executor.RunAsync(ctx2, (conn, tx, ct) => Task.FromResult((200, "{\"second\":true}"))));
    }

    [Fact]
    public async Task Concurrent_same_key_loser_rolls_back_and_replays_winner()
    {
        var executor = new IdempotencyExecutor(_fx.ConnectionString, new IdempotencyStore());
        var key = "k-" + Guid.NewGuid();
        var body = "{\"x\":1}";
        var winnerBody = "{\"winner\":true}";
        var raceUser = "race-" + Guid.NewGuid().ToString("N")[..8];
        var ctx = MakeCtx(key, body, 1);
        var result = await executor.RunAsync(ctx, async (conn, tx, ct) =>
        {
            // 模拟并发胜者已抢先提交（独立连接）：同键同用户同 hash
            await using var other = await _fx.OpenAsync();
            await other.ExecuteAsync(
                "INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body) VALUES(@k,@h,1,200,@b)",
                new { k = key, h = HashOf(body), b = winnerBody });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@u,N'race',N'x')",
                new { u = raceUser }, tx);
            return (200, "{\"loser\":true}");
        });
        var (status, respBody) = await ExecuteAsync(result, ctx);
        Assert.Equal(200, status);
        Assert.Equal(winnerBody, respBody); // 重放胜者响应，不是败者自己的
        await using var c = await _fx.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.app_user WHERE username=@u", new { u = raceUser })); // 败者业务已回滚
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.idempotency_record WHERE idem_key=@key", new { key }));
    }

    [Fact]
    public async Task Concurrent_same_key_different_hash_loser_gets_conflict_and_rolls_back()
    {
        var executor = new IdempotencyExecutor(_fx.ConnectionString, new IdempotencyStore());
        var key = "k-" + Guid.NewGuid();
        var raceUser = "race-" + Guid.NewGuid().ToString("N")[..8];
        var ctx = MakeCtx(key, "{\"x\":1}", 1);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => executor.RunAsync(ctx, async (conn, tx, ct) =>
        {
            await using var other = await _fx.OpenAsync();
            await other.ExecuteAsync(
                "INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body) VALUES(@k,@h,1,200,@b)",
                new { k = key, h = new string('b', 64), b = "{\"winner\":true}" }); // 异 hash
            await conn.ExecuteAsync(
                "INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@u,N'race',N'x')",
                new { u = raceUser }, tx);
            return (200, "{\"loser\":true}");
        }));
        await using var c = await _fx.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.app_user WHERE username=@u", new { u = raceUser }));
    }
}
