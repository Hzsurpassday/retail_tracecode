using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Settings;

public sealed record SettingView(long Id, string Key, string? ValueJson, bool IsSecret, bool HasSecretValue,
    string Version, string? UpdatedBy, DateTime? UpdatedAtUtc, string? Reason);

public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>
/// 配置中心（SET-01/02）：键目录+边界校验；密钥 DPAPI 只写不回显；rowversion 乐观并发；
/// 每次变更同事务审计（密钥审计不含明文）。读取方法自开连接；UpdateAsync 复用调用方事务。
/// </summary>
public sealed class SettingsService
{
    public sealed record SettingDef(string Key, bool IsSecret, string DefaultJson, string Kind, long Min, long Max);

    public static readonly IReadOnlyDictionary<string, SettingDef> Catalog = new Dictionary<string, SettingDef>
    {
        ["erp.connection"] = new("erp.connection", true, "null", "secret", 0, 0),
        ["top.appkey"] = new("top.appkey", true, "null", "secret", 0, 0),
        ["top.appsecret"] = new("top.appsecret", true, "null", "secret", 0, 0),
        ["sync.pollSeconds"] = new("sync.pollSeconds", false, "30", "int", 5, 3600),
        ["sync.rescanHours"] = new("sync.rescanHours", false, "72", "int", 1, 720),
        ["sync.dailyRescanDays"] = new("sync.dailyRescanDays", false, "30", "int", 1, 365),
        ["sync.stabilizeSeconds"] = new("sync.stabilizeSeconds", false, "10", "int", 5, 300),
        ["sync.roundIntervalSeconds"] = new("sync.roundIntervalSeconds", false, "5", "int", 5, 60),
        ["sync.paused"] = new("sync.paused", false, "false", "bool", 0, 0),
        ["supplier.auditPassValues"] = new("supplier.auditPassValues", false, "[]", "stringArray", 0, 0),
        ["import.maxFileBytes"] = new("import.maxFileBytes", false, "20971520", "int", 1024, 104857600),
        ["import.maxRows"] = new("import.maxRows", false, "10000", "int", 1, 100000),
    };

    private readonly string _cs;
    private readonly ISecretProtector _protector;
    public SettingsService(string connectionString, ISecretProtector protector)
    {
        _cs = connectionString;
        _protector = protector;
    }

    public async Task<IReadOnlyList<SettingView>> ListAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        var rows = await c.QueryAsync<Row>(new CommandDefinition(
            "SELECT id,setting_key,value_json,is_secret,secret_protected,updated_by,updated_at,reason,ver FROM dbo.app_setting",
            cancellationToken: ct));
        var byKey = rows.ToDictionary(r => r.setting_key, StringComparer.Ordinal);
        var list = new List<SettingView>();
        foreach (var def in Catalog.Values)
        {
            if (byKey.TryGetValue(def.Key, out var r))
                list.Add(new SettingView(r.id, def.Key, def.IsSecret ? null : r.value_json, def.IsSecret,
                    r.secret_protected is not null, RowVer.ToHex(r.ver), r.updated_by, r.updated_at, r.reason));
            else
                list.Add(new SettingView(0, def.Key, def.IsSecret ? null : def.DefaultJson, def.IsSecret, false, "0", null, null, null));
        }
        return list;
    }

    public async Task<SettingView> UpdateAsync(SqlConnection conn, SqlTransaction tx, string key, string? valueJson, string? secretValue,
        string expectedVersionHex, string reason, string actor, CancellationToken ct)
    {
        if (!Catalog.TryGetValue(key, out var def))
            throw new SettingValidationException($"未知配置项: {key}");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new SettingValidationException("reason 必填且不超过 500 字"); // app_setting.reason 为 nvarchar(500)
        byte[]? protectedBytes = null;
        if (def.IsSecret)
        {
            if (string.IsNullOrEmpty(secretValue) || valueJson is not null)
                throw new SettingValidationException("密钥配置只接受 secretValue（只写），不接受 valueJson");
            protectedBytes = _protector.Protect(secretValue);
        }
        else
        {
            if (valueJson is null || secretValue is not null)
                throw new SettingValidationException("非密钥配置只接受 valueJson");
            ValidateJson(def, valueJson);
        }

        // 读旧值（UPDLOCK 串行化同键并发），审计 before 摘要不含密钥明文
        var old = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT id,setting_key,value_json,is_secret,secret_protected,updated_by,updated_at,reason,ver FROM dbo.app_setting WITH (UPDLOCK, HOLDLOCK) WHERE setting_key=@k",
            new { k = key }, tx, cancellationToken: ct));
        var beforeDigest = old is null ? null
            : old.is_secret ? JsonSerializer.Serialize(new { key, isSecret = true }) : old.value_json;

        if (expectedVersionHex == "0")
        {
            if (old is not null)
                throw new SettingVersionException($"配置 {key} 已存在，请携带当前 version 更新");
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.app_setting(setting_key,value_json,secret_protected,is_secret,protection_scope,updated_by,reason)
VALUES(@k,@vj,@sp,@is,@scope,@by,@reason)",
                // brief 原文 `is =` 为 C# 关键字，匿名类型成员名须 @is（CS1525，T6 偏差 2）；属性名仍为 is，Dapper @is 不变
                new { k = key, vj = def.IsSecret ? null : valueJson, sp = protectedBytes, @is = def.IsSecret ? 1 : 0,
                      scope = def.IsSecret ? "CurrentUser" : null, by = actor, reason }, tx, cancellationToken: ct));
        }
        else
        {
            if (old is null)
                throw new SettingVersionException($"配置 {key} 不存在，新建请传 expectedVersion=\"0\"");
            var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.app_setting SET value_json=@vj, secret_protected=@sp, is_secret=@is, protection_scope=@scope,
       updated_by=@by, reason=@reason, updated_at=SYSUTCDATETIME()
WHERE setting_key=@k AND ver=@ver",
                new { k = key, vj = def.IsSecret ? null : valueJson, sp = protectedBytes, @is = def.IsSecret ? 1 : 0, // @is 同 INSERT（T6 偏差 2）
                      scope = def.IsSecret ? "CurrentUser" : null, by = actor, reason, ver = RowVer.FromHex(expectedVersionHex) },
                tx, cancellationToken: ct));
            if (rows == 0)
                throw new SettingVersionException($"配置 {key} 版本冲突，请刷新后重试");
        }
        var afterDigest = def.IsSecret ? JsonSerializer.Serialize(new { key, isSecret = true }) : valueJson;
        await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "settings.update", "app_setting",
            key, reason, beforeDigest, afterDigest, null, null, "LIVE"));
        // 同事务读回（不可另开连接：未提交 X 锁会阻塞 ListAsync 直至命令超时）
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(
            "SELECT id,setting_key,value_json,is_secret,secret_protected,updated_by,updated_at,reason,ver FROM dbo.app_setting WHERE setting_key=@k",
            new { k = key }, tx, cancellationToken: ct));
        return new SettingView(row.id, key, def.IsSecret ? null : row.value_json, def.IsSecret,
            row.secret_protected is not null, RowVer.ToHex(row.ver), row.updated_by, row.updated_at, row.reason);
    }

    private static void ValidateJson(SettingDef def, string json)
    {
        JsonElement e;
        try { e = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { throw new SettingValidationException($"配置 {def.Key} 不是合法 JSON"); }
        switch (def.Kind)
        {
            case "int":
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var n) || n < def.Min || n > def.Max)
                    throw new SettingValidationException($"配置 {def.Key} 须为 [{def.Min},{def.Max}] 内整数");
                break;
            case "bool":
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new SettingValidationException($"配置 {def.Key} 须为布尔值");
                break;
            case "stringArray":
                if (e.ValueKind != JsonValueKind.Array || e.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    throw new SettingValidationException($"配置 {def.Key} 须为字符串数组");
                break;
        }
    }

    private async Task<string?> GetRawAsync(string key, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value_json FROM dbo.app_setting WHERE setting_key=@k", new { k = key }, cancellationToken: ct));
    }

    private async Task<long> GetIntAsync(string key, CancellationToken ct)
    {
        var raw = await GetRawAsync(key, ct);
        return raw is not null && long.TryParse(raw, out var n) ? n : long.Parse(Catalog[key].DefaultJson);
    }

    public async Task<SyncPolicy> GetSyncPolicyAsync(CancellationToken ct) => new(
        (int)await GetIntAsync("sync.pollSeconds", ct),
        (int)await GetIntAsync("sync.rescanHours", ct),
        (int)await GetIntAsync("sync.dailyRescanDays", ct),
        (int)await GetIntAsync("sync.stabilizeSeconds", ct),
        (int)await GetIntAsync("sync.roundIntervalSeconds", ct));

    public async Task<bool> IsChannelPausedAsync(CancellationToken ct)
        => (await GetRawAsync("sync.paused", ct)) == "true";

    public async Task<IReadOnlyList<string>> GetAuditPassValuesAsync(CancellationToken ct)
    {
        var raw = await GetRawAsync("supplier.auditPassValues", ct);
        if (raw is null) return Array.Empty<string>();
        return JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
    }

    public async Task<(long MaxFileBytes, int MaxRows)> GetImportBoundsAsync(CancellationToken ct)
        => (await GetIntAsync("import.maxFileBytes", ct), (int)await GetIntAsync("import.maxRows", ct));

    public async Task<string> GetSecretAsync(string key, CancellationToken ct)
    {
        if (!Catalog.TryGetValue(key, out var def) || !def.IsSecret)
            throw new SettingValidationException($"非密钥配置项: {key}");
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        var bytes = await c.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "SELECT secret_protected FROM dbo.app_setting WHERE setting_key=@k", new { k = key }, cancellationToken: ct));
        if (bytes is null)
            throw new SettingValidationException($"密钥 {key} 尚未配置");
        return _protector.Unprotect(bytes);
    }

    /// <summary>防 SSRF：只接受已保存 erp.connection 的 app_setting.id，不接受任意连接串/URL；强制 5 秒超时；错误信息不回显连接串。</summary>
    public async Task<ConnectionTestResult> TestErpConnectionAsync(long configId, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT id,setting_key,value_json,is_secret,secret_protected,updated_by,updated_at,reason,ver FROM dbo.app_setting WHERE id=@id",
            new { id = configId }, cancellationToken: ct));
        if (row is null || row.setting_key != "erp.connection" || !row.is_secret || row.secret_protected is null)
            throw new SettingValidationException("连接测试仅支持已保存的 erp.connection 配置项");
        string plain;
        try { plain = _protector.Unprotect(row.secret_protected); }
        catch (SecretUndecryptableException) { return new ConnectionTestResult(false, "ERP 凭据无法解密（DPAPI 作用域不符），请重新保存"); }
        try
        {
            var b = new SqlConnectionStringBuilder(plain) { ConnectTimeout = 5 };
            await using var erp = new SqlConnection(b.ConnectionString);
            await erp.OpenAsync(ct);
            await erp.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
            return new ConnectionTestResult(true, "连接成功");
        }
        catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException)
        {
            return new ConnectionTestResult(false, "ERP 连接失败（超时、拒绝或凭据无效）");
        }
    }

    private sealed class Row
    {
        public long id { get; set; }
        public string setting_key { get; set; } = "";
        public string? value_json { get; set; }
        public bool is_secret { get; set; }
        public byte[]? secret_protected { get; set; }
        public string? updated_by { get; set; }
        public DateTime? updated_at { get; set; }
        public string? reason { get; set; }
        public byte[] ver { get; set; } = Array.Empty<byte>();
    }
}
