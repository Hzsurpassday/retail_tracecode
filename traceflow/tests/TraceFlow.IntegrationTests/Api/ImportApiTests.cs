using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TraceFlow.IntegrationTests.Api;

/// <summary>
/// T16 携带项 4（HTTP 级）：POST /imports 202 形状（jobId/batchId 均字符串）、
/// GET /imports/{id} 详情形状与分页、POST commit 过期 expectedVersion→409 VERSION_CONFLICT。
/// 宿主含后台 Worker（2 秒轮询），暂存行由 import-stage 作业异步写入→详情断言前轮询等待。
/// </summary>
[SupportedOSPlatform("windows")]
public class ImportApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private const string ProductHeader =
        "spid,display_code,barcode,name,approval_no,manufacturer,dosage_form,strength,package_spec,unit,trace_classification,classification_reason,manufacturer_identity";

    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public ImportApiTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await SeedUserAsync("impadmin", "管理员", "Admin-Password-123",
            new[] { "master:match", "trace:read", "audit:read", "system:manage", "job:rematch", "master:classify" });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task SeedUserAsync(string username, string displayName, string password, string[] perms)
    {
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=@u)
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(@u,@d,@h);
SELECT id FROM dbo.app_user WHERE username=@u",
            new { u = username, d = displayName, h = hasher.HashPassword(username, password) });
        foreach (var p in perms)
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')",
                new { id, p });
    }

    private async Task<HttpClient> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private static async Task<HttpResponseMessage> PostWithIdemAsync(HttpClient client, string url, object body)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.PostAsJsonAsync(url, body);
    }

    private static async Task<string> StageAsync(HttpClient client, string csv, string fileName)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        var resp = await PostWithIdemAsync(client, "/api/v1/imports", new
        {
            fileKind = "PRODUCT",
            fileHash = Convert.ToHexString(SHA256.HashData(bytes)),
            content = Convert.ToBase64String(bytes),
            fileName,
        });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("batchId").GetString()!;
    }

    [Fact]
    public async Task Post_import_stage_returns_202_with_string_ids()
    {
        var client = await LoginAsync("impadmin", "Admin-Password-123");
        var csv = ProductHeader + "\nT16S1,,,暂存形状药,,,,,,盒,UNKNOWN,,\n";
        var bytes = Encoding.UTF8.GetBytes(csv);
        var resp = await PostWithIdemAsync(client, "/api/v1/imports", new
        {
            fileKind = "PRODUCT",
            fileHash = Convert.ToHexString(SHA256.HashData(bytes)),
            content = Convert.ToBase64String(bytes),
            fileName = "t16-stage-shape.csv",
        });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        // API-01：外部可见作业/批次 ID 为字符串（内部 int64 代理不外泄为 number）
        Assert.Equal(JsonValueKind.String, data.GetProperty("jobId").ValueKind);
        Assert.Equal(JsonValueKind.String, data.GetProperty("batchId").ValueKind);
        Assert.NotEqual("0", data.GetProperty("jobId").GetString());
        Assert.NotEqual("0", data.GetProperty("batchId").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("requestId").ValueKind);
        // 缺必填字段→422
        var bad = await PostWithIdemAsync(client, "/api/v1/imports", new { fileKind = "PRODUCT" });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
    }

    [Fact]
    public async Task Get_import_detail_shape_and_pagination()
    {
        var client = await LoginAsync("impadmin", "Admin-Password-123");
        var csv = ProductHeader + "\n" +
            "T16D1,,,详情药一,,,,,,盒,UNKNOWN,,\n" +
            "T16D2,,,详情药二,,,,,,盒,EXEMPT,,\n"; // 第 2 行 EXEMPT 缺 reason→ERROR
        var batchId = await StageAsync(client, csv, "t16-detail.csv");

        // 后台 Worker 异步写行：轮询直至 import_row 就绪（2 秒轮询周期，30 秒上限）
        JsonDocument? doc = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await client.GetAsync($"/api/v1/imports/{batchId}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            doc?.Dispose();
            doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.GetProperty("data").GetProperty("total").GetInt32() == 2) break;
            await Task.Delay(500);
        }
        using (doc)
        {
            var data = doc!.RootElement.GetProperty("data");
            Assert.Equal(2, data.GetProperty("total").GetInt32());
            Assert.Equal(1, data.GetProperty("page").GetInt32());
            Assert.Equal(50, data.GetProperty("pageSize").GetInt32()); // 默认 50
            var batch = data.GetProperty("batch");
            Assert.Equal(batchId, batch.GetProperty("batchId").GetString());
            Assert.Equal("PRODUCT", batch.GetProperty("fileKind").GetString());
            Assert.Equal("STAGED", batch.GetProperty("status").GetString());
            Assert.Equal(1, batch.GetProperty("successRows").GetInt32());
            Assert.Equal(1, batch.GetProperty("failedRows").GetInt32());
            Assert.Equal(16, batch.GetProperty("version").GetString()!.Length); // rowversion 16 位 hex 字符串
            var rows = data.GetProperty("rows");
            Assert.Equal(2, rows.GetArrayLength());
            Assert.Equal(1, rows[0].GetProperty("rowNumber").GetInt32());
            Assert.Equal("OK", rows[0].GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("error").ValueKind);
            Assert.Contains("T16D1", rows[0].GetProperty("rawJson").GetString());
            Assert.Equal(2, rows[1].GetProperty("rowNumber").GetInt32());
            Assert.Equal("ERROR", rows[1].GetProperty("status").GetString());
            Assert.Contains("classification_reason", rows[1].GetProperty("error").GetString());
        }
        // 分页边界：pageSize 钳制与越界页（ROW_NUMBER）
        var one = await client.GetFromJsonAsync<DetailResp>($"/api/v1/imports/{batchId}?page=1&pageSize=1");
        Assert.Single(one!.Data.Rows);
        Assert.Equal(2, one.Data.Total);
        var clamp = await client.GetFromJsonAsync<DetailResp>($"/api/v1/imports/{batchId}?pageSize=1000");
        Assert.Equal(200, clamp!.Data.PageSize);
        var far = await client.GetFromJsonAsync<DetailResp>($"/api/v1/imports/{batchId}?page=9999&pageSize=200");
        Assert.Empty(far!.Data.Rows);
        Assert.Equal(2, far.Data.Total);
        // 不存在批次→404
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/imports/999999999")).StatusCode);
    }

    [Fact]
    public async Task Commit_import_stale_version_409_and_validation()
    {
        var client = await LoginAsync("impadmin", "Admin-Password-123");
        var csv = ProductHeader + "\nT16C1,,,提交冲突药,,,,,,盒,UNKNOWN,,\n";
        var batchId = await StageAsync(client, csv, "t16-commit.csv");
        // 过期 expectedVersion（全零 hex 永不等于真实 rowversion）→409 VERSION_CONFLICT；
        // 版本检查先于状态检查，后台 Worker 是否已写行不影响本断言
        var stale = await PostWithIdemAsync(client, $"/api/v1/imports/{batchId}/commit",
            new { expectedVersion = "0000000000000000", reason = "过期版本" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("VERSION_CONFLICT", await stale.Content.ReadAsStringAsync());
        // expectedVersion 必填→422
        var missing = await PostWithIdemAsync(client, $"/api/v1/imports/{batchId}/commit", new { reason = "缺版本" });
        Assert.Equal((HttpStatusCode)422, missing.StatusCode);
        // 不存在批次→404
        var notFound = await PostWithIdemAsync(client, "/api/v1/imports/999999999/commit",
            new { expectedVersion = "0", reason = "不存在" });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);
    private sealed record DetailResp(DetailData Data);
    private sealed record DetailData(DetailRow[] Rows, int Total, int Page, int PageSize);
    private sealed record DetailRow(int RowNumber, string Status, string? Error, string RawJson);
}
