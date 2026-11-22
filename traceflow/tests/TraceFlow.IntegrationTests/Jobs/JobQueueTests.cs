using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Jobs;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

public class JobQueueTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public JobQueueTests(Sql2008Fixture fx) => _fx = fx;

    // 与 brief 种子代码的最小偏差（接口/实现 SQL 除参数名缺陷外保持 verbatim）：
    // 1) brief 硬编码 org_id=1，但每类独立测试库的 organization 为空（IDENTITY 未种子），直接插入触发 FK 547。
    //    沿用 Task 6/7/8 模式：幂等共享门店（IF NOT EXISTS + SELECT，0001 全库仅一个 active 门店），org_id 参数化。
    // 2) brief 各测试共用 job_kind=N'test-job'，而 ClaimAsync 按 (next_run_at,id) 取最旧 PENDING：
    //    Enqueue_same_intent_idempotent 会遗留一条 PENDING 遗留 job，后续测试的 Claim 可能抓到遗留而非本测试种子，
    //    导致 Expired_lease 测试的 Assert.Equal(first.Id, second.Id) 依执行顺序而碎。
    //    改为每测试独立 job_kind（<=nvarchar(32)），断言逻辑保持 brief 原样。
    private static string NewKind(string prefix) => prefix + Guid.NewGuid().ToString("N")[..(32 - prefix.Length)];

    private static async Task<long> SeedOrgAsync(SqlConnection c)
        => await c.ExecuteScalarAsync<long>(@"IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
                                              INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
                                            SELECT id FROM dbo.organization WHERE org_code = N'O1'");

    private async Task<long> SeedJobAsync(string kind, string intent, string env = "LIVE")
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var q = new JobQueue(_fx.ConnectionString);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var id = await q.EnqueueAsync(c, tx, orgId, env, kind, intent, null, null, default);
        await tx.CommitAsync();
        return id;
    }

    [Fact]
    public async Task Two_workers_claim_same_job_only_one_wins()
    {
        var kind = NewKind("tj-");
        var id = await SeedJobAsync(kind, "claim-race-" + Guid.NewGuid());
        var q1 = new JobQueue(_fx.ConnectionString);
        var q2 = new JobQueue(_fx.ConnectionString);
        var t1 = q1.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        var t2 = q2.ClaimAsync("w2", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        var results = await Task.WhenAll(t1, t2);
        // 两个独立连接经 Task.WhenAll 真实竞态（AT-058）；断言：同一 id 至多被一个 worker 认领
        var winners = results.Where(r => r is not null && r.Id == id).ToList();
        Assert.Single(winners);
        Assert.Equal(1, winners[0]!.FencingToken);
    }

    [Fact]
    public async Task Complete_with_correct_token_succeeds_stale_token_rejected()
    {
        var kind = NewKind("tj-");
        var id = await SeedJobAsync(kind, "complete-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var claim = await q.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        Assert.Equal(id, claim!.Id);
        Assert.False(await q.CompleteAsync(claim.Id, "w1", claim.FencingToken + 99, default)); // 旧/错 token
        Assert.True(await q.CompleteAsync(claim.Id, "w1", claim.FencingToken, default));
        Assert.False(await q.CompleteAsync(claim.Id, "w1", claim.FencingToken, default)); // 已 DONE
    }

    [Fact]
    public async Task Expired_lease_reclaimed_token_increments_old_worker_rejected()
    {
        var kind = NewKind("tj-");
        var id = await SeedJobAsync(kind, "lease-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var first = await q.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(first);
        Assert.Equal(id, first!.Id);
        await using (var c = await _fx.OpenAsync()) // 模拟租约过期（时钟前拨）
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id", new { id = first.Id });
        var recovered = await q.RecoverExpiredLeasesAsync(default);
        Assert.True(recovered >= 1);
        var second = await q.ClaimAsync("w2", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(first.FencingToken + 1, second.FencingToken);
        Assert.False(await q.CompleteAsync(first.Id, "w1", first.FencingToken, default)); // 旧 Worker 写回被拒
        Assert.True(await q.CompleteAsync(second.Id, "w2", second.FencingToken, default));
    }

    [Fact]
    public async Task Enqueue_same_intent_idempotent()
    {
        var kind = NewKind("tj-");
        var intent = "idem-" + Guid.NewGuid();
        var id1 = await SeedJobAsync(kind, intent);
        var id2 = await SeedJobAsync(kind, intent);
        Assert.Equal(id1, id2);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=@kind AND intent_key=@intent", new { kind, intent }));
    }

    [Fact]
    public async Task Reschedule_keeps_token_guard()
    {
        var kind = NewKind("tj-");
        var id = await SeedJobAsync(kind, "resched-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var claim = await q.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        Assert.Equal(id, claim!.Id);
        Assert.False(await q.RescheduleAsync(claim.Id, "wX", claim.FencingToken, DateTime.UtcNow.AddMinutes(1), "err", default));
        Assert.True(await q.RescheduleAsync(claim.Id, "w1", claim.FencingToken, DateTime.UtcNow.AddMinutes(1), "err", default));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id }));
    }

    // 以下为任务书自检清单要求的补充覆盖（brief 5 测试之外）：

    [Fact]
    public async Task Enqueue_inside_caller_transaction_rolls_back_together()
    {
        var kind = NewKind("tj-");
        var intent = "rollback-" + Guid.NewGuid();
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var q = new JobQueue(_fx.ConnectionString);
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await q.EnqueueAsync(c, tx, orgId, "LIVE", kind, intent, null, null, default);
            await tx.RollbackAsync(); // T1/T2：入队随调用方事务回滚
        }
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=@kind AND intent_key=@intent", new { kind, intent }));
    }

    [Fact]
    public async Task Claim_only_returns_jobs_of_requested_env()
    {
        var kind = NewKind("te-");
        var intent = "env-" + Guid.NewGuid();
        var id = await SeedJobAsync(kind, intent, "MOCK");
        var q = new JobQueue(_fx.ConnectionString);
        Assert.Null(await q.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default));
        var mock = await q.ClaimAsync("w1", "MOCK", new[] { kind }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(mock);
        Assert.Equal(id, mock!.Id);
        Assert.Equal(intent, mock.IntentKey);
    }

    [Fact]
    public async Task Recover_expired_leases_skips_upload_dispatch()
    {
        var kind = NewKind("tr-");
        await using var c = await _fx.OpenAsync();
        var orgId = await SeedOrgAsync(c);
        var q = new JobQueue(_fx.ConnectionString);
        long genericId, uploadId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            genericId = await q.EnqueueAsync(c, tx, orgId, "LIVE", kind, "rec-" + Guid.NewGuid(), null, null, default);
            uploadId = await q.EnqueueAsync(c, tx, orgId, "LIVE", "upload-dispatch", "rec-up-" + Guid.NewGuid(), null, null, default);
            await tx.CommitAsync();
        }
        var g = await q.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        var u = await q.ClaimAsync("w2", "LIVE", new[] { "upload-dispatch" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(g);
        Assert.NotNull(u);
        Assert.Equal(genericId, g!.Id);
        Assert.Equal(uploadId, u!.Id);
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@g OR id=@u", new { g = g.Id, u = u.Id });
        var recovered = await q.RecoverExpiredLeasesAsync(default);
        Assert.True(recovered >= 1);
        // 通用 job 失租回收为 PENDING；upload-dispatch 必须由 Task 10 发送恢复处理，不得在此重新入队（JOB-02）
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = g.Id }));
        Assert.Equal("RUNNING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = u.Id }));
        Assert.Equal("w2", await c.ExecuteScalarAsync<string>("SELECT lease_owner FROM dbo.outbox_job WHERE id=@id", new { id = u.Id }));
    }

    [Fact]
    public async Task Fail_guards_owner_token_and_running_state()
    {
        var kind = NewKind("tj-");
        var id = await SeedJobAsync(kind, "fail-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var claim = await q.ClaimAsync("w1", "LIVE", new[] { kind }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        Assert.Equal(id, claim!.Id);
        Assert.False(await q.FailAsync(claim.Id, "wX", claim.FencingToken, "err", default)); // 错 owner
        Assert.False(await q.FailAsync(claim.Id, "w1", claim.FencingToken + 99, "err", default)); // 错 token
        Assert.True(await q.FailAsync(claim.Id, "w1", claim.FencingToken, "err", default));
        Assert.False(await q.FailAsync(claim.Id, "w1", claim.FencingToken, "err", default)); // 已 FAILED，非 RUNNING
    }
}