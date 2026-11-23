using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Billing;
using TraceFlow.Domain.Sources;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Settings;

public sealed record SettingView(long Id, string Key, string? ValueJson, bool IsSecret, bool HasSecretValue,
    string Version, string? UpdatedBy, DateTime? UpdatedAtUtc, string? Reason, string? ProtectionScope);

public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>M3 销售核验：规则版本（写入 verification.rule_version 与任务快照）与单次核验平台调用码数上限。</summary>
public sealed record SaleVerifySettings(string RuleVersion, int MaxCodesPerRun);

/// <summary>M3 回查/发送策略：退避基数（指数）、最大次数、转人工小时数、未受理证据延迟窗口、发送作业租约。</summary>
public sealed record QueryPolicy(int BackoffSeconds, int MaxAttempts, int ManualAfterHours,
    int ConsistencyWindowSeconds, int DispatchLeaseSeconds);

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
        ["top.billPrefix"] = new("top.billPrefix", false, "\"TF\"", "string", 0, 0),
        ["top.billMaxLength"] = new("top.billMaxLength", false, "32", "int", 8, 64),
        ["upstream.pollSeconds"] = new("upstream.pollSeconds", false, "300", "int", 30, 3600),
    };

    /// <summary>
    /// 账单码 = 前缀 + Base35(taskId)（4 位可容 35^4-1≈150 万任务）+ 可选 "P{n}" 分片后缀（最多 3 位，"P99"）。
    /// 预留 7 位后 prefix.Length + 7 ≤ top.billMaxLength，部署量级内生成的单号恒可容纳（超出仍由生成器抛错，不截断）。
    /// </summary>
    public const int ReservedBillSuffixLength = 7;

    /// <summary>
    /// M3 销售核验/分片/回查/发送运维键的只读默认值。不注册进 <see cref="Catalog"/>：该目录是对外可写目录，
    /// 其条目数被设置页契约锁定（SettingsApiTests）；本组键按「app_setting 行值 → 本表默认」回落，
    /// 不播种行（brief：read-only fallback, not a new seeded row），需要覆盖时直写 app_setting 行即可。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> M3Defaults = new Dictionary<string, string>
    {
        ["sale.verify.ruleVersion"] = "\"M3-1\"",
        ["sale.verify.maxCodesPerRun"] = "200",
        ["upload.part.maxCodes"] = "200",
        ["top.query.backoffSeconds"] = "30",
        ["top.query.maxAttempts"] = "20",
        ["top.query.manualAfterHours"] = "24",
        ["top.query.consistencyWindowSeconds"] = "600",
        ["top.dispatch.leaseSeconds"] = "120",
        ["top.retryPathCertified"] = "false",
    };

    public static void EnsureBillContract(string prefix, long maxLength)
    {
        if (prefix.Length + ReservedBillSuffixLength > maxLength)
            throw new SettingValidationException(
                $"账单码合同不成立：top.billPrefix 长度 {prefix.Length} + 预留 {ReservedBillSuffixLength} 位（Base35 序号与 P 分片后缀）" +
                $"超过 top.billMaxLength={maxLength}，生成的单号必然超长；请缩短前缀或提高上限");
    }

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
            "SELECT id,setting_key,value_json,is_secret,secret_protected,protection_scope,updated_by,updated_at,reason,ver FROM dbo.app_setting",
            cancellationToken: ct));
        var byKey = rows.ToDictionary(r => r.setting_key, StringComparer.Ordinal);
        var list = new List<SettingView>();
        foreach (var def in Catalog.Values)
        {
            if (byKey.TryGetValue(def.Key, out var r))
                list.Add(new SettingView(r.id, def.Key, def.IsSecret ? null : r.value_json, def.IsSecret,
                    r.secret_protected is not null, RowVer.ToHex(r.ver), r.updated_by, r.updated_at, r.reason, r.protection_scope));
            else
                list.Add(new SettingView(0, def.Key, def.IsSecret ? null : def.DefaultJson, def.IsSecret, false, "0", null, null, null, null));
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
            "SELECT id,setting_key,value_json,is_secret,secret_protected,protection_scope,updated_by,updated_at,reason,ver FROM dbo.app_setting WHERE setting_key=@k",
            new { k = key }, tx, cancellationToken: ct));
        return new SettingView(row.id, key, def.IsSecret ? null : row.value_json, def.IsSecret,
            row.secret_protected is not null, RowVer.ToHex(row.ver), row.updated_by, row.updated_at, row.reason, row.protection_scope);
    }

    /// <summary>
    /// §4.7/T7 Important#2：账单码合同两键互相约束——任一键写入后须满足
    /// prefix.Length + ReservedBillSuffixLength ≤ billMaxLength，否则生成的单号必然超长（建单抛错）。
    /// sp_getapplock 串行化两键写入，消除「两键并发各看都合法、合起来却超长」的竞态；随后同一快照 UPDLOCK 读取两行。
    /// </summary>
    public async Task EnsureBillContractCouplingAsync(SqlConnection conn, SqlTransaction tx, string key, string? valueJson, CancellationToken ct)
    {
        if (key is not ("top.billPrefix" or "top.billMaxLength") || valueJson is null) return;
        string? proposedPrefix = null;
        long? proposedMaxLength = null;
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            var e = doc.RootElement;
            if (key == "top.billPrefix" && e.ValueKind == JsonValueKind.String) proposedPrefix = e.GetString();
            if (key == "top.billMaxLength" && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) proposedMaxLength = n;
        }
        catch (JsonException) { return; } // 非法 JSON/类型交给 UpdateAsync 统一报错
        if (proposedPrefix is null && proposedMaxLength is null) return;
        // 单键自身的类型/取值/字符集错误比耦合更早定位，留给 UpdateAsync 报原文
        if (proposedPrefix is not null && !Regex.IsMatch(proposedPrefix, "^[A-Z0-9]{1,16}$")) return;
        var maxDef = Catalog["top.billMaxLength"];
        if (proposedMaxLength is long ml && (ml < maxDef.Min || ml > maxDef.Max)) return;

        await conn.ExecuteAsync(new CommandDefinition(@"
DECLARE @rc int;
EXEC @rc = sp_getapplock @Resource=@res, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=30000;
IF @rc < 0 RAISERROR('applock %s failed (%d)', 16, 1, @res, @rc);",
            new { res = "settings:billcontract" }, tx, cancellationToken: ct));
        var rows = (await conn.QueryAsync<BillContractRow>(new CommandDefinition(
            "SELECT setting_key, value_json FROM dbo.app_setting WITH (UPDLOCK, HOLDLOCK) WHERE setting_key IN (N'top.billPrefix', N'top.billMaxLength')",
            transaction: tx, cancellationToken: ct))).ToDictionary(r => r.setting_key, r => r.value_json, StringComparer.Ordinal);

        var prefix = proposedPrefix
            ?? StoredString(rows, "top.billPrefix")
            ?? JsonSerializer.Deserialize<string>(Catalog["top.billPrefix"].DefaultJson)!;
        var maxLength = proposedMaxLength
            ?? StoredInt64(rows, "top.billMaxLength")
            ?? long.Parse(Catalog["top.billMaxLength"].DefaultJson);
        EnsureBillContract(prefix, maxLength);
    }

    private static string? StoredString(IReadOnlyDictionary<string, string?> rows, string key)
    {
        if (!rows.TryGetValue(key, out var raw) || raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static long? StoredInt64(IReadOnlyDictionary<string, string?> rows, string key)
    {
        if (!rows.TryGetValue(key, out var raw) || raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt64(out var n) ? n : null;
        }
        catch (JsonException) { return null; }
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
            case "string":
                if (e.ValueKind != JsonValueKind.String || e.GetString()!.Length is < 1 or > 200)
                    throw new SettingValidationException($"配置 {def.Key} 须为 1..200 字符字符串");
                // 账单码生成器只接受大写字母数字（BillCodeGenerator），非法前缀会让之后每次建单抛未映射异常
                if (def.Key == "top.billPrefix" && !Regex.IsMatch(e.GetString()!, "^[A-Z0-9]{1,16}$"))
                    throw new SettingValidationException("配置 top.billPrefix 须为 1..16 位大写字母或数字（A-Z0-9）");
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
    {
        // R11a fail-closed：只有「确实没有设置行」才回落到 Catalog 默认 false（开）。
        // 行存在即须按值判定——COUNT 与 MAX 同查以区分「无行」与「有行但值为 NULL」（单读 value_json 无法区分）。
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        var row = await c.QuerySingleAsync<PauseRow>(new CommandDefinition(
            "SELECT COUNT(*) AS row_count, MAX(value_json) AS value_json FROM dbo.app_setting WHERE setting_key=@k",
            new { k = "sync.paused" }, cancellationToken: ct));
        if (row.row_count == 0) return false;
        if (row.value_json is null) return true; // 行存在但值缺失 → 保守视为暂停
        try
        {
            using var doc = JsonDocument.Parse(row.value_json);
            return doc.RootElement.ValueKind != JsonValueKind.False; // R11a：非布尔 false 一律视为暂停（fail-closed）
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public async Task<IReadOnlyList<string>> GetAuditPassValuesAsync(CancellationToken ct)
    {
        var raw = await GetRawAsync("supplier.auditPassValues", ct);
        if (raw is null) return Array.Empty<string>();
        return JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
    }

    public async Task<(long MaxFileBytes, int MaxRows)> GetImportBoundsAsync(CancellationToken ct)
        => (await GetIntAsync("import.maxFileBytes", ct), (int)await GetIntAsync("import.maxRows", ct));

    public async Task<BillCodeContract> GetBillContractAsync(CancellationToken ct)
    {
        var raw = await GetRawAsync("top.billPrefix", ct) ?? Catalog["top.billPrefix"].DefaultJson;
        var prefix = JsonSerializer.Deserialize<string>(raw) ?? "TF";
        return new BillCodeContract(prefix, (int)await GetIntAsync("top.billMaxLength", ct));
    }

    public async Task<int> GetUpstreamPollSecondsAsync(CancellationToken ct)
        => (int)await GetIntAsync("upstream.pollSeconds", ct);

    // M3 读取器：与 GetIntAsync 同形（自开连接、单键读取、无行回落默认），默认值取 M3Defaults。

    /// <summary>
    /// 只接受 InvariantCulture 整数且必须 &gt; 0：非数字、0、负数一律回落默认值。
    /// 0/负数若被采信会让回查退避变成 0 或负秒数（退避失效/立即重试），故按默认值 fail-safe。
    /// 直接用 int.TryParse（不走 long 再强转），避免越界 long 静默截断成错误 int。
    /// </summary>
    private async Task<int> GetM3IntAsync(string key, CancellationToken ct)
    {
        var fallback = int.Parse(M3Defaults[key], CultureInfo.InvariantCulture);
        var raw = await GetRawAsync(key, ct);
        return raw is not null
            && int.TryParse(raw.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n > 0
            ? n
            : fallback;
    }

    private async Task<string> GetM3StringAsync(string key, CancellationToken ct)
    {
        var fallback = JsonSerializer.Deserialize<string>(M3Defaults[key])!;
        var raw = await GetRawAsync(key, ct);
        if (raw is null) return fallback;
        try { return JsonSerializer.Deserialize<string>(raw) ?? fallback; }
        catch (JsonException) { return raw; } // 容忍未加引号的裸文本（人工直写 app_setting）
    }

    /// <summary>只接受大小写不敏感的 true/false 文本；其余（1/0、JSON 引号字符串、乱码）一律回落默认 false。</summary>
    private async Task<bool> GetM3BoolAsync(string key, CancellationToken ct)
    {
        var fallback = bool.Parse(M3Defaults[key]);
        var raw = await GetRawAsync(key, ct);
        return raw is not null && bool.TryParse(raw.AsSpan().Trim(), out var b) ? b : fallback;
    }

    public async Task<SaleVerifySettings> GetSaleVerifySettingsAsync(CancellationToken ct) => new(
        await GetM3StringAsync("sale.verify.ruleVersion", ct),
        await GetM3IntAsync("sale.verify.maxCodesPerRun", ct));

    public Task<int> GetPartMaxCodesAsync(CancellationToken ct)
        => GetM3IntAsync("upload.part.maxCodes", ct);

    public async Task<QueryPolicy> GetQueryPolicyAsync(CancellationToken ct) => new(
        await GetM3IntAsync("top.query.backoffSeconds", ct),
        await GetM3IntAsync("top.query.maxAttempts", ct),
        await GetM3IntAsync("top.query.manualAfterHours", ct),
        await GetM3IntAsync("top.query.consistencyWindowSeconds", ct),
        await GetM3IntAsync("top.dispatch.leaseSeconds", ct));

    /// <summary>受控重试合同门禁：默认 false，读取失败（缺行/非法值）一律按 false（fail-closed，不放行重试）。</summary>
    public Task<bool> GetRetryPathCertifiedAsync(CancellationToken ct)
        => GetM3BoolAsync("top.retryPathCertified", ct);

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
        public string? protection_scope { get; set; }
        public string? updated_by { get; set; }
        public DateTime? updated_at { get; set; }
        public string? reason { get; set; }
        public byte[] ver { get; set; } = Array.Empty<byte>();
    }

    private sealed class BillContractRow
    {
        public string setting_key { get; set; } = "";
        public string? value_json { get; set; }
    }

    /// <summary>存在性+值分离读取：row_count 为 0 表示无设置行；有行而 value_json 为 NULL 表示值缺失。</summary>
    private sealed class PauseRow
    {
        public int row_count { get; set; }
        public string? value_json { get; set; }
    }
}
