using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TraceFlow.Application.Security;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Api;

/// <summary>
/// Task 7 上传中心 HTTP 层（UI-09）：成功率口径（最终全部成功，部分成功单列）、详情证据/脱敏、
/// 人工核对证据与一致性窗口、受控重试合同门禁（false 零写入）、未知任务 404、分页与环境过滤。
/// 种子直写 SQL；测试宿主移除后台 HostedService，确保「无写入」断言不被异步作业干扰。
/// </summary>
[SupportedOSPlatform("windows")]
public class UploadApiTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    private WebApplicationFactory<Program> _factory = null!;

    private const string AdminPassword = "Admin-Password-123";
    private const string OperatorPassword = "Oper-Password-123";
    private const string SanitizedRaw = "{\"requestId\":\"mock-1\",\"msgCode\":\"SUCCESS\",\"msg\":\"OK\"}";

    public async Task InitializeAsync()
    {
        _fx = new Sql2008Fixture();
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
            b.ConfigureServices(s => s.RemoveAll<IHostedService>());
        });
        await using var c = await _fx.OpenAsync();
        await SeedUserAsync(c, "upload-admin", "上传管理员", AdminPassword, PermissionCatalog.All);
        await SeedUserAsync(c, "upload-operator", "上传操作员", OperatorPassword, new[] { "trace:read" });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fx.DisposeAsync();
    }

    private static async Task SeedUserAsync(SqlConnection c, string username, string display, string password,
        IEnumerable<string> permissions)
    {
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@username)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@username,@display,@h);
SELECT id FROM dbo.app_user WHERE username=@username",
            new { username, display, h = hasher.HashPassword(username, password) });
        foreach (var p in permissions)
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    // ---------------------------------------------------------------- 种子

    private static string Code(int n) => "8" + n.ToString("D19");
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private sealed record Seed(long OrgId, long TaskId, long PartId, long JobId, string BillCode,
        IReadOnlyList<long> CodeIds);

    /// <summary>任务 + 分片 +（可选）UPLOAD 尝试/逐码结论/发送作业；payload 埋入唯一标记用于脱敏断言。</summary>
    private async Task<Seed> SeedUploadAsync(string tag, int codeBase, string env = "MOCK", string taskState = "Succeeded",
        string partState = "Succeeded", int successCodes = 0, int failedCodes = 0, int unknownCodes = 0,
        DateTime? firstSentAt = null, bool withJob = false, string jobState = "DONE", int jobAttempts = 3,
        bool withAttempt = false)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var eventKey = $"KSOA:{tag}:RETAIL_321";
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,payload_json,state,
    source_integrity,match_state,verification_state,dependency_state)
OUTPUT inserted.id
VALUES(@orgId,@env,@key,N'RETAIL_321',@hash,@hash,@payload,@state,
    N'COMPLETE',N'COMPLETE',N'PASSED',N'NONE')",
            new { orgId, env, key = eventKey, hash = Sha(tag), state = taskState,
                  payload = "{\"lines\":[],\"note\":\"SHOULD-NOT-LEAK\"}" });
        var billCode = "TFU" + Guid.NewGuid().ToString("N")[..10];
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state,
    first_sent_at)
OUTPUT inserted.id
VALUES(@taskId,@orgId,@env,0,@bill,N'{}',@hash,@hash,@state,@firstSentAt)",
            new { taskId, orgId, env, bill = billCode, hash = Sha(tag + ":part"), state = partState,
                  firstSentAt = firstSentAt ?? (DateTime?)null });

        var codeIds = new List<long>();
        var total = successCodes + failedCodes + unknownCodes;
        if (total > 0 || withAttempt)
        {
            var attemptId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token,started_at,finished_at,
    http_status,request_id,platform_task_id,raw_response)
OUTPUT inserted.id
VALUES(@taskId,@partId,1,N'UPLOAD',@env,1,DATEADD(minute,-30,SYSUTCDATETIME()),SYSUTCDATETIME(),200,N'mock-1',
    N'PT-1',@raw)", new { taskId, partId, env, raw = SanitizedRaw });
            var n = 0;
            for (var i = 0; i < successCodes; i++, n++)
                codeIds.Add(await InsertCodeResultAsync(c, orgId, taskId, partId, attemptId, Code(codeBase + n), "SUCCESS"));
            for (var i = 0; i < failedCodes; i++, n++)
                codeIds.Add(await InsertCodeResultAsync(c, orgId, taskId, partId, attemptId, Code(codeBase + n), "FAILED"));
            for (var i = 0; i < unknownCodes; i++, n++)
                codeIds.Add(await InsertCodeResultAsync(c, orgId, taskId, partId, attemptId, Code(codeBase + n), "UNKNOWN"));
        }

        long jobId = 0;
        if (withJob)
        {
            var queue = new TraceFlow.Infrastructure.Jobs.JobQueue(_fx.ConnectionString,
                new TraceFlow.Infrastructure.Security.AuditLog());
            await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
            {
                jobId = await queue.EnqueueAsync(c, tx, orgId, env, "upload-dispatch", $"part:{partId}",
                    JsonSerializer.Serialize(new { orgId, env, taskId, partId, trigger = "test" }), null, default);
                // 必须提交：未提交的 EnqueueAsync 会随事务析构回滚，后续「作业未被改写」断言将读到不存在的行。
                await tx.CommitAsync();
            }
            await using (var tx2 = (SqlTransaction)await c.BeginTransactionAsync())
            {
                await c.ExecuteAsync(@"
UPDATE dbo.outbox_job SET job_state=@state, attempt_count=@attempts, finished_at=SYSUTCDATETIME() WHERE id=@id",
                    new { state = jobState, attempts = jobAttempts, id = jobId }, tx2);
                await tx2.CommitAsync();
            }
        }
        return new Seed(orgId, taskId, partId, jobId, billCode, codeIds);
    }

    private static async Task<long> InsertCodeResultAsync(SqlConnection c, long orgId, long taskId, long partId,
        long attemptId, string code, string result)
    {
        var codeId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.trace_code WHERE code_normalized=@code)
    INSERT INTO dbo.trace_code(code_normalized,code_raw,format) VALUES(@code,@code,N'S8P20');
SELECT id FROM dbo.trace_code WHERE code_normalized=@code", new { code });
        await c.ExecuteAsync(@"
INSERT INTO dbo.code_result(task_id,part_id,code_id,result,reason,attempt_id)
VALUES(@taskId,@partId,@codeId,@result,@reason,@attemptId)",
            new { taskId, partId, codeId, result,
                  reason = result == "FAILED" ? "平台拒绝" : (string?)null, attemptId });
        return codeId;
    }

    // ---------------------------------------------------------------- 登录

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static void NewIdemKey(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
    }

    // ---------------------------------------------------------------- 列表/成功率

    [Fact]
    public async Task Upload_list_uses_final_success_rate_not_partial()
    {
        var succeeded = await SeedUploadAsync("L1", 100, taskState: "Succeeded", partState: "Succeeded", successCodes: 2);
        var partial = await SeedUploadAsync("L2", 200, taskState: "PartialSuccess", partState: "PartialSuccess",
            successCodes: 1, failedCodes: 1);
        await SeedUploadAsync("L3", 300, env: "LIVE", taskState: "Succeeded", partState: "Succeeded", successCodes: 1);

        var client = await LoginAsync("upload-admin", AdminPassword);
        // 列表/详情仅「登录」可读。
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync("/api/v1/upload-tasks")).StatusCode);
        var resp = await client.GetAsync("/api/v1/upload-tasks");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(2, data.GetProperty("total").GetInt32());
            var items = data.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            var succeededItem = items.Single(i => i.GetProperty("id").GetString() == succeeded.TaskId.ToString());
            Assert.Equal(2, succeededItem.GetProperty("succeededCodes").GetInt32());
            Assert.Equal(JsonValueKind.String, succeededItem.GetProperty("id").ValueKind);
            var partialItem = items.Single(i => i.GetProperty("id").GetString() == partial.TaskId.ToString());
            Assert.Equal("PartialSuccess", partialItem.GetProperty("state").GetString());
            Assert.Equal(1, partialItem.GetProperty("failedCodes").GetInt32());
            // 部分成功是独立分片计数，绝不并入 Failed（口径：只有全部失败才计 failedParts）。
            Assert.Equal(1, partialItem.GetProperty("partialSuccessParts").GetInt32());
            Assert.Equal(0, partialItem.GetProperty("failedParts").GetInt32());

            // 成功率只计最终全部成功：1/(1+0)=1，部分成功单列且不混入分子/分母。
            var summary = data.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("succeeded").GetInt32());
            Assert.Equal(0, summary.GetProperty("failed").GetInt32());
            Assert.Equal(1, summary.GetProperty("partialSuccess").GetInt32());
            Assert.Equal(1m, summary.GetProperty("finalSuccessRate").GetDecimal());
        }

        // env 过滤：LIVE 与 MOCK 分开计数（默认当前环境 MOCK）。
        var live = await client.GetAsync("/api/v1/upload-tasks?env=LIVE");
        using (var doc = JsonDocument.Parse(await live.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetProperty("total").GetInt32());
            Assert.Equal("LIVE", Assert.Single(data.GetProperty("items").EnumerateArray().ToList()).GetProperty("env").GetString());
        }
        // state/taskType 过滤。
        var filtered = await client.GetAsync("/api/v1/upload-tasks?state=PartialSuccess&taskType=RETAIL_321");
        using (var doc = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync()))
            Assert.Equal(1, doc.RootElement.GetProperty("data").GetProperty("total").GetInt32());
        // 分页口径：page<1 / pageSize<1 → 400；pageSize>100 收敛到 100。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/upload-tasks?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/upload-tasks?pageSize=0")).StatusCode);
        var clamped = await client.GetAsync("/api/v1/upload-tasks?pageSize=1000");
        using (var doc = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync()))
            Assert.Equal(100, doc.RootElement.GetProperty("data").GetProperty("pageSize").GetInt32());
    }

    // ---------------------------------------------------------------- 详情

    [Fact]
    public async Task Upload_detail_exposes_attempts_and_code_results_without_raw_secrets()
    {
        var seed = await SeedUploadAsync("D1", 400, successCodes: 1, failedCodes: 1);
        var client = await LoginAsync("upload-admin", AdminPassword);
        var resp = await client.GetAsync($"/api/v1/upload-tasks/{seed.TaskId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(seed.TaskId.ToString(), data.GetProperty("task").GetProperty("taskId").GetString());
        Assert.Equal(seed.BillCode, data.GetProperty("parts")[0].GetProperty("billCode").GetString());
        Assert.Equal("Succeeded", data.GetProperty("parts")[0].GetProperty("state").GetString());
        var attempt = Assert.Single(data.GetProperty("attempts").EnumerateArray().ToList());
        Assert.Equal(SanitizedRaw, attempt.GetProperty("rawResponse").GetString());
        Assert.Equal("UPLOAD", attempt.GetProperty("attemptKind").GetString());
        var results = data.GetProperty("codeResults").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.GetProperty("result").GetString() == "FAILED"
            && r.GetProperty("reason").GetString() == "平台拒绝");
        Assert.Equal(JsonValueKind.String, results[0].GetProperty("codeId").ValueKind);
        var kinds = data.GetProperty("timeline").EnumerateArray()
            .Select(t => t.GetProperty("kind").GetString()).ToList();
        Assert.Contains("TASK_CREATED", kinds);
        Assert.Contains("ATTEMPT_UPLOAD", kinds);
        Assert.Contains("ATTEMPT_UPLOAD_FINISHED", kinds);
        // 脱敏：payload/密钥绝不回显。
        Assert.DoesNotContain("SHOULD-NOT-LEAK", text);
        Assert.DoesNotContain("payload_json", text);
        Assert.DoesNotContain("appSecret", text);
    }

    [Fact]
    public async Task Unknown_task_is_404()
    {
        var client = await LoginAsync("upload-admin", AdminPassword);
        var detail = await client.GetAsync("/api/v1/upload-tasks/999999");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Contains("NOT_FOUND", await detail.Content.ReadAsStringAsync());
        NewIdemKey(client);
        var manual = await client.PostAsJsonAsync("/api/v1/upload-tasks/999999/manual-verify", new
        {
            decision = "ACCEPTED",
            evidence = new { platformBillCode = "TF-NONE" },
        });
        Assert.Equal(HttpStatusCode.NotFound, manual.StatusCode);
    }

    // ---------------------------------------------------------------- 人工核对

    [Fact]
    public async Task Manual_verify_ACCEPTED_requires_platform_evidence()
    {
        var seed = await SeedUploadAsync("M1", 500, taskState: "ManualAction", partState: "ManualAction",
            firstSentAt: DateTime.UtcNow.AddHours(-2));
        var client = await LoginAsync("upload-admin", AdminPassword);

        NewIdemKey(client);
        var noEvidence = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/manual-verify", new
        {
            decision = "ACCEPTED",
            evidence = new { },
            expectedVersion = 1,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noEvidence.StatusCode);
        Assert.Contains("EVIDENCE_REQUIRED", await noEvidence.Content.ReadAsStringAsync());

        NewIdemKey(client);
        var mismatch = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/manual-verify", new
        {
            decision = "ACCEPTED",
            evidence = new { platformBillCode = "TF-NOT-MINE" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Contains("EVIDENCE_MISMATCH", await mismatch.Content.ReadAsStringAsync());

        // 拒绝路径零写入：分片仍 ManualAction、无审计。
        await using (var c = await _fx.OpenAsync())
        {
            Assert.Equal("ManualAction", await c.ExecuteScalarAsync<string>(
                "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.upload.manual_verified'"));
        }

        NewIdemKey(client);
        var accepted = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/manual-verify", new
        {
            decision = "ACCEPTED",
            evidence = new { platformBillCode = seed.BillCode, contractEvidence = new { contractNo = "C-2026-1" } },
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var doc = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("Accepted", data.GetProperty("state").GetString());
        Assert.Equal("ACCEPTED", data.GetProperty("decision").GetString());
        await using (var c = await _fx.OpenAsync())
        {
            Assert.Equal("Accepted", await c.ExecuteScalarAsync<string>(
                "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
            Assert.NotNull(await c.ExecuteScalarAsync<DateTime?>(
                "SELECT next_query_at FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.upload.manual_verified' AND env=N'MOCK'"));
        }
    }

    [Fact]
    public async Task Manual_verify_rejects_NOT_ACCEPTED_before_consistency_window()
    {
        // 首次发送就在此刻 → 一致性窗口（默认 600s）未过，禁止判定平台未受理。
        var seed = await SeedUploadAsync("M2", 600, taskState: "ManualAction", partState: "ManualAction",
            firstSentAt: DateTime.UtcNow.AddSeconds(-5));
        var client = await LoginAsync("upload-admin", AdminPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/manual-verify", new
        {
            decision = "NOT_ACCEPTED",
            evidence = new { contractEvidence = new { contractNo = "C-1", signedAt = "2026-11-20" } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("CONSISTENCY_WINDOW_NOT_MET", await resp.Content.ReadAsStringAsync());
        await using var c = await _fx.OpenAsync();
        Assert.Equal("ManualAction", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.upload.manual_verified'"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.idempotency_record"));
    }

    [Fact]
    public async Task Manual_verify_rejects_non_ManualAction_task()
    {
        var seed = await SeedUploadAsync("M3", 700, taskState: "Succeeded", partState: "Succeeded", successCodes: 1);
        var client = await LoginAsync("upload-admin", AdminPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/manual-verify", new
        {
            decision = "ACCEPTED",
            evidence = new { platformBillCode = seed.BillCode },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("NOT_MANUAL_ACTION", await resp.Content.ReadAsStringAsync());
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Succeeded", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.upload.manual_verified'"));
    }

    // ---------------------------------------------------------------- 受控重试

    [Fact]
    public async Task Controlled_retry_disabled_returns_422_RETRY_PATH_NOT_CERTIFIED()
    {
        var seed = await SeedUploadAsync("R1", 800, taskState: "Failed", partState: "Failed", failedCodes: 1,
            withJob: true, jobState: "DONE", jobAttempts: 3);
        var client = await LoginAsync("upload-admin", AdminPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/controlled-retry", new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("RETRY_PATH_NOT_CERTIFIED", await resp.Content.ReadAsStringAsync());
        await using var c = await _fx.OpenAsync();
        // 拒绝即零写入：任务/分片/作业/尝试/审计/幂等记录全部不变。
        Assert.Equal("Failed", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_task WHERE id=@id", new { id = seed.TaskId }));
        Assert.Equal("Failed", await c.ExecuteScalarAsync<string>(
            "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
        Assert.Equal("DONE", await c.ExecuteScalarAsync<string>(
            "SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(
            "SELECT attempt_count FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id", new { id = seed.TaskId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.upload.controlled_retry'"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.idempotency_record"));
    }

    [Fact]
    public async Task Controlled_retry_enabled_transitions_and_resets_dispatch_job()
    {
        await using (var c = await _fx.OpenAsync())
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_setting WHERE setting_key=N'top.retryPathCertified')
    INSERT INTO dbo.app_setting(setting_key,value_json,updated_by) VALUES(N'top.retryPathCertified',N'true',N'test');
ELSE UPDATE dbo.app_setting SET value_json=N'true', updated_by=N'test' WHERE setting_key=N'top.retryPathCertified'");
        var seed = await SeedUploadAsync("R2", 900, taskState: "Failed", partState: "Failed", failedCodes: 1,
            withJob: true, jobState: "DONE", jobAttempts: 3);
        var client = await LoginAsync("upload-admin", AdminPassword);
        NewIdemKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/controlled-retry", new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            // 任务级聚合：分片 RetryWait 属不可结算态 → 任务归一为 Dispatching（仍在推进，不得提前落定）；
            // 受控重试本身的语义断言在下方（分片 Failed → RetryWait + 作业复位）。
            Assert.Equal("Dispatching", data.GetProperty("state").GetString());
            Assert.True(data.GetProperty("retryAllowed").GetBoolean());
            Assert.Equal(1, data.GetProperty("jobsReset").GetInt32());
        }
        await using (var c = await _fx.OpenAsync())
        {
            Assert.Equal("RetryWait", await c.ExecuteScalarAsync<string>(
                "SELECT state FROM dbo.upload_part WHERE id=@id", new { id = seed.PartId }));
            Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>(
                "SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT attempt_count FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
            Assert.Null(await c.ExecuteScalarAsync<string>(
                "SELECT lease_owner FROM dbo.outbox_job WHERE id=@id", new { id = seed.JobId }));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id", new { id = seed.TaskId }));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'task.upload.controlled_retry' AND env=N'MOCK'"));
        }
    }

    [Fact]
    public async Task Upload_actions_require_dedicated_permissions()
    {
        var seed = await SeedUploadAsync("P1", 1000, taskState: "Failed", partState: "Failed", withJob: true);
        var client = await LoginAsync("upload-operator", OperatorPassword);
        NewIdemKey(client);
        var manual = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/manual-verify", new
        {
            decision = "ACCEPTED",
            evidence = new { platformBillCode = seed.BillCode },
        });
        Assert.Equal(HttpStatusCode.Forbidden, manual.StatusCode);
        NewIdemKey(client);
        var retry = await client.PostAsJsonAsync($"/api/v1/upload-tasks/{seed.TaskId}/controlled-retry", new { });
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);
}