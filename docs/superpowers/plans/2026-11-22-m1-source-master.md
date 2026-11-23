# TraceFlow M1 来源与主数据 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 M0 基础上交付来源同步与主数据闭环：KSOA fixture 适配契约、T1 来源镜像同步（检查点/重叠回扫/缺口补拉/稳定期/缺键异常/SOURCE_CHANGED）、商品供应商档案导入（暂存/预览/提交）、追溯分类、五项+双向唯一自动匹配与人工确认、设置页基础、UI-02/03，并关闭 M0 携带的 9 项硬性加固。

**Architecture:** 沿用 M0 单体（Kestrel + UseWindowsService + 持久任务引擎）。新增 `ISourceAdapter` 端口：M1 只交付 Fixture 适配器（G-ERP 未取证，真实 ERP 同步禁用）；同步作业以 T1 事务（镜像+检查点+异常同提交）落库，稳定期两轮摘要一致才 COMPLETE；主数据经 CSV 导入暂存/提交积累（主档表未取证，不猜表名），匹配作业五项一致+双向唯一才 AUTO_CONFIRMED，人工确认锁定不被自动替换；全部新端点走 M0 鉴权/CSRF/幂等管线。

**Tech Stack:** .NET 8 / ASP.NET Core 8 / Dapper 2.x / Microsoft.Data.SqlClient 5.x / xUnit / React 18 + TypeScript + Vite 5（全部沿用 M0，无新依赖）。

**Spec:** `docs/superpowers/specs/2026-11-22-retail-pharmacy-trace-design.md`（已批设计）。同时必读：`docs/PRD-TraceFlow-单门店-V1.0.md`、`docs/PRD-TraceFlow-验收与代码审查清单.md`、`docs/PRD-TraceFlow-开发Agent交接说明.md`、`docs/superpowers/plans/2026-11-22-roadmap.md`、`docs/delivery/M0-rulings-and-carryforwards.md`（M0 裁定与携带项，本计划 Task 1/3/4/9 显式关闭）。

**分支：** 从 `m0-foundation`（HEAD 1ea43cb，PR #1 待合并）创建 `m1-source-master`。M0 代码是既成事实接口，本计划逐字消费；不得修改 M0 已交付语义（除本计划显式列出的加固点）。

## Global Constraints

（与 M0 计划共用，逐字沿用）

- 业务库兼容 **SQL Server 2008 R2**：禁止 OFFSET/FETCH、SEQUENCE、JSON_VALUE/OPENJSON、STRING_AGG、temporal table、CREATE OR ALTER（DB-03）；分页用 ROW_NUMBER/键集；全部 SQL 在真实 2008 R2 实例验证（AT-070）。
- 数据访问只用 **Dapper + 手写 T-SQL**，不用 EF Core（设计 §1.3）。
- KSOA 源库**只读**：源连接只允许 SELECT，不含 DML/DDL/NOLOCK/触发器（SRC-01/SRC-02）；迁移工具只连 TraceFlow 目标库。
- 外部 ID/单号/码一律字符串，索引键列 `COLLATE Latin1_General_BIN2`；数量 `decimal(18,6)`，禁 float 计量；内部 UTC `datetime2(3)`；版本 `rowversion` 序列化为字符串（DB-01/DB-02/API-01）。
- AppKey/AppSecret/连接密码不落 Git、日志、前端、错误响应（NFR-05/AT-071）；凭据 DPAPI 加密，密文不可解密→停调用+告警，无默认凭据兜底。
- 上传状态机 12 态按 PRD §9.2（路线图映射表）；逐码结果含 UNKNOWN；非法转换拒绝。
- 普通测试一律假平台/fixture；**不得向正式平台上传虚构交易、不得写 ERP 库**；真实平台调用仅限经用户授权的真实业务。
- 无默认公开管理员密码（AUTH-02）；匿名拒绝、逐请求权限校验（AUTH-01）；审计不可变（AUTH-03/AUD-01）。
- 前端遵循 NFR-02 React 规范：独立请求并行、长表分页、不滥用 useMemo、不在组件内定义组件、无副作用派生状态、不变更 props/state。
- 时间显示 Asia/Shanghai；API 前缀 `/api/v1`；错误统一 `code/message/fieldErrors/retryable/requestId`；401/403/409/422/429/503 语义按 API-01。
- 保留既有用户资料：`retail_tracecode/`（首轮 TS 参考代码）、`zww-md/`、`docs/` 现有文件一律不覆盖不删除。
- 未取证适配能力显示"未接入/未验证"并禁用对应发送；缺口按需求 ID 记录，不静默降级。

### M1 附加约束（本阶段裁定，执行者必须遵守）

- **测试引擎**：用户已授权开发期用本机 SQL Server 2019 降级测试。集成测试命令统一为（Git Bash，仓库根 `E:\Viding\Retail Trace Code`）：
  `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
  交付报告必须如实标注"2008 R2 未验证，交付前补验 AT-070"。禁止声称 2008 R2 运行时已验证。
- **G-ERP 门禁**：M1 只有 Fixture 适配器（`ContractVersion = "ksoa-fixture-1"`）。`env=LIVE` 时来源同步调度不入队、手动触发 422 `ERP_NOT_CERTIFIED`；真实 KSOA 适配器是抛 `AdapterNotCertifiedException` 的占位，不写任何源库 SQL。AT-001 的 M1 证据 = 契约文档 + 占位拒绝 + 静态门禁（无源库写路径），真实只读账号取证属 G-ERP，交付前完成。
- **rowversion 序列化约定**：`Convert.ToHexString(bytes)`（16 位大写十六进制，无连字符）。`RowVer.FromHex` 校验失败抛 `FormatException`→端点 422。新建对象（尚无版本行）的 expectedVersion 哨兵值为字符串 `"0"`。
- **CodeFormat 持久化 token**：`Empty→"EMPTY"`、`Supported8Prefix20→"S8P20"`、`Unsupported→"UNSUP"`（`CodeFormatStorage`）。禁止 `enum.ToString()` 直接落库。0007 迁移把 `source_line.code_format` 与 `trace_code.format` 加宽到 `nvarchar(32)`（M0 携带项 1，强制）。
- **稳定键规则（候选，待 G-ERP 取证）**：销售 docKey=`lshh`、lineKey=`plh`；入库 docKey=`djbh`、lineKey=`dj_sn`。NULL/空 lineKey→`MISSING_LINE_KEY` 接入异常（raw 保留，不造行号、不静默丢行）；同单重复 lineKey→`DUPLICATE_LINE_KEY`；孤儿行（行外键无对应头）→`ORPHAN_LINE`；同 docKey 不同业务日且内容变化→`CROSS_DAY_REUSE`；同 docKey 跨单据类型出现（跨表键碰撞，如 lshh 与 djbh 同值）→`KEY_REUSE`。任一异常→该版本 `INVALID`，不 eligible，无"点击放行"。行键按单内唯一（`UQ_line_key(snapshot_id, source_line_key)`），plh/dj_sn 跨单重复属正常，不报异常。
- **稳定期参数（SYNC-02/04 默认值，可配置有界）**：轮询 30 秒、回扫 72 小时、每日回扫 30 天、稳定期 10 秒、轮间隔 ≥5 秒。COMPLETE 条件：行数>0、无接入异常、≥2 轮摘要一致、轮间隔≥5 秒、首见起≥10 秒。零行头单永不 COMPLETE（`EMPTY_DETAIL` 异常）。
- **检查点（SYNC-03/T1）**：`sync_checkpoint(entity, kind=N'WINDOW')` 与镜像同事务提交；窗口起点=检查点终点−72h（首次=now−30d），**任何停机缺口不得截断**（AT-007）；失败回滚检查点不动（AT-006）。
- **导入边界（MD-03）**：默认 20 MB / 10000 行（可配置有界）；fileKind 有界枚举 `PRODUCT/SUPPLIER/PLATFORM_DRUG/ENTERPRISE`；同 (org,fileKind,fileHash) 幂等；提交不触碰任何映射行（锁定映射不被覆盖）；企业版本行只增不改（ENT-02）。
- **匹配（MATCH-01..05）**：五项=批准文号(norm)/生产企业身份/剂型/含量规格/销售包装规格；缺字段不通配；双向有效唯一由 0003 过滤唯一索引兜底；人工确认锁定（is_locked=1）不被自动重跑替换；身份变更→REVIEW_REQUIRED；强冲突（任一字段矛盾）人工确认 409，不得"忽略"。
- **供应商（ENT-01..03）**：信用代码精确+双向唯一+审核状态∈配置通过值集（默认空集=不自动确认，枚举待 G-TOP 取证）才 AUTO_CONFIRMED；名称只作候选；角色分别存储不互绑；不自动 saveent。
- **审计 env 约定（M0 M-8）**：控制面/人工操作审计 env 恒 `"LIVE"`（约定：审计是控制面事实，不分业务环境）；自动作业审计 env 用作业 env。
- **提交规则**：每个 Task 末尾提交；`git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit`（不改 git config）；只提交本任务文件；仓库中既有未跟踪用户文件（PRD 原文、docs/research、docs/review 等）一律不动。
- **新文件一律以换行符结尾**；`TreatWarningsAsErrors` 已开（traceflow/Directory.Build.props），0 警告才能过。
- **契约补充裁定**（OpenAPI M1 清单缺口，本计划补齐并在 Task 16 回写 openapi.yaml）：新增 `GET /imports`、`GET /imports/{id}`（预览）、`GET /settings`、`POST /product-mappings/{id}/disable`、`POST /supplier-mappings/{id}/disable`。`ConfirmMappingRequest.targetId`：商品=平台 `platform_drug_id`（外部字符串）；供应商=映射目标企业版本的 `ent_id`。确认只校验既有候选对，不支持改绑目标（改绑=先解除再匹配）。

## File Structure（M1 新增/修改）

```
traceflow/
  db/migrations/0007_m1_source_master.sql        # T1：format 加宽/稳定期列/幂等键用户作用域/映射对唯一
  src/TraceFlow.Domain/
    Codes/CodeFormatStorage.cs                   # T1
    Versioning/RowVer.cs                         # T1
    Matching/DrugIdentity.cs, IdentityMatch.cs, DrugIdentityMatcher.cs,
             ApprovalNoNormalizer.cs, MappingStates.cs   # T2
    Sources/ExpiryParser.cs, BusinessTimeParser.cs, SourceHasher.cs,
            SyncPolicy.cs, SyncWindowPolicy.cs   # T2
    Master/CsvReader.cs                          # T2
  src/TraceFlow.Application/Abstractions/
    ISourceAdapter.cs                            # T5（DTO+端口+工厂）
    ServiceExceptions.cs                         # T4（AdapterNotCertified/SettingValidation/SettingVersion/ImportBounds）
  src/TraceFlow.Infrastructure/
    Sources/FixtureKsoaAdapter.cs, RealKsoaAdapter.cs, SourceAdapterFactory.cs,
            SourceSyncService.cs, SyncScheduler.cs        # T5/T7/T8/T9
    Settings/SettingsService.cs                  # T6
    Master/ImportService.cs, MatchingService.cs, PrefixIndexService.cs  # T10/T12
    Jobs/SourceSyncJobHandler.cs, ImportStageJobHandler.cs, MasterMatchJobHandler.cs  # T9/T10/T12
    Db/OrgBootstrap.cs                           # T5
    Jobs/JobQueue.cs                             # T4 修改（回收审计）
    Security/IdempotencyStore.cs, PermissionService.cs(不动)  # T3 修改
  src/TraceFlow.Host/
    Security/IdempotencyExecutor.cs              # T3 重写（实例服务）
    Security/ApiErrors.cs                        # T4 修改
    Endpoints/AdminEndpoints.cs, AuthEndpoints.cs # T3/T4 修改
    Endpoints/SettingsEndpoints.cs               # T6
    Endpoints/OpsEndpoints.cs                    # T9（sync-jobs/jobs/audit-logs）
    Endpoints/ImportEndpoints.cs                 # T10
    Endpoints/MasterEndpoints.cs                 # T11/T13
    SyncSchedulerHostedService.cs                # T9
    Program.cs                                   # T3/T4/T5/T6/T9/T10/T11/T12/T13 接线
  src/TraceFlow.Web/src/
    api.ts, App.tsx, components/AppShell.tsx     # T15 修改
    pages/ProductsPage.tsx, components/ImportWizard.tsx   # T14
    pages/SuppliersPage.tsx, pages/SettingsPage.tsx       # T15
    vite.config.ts                               # T15 修改（代理 5152）
  tests/TraceFlow.UnitTests/                     # 各任务新增（见各 Task）
  tests/TraceFlow.IntegrationTests/
    TestSeed.cs                                  # T7（org/KSOA 幂等种子）
    Sql2008Fixture.cs                            # T4 修改（BIN2 opt-in）
    Migrations/Migration0007Tests.cs             # T1
    Security/IdempotencyHardeningTests.cs        # T3
    Jobs/LeaseRecoveryAuditTests.cs              # T4
    Settings/SettingsServiceTests.cs             # T6
    Sources/SourceSyncTests.cs, SourceStabilizationTests.cs, SyncSchedulerTests.cs  # T7/T8/T9
    Master/ImportServiceTests.cs, MatchingServiceTests.cs, SupplierMatchingTests.cs,
           ClassificationTests.cs, MappingConfirmTests.cs   # T10/T12/T11/T13
    Api/M1ApiGateTests.cs                        # T9（管线顺序复审，M0 携带项 2）
docs/contracts/ksoa-adapter-contract.md          # T5 新建，T16 定稿
docs/contracts/openapi.yaml, data-dictionary.md, adapter-facts.md, source-mapping.md  # T16
docs/traceability/matrix.md                      # T16
docs/delivery/M1-gate-report.md                  # T16
```

## 任务总览（依赖顺序）

| # | 任务 | 关键验收 |
| --- | --- | --- |
| 1 | 0007 迁移 + CodeFormatStorage + RowVer | M0 携带 1；AT-070 增量 |
| 2 | Domain 值对象（身份匹配/时间/效期/哈希/窗口/CSV） | MATCH-01 单元、SRC-05 |
| 3 | 幂等加固（DI/用户作用域/2627/query hash） | M0 携带 3；AT-066 |
| 4 | 输入验证+租约回收审计+卫生+BIN2 变体 | M0 携带 4/5/7/9 |
| 5 | 适配契约 + Fixture 适配器 + 工厂 + OrgBootstrap | SRC-01..04、G-ERP 诚实 |
| 6 | SettingsService + 设置端点 | UI-10 基础、NFR-05 |
| 7 | 来源同步核心 T1（镜像/检查点/缺键/跨日） | AT-002/003/006/008 |
| 8 | 稳定期 + 完整性 + SOURCE_CHANGED | AT-005/057、SYNC-02/05 |
| 9 | 调度器 + 同步作业 + ops 端点 + 管线门禁 | AT-004/007、M0 携带 2/6 |
| 10 | 导入暂存/预览/提交 | MD-02/03、AT-010/011 数据面 |
| 11 | 商品/供应商列表 + 追溯分类 | MD-01、UI-02/03 数据面 |
| 12 | 自动匹配作业 + 前缀冲突 + 供应商 ENT 规则 | AT-012..021 自动侧 |
| 13 | 人工确认/解除端点 | MATCH-05、AT-015/016/019、UI-11 |
| 14 | 前端商品页 + 导入向导 | UI-02 |
| 15 | 前端供应商页 + 设置页 + 壳抛光 | UI-03/10、M0 携带 8 |
| 16 | 契约/追踪矩阵/门禁报告 + 全量复跑 | 交付诚实性 |

---
### Task 1: 0007 迁移 + CodeFormat 存储 token + RowVer

**Files:**
- Create: `traceflow/db/migrations/0007_m1_source_master.sql`
- Create: `traceflow/src/TraceFlow.Domain/Codes/CodeFormatStorage.cs`
- Create: `traceflow/src/TraceFlow.Domain/Versioning/RowVer.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Codes/CodeFormatStorageTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Versioning/RowVerTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/Migration0007Tests.cs`
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/Sql2008CompatibilityTests.cs:43`（前缀白名单加 `"0007"`）

**Interfaces:**
- Consumes: M0 `CodeFormat { Empty, Supported8Prefix20, Unsupported }`（TraceFlow.Domain/Codes/CodeFormat.cs）；MigrationRunner（`new MigrationRunner(conn, scriptsDir).RunAsync()`，文件名 `^\d{4}_.+\.sql$`，GO 分批，校验摘要不一致即停）。
- Produces:
  - `CodeFormatStorage.ToToken(CodeFormat format) → string`（EMPTY/S8P20/UNSUP）；`FromToken(string token) → CodeFormat`（未知 token 抛 `ArgumentOutOfRangeException`）。
  - `RowVer.ToHex(byte[] ver) → string`（16 位大写 hex）；`RowVer.FromHex(string hex) → byte[]`（非法抛 `FormatException`）。
  - 0007 后 schema：`source_line.code_format nvarchar(32) NOT NULL DEFAULT N'EMPTY'`；`trace_code.format nvarchar(32) NOT NULL`；`source_snapshot.stabilize_rounds int NOT NULL DEFAULT 0`、`source_snapshot.last_round_at datetime2(3) NULL`；`idempotency_record` 唯一键改为 `(idem_key, user_id)`；`product_mapping` 新增 `UX_pmap_pair UNIQUE(product_id, platform_drug_id)`；`supplier_mapping` 新增 `UX_smap_pair UNIQUE(supplier_id, enterprise_version_id)`；`import_batch` 新增 `staged_content nvarchar(max) NULL`（暂存 CSV 原文，供 import-stage 作业重解析；Task 10 消费）。

- [ ] **Step 1: 写失败测试（单元）**

`traceflow/tests/TraceFlow.UnitTests/Codes/CodeFormatStorageTests.cs`：

```csharp
using TraceFlow.Domain.Codes;

namespace TraceFlow.UnitTests.Codes;

public class CodeFormatStorageTests
{
    [Theory]
    [InlineData(CodeFormat.Empty, "EMPTY")]
    [InlineData(CodeFormat.Supported8Prefix20, "S8P20")]
    [InlineData(CodeFormat.Unsupported, "UNSUP")]
    public void Token_roundtrip(CodeFormat format, string token)
    {
        Assert.Equal(token, CodeFormatStorage.ToToken(format));
        Assert.Equal(format, CodeFormatStorage.FromToken(token));
    }

    [Fact]
    public void All_tokens_fit_legacy_nvarchar16_and_widened_column()
    {
        // M0 携带项 1：Supported8Prefix20 枚举名 18 字符放不下 nvarchar(16)；token 必须 ≤16。
        foreach (CodeFormat f in Enum.GetValues<CodeFormat>())
            Assert.True(CodeFormatStorage.ToToken(f).Length <= 16);
    }

    [Fact]
    public void Unknown_token_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => CodeFormatStorage.FromToken("Supported8Prefix20"));

    [Fact]
    public void Legacy_empty_default_token_accepted()
        => Assert.Equal(CodeFormat.Empty, CodeFormatStorage.FromToken("EMPTY"));
}
```

`traceflow/tests/TraceFlow.UnitTests/Versioning/RowVerTests.cs`：

```csharp
using TraceFlow.Domain.Versioning;

namespace TraceFlow.UnitTests.Versioning;

public class RowVerTests
{
    [Fact]
    public void Roundtrip_16_upper_hex()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x0a, 0xbc, 0x12, 0xff };
        var hex = RowVer.ToHex(bytes);
        Assert.Equal("000000000ABC12FF", hex);
        Assert.Equal(bytes, RowVer.FromHex(hex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000000ABC12F")]     // 15 位
    [InlineData("000000000ABC12FFF")]   // 17 位
    [InlineData("000000000ABC12GG")]    // 非 hex
    public void Invalid_hex_throws_FormatException(string bad)
        => Assert.Throws<FormatException>(() => RowVer.FromHex(bad));
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter "FullyQualifiedName~CodeFormatStorageTests|FullyQualifiedName~RowVerTests"`
Expected: 编译失败（CodeFormatStorage/RowVer 不存在）。

- [ ] **Step 3: 实现**

`traceflow/src/TraceFlow.Domain/Codes/CodeFormatStorage.cs`：

```csharp
namespace TraceFlow.Domain.Codes;

/// <summary>
/// M0 携带项 1：CodeFormat 枚举名（Supported8Prefix20=18 字符）超出 M0 DDL nvarchar(16)。
/// 持久层一律经此映射为短 token；0007 迁移同时把列加宽到 nvarchar(32) 双保险。
/// 禁止 enum.ToString() 直接落库。
/// </summary>
public static class CodeFormatStorage
{
    public static string ToToken(CodeFormat format) => format switch
    {
        CodeFormat.Empty => "EMPTY",
        CodeFormat.Supported8Prefix20 => "S8P20",
        CodeFormat.Unsupported => "UNSUP",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知 CodeFormat"),
    };

    public static CodeFormat FromToken(string token) => token switch
    {
        "EMPTY" => CodeFormat.Empty,
        "S8P20" => CodeFormat.Supported8Prefix20,
        "UNSUP" => CodeFormat.Unsupported,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "未知 format 存储 token"),
    };
}
```

`traceflow/src/TraceFlow.Domain/Versioning/RowVer.cs`：

```csharp
namespace TraceFlow.Domain.Versioning;

/// <summary>API-01：rowversion 序列化为 16 位大写十六进制字符串（无连字符）。</summary>
public static class RowVer
{
    public static string ToHex(byte[] ver) => Convert.ToHexString(ver);

    public static byte[] FromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 16)
            throw new FormatException("rowversion 须为 16 位十六进制字符串");
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c))
                throw new FormatException("rowversion 含非十六进制字符");
        return Convert.FromHexString(hex);
    }
}
```

- [ ] **Step 4: 运行单元测试确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter "FullyQualifiedName~CodeFormatStorageTests|FullyQualifiedName~RowVerTests"`
Expected: PASS（9 用例）。

- [ ] **Step 5: 写失败测试（集成）**

`traceflow/tests/TraceFlow.IntegrationTests/Migrations/Migration0007Tests.cs`：

```csharp
using Dapper;
using TraceFlow.Domain.Codes;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class Migration0007Tests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Migration0007Tests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Format_columns_widened_and_default_token()
    {
        await using var c = await _fx.OpenAsync();
        var len = await c.ExecuteScalarAsync<int>(@"
SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='source_line' AND COLUMN_NAME='code_format'");
        Assert.Equal(32, len);
        var len2 = await c.ExecuteScalarAsync<int>(@"
SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='trace_code' AND COLUMN_NAME='format'");
        Assert.Equal(32, len2);
        // 全枚举名（18 字符）现在也能放下；token 写入/读回一致
        await c.ExecuteAsync("INSERT INTO dbo.trace_code(code_normalized,code_raw,format) VALUES(N'81234567890123456789',N'81234567890123456789',@f)",
            new { f = CodeFormatStorage.ToToken(CodeFormat.Supported8Prefix20) });
        var stored = await c.ExecuteScalarAsync<string>("SELECT format FROM dbo.trace_code WHERE code_normalized=N'81234567890123456789'");
        Assert.Equal(CodeFormat.Supported8Prefix20, CodeFormatStorage.FromToken(stored!));
    }

    [Fact]
    public async Task Snapshot_stabilization_columns_exist()
    {
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='source_snapshot' AND COLUMN_NAME IN ('stabilize_rounds','last_round_at')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Idempotency_unique_now_user_scoped()
    {
        await using var c = await _fx.OpenAsync();
        // 同键不同用户允许两条（M0 携带项 3：跨用户重放隔离）
        await c.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(N'k0007',REPLICATE('a',64),1,200,N'{}')");
        await c.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(N'k0007',REPLICATE('a',64),2,200,N'{}')");
        // 同键同用户仍唯一
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body)
VALUES(N'k0007',REPLICATE('a',64),1,200,N'{}')"));
    }

    [Fact]
    public async Task Mapping_pair_unique_indexes_exist()
    {
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM sys.indexes WHERE name IN ('UX_pmap_pair','UX_smap_pair')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Import_batch_staged_content_column_exists()
    {
        await using var c = await _fx.OpenAsync();
        var ok = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='import_batch' AND COLUMN_NAME='staged_content' AND DATA_TYPE='nvarchar' AND CHARACTER_MAXIMUM_LENGTH=-1");
        Assert.Equal(1, ok);
    }

    [Fact]
    public async Task Migration_0007_recorded_with_checksum()
    {
        await using var c = await _fx.OpenAsync();
        var ok = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.schema_version WHERE version=7 AND success=1 AND name LIKE '0007_%'");
        Assert.Equal(1, ok);
    }
}
```

- [ ] **Step 6: 运行确认失败**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~Migration0007Tests"`
Expected: FAIL（列宽 16、无 stabilize 列、全局唯一键拒绝第二条、无 pair 索引、无 staged_content 列、无 version=7）。

- [ ] **Step 7: 写迁移**

`traceflow/db/migrations/0007_m1_source_master.sql`（2008 R2 语法；不修改 0001–0006 任何字节——MigrationRunner 校验摘要）：

```sql
-- 0007 M1：format 加宽（M0 携带项 1）/稳定期列/幂等键用户作用域/映射对唯一
ALTER TABLE dbo.source_line DROP CONSTRAINT DF_line_codefmt;
ALTER TABLE dbo.source_line ALTER COLUMN code_format nvarchar(32) NOT NULL;
UPDATE dbo.source_line SET code_format = N'EMPTY' WHERE code_format = N'Empty';
ALTER TABLE dbo.source_line ADD CONSTRAINT DF_line_codefmt DEFAULT N'EMPTY' FOR code_format;
GO
ALTER TABLE dbo.trace_code ALTER COLUMN format nvarchar(32) NOT NULL;
GO
ALTER TABLE dbo.source_snapshot ADD stabilize_rounds int NOT NULL CONSTRAINT DF_snapshot_rounds DEFAULT 0;
ALTER TABLE dbo.source_snapshot ADD last_round_at datetime2(3) NULL;
GO
ALTER TABLE dbo.idempotency_record DROP CONSTRAINT UQ_idempotency_key;
ALTER TABLE dbo.idempotency_record ADD CONSTRAINT UQ_idempotency_user_key UNIQUE (idem_key, user_id);
GO
CREATE UNIQUE INDEX UX_pmap_pair ON dbo.product_mapping(product_id, platform_drug_id);
CREATE UNIQUE INDEX UX_smap_pair ON dbo.supplier_mapping(supplier_id, enterprise_version_id);
GO
ALTER TABLE dbo.import_batch ADD staged_content nvarchar(max) NULL;
```

注意：`trace_code.format` 无默认约束（0005 核实），直接 ALTER COLUMN。`source_line.code_format` 无 CHECK/索引引用，DROP DEFAULT→ALTER→重建 DEFAULT 安全。IntegrationTests csproj 已按通配 `..\..\db\migrations\*.sql` 复制脚本，无需改 csproj。

同一步骤内扩展静态门禁前缀白名单：`traceflow/tests/TraceFlow.IntegrationTests/Migrations/Sql2008CompatibilityTests.cs` 第 43 行

```csharp
        foreach (var prefix in new[] { "0001", "0002", "0003", "0004", "0005", "0006", "0007" })
```

（该测试按目录扫描全部 `*.sql`，0007 自动进入禁词扫描；白名单断言确保门禁覆盖新脚本。0007 内容不含任何禁词。）

- [ ] **Step 8: 运行集成测试确认通过 + 全量回归**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: Migration0007Tests 6/6 PASS；M0 既有 45 集成用例全绿（0007 不破坏既有约束；CodeConstraintTests 的 N'Supported' 种子仍合法——format 列无 CHECK）。

- [ ] **Step 9: Commit**

```bash
git add traceflow/db/migrations/0007_m1_source_master.sql traceflow/src/TraceFlow.Domain/Codes/CodeFormatStorage.cs traceflow/src/TraceFlow.Domain/Versioning/RowVer.cs traceflow/tests/TraceFlow.UnitTests/Codes/CodeFormatStorageTests.cs traceflow/tests/TraceFlow.UnitTests/Versioning/RowVerTests.cs traceflow/tests/TraceFlow.IntegrationTests/Migrations/Migration0007Tests.cs traceflow/tests/TraceFlow.IntegrationTests/Migrations/Sql2008CompatibilityTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(M1): 0007 migration widens format columns, adds stabilization and user-scoped idempotency"
```

---

### Task 2: Domain 值对象（身份匹配/批准文号/时间/效期/哈希/窗口/CSV）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Matching/DrugIdentity.cs`（含 IdentityMatch）
- Create: `traceflow/src/TraceFlow.Domain/Matching/DrugIdentityMatcher.cs`
- Create: `traceflow/src/TraceFlow.Domain/Matching/ApprovalNoNormalizer.cs`
- Create: `traceflow/src/TraceFlow.Domain/Matching/MappingStates.cs`
- Create: `traceflow/src/TraceFlow.Domain/Sources/ExpiryParser.cs`
- Create: `traceflow/src/TraceFlow.Domain/Sources/BusinessTimeParser.cs`
- Create: `traceflow/src/TraceFlow.Domain/Sources/SourceHasher.cs`
- Create: `traceflow/src/TraceFlow.Domain/Sources/SyncPolicy.cs`（含 SyncWindowPolicy）
- Create: `traceflow/src/TraceFlow.Domain/Master/CsvReader.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Matching/DrugIdentityMatcherTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Matching/ApprovalNoNormalizerTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Sources/SourceValueObjectTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Master/CsvReaderTests.cs`

**Interfaces:**
- Consumes: 无（纯 Domain，零依赖）。
- Produces（后续任务逐字消费）:
  - `sealed record DrugIdentity(string? ApprovalNoNorm, string? ManufacturerIdentity, string? DosageForm, string? Strength, string? PackageSpec)`
  - `sealed record IdentityMatch(IReadOnlyList<string> Equal, IReadOnlyList<string> Missing, IReadOnlyList<string> Conflicts)`，属性 `IsFullMatch`（Equal.Count==5）、`HasConflict`、`HasOverlap`（Equal.Count>0）
  - `static IdentityMatch DrugIdentityMatcher.Evaluate(DrugIdentity local, DrugIdentity platform)`；字段名常量 `"approvalNo","manufacturerIdentity","dosageForm","strength","packageSpec"`
  - `static string? ApprovalNoNormalizer.Normalize(string? raw)`
  - `static class MappingStates`：`Unresolved/Candidate/AutoConfirmed/ManualConfirmed/Conflict/ReviewRequired/Disabled` 常量 + `IReadOnlyList<string> Active`（两种 CONFIRMED）
  - `static string ExpiryParser.ParsePrecision(string? raw)` → `"DAY"/"MONTH"/"UNKNOWN"`
  - `static DateTime? BusinessTimeParser.ParseUtc(string? dateRaw, string? timeRaw)`（Asia/Shanghai→UTC；非法→null）；`public static readonly TimeZoneInfo BusinessTimeParser.Shanghai`（供业务日换算）
  - `static string SourceHasher.LineHash(string lineKey, string productId, decimal qty, string? unit, string? batch, string? expiry, string? traceCodeRaw, string eventKind)`；`static string SourceHasher.DocumentHash(string docKey, DateTime? businessUtc, IReadOnlyList<string> sortedLineHashes)`（SHA256 大写 hex）
  - `sealed record SyncPolicy(int PollSeconds, int RescanHours, int DailyRescanDays, int StabilizeSeconds, int RoundIntervalSeconds)` + `SyncPolicy.Default = new(30,72,30,10,5)`
  - `static (DateTime Start, DateTime End) SyncWindowPolicy.Incremental(DateTime? checkpointEndUtc, DateTime nowUtc, SyncPolicy p)`；`static (DateTime Start, DateTime End) SyncWindowPolicy.Daily(DateTime nowUtc, SyncPolicy p)`
  - `static IReadOnlyList<IReadOnlyDictionary<string,string>> CsvReader.Parse(string text, IReadOnlyList<string> requiredColumns)`；`sealed class CsvFormatException : Exception`（Message 含行号）

- [ ] **Step 1: 写失败测试**

`traceflow/tests/TraceFlow.UnitTests/Matching/DrugIdentityMatcherTests.cs`：

```csharp
using TraceFlow.Domain.Matching;

namespace TraceFlow.UnitTests.Matching;

public class DrugIdentityMatcherTests
{
    private static readonly DrugIdentity Full =
        new("国药准字H20051234", "ENT-MFR-001", "片剂", "0.25g", "12片/盒");

    [Fact]
    public void All_five_equal_is_full_match() // AT-012 单元侧
    {
        var m = DrugIdentityMatcher.Evaluate(Full, Full with { });
        Assert.True(m.IsFullMatch);
        Assert.False(m.HasConflict);
        Assert.Equal(5, m.Equal.Count);
    }

    [Fact]
    public void Same_name_different_package_conflicts_not_full() // AT-013：名称不在五项内，包装矛盾→冲突字段
    {
        var m = DrugIdentityMatcher.Evaluate(Full, Full with { PackageSpec = "24片/盒" });
        Assert.False(m.IsFullMatch);
        Assert.True(m.HasConflict);
        Assert.Contains("packageSpec", m.Conflicts);
        Assert.Equal(4, m.Equal.Count);
    }

    [Fact]
    public void Missing_field_never_wildcards() // AT-014
    {
        var m = DrugIdentityMatcher.Evaluate(Full with { ManufacturerIdentity = null }, Full);
        Assert.False(m.IsFullMatch);
        Assert.False(m.HasConflict);
        Assert.Contains("manufacturerIdentity", m.Missing);
        Assert.Equal(4, m.Equal.Count);
    }

    [Fact]
    public void Empty_string_counts_as_missing_not_equal()
    {
        var m = DrugIdentityMatcher.Evaluate(Full with { Strength = "  " }, Full);
        Assert.Contains("strength", m.Missing);
        Assert.False(m.IsFullMatch);
    }

    [Fact]
    public void Zero_overlap_has_no_equal_no_conflict()
    {
        var m = DrugIdentityMatcher.Evaluate(
            new DrugIdentity(null, null, null, null, null),
            new DrugIdentity("X", "Y", "Z", "W", "V"));
        Assert.False(m.HasOverlap);
        Assert.Equal(5, m.Missing.Count);
    }

    [Fact]
    public void Trim_before_compare()
    {
        var m = DrugIdentityMatcher.Evaluate(Full with { DosageForm = " 片剂 " }, Full);
        Assert.True(m.IsFullMatch);
    }
}
```

`traceflow/tests/TraceFlow.UnitTests/Matching/ApprovalNoNormalizerTests.cs`：

```csharp
using TraceFlow.Domain.Matching;

namespace TraceFlow.UnitTests.Matching;

public class ApprovalNoNormalizerTests
{
    [Theory]
    [InlineData("国药准字H20051234", "国药准字H20051234")]
    [InlineData(" 国药准字 h20051234 ", "国药准字H20051234")]
    [InlineData("国药准字H2005-1234", "国药准字H20051234")] // 分隔符去除，数字版本信息保留
    [InlineData("H20051234（再注册）", "H20051234再注册")]   // 有意义文字保留
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Normalize_keeps_letters_digits_version(string? raw, string? expected)
        => Assert.Equal(expected, ApprovalNoNormalizer.Normalize(raw));
}
```

`traceflow/tests/TraceFlow.UnitTests/Sources/SourceValueObjectTests.cs`：

```csharp
using TraceFlow.Domain.Sources;

namespace TraceFlow.UnitTests.Sources;

public class SourceValueObjectTests
{
    [Theory]
    [InlineData("2027-05-31", "DAY")]
    [InlineData("2027/5/3", "DAY")]
    [InlineData("2027-05", "MONTH")]
    [InlineData("2027/5", "MONTH")]
    [InlineData("20270531", "UNKNOWN")] // 未验证格式不猜精度
    [InlineData("", "UNKNOWN")]
    [InlineData(null, "UNKNOWN")]
    [InlineData("长期", "UNKNOWN")]
    public void Expiry_precision(string? raw, string expected)
        => Assert.Equal(expected, ExpiryParser.ParsePrecision(raw));

    [Fact]
    public void Business_time_shanghai_to_utc()
    {
        var utc = BusinessTimeParser.ParseUtc("2026-11-20", "14:32:05");
        Assert.Equal(new DateTime(2026, 11, 20, 6, 32, 5, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Business_time_date_only_midnight()
    {
        var utc = BusinessTimeParser.ParseUtc("2026-11-20", null);
        Assert.Equal(new DateTime(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc), utc);
    }

    [Theory]
    [InlineData("2026-13-40", "00:00:00")]
    [InlineData("not-a-date", null)]
    [InlineData(null, null)]
    public void Invalid_time_returns_null(string? d, string? t)
        => Assert.Null(BusinessTimeParser.ParseUtc(d, t));

    [Fact]
    public void Line_hash_deterministic_and_sensitive()
    {
        var h1 = SourceHasher.LineHash("1", "SP0001", 2m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        var h2 = SourceHasher.LineHash("1", "SP0001", 2m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        var h3 = SourceHasher.LineHash("1", "SP0001", 3m, "盒", "P1", "2027-05", "81234567890123456789", "SALE");
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(64, h1.Length);
    }

    [Fact]
    public void Document_hash_order_insensitive_via_sorted_inputs()
    {
        var a = SourceHasher.DocumentHash("000123", new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), new[] { "aa", "bb" });
        var b = SourceHasher.DocumentHash("000123", new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), new[] { "aa", "bb" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Incremental_window_overlaps_checkpoint() // SYNC-01/04
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var cp = now.AddHours(-1);
        var (start, end) = SyncWindowPolicy.Incremental(cp, now, SyncPolicy.Default);
        Assert.Equal(cp.AddHours(-72), start);
        Assert.Equal(now, end);
    }

    [Fact]
    public void Gap_backfill_never_truncates() // AT-007：停机 10 天
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var cp = now.AddDays(-10);
        var (start, end) = SyncWindowPolicy.Incremental(cp, now, SyncPolicy.Default);
        Assert.True(start <= cp, "窗口起点必须不晚于检查点终点，缺口全覆盖");
        Assert.Equal(now, end);
        Assert.True(start < now.AddDays(-10).AddHours(-71));
    }

    [Fact]
    public void First_run_uses_daily_lookback()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var (start, _) = SyncWindowPolicy.Incremental(null, now, SyncPolicy.Default);
        Assert.Equal(now.AddDays(-30), start);
    }

    [Fact]
    public void Daily_window_30_days()
    {
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var (start, end) = SyncWindowPolicy.Daily(now, SyncPolicy.Default);
        Assert.Equal(now.AddDays(-30), start);
        Assert.Equal(now, end);
    }
}
```

`traceflow/tests/TraceFlow.UnitTests/Master/CsvReaderTests.cs`：

```csharp
using TraceFlow.Domain.Master;

namespace TraceFlow.UnitTests.Master;

public class CsvReaderTests
{
    private static readonly string[] Cols = { "spid", "name" };

    [Fact]
    public void Parses_header_and_rows_with_quotes()
    {
        var rows = CsvReader.Parse("spid,name\nSP0001,\"阿莫西林,胶囊\"\nSP0002,\"含\"\"引号\"\"\"\n", Cols);
        Assert.Equal(2, rows.Count);
        Assert.Equal("阿莫西林,胶囊", rows[0]["name"]);
        Assert.Equal("含\"引号\"", rows[1]["name"]);
    }

    [Fact]
    public void Bom_and_crlf_accepted()
    {
        var rows = CsvReader.Parse("\uFEFFspid,name\r\nSP0001,盒\r\n", Cols);
        Assert.Single(rows);
        Assert.Equal("SP0001", rows[0]["spid"]);
    }

    [Fact]
    public void Missing_column_throws_with_row_context()
    {
        var ex = Assert.Throws<CsvFormatException>(() => CsvReader.Parse("spid,other\nA,B\n", Cols));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Wrong_field_count_throws_with_row_number()
    {
        var ex = Assert.Throws<CsvFormatException>(() => CsvReader.Parse("spid,name\nA\n", Cols));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void Empty_lines_skipped_values_trimmed()
    {
        var rows = CsvReader.Parse("spid,name\n\n SP0001 , 盒 \n\n", Cols);
        Assert.Single(rows);
        Assert.Equal("SP0001", rows[0]["spid"]);
        Assert.Equal("盒", rows[0]["name"]);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter "FullyQualifiedName~DrugIdentityMatcherTests|FullyQualifiedName~ApprovalNoNormalizerTests|FullyQualifiedName~SourceValueObjectTests|FullyQualifiedName~CsvReaderTests"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现（完整代码）**

`traceflow/src/TraceFlow.Domain/Matching/DrugIdentity.cs`：

```csharp
namespace TraceFlow.Domain.Matching;

/// <summary>MATCH-01 五项身份。全部可空：缺字段≠通配（AT-014）。</summary>
public sealed record DrugIdentity(string? ApprovalNoNorm, string? ManufacturerIdentity,
    string? DosageForm, string? Strength, string? PackageSpec);

public sealed record IdentityMatch(IReadOnlyList<string> Equal, IReadOnlyList<string> Missing, IReadOnlyList<string> Conflicts)
{
    public bool IsFullMatch => Equal.Count == 5;
    public bool HasConflict => Conflicts.Count > 0;
    public bool HasOverlap => Equal.Count > 0;
}
```

`traceflow/src/TraceFlow.Domain/Matching/DrugIdentityMatcher.cs`：

```csharp
namespace TraceFlow.Domain.Matching;

/// <summary>
/// MATCH-01/02：五项一致才可能自动确认；任一字段矛盾→冲突；任一字段缺失→不完整（不通配）。
/// 名称/条码/批号/前七位不在五项内，绝不单独自动绑定。
/// </summary>
public static class DrugIdentityMatcher
{
    public static IdentityMatch Evaluate(DrugIdentity local, DrugIdentity platform)
    {
        var equal = new List<string>();
        var missing = new List<string>();
        var conflicts = new List<string>();
        Compare("approvalNo", local.ApprovalNoNorm, platform.ApprovalNoNorm, equal, missing, conflicts);
        Compare("manufacturerIdentity", local.ManufacturerIdentity, platform.ManufacturerIdentity, equal, missing, conflicts);
        Compare("dosageForm", local.DosageForm, platform.DosageForm, equal, missing, conflicts);
        Compare("strength", local.Strength, platform.Strength, equal, missing, conflicts);
        Compare("packageSpec", local.PackageSpec, platform.PackageSpec, equal, missing, conflicts);
        return new IdentityMatch(equal, missing, conflicts);
    }

    private static void Compare(string field, string? a, string? b,
        List<string> equal, List<string> missing, List<string> conflicts)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) missing.Add(field);
        else if (string.Equals(x, y, StringComparison.Ordinal)) equal.Add(field);
        else conflicts.Add(field);
    }
}
```

`traceflow/src/TraceFlow.Domain/Matching/ApprovalNoNormalizer.cs`：

```csharp
namespace TraceFlow.Domain.Matching;

/// <summary>MATCH-02：保留有意义字母、数字及版本信息；去空白/标点，大写。raw 另行保留。</summary>
public static class ApprovalNoNormalizer
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var kept = raw.Where(char.IsLetterOrDigit).ToArray();
        if (kept.Length == 0) return null;
        return new string(kept).ToUpperInvariant();
    }
}
```

`traceflow/src/TraceFlow.Domain/Matching/MappingStates.cs`：

```csharp
namespace TraceFlow.Domain.Matching;

/// <summary>MATCH-04 七态（0003 CK_pmap_state/CK_smap_state 逐字一致）。</summary>
public static class MappingStates
{
    public const string Unresolved = "UNRESOLVED";
    public const string Candidate = "CANDIDATE";
    public const string AutoConfirmed = "AUTO_CONFIRMED";
    public const string ManualConfirmed = "MANUAL_CONFIRMED";
    public const string Conflict = "CONFLICT";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string Disabled = "DISABLED";

    public static readonly IReadOnlyList<string> Active = new[] { AutoConfirmed, ManualConfirmed };
}
```

`traceflow/src/TraceFlow.Domain/Sources/ExpiryParser.cs`：

```csharp
using System.Text.RegularExpressions;

namespace TraceFlow.Domain.Sources;

/// <summary>SRC-05：效期精度 DAY/MONTH/UNKNOWN；不把月效期虚构成具体日，未验证格式不猜。</summary>
public static class ExpiryParser
{
    private static readonly Regex Day = new(@"^\d{4}[-/]\d{1,2}[-/]\d{1,2}$", RegexOptions.Compiled);
    private static readonly Regex Month = new(@"^\d{4}[-/]\d{1,2}$", RegexOptions.Compiled);

    public static string ParsePrecision(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return "UNKNOWN";
        if (Day.IsMatch(s)) return "DAY";
        if (Month.IsMatch(s)) return "MONTH";
        return "UNKNOWN";
    }
}
```

`traceflow/src/TraceFlow.Domain/Sources/BusinessTimeParser.cs`：

```csharp
using System.Globalization;

namespace TraceFlow.Domain.Sources;

/// <summary>
/// DB-02：KSOA rq(日期)+ontime(时刻) 按 Asia/Shanghai 解析为 UTC。
/// 部署目标 Windows（ADR-0001），用 Windows 时区 ID "China Standard Time"。
/// 解析失败返回 null（调用方记 BAD_BUSINESS_TIME 异常，不猜时间）。
/// </summary>
public static class BusinessTimeParser
{
    public static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "yyyy/M/d", "yyyy/MM/dd", "yyyy-M-d" };
    private static readonly string[] TimeFormats = { @"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm" }; // TimeSpan.TryParseExact 语法（执行期裁决：原 "HH:mm:ss" 为 DateTime 语法，TimeSpan 拒绝）

    public static DateTime? ParseUtc(string? dateRaw, string? timeRaw)
    {
        if (string.IsNullOrWhiteSpace(dateRaw)) return null;
        if (!DateTime.TryParseExact(dateRaw.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return null;
        var time = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(timeRaw) &&
            !TimeSpan.TryParseExact(timeRaw.Trim(), TimeFormats, CultureInfo.InvariantCulture, out time))
            return null;
        var local = date.Date + time;
        // datetime2 域守卫（1753-9999）。不可用 DateTime.MinValue/MaxValue 做 ConvertTimeToUtc 边界比较——会溢出。
        if (local.Year < 1753 || local.Year > 9999) return null;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Shanghai);
    }
}
```

`traceflow/src/TraceFlow.Domain/Sources/SourceHasher.cs`：

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TraceFlow.Domain.Sources;

/// <summary>SYNC-01/02：行/单内容摘要（变更检测与稳定期两轮一致判定）。SHA256 大写 hex。
/// 执行期裁决（Task 2 fix round 1）：自由文本字段统一转义分隔符，防边界移位碰撞。</summary>
public static class SourceHasher
{
    public static string LineHash(string lineKey, string productId, decimal qty, string? unit,
        string? batch, string? expiry, string? traceCodeRaw, string eventKind)
        => Sha(string.Join("|", Esc(lineKey), Esc(productId),
            Esc(qty.ToString("0.000000", CultureInfo.InvariantCulture)),
            Esc(unit), Esc(batch), Esc(expiry), Esc(traceCodeRaw), Esc(eventKind)));

    public static string DocumentHash(string docKey, DateTime? businessUtc, IReadOnlyList<string> sortedLineHashes)
        => Sha(Esc(docKey) + "|" + Esc(businessUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))
            + "|" + string.Join(";", sortedLineHashes));

    private static string Esc(string? s) => (s ?? "").Replace("\\", "\\\\").Replace("|", "\\|");

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
```

Task 2 回归测试（fix round 1 追加，`SourceValueObjectTests`）：`Line_hash_delimiter_shift_does_not_collide`（unit="盒|P1"/batch="" vs unit="盒"/batch="P1" 守卫对）、`Line_hash_pipe_in_batch_does_not_collide`（真实旧码碰撞对，RED 证据）、`Line_hash_backslash_escape_does_not_collide`（反斜杠双写守卫）。

`traceflow/src/TraceFlow.Domain/Sources/SyncPolicy.cs`：

```csharp
namespace TraceFlow.Domain.Sources;

/// <summary>SYNC-02/04 默认工程参数（可调有界，设置服务校验）。非监管时限。</summary>
public sealed record SyncPolicy(int PollSeconds, int RescanHours, int DailyRescanDays, int StabilizeSeconds, int RoundIntervalSeconds)
{
    public static readonly SyncPolicy Default = new(30, 72, 30, 10, 5);
}

public static class SyncWindowPolicy
{
    /// <summary>
    /// SYNC-01/04：起点=检查点终点−回扫窗（重叠回扫采到更小单号/晚到行，AT-004）；
    /// 起点恒 ≤ 检查点终点 → 任意停机缺口全覆盖，绝不截断成 now−72h（AT-007）。
    /// 首次无检查点：now−每日回扫天数。
    /// </summary>
    public static (DateTime Start, DateTime End) Incremental(DateTime? checkpointEndUtc, DateTime nowUtc, SyncPolicy p)
        => (checkpointEndUtc.HasValue ? checkpointEndUtc.Value.AddHours(-p.RescanHours) : nowUtc.AddDays(-p.DailyRescanDays), nowUtc);

    public static (DateTime Start, DateTime End) Daily(DateTime nowUtc, SyncPolicy p)
        => (nowUtc.AddDays(-p.DailyRescanDays), nowUtc);
}
```

`traceflow/src/TraceFlow.Domain/Master/CsvReader.cs`：

```csharp
using System.Text;

namespace TraceFlow.Domain.Master;

public sealed class CsvFormatException : Exception
{
    public CsvFormatException(string message) : base(message) { }
}

/// <summary>
/// MD-03 导入 CSV：UTF-8（可带 BOM）、RFC4180 子集（引号/双引号转义/逗号/换行）。
/// 表头必须恰好覆盖 requiredColumns（顺序不限，不允许多余列）。值去首尾空白。
/// </summary>
public static class CsvReader
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text, IReadOnlyList<string> requiredColumns)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var records = SplitRecords(text);
        if (records.Count == 0) throw new CsvFormatException("CSV 为空：缺少表头行");
        var header = records[0];
        foreach (var col in requiredColumns)
            if (!header.Contains(col))
                throw new CsvFormatException($"CSV 表头缺少必需列 {col}");
        if (header.Count != requiredColumns.Count)
            throw new CsvFormatException($"CSV 表头含未定义列（期望 {requiredColumns.Count} 列，实际 {header.Count} 列）");
        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (var i = 1; i < records.Count; i++)
        {
            var fields = records[i];
            if (fields.Count != header.Count)
                throw new CsvFormatException($"CSV 第 {i + 1} 行字段数 {fields.Count} 与表头 {header.Count} 不一致");
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var j = 0; j < header.Count; j++) dict[header[j]] = fields[j].Trim();
            rows.Add(dict);
        }
        return rows;
    }

    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(sb.ToString()); sb.Clear();
                if (fields.Count > 1 || fields[0].Trim().Length > 0) records.Add(fields);
                fields = new List<string>();
            }
            else sb.Append(ch);
        }
        if (inQuotes) throw new CsvFormatException("CSV 引号未闭合");
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            if (fields.Count > 1 || fields[0].Trim().Length > 0) records.Add(fields);
        }
        return records;
    }
}
```

- [ ] **Step 4: 运行单元测试确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全部 PASS（M0 87 + 本任务新增 30）。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Domain/Matching traceflow/src/TraceFlow.Domain/Sources traceflow/src/TraceFlow.Domain/Master traceflow/tests/TraceFlow.UnitTests/Matching traceflow/tests/TraceFlow.UnitTests/Sources traceflow/tests/TraceFlow.UnitTests/Master
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(M1): domain value objects for identity matching, source time/hash, sync window, CSV"
```

---
### Task 3: 幂等加固（DI 实例化 / 用户作用域 / query hash / 并发 2627 语义）

关闭 M0 携带项 3（M-1/M-4）：键按 user_id 作用域、并发同键 2627→重放或 409、hash 纳入 query string、IdempotencyExecutor 走 DI。AT-066 既有 e2e 必须保持绿。

**Files:**
- Modify: `traceflow/src/TraceFlow.Host/Security/IdempotencyExecutor.cs`（静态→实例，全文重写）
- Modify: `traceflow/src/TraceFlow.Infrastructure/Security/IdempotencyStore.cs:12`（TryGet 加 user_id 谓词）
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs:17-18`（注入执行器）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs:32`（DI 注册）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Security/IdempotencyHardeningTests.cs`

**Interfaces:**
- Consumes: M0 `IIdempotencyStore.TryGetAsync(conn, tx, key, requestHash, userId, ct) → Task<IdempotencyHit?>`（hash 不符抛 `IdempotencyConflictException`）、`SaveAsync(conn, tx, key, requestHash, userId, status, body, ct)`；`IdempotencyHit(int StatusCode, string Body)`；0007 后 `UQ_idempotency_user_key UNIQUE(idem_key, user_id)`；ApiErrors 已映射 `IdempotencyConflictException→409 IDEMPOTENCY_CONFLICT`。
- Produces:
  - `IdempotencyExecutor`（实例类，命名空间 `TraceFlow.Host.Security`）：ctor `(string connectionString, IIdempotencyStore store)`；`Task<IResult> RunAsync(HttpContext ctx, Func<SqlConnection, SqlTransaction, CancellationToken, Task<(int Status, string Body)>> action)`。**不再有 connectionString 参数**——后续 Task 6/10/11/13 的端点一律注入 `IdempotencyExecutor` 并调 `idem.RunAsync(ctx, action)`。
  - hash 公式（后续测试复用）：`SHA256(Method + "|" + Path + "|" + QueryString + "|" + body)` 大写 hex。
  - 并发同键语义（裁定）：Save 撞 2601/2627 → 回滚整个事务（业务+幂等）→ 无事务重读胜者记录 → 同 hash 重放胜者响应；异 hash 抛 `IdempotencyConflictException`（409）；重读为空同样抛冲突。

- [ ] **Step 1: 写失败测试**

`traceflow/tests/TraceFlow.IntegrationTests/Security/IdempotencyHardeningTests.cs`：

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
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
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~IdempotencyHardeningTests"`
Expected: FAIL（ctor 签名不符编译错误；用户作用域测试命中跨用户记录）。

- [ ] **Step 3: 实现**

`traceflow/src/TraceFlow.Host/Security/IdempotencyExecutor.cs` 全文替换：

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Security;

public sealed class IdempotencyExecutor
{
    private readonly string _connectionString;
    private readonly IIdempotencyStore _store;
    public IdempotencyExecutor(string connectionString, IIdempotencyStore store)
    {
        _connectionString = connectionString;
        _store = store;
    }

    public async Task<IResult> RunAsync(HttpContext ctx,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<(int Status, string Body)>> action)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is 0 or > 128)
            return Results.Json(new { code = "VALIDATION", message = "缺少或超长 Idempotency-Key", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        // M0 携带 3：hash 纳入 QueryString——同键同 body 不同 query 视为不同请求（409）
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            ctx.Request.Method + "|" + ctx.Request.Path + "|" + ctx.Request.QueryString + "|" + body)));
        var userId = long.Parse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        var hit = await _store.TryGetAsync(conn, tx, key, hash, userId, ctx.RequestAborted);
        if (hit is not null)
        {
            await tx.CommitAsync();
            return Results.Content(hit.Body, "application/json", statusCode: hit.StatusCode);
        }
        var (status, resultBody) = await action(conn, tx, ctx.RequestAborted);
        // 业务函数内部自行回滚并返回错误元组时（tx.Connection is null），跳过 Save/Commit 直接返回。
        if (tx.Connection is null)
            return Results.Content(resultBody, "application/json", statusCode: status);
        try
        {
            await _store.SaveAsync(conn, tx, key, hash, userId, status, resultBody, ctx.RequestAborted);
            await tx.CommitAsync(); // 幂等记录与业务同事务（API-02）
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // 并发同键竞态败者（M0 携带 3 裁定）：业务+幂等整体回滚，重读胜者记录。
            // 同 hash→重放胜者响应；异 hash→TryGet 抛 IdempotencyConflictException→409。
            await tx.RollbackAsync();
            var winner = await _store.TryGetAsync(conn, null!, key, hash, userId, ctx.RequestAborted);
            if (winner is null)
                throw new IdempotencyConflictException();
            return Results.Content(winner.Body, "application/json", statusCode: winner.StatusCode);
        }
        return Results.Content(resultBody, "application/json", statusCode: status);
    }
}
```

`traceflow/src/TraceFlow.Infrastructure/Security/IdempotencyStore.cs` TryGetAsync 的 SQL 改为：

```csharp
            "SELECT request_hash AS Hash, response_status AS Status, response_body AS Body FROM dbo.idempotency_record WHERE idem_key=@key AND user_id=@userId",
            new { key, userId }, tx);
```

`traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs`：创建用户端点签名改为注入执行器（action 内部逻辑逐字不动）：

```csharp
        app.MapPost("/api/v1/admin/users", (HttpContext ctx, IAuditLog audit, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
```

（其余 action 体与 `.WithMetadata(...)` 保持原样；`MapAdminEndpoints(this WebApplication app, string connectionString)` 签名不变——permissions 端点仍用 connectionString。）

`traceflow/src/TraceFlow.Host/Program.cs` 在 `builder.Services.AddSingleton<IIdempotencyStore, IdempotencyStore>();` 之后加：

```csharp
builder.Services.AddSingleton(sp => new IdempotencyExecutor(connectionString, sp.GetRequiredService<IIdempotencyStore>()));
```

- [ ] **Step 4: 运行确认通过 + 全量回归**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: IdempotencyHardeningTests 4/4 PASS；IdempotencyStoreTests 2/2 与 ApiGateTests（含 AT-066）保持绿。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Host/Security/IdempotencyExecutor.cs traceflow/src/TraceFlow.Infrastructure/Security/IdempotencyStore.cs traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Security/IdempotencyHardeningTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(M1): harden idempotency - DI executor, user-scoped keys, query hash, 2627 replay-or-conflict"
```

---

### Task 4: 输入验证 + 租约回收审计 + 服务异常契约 + 卫生 + BIN2 变体

关闭 M0 携带项 4/5/7/9 与 M-2/M-3/M-7/M-9/M-10：登录空用户名 NRE、admin 列宽、GrantAsync 2601、ApiErrors 400 文案、空连接串守卫、租约回收审计、静态门禁根断言、BIN2 opt-in、UnitTest1/EOF/冗余包。同时建立 M1 服务异常类型（Task 5/6/9/10 消费）。

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs`
- Modify: `traceflow/src/TraceFlow.Host/Security/ApiErrors.cs`
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/AuthEndpoints.cs:27-46`
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs`（列宽验证 + Grant 2601→409）
- Modify: `traceflow/src/TraceFlow.Host/Security/AdminInitializer.cs`（长度守卫 + 重复友好失败）
- Modify: `traceflow/src/TraceFlow.Infrastructure/Jobs/JobQueue.cs`（ctor 加 IAuditLog + 回收审计）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（IsNullOrWhiteSpace + JobQueue 工厂）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Jobs/JobQueueTests.cs`（10 处 ctor）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Jobs/SendRecoveryTests.cs`（6 处 ctor）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Sql2008Fixture.cs:49`（BIN2 opt-in）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/UploadConstraintTests.cs`（10 处 `N'BLOCKED'`→`N'Blocked'`）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/Sql2008CompatibilityTests.cs:20`（根存在断言）
- Modify: `traceflow/src/TraceFlow.Application/TraceFlow.Application.csproj`（移除 Identity.Core）
- Modify: `traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj`（加入 Identity.Core）
- Delete: `traceflow/tests/TraceFlow.UnitTests/UnitTest1.cs`、`traceflow/tests/TraceFlow.IntegrationTests/UnitTest1.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Jobs/LeaseRecoveryAuditTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/InputValidationTests.cs`

**Interfaces:**
- Consumes: M0 `IAuditLog.AppendAsync(conn, tx?, AuditEntry)`（AuditLog 无参 ctor）；`AuditEntry(Actor, ActorKind, Action, ObjectType, ObjectId?, Reason?, BeforeDigest?, AfterDigest?, RequestId?, RuleVersion?, Env)`；JobQueue 既有方法签名全部不变。
- Produces:
  - `TraceFlow.Application.Abstractions` 四个异常（均单参 message ctor）：`AdapterNotCertifiedException`（→422 ERP_NOT_CERTIFIED）、`SettingValidationException`（→422 VALIDATION）、`SettingVersionException`（→409 VERSION_CONFLICT）、`ImportBoundsException`（→422 IMPORT_BOUNDS）。
  - `JobQueue(string connectionString, IAuditLog audit)`——**后续所有 new JobQueue 一律两参**。
  - 回收审计动作名 `job.lease.recovered`，actor `SYSTEM`，actorKind `Worker`，objectType `outbox_job`，env=作业 env，afterDigest=JSON `{JobKind,IntentKey,prevOwner}`。
  - BIN2 开关：`TRACEFLOW_TEST_DB_BIN2=1` → 测试库 `COLLATE Latin1_General_BIN2`。

- [ ] **Step 1: 写失败测试（回收审计）**

`traceflow/tests/TraceFlow.IntegrationTests/Jobs/LeaseRecoveryAuditTests.cs`：

```csharp
using Dapper;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

public class LeaseRecoveryAuditTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public LeaseRecoveryAuditTests(Sql2008Fixture fx) => _fx = fx;

    private async Task<long> SeedOrgAsync()
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code=N'LRAT')
    INSERT INTO dbo.organization(org_code,name) VALUES(N'LRAT',N'回收审计测试店');
SELECT id FROM dbo.organization WHERE org_code=N'LRAT'");
    }

    [Fact]
    public async Task Expired_lease_recovery_writes_audit_row()
    {
        var orgId = await SeedOrgAsync();
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var intent = "lr-" + Guid.NewGuid().ToString("N");
        var jobId = await queue.EnqueueAsync(c, tx, orgId, "MOCK", "source-sync", intent, null, null, default);
        await tx.CommitAsync();
        var claimed = await queue.ClaimAsync("w-test", "MOCK", new[] { "source-sync" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claimed);
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id", new { id = jobId });

        var recovered = await queue.RecoverExpiredLeasesAsync(default);

        Assert.True(recovered >= 1);
        var audit = await c.ExecuteScalarAsync<string?>(
            "SELECT after_digest FROM dbo.audit_log WHERE action=N'job.lease.recovered' AND object_type=N'outbox_job' AND object_id=@id",
            new { id = jobId.ToString() });
        Assert.NotNull(audit);
        Assert.Contains("source-sync", audit);
        Assert.Contains("w-test", audit); // prevOwner 留痕
        var state = await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = jobId });
        Assert.Equal("PENDING", state);
        var env = await c.ExecuteScalarAsync<string>("SELECT env FROM dbo.audit_log WHERE action=N'job.lease.recovered' AND object_id=@id", new { id = jobId.ToString() });
        Assert.Equal("MOCK", env.Trim());
    }

    [Fact]
    public async Task Expired_upload_dispatch_is_not_recovered_and_not_audited()
    {
        var orgId = await SeedOrgAsync();
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var intent = "lr-d-" + Guid.NewGuid().ToString("N");
        var jobId = await queue.EnqueueAsync(c, tx, orgId, "MOCK", "upload-dispatch", intent, null, null, default);
        await tx.CommitAsync();
        await queue.ClaimAsync("w-test", "MOCK", new[] { "upload-dispatch" }, TimeSpan.FromMinutes(5), default);
        await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id", new { id = jobId });

        await queue.RecoverExpiredLeasesAsync(default);

        var state = await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id = jobId });
        Assert.Equal("RUNNING", state); // JOB-02：失租发送必须走发送恢复→回查
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'job.lease.recovered' AND object_id=@id", new { id = jobId.ToString() }));
    }
}
```

- [ ] **Step 2: 写失败测试（输入验证，WAF）**

`traceflow/tests/TraceFlow.IntegrationTests/Api/InputValidationTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class InputValidationTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public InputValidationTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'ivadmin')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'ivadmin',N'验证管理员',@h)",
            new { h = hasher.HashPassword("ivadmin", "Admin-Password-123") });
        var id = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.app_user WHERE username=N'ivadmin'");
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=N'system:manage')
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,N'system:manage',N'test')", new { id });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "ivadmin", password = "Admin-Password-123" });
        resp.EnsureSuccessStatusCode();
        var authed = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", authed!.Data.CsrfToken);
        return client;
    }

    private async Task<HttpClient> AnonClientWithCsrfAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        return client;
    }

    [Fact]
    public async Task Login_empty_username_returns_401_and_audits_empty_actor()
    {
        var client = await AnonClientWithCsrfAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "", password = "whatever123" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.True(await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'auth.login.failed' AND actor=N'(empty)'") >= 1);
    }

    [Fact]
    public async Task Login_null_username_returns_401_not_500()
    {
        var client = await AnonClientWithCsrfAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { password = "whatever123" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData(65, 0, 0)]   // username 超 64
    [InlineData(0, 101, 0)]  // displayName 超 100
    [InlineData(0, 0, 1001)] // reason 超 1000
    public async Task Admin_create_overlength_columns_return_422(int usernameLen, int displayLen, int reasonLen)
    {
        var client = await AdminClientAsync();
        var body = new
        {
            username = usernameLen > 0 ? new string('u', usernameLen) : "iv-ok-" + Guid.NewGuid().ToString("N")[..8],
            password = "1234567890",
            displayName = displayLen > 0 ? new string('d', displayLen) : null,
            reason = reasonLen > 0 ? new string('r', reasonLen) : null,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_create_null_password_returns_422_not_500()
    {
        var client = await AdminClientAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(new { username = "iv-np-" + Guid.NewGuid().ToString("N")[..8] }) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~LeaseRecoveryAuditTests|FullyQualifiedName~InputValidationTests"`
Expected: FAIL（JobQueue 单参 ctor 编译错误；空用户名 NRE→500；超长列→500）。

- [ ] **Step 4: 实现服务异常 + ApiErrors**

`traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs`：

```csharp
namespace TraceFlow.Application.Abstractions;

// M1 服务层异常→HTTP 契约（ApiErrors 统一映射；端点不各自写错误臂）。
public sealed class AdapterNotCertifiedException : Exception
{
    public AdapterNotCertifiedException(string message) : base(message) { }
}
public sealed class SettingValidationException : Exception
{
    public SettingValidationException(string message) : base(message) { }
}
public sealed class SettingVersionException : Exception
{
    public SettingVersionException(string message) : base(message) { }
}
public sealed class ImportBoundsException : Exception
{
    public ImportBoundsException(string message) : base(message) { }
}
```

`ApiErrors.HandleAsync` 的 switch 改为（保留既有臂，新增五臂 + 400 专属文案）：

```csharp
        var (status, code, retryable) = ex switch
        {
            IdempotencyConflictException => (409, "IDEMPOTENCY_CONFLICT", false),
            SecretUndecryptableException => (503, "SECRET_UNAVAILABLE", false),
            AdapterNotCertifiedException => (422, "ERP_NOT_CERTIFIED", false),
            SettingValidationException => (422, "VALIDATION", false),
            SettingVersionException => (409, "VERSION_CONFLICT", false),
            ImportBoundsException => (422, "IMPORT_BOUNDS", false),
            FormatException => (422, "VALIDATION", false), // RowVer.FromHex 等格式校验（全局约束：FormatException→端点 422）
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => (400, "CSRF_INVALID", false),
            BadHttpRequestException => (400, "BAD_REQUEST", false),
            System.Text.Json.JsonException => (400, "BAD_REQUEST", false),
            _ => (500, "INTERNAL", true),
        };
```

message switch 改为：

```csharp
        await ctx.Response.WriteAsJsonAsync(new { code, message = ex switch
        {
            IdempotencyConflictException or SecretUndecryptableException
                or AdapterNotCertifiedException or SettingValidationException
                or SettingVersionException or ImportBoundsException or FormatException => ex.Message,
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => "CSRF 令牌缺失或无效",
            BadHttpRequestException or System.Text.Json.JsonException => "请求参数或格式无效",
            _ => "服务内部错误",
        }, fieldErrors = Array.Empty<object>(), retryable, requestId = ctx.TraceIdentifier });
```

- [ ] **Step 5: 实现登录/admin/AdminInitializer 验证**

`AuthEndpoints.cs` 登录端点开头改为（其余逐字不动；claims/成功审计用 `username` 变量替换 `req.Username`）：

```csharp
        app.MapPost("/api/v1/auth/login", async (HttpContext ctx, LoginRequest req, IAntiforgery af, IAuditLog audit) =>
        {
            await af.ValidateRequestAsync(ctx);
            var username = req?.Username ?? "";
            var password = req?.Password ?? "";
            await using var conn = new SqlConnection(connectionString);
            var user = await conn.QuerySingleOrDefaultAsync<(long Id, string Hash, bool Active)?>(
                "SELECT id AS Id, password_hash AS Hash, is_active AS Active FROM dbo.app_user WHERE username=@u", new { u = username });
            if (user is null || !user.Value.Active ||
                hasher.VerifyHashedPassword(username, user.Value.Hash, password) == PasswordVerificationResult.Failed)
            {
                // audit_log.actor nvarchar(64)：空用户名记 "(empty)"，超长截断，失败审计永不抛（AUTH-02）。
                var actor = username.Length == 0 ? "(empty)" : username.Length > 64 ? username[..64] : username;
                await audit.AppendAsync(conn, null, new AuditEntry(actor, "User", "auth.login.failed", "app_user", null, "登录失败", null, null, ctx.TraceIdentifier, null, "LIVE"));
                return Results.Json(new { code = "AUTH_FAILED", message = "用户名或密码错误", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 401);
            }
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Value.Id.ToString()), new Claim(ClaimTypes.Name, username) };
            await ctx.SignInAsync("tf", new ClaimsPrincipal(new ClaimsIdentity(claims, "tf")));
            await audit.AppendAsync(conn, null, new AuditEntry(username, "User", "auth.login", "app_user", user.Value.Id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
            return Results.Ok(new { data = new { username }, requestId = ctx.TraceIdentifier });
        });
```

`AdminEndpoints.cs` 创建用户 action 内验证块替换为：

```csharp
                if (req is null)
                    return (422, JsonSerializer.Serialize(new { code = "VALIDATION", message = "请求体无效", retryable = false, requestId = ctx.TraceIdentifier }));
                if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length > 64 ||
                    string.IsNullOrEmpty(req.Password) || req.Password.Length < 10 ||
                    (req.DisplayName is not null && req.DisplayName.Length > 100) ||
                    (req.Reason is not null && req.Reason.Length > 1000))
                    return (422, JsonSerializer.Serialize(new { code = "VALIDATION", message = "用户名必填且≤64位，密码至少10位，显示名≤100位，原因≤1000位", retryable = false, requestId = ctx.TraceIdentifier }));
```

permissions 端点：开头加 `if (req is null || (req.Reason is not null && req.Reason.Length > 1000)) return Results.Json(new { code = "VALIDATION", message = "请求体无效或原因超长", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);`；Grant/Revoke 调用包 try/catch：

```csharp
            try
            {
                if (req.Grant) await perms.GrantAsync(conn, tx, id, req.Permission, ctx.User.Identity!.Name!);
                else await perms.RevokeAsync(conn, tx, id, req.Permission, ctx.User.Identity!.Name!);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await tx.RollbackAsync();
                return Results.Json(new { code = "CONFLICT", message = "权限已授予（并发重复）", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 409);
            }
```

`AdminInitializer.cs`：用户名读取后加 `if (username.Length > 64) { Console.Error.WriteLine("用户名不得超过 64 位"); return 2; }`；INSERT 包 try/catch：

```csharp
        long id;
        try
        {
            id = await c.ExecuteScalarAsync<long>(
                "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(@u,@u,@h)",
                new { u = username, h = hasher.HashPassword(username, p1) }, tx);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync();
            Console.Error.WriteLine("用户名已存在");
            return 2;
        }
```

- [ ] **Step 6: 实现 JobQueue 回收审计 + Program 守卫**

`JobQueue.cs`：加字段与 ctor：

```csharp
    private readonly string _conn;
    private readonly IAuditLog _audit;
    public JobQueue(string connectionString, IAuditLog audit)
    {
        _conn = connectionString;
        _audit = audit;
    }
```

`RecoverExpiredLeasesAsync` 全文替换（选取+更新+审计同事务；upload-dispatch 仍排除）：

```csharp
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        // upload-dispatch 失租不在此回收：必须走发送恢复→受理未知→回查（JOB-02）
        var expired = (await conn.QueryAsync<(long Id, string JobKind, string IntentKey, string Env, string? LeaseOwner)>(@"
SELECT id AS Id, job_kind AS JobKind, intent_key AS IntentKey, env AS Env, lease_owner AS LeaseOwner
FROM dbo.outbox_job WITH (UPDLOCK, READPAST)
WHERE job_state = N'RUNNING' AND lease_until < SYSUTCDATETIME() AND job_kind <> N'upload-dispatch'",
            transaction: tx)).ToList();
        if (expired.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }
        var rows = await conn.ExecuteAsync(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=SYSUTCDATETIME()
WHERE job_state=N'RUNNING' AND lease_until < SYSUTCDATETIME() AND job_kind <> N'upload-dispatch'",
            transaction: tx);
        // 设计 §4.8：租约回收事件只读留痕（M0 携带 5）
        foreach (var j in expired)
            await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "job.lease.recovered", "outbox_job",
                j.Id.ToString(), null, null,
                System.Text.Json.JsonSerializer.Serialize(new { j.JobKind, j.IntentKey, prevOwner = j.LeaseOwner }),
                null, null, j.Env));
        await tx.CommitAsync(ct);
        return rows;
    }
```

`JobQueueTests.cs`（10 处）与 `SendRecoveryTests.cs`（6 处）：`new JobQueue(_fx.ConnectionString)` 全部替换为 `new JobQueue(_fx.ConnectionString, new AuditLog())`；两文件头部确保有 `using TraceFlow.Infrastructure.Security;`。

`Program.cs`：连接串守卫改 `if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("缺少 ConnectionStrings:TraceFlow（部署配置提供，不入 Git）");`（原 `?? throw` 改为读取后判断）；JobQueue 注册改工厂：

```csharp
builder.Services.AddSingleton<IJobQueue>(sp => new JobQueue(connectionString, sp.GetRequiredService<IAuditLog>()));
```

- [ ] **Step 7: BIN2 变体 + 字面量修复 + 静态门禁根断言**

`Sql2008Fixture.cs` 建库语句替换：

```csharp
            // BIN2 变体（M0 携带 7）：opt-in 机械拦截大小写接缝（env/状态字面量）。默认 CI 排序不变。
            var bin2 = Environment.GetEnvironmentVariable("TRACEFLOW_TEST_DB_BIN2") == "1";
            await master.ExecuteAsync(bin2
                ? $"CREATE DATABASE [{_dbName}] COLLATE Latin1_General_BIN2"
                : $"CREATE DATABASE [{_dbName}]");
```

`UploadConstraintTests.cs`：全部 `N'BLOCKED'`（10 处）替换为 `N'Blocked'`（CK_task_state/CK_part_state 的精确值；BIN2 下 CI 依赖字面量必然违反 CHECK）。

`Sql2008CompatibilityTests.cs` 第一个测试的根遍历改为（禁止静默空扫，M-9）：

```csharp
        foreach (var root in roots)
        {
            Assert.True(Directory.Exists(root), $"门禁根目录不存在（禁止静默空扫）: {root}");
            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".sql") || f.EndsWith(".cs")))
            {
                var text = File.ReadAllText(file);
                foreach (var pattern in forbidden)
                    Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                        $"{file} 含 2008 R2 禁用语法: {pattern}");
            }
        }
```

- [ ] **Step 8: 卫生项**

```bash
git rm traceflow/tests/TraceFlow.UnitTests/UnitTest1.cs traceflow/tests/TraceFlow.IntegrationTests/UnitTest1.cs
```

`TraceFlow.Application.csproj` 删除 `<PackageReference Include="Microsoft.Extensions.Identity.Core" Version="8.0.11" />`（Application 无 Identity 消费方）；`TraceFlow.Host.csproj` 的 PackageReference ItemGroup 加入同版本包（PasswordHasher 真实消费方在 Host）。

EOF 换行补齐（仅 traceflow/ 内已跟踪文件；不触碰 docs/ 与用户资料）：

```bash
git ls-files -z -- 'traceflow/*.cs' 'traceflow/*.sql' 'traceflow/*.ts' 'traceflow/*.tsx' 'traceflow/*.csproj' 'traceflow/*.json' | while IFS= read -r -d '' f; do
  if [ -f "$f" ] && [ -n "$(tail -c 1 "$f")" ]; then printf '\n' >> "$f"; fi
done
```

> **执行裁决（2026-11-22，Task 4）**：本循环给 0002–0005 迁移文件补了 EOF 换行，SHA-256 随之变化，与 Task 1"不修改 0001–0006 任何字节"注记冲突。裁决接受这次一次性规范化：已实测本机 SQL Server 无任何持久 TraceFlow* 库（系统未部署，M0 仅测试库且用后即删），checksum 门禁对任何外部已迁移库会显式报错停机（fail-loud），不会静默漂移。此后 0001–0007 以 Task 4 后的字节为新基线，继续不可变。若存在外部已迁移库，需人工重基线 schema_version.checksum 或还原旧字节。

- [ ] **Step 9: 运行确认通过（含 BIN2 全量）**

Run:
`TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
`TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 TRACEFLOW_TEST_DB_BIN2=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
`dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 两变体全绿（BIN2 暴露的字面量问题已在 Step 7 修复）；Unit 全绿；sln Release 0 警告。

- [ ] **Step 10: Commit**

```bash
git add traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs traceflow/src/TraceFlow.Host/Security/ApiErrors.cs traceflow/src/TraceFlow.Host/Endpoints/AuthEndpoints.cs traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs traceflow/src/TraceFlow.Host/Security/AdminInitializer.cs traceflow/src/TraceFlow.Infrastructure/Jobs/JobQueue.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Jobs/JobQueueTests.cs traceflow/tests/TraceFlow.IntegrationTests/Jobs/SendRecoveryTests.cs traceflow/tests/TraceFlow.IntegrationTests/Sql2008Fixture.cs traceflow/tests/TraceFlow.IntegrationTests/Migrations/UploadConstraintTests.cs traceflow/tests/TraceFlow.IntegrationTests/Migrations/Sql2008CompatibilityTests.cs traceflow/src/TraceFlow.Application/TraceFlow.Application.csproj traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj traceflow/tests/TraceFlow.IntegrationTests/Jobs/LeaseRecoveryAuditTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/InputValidationTests.cs
git add -u traceflow/
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(M1): input validation, lease-recovery audit, service exceptions, BIN2 fixture variant, hygiene"
```

（`git add -u traceflow/` 收纳 UnitTest1 删除与 EOF 换行修补；不触碰 traceflow/ 之外任何文件。）

------

### Task 5: 来源适配器端口 + KSOA fixture 适配器 + 组织/来源系统引导

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Abstractions/ISourceAdapter.cs`（DTO + 端口）
- Create: `traceflow/src/TraceFlow.Infrastructure/Sources/FixtureKsoaAdapter.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Sources/RealKsoaAdapter.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Sources/SourceAdapterFactory.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Bootstrap/OrgBootstrap.cs`
- Create: `docs/contracts/ksoa-adapter-contract.md`
- Modify: `traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj`（fixture 复制到输出）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj`（同上）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（Build 后调用 OrgBootstrap）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/FixtureKsoaAdapterTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/OrgBootstrapTests.cs`

**Interfaces:**
- Consumes: Task 2 `BusinessTimeParser.ParseUtc(string?,string?)`、`BusinessTimeParser.Shanghai`（public static readonly TimeZoneInfo）；Task 4 `AdapterNotCertifiedException(string message)`；M0 `Sql2008Fixture`（命名空间 `TraceFlow.IntegrationTests`；`IClassFixture<Sql2008Fixture>` 每类独立测试库；`ConnectionString` 属性与 `OpenAsync()` 打开连接助手）。
- Produces（后续任务全部按这些精确签名消费）:
  - `sealed record SourceLineDto(string? SourceLineKey, string ProductSourceId, decimal QuantityOriginal, string? UnitOriginal, string? BatchRaw, string? ExpiryRaw, string? TraceCodeRaw, string EventKind, string RawLineJson)`
  - `sealed record SourceKeyIssue(string? SourceLineKey, string IssueKind, string Detail, string RawLineJson)`（IssueKind ∈ MISSING_LINE_KEY/DUPLICATE_LINE_KEY/ORPHAN_LINE/CROSS_DAY_REUSE/BAD_BUSINESS_TIME/MISSING_DOC_KEY）
  - `sealed record SourceDocumentDto(string SourceDocumentKey, string DocumentType, DateTime? BusinessDateTimeUtc, string? SourceTimeRaw, string? SourceTimeZone, string RawHeaderJson, IReadOnlyList<SourceLineDto> Lines, IReadOnlyList<SourceKeyIssue> KeyIssues)`（DocumentType ∈ "SALE"/"PURCHASE_IN"）
  - `interface ISourceAdapter { string ContractVersion { get; } Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct); }`（entity ∈ "sale"/"purchase-in"）
  - `sealed class FixtureKsoaAdapter : ISourceAdapter`，ctor `(string fixtureDir)`，ContractVersion = `"ksoa-fixture-1"`
  - `sealed class RealKsoaAdapter : ISourceAdapter`，ContractVersion = `"ksoa-real-0"`，FetchAsync 一律抛 `AdapterNotCertifiedException`（G-ERP 未过门禁前零 SQL）
  - `static class SourceAdapterFactory { static ISourceAdapter Create(string mode, string fixtureDir); }`（"fixture"/"real"，其余 ArgumentException）
  - `static class OrgBootstrap { static Task EnsureAsync(string connectionString, CancellationToken ct = default); }`

- [ ] **Step 1: 写失败测试（适配器，纯文件读取不需要数据库，但放 IntegrationTests 工程以复用 fixture 复制）**

`traceflow/tests/TraceFlow.IntegrationTests/Sources/FixtureKsoaAdapterTests.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

public class FixtureKsoaAdapterTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa");

    [Fact]
    public async Task Sale_normal_doc_key_lines_and_codes_associated_per_line()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        Assert.Equal("ksoa-fixture-1", adapter.ContractVersion);
        var docs = await adapter.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = Assert.Single(docs, d => d.SourceDocumentKey == "000123");
        Assert.Equal("SALE", doc.DocumentType);
        // 2026-11-20 14:32:05 Asia/Shanghai = 06:32:05 UTC（AT-002 业务时间）
        Assert.Equal(new DateTime(2026, 11, 20, 6, 32, 5, DateTimeKind.Utc), doc.BusinessDateTimeUtc);
        Assert.Equal(3, doc.Lines.Count);
        var l1 = doc.Lines.Single(l => l.SourceLineKey == "1");
        Assert.Equal("SP0001", l1.ProductSourceId);
        Assert.Equal("81234567890123456789", l1.TraceCodeRaw); // 码随行走，不挂单头
        Assert.Equal("SALE", l1.EventKind);
        var l3 = doc.Lines.Single(l => l.SourceLineKey == "3");
        Assert.Null(l3.TraceCodeRaw); // 未扫码行保留 null，不造码
        Assert.Empty(doc.KeyIssues);
    }

    [Fact]
    public async Task Purchase_in_lines_carry_batch_and_purchase_event()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        var docs = await adapter.FetchAsync("purchase-in", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = Assert.Single(docs);
        Assert.Equal("RK20261120001", doc.SourceDocumentKey);
        Assert.Equal("PURCHASE_IN", doc.DocumentType);
        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("PURCHASE_IN", l.EventKind));
        Assert.Equal("P20260101", doc.Lines.Single(l => l.SourceLineKey == "1").BatchRaw);
        Assert.Equal(10m, doc.Lines.Single(l => l.SourceLineKey == "1").QuantityOriginal);
    }

    [Fact]
    public async Task Missing_plh_becomes_issue_with_raw_preserved_not_fabricated_line()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        var docs = await adapter.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = docs.Single(d => d.SourceDocumentKey == "000124");
        Assert.Empty(doc.Lines); // 不造行号（SRC-02）
        var issue = Assert.Single(doc.KeyIssues);
        Assert.Equal("MISSING_LINE_KEY", issue.IssueKind);
        Assert.Null(issue.SourceLineKey);
        Assert.Contains("81234567890123456791", issue.RawLineJson); // raw 完整保留
    }

    [Fact]
    public async Task Mixed_sign_receipt_splits_events_without_netting()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        var docs = await adapter.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
        var doc = docs.Single(d => d.SourceDocumentKey == "000125");
        Assert.Equal("SALE", doc.Lines.Single(l => l.SourceLineKey == "1").EventKind);
        Assert.Equal("CONSUMER_RETURN", doc.Lines.Single(l => l.SourceLineKey == "2").EventKind);
        Assert.Equal("NO_EVENT", doc.Lines.Single(l => l.SourceLineKey == "3").EventKind);
        Assert.Equal(-1m, doc.Lines.Single(l => l.SourceLineKey == "2").QuantityOriginal);
    }

    [Fact]
    public async Task Duplicate_line_key_and_orphan_line_become_issues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "dup.json"), """
            { "salemast": [{ "lshh": "D1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [
                { "lshh": "D1", "plh": "1", "spid": "SP0001", "shl": 1 },
                { "lshh": "D1", "plh": "1", "spid": "SP0001", "shl": 2 },
                { "lshh": "GHOST", "plh": "9", "spid": "SP0001", "shl": 1 } ] }
            """);
            var docs = await new FixtureKsoaAdapter(dir).FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
            var d1 = docs.Single(d => d.SourceDocumentKey == "D1");
            Assert.Single(d1.Lines); // 首条保留
            Assert.Contains(d1.KeyIssues, i => i.IssueKind == "DUPLICATE_LINE_KEY" && i.SourceLineKey == "1");
            var ghost = docs.Single(d => d.SourceDocumentKey == "GHOST");
            Assert.Contains(ghost.KeyIssues, i => i.IssueKind == "ORPHAN_LINE");
            Assert.Empty(ghost.Lines);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Same_doc_key_across_business_days_flags_cross_day_reuse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tf-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.json"), """
            { "salemast": [{ "lshh": "R1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R1", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            await File.WriteAllTextAsync(Path.Combine(dir, "b.json"), """
            { "salemast": [{ "lshh": "R1", "rq": "2026-11-21", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R1", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            var docs = await new FixtureKsoaAdapter(dir).FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None);
            Assert.Equal(2, docs.Count);
            Assert.DoesNotContain(docs[0].KeyIssues, i => i.IssueKind == "CROSS_DAY_REUSE");
            Assert.Contains(docs[1].KeyIssues, i => i.IssueKind == "CROSS_DAY_REUSE");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Real_adapter_throws_not_certified_and_factory_routes_modes()
    {
        var real = SourceAdapterFactory.Create("real", FixtureDir);
        Assert.Equal("ksoa-real-0", real.ContractVersion);
        await Assert.ThrowsAsync<AdapterNotCertifiedException>(
            () => real.FetchAsync("sale", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None));
        Assert.IsType<FixtureKsoaAdapter>(SourceAdapterFactory.Create("fixture", FixtureDir));
        Assert.Throws<ArgumentException>(() => SourceAdapterFactory.Create("anything-else", FixtureDir));
    }

    [Fact]
    public async Task Unknown_entity_throws_argument_exception()
    {
        var adapter = new FixtureKsoaAdapter(FixtureDir);
        await Assert.ThrowsAsync<ArgumentException>(
            () => adapter.FetchAsync("inventory", DateTime.MinValue, DateTime.MaxValue, CancellationToken.None));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~FixtureKsoaAdapterTests"`
Expected: 编译失败（ISourceAdapter/FixtureKsoaAdapter 不存在）。

- [ ] **Step 3: 实现端口与 DTO**

`traceflow/src/TraceFlow.Application/Abstractions/ISourceAdapter.cs`：

```csharp
namespace TraceFlow.Application.Abstractions;

/// <summary>来源行 DTO：raw 字段原样携带（SRC-04 raw 永不改写）；SourceLineKey 为 ERP 真实行键字符串（SRC-02）。</summary>
public sealed record SourceLineDto(
    string? SourceLineKey, string ProductSourceId, decimal QuantityOriginal,
    string? UnitOriginal, string? BatchRaw, string? ExpiryRaw, string? TraceCodeRaw,
    string EventKind, string RawLineJson);

/// <summary>键/时间异常：raw 行完整保留在 RawLineJson，供接入异常台账展示（不丢行、不造行号）。</summary>
public sealed record SourceKeyIssue(string? SourceLineKey, string IssueKind, string Detail, string RawLineJson);

/// <summary>来源单据 DTO：BusinessDateTimeUtc 已由适配器按来源时区换算；KeyIssues 非空→服务侧判 INVALID。</summary>
public sealed record SourceDocumentDto(
    string SourceDocumentKey, string DocumentType, DateTime? BusinessDateTimeUtc,
    string? SourceTimeRaw, string? SourceTimeZone, string RawHeaderJson,
    IReadOnlyList<SourceLineDto> Lines, IReadOnlyList<SourceKeyIssue> KeyIssues);

/// <summary>
/// 来源适配器端口（DB-01 只读）。entity ∈ sale/purchase-in。
/// 真实 KSOA 适配器在 G-ERP 取证门禁通过前禁止实现查询（AdapterNotCertifiedException）。
/// </summary>
public interface ISourceAdapter
{
    string ContractVersion { get; }
    Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct);
}
```

- [ ] **Step 4: 实现 FixtureKsoaAdapter**

`traceflow/src/TraceFlow.Infrastructure/Sources/FixtureKsoaAdapter.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// Mock 适配器：读 docs/contracts/fixtures/ksoa 的候选结构 JSON（G-ERP 前仅 Mock 使用）。
/// 静态 fixture 不按窗口过滤——窗口语义属真实适配器职责；重复读取由服务侧 content_hash 去重。
/// 键规则（SRC-02）：sale=(lshh,plh)，purchase-in=(djbh,dj_sn)，全程字符串；
/// plh 空→MISSING_LINE_KEY；单内重复→DUPLICATE_LINE_KEY（首条保留）；无单头行→ORPHAN_LINE；
/// 同 docKey 跨业务日→CROSS_DAY_REUSE；rq+ontime 解析失败→BAD_BUSINESS_TIME。
/// </summary>
public sealed class FixtureKsoaAdapter : ISourceAdapter
{
    private readonly string _fixtureDir;
    public FixtureKsoaAdapter(string fixtureDir) => _fixtureDir = fixtureDir;
    public string ContractVersion => "ksoa-fixture-1";

    public Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct)
    {
        if (entity is not ("sale" or "purchase-in"))
            throw new ArgumentException($"未知来源实体: {entity}", nameof(entity));
        var docs = new List<SourceDocumentDto>();
        var seenDays = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        if (!Directory.Exists(_fixtureDir))
            return Task.FromResult<IReadOnlyList<SourceDocumentDto>>(docs);
        foreach (var file in Directory.GetFiles(_fixtureDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            using var j = JsonDocument.Parse(File.ReadAllText(file));
            var root = j.RootElement;
            if (entity == "sale" && root.TryGetProperty("salemast", out var sm))
                docs.AddRange(ParseDocuments(sm, root.GetProperty("salemxls"), "lshh", "plh", "SALE",
                    qty => qty > 0 ? "SALE" : qty < 0 ? "CONSUMER_RETURN" : "NO_EVENT", seenDays));
            else if (entity == "purchase-in" && root.TryGetProperty("jh_rkhz", out var ph))
                docs.AddRange(ParseDocuments(ph, root.GetProperty("jh_rkmx"), "djbh", "dj_sn", "PURCHASE_IN",
                    _ => "PURCHASE_IN", seenDays));
        }
        return Task.FromResult<IReadOnlyList<SourceDocumentDto>>(docs);
    }

    private static IEnumerable<SourceDocumentDto> ParseDocuments(
        JsonElement headers, JsonElement lines, string keyProp, string lineKeyProp, string docType,
        Func<decimal, string> eventKindOf, Dictionary<string, DateOnly> seenDays)
    {
        var headerMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var missingKeyIssues = new List<SourceKeyIssue>();
        foreach (var h in headers.EnumerateArray())
        {
            var key = Str(h, keyProp);
            if (string.IsNullOrWhiteSpace(key))
            {
                missingKeyIssues.Add(new SourceKeyIssue(null, "MISSING_DOC_KEY", "单头单据键为空", h.GetRawText()));
                continue;
            }
            headerMap[key] = h;
        }
        var byDoc = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var orphans = new Dictionary<string, List<SourceKeyIssue>>(StringComparer.Ordinal);
        foreach (var l in lines.EnumerateArray())
        {
            var key = Str(l, keyProp);
            if (key is null || !headerMap.ContainsKey(key))
            {
                var orphanKey = key ?? "(missing-doc-key)";
                var issue = new SourceKeyIssue(Str(l, lineKeyProp), "ORPHAN_LINE",
                    $"行的 {keyProp}={key ?? "(null)"} 无对应单头", l.GetRawText());
                (orphans.TryGetValue(orphanKey, out var list) ? list : orphans[orphanKey] = new List<SourceKeyIssue>()).Add(issue);
                continue;
            }
            (byDoc.TryGetValue(key, out var list) ? list : byDoc[key] = new List<JsonElement>()).Add(l);
        }
        var result = new List<SourceDocumentDto>();
        foreach (var (docKey, header) in headerMap)
        {
            var docIssues = new List<SourceKeyIssue>();
            var rq = Str(header, "rq");
            var ontime = Str(header, "ontime");
            var businessUtc = BusinessTimeParser.ParseUtc(rq, ontime);
            if (businessUtc is null)
                docIssues.Add(new SourceKeyIssue(null, "BAD_BUSINESS_TIME",
                    $"rq={rq ?? "(null)"} ontime={ontime ?? "(null)"} 无法按 Asia/Shanghai 解析", header.GetRawText()));
            else
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(businessUtc.Value, BusinessTimeParser.Shanghai));
                if (seenDays.TryGetValue(docKey, out var prev) && prev != day)
                    docIssues.Add(new SourceKeyIssue(null, "CROSS_DAY_REUSE",
                        $"docKey={docKey} 跨业务日复用 {prev:yyyy-MM-dd}→{day:yyyy-MM-dd}", header.GetRawText()));
                seenDays[docKey] = day;
            }
            var docLines = new List<SourceLineDto>();
            var seenLineKeys = new HashSet<string>(StringComparer.Ordinal);
            if (byDoc.TryGetValue(docKey, out var rawLines))
                foreach (var l in rawLines)
                {
                    var lineKey = Str(l, lineKeyProp);
                    if (string.IsNullOrWhiteSpace(lineKey))
                    {
                        docIssues.Add(new SourceKeyIssue(null, "MISSING_LINE_KEY",
                            $"{lineKeyProp} 为空，无法构成稳定行键；不造行号（SRC-02）", l.GetRawText()));
                        continue;
                    }
                    if (!seenLineKeys.Add(lineKey))
                    {
                        docIssues.Add(new SourceKeyIssue(lineKey, "DUPLICATE_LINE_KEY",
                            $"{lineKeyProp}={lineKey} 单内重复，保留首条", l.GetRawText()));
                        continue;
                    }
                    var qty = l.TryGetProperty("shl", out var shl) && shl.ValueKind == JsonValueKind.Number ? shl.GetDecimal() : 0m;
                    docLines.Add(new SourceLineDto(lineKey, Str(l, "spid") ?? "", qty,
                        Str(l, "dw"), Str(l, "pihao"), Str(l, "sxrq"), Str(l, "TraceCode"),
                        eventKindOf(qty), l.GetRawText()));
                }
            result.Add(new SourceDocumentDto(docKey, docType, businessUtc,
                rq is null && ontime is null ? null : $"{rq} {ontime}".Trim(), "Asia/Shanghai",
                header.GetRawText(), docLines, docIssues));
        }
        foreach (var (orphanKey, issues) in orphans)
            result.Add(new SourceDocumentDto(orphanKey, docType, null, null, "Asia/Shanghai", "{}",
                Array.Empty<SourceLineDto>(), issues));
        if (missingKeyIssues.Count > 0)
            result.Add(new SourceDocumentDto("(missing-doc-key)", docType, null, null, "Asia/Shanghai", "{}",
                Array.Empty<SourceLineDto>(), missingKeyIssues));
        return result;
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 5: 实现 RealKsoaAdapter + SourceAdapterFactory**

`traceflow/src/TraceFlow.Infrastructure/Sources/RealKsoaAdapter.cs`：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// 真实 KSOA 直连适配器占位（DB-01 只读）。G-ERP 取证门禁（真实库结构/键稳定性/时区/换算方向）
/// 通过并更新 ksoa-adapter-contract.md 之前，禁止写任何 SQL——一律抛 AdapterNotCertifiedException。
/// </summary>
public sealed class RealKsoaAdapter : ISourceAdapter
{
    public string ContractVersion => "ksoa-real-0";

    public Task<IReadOnlyList<SourceDocumentDto>> FetchAsync(string entity, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct)
        => throw new AdapterNotCertifiedException("KSOA 真实适配器未取证（G-ERP 未通过）；当前仅允许 Mock fixture 通道");
}
```

`traceflow/src/TraceFlow.Infrastructure/Sources/SourceAdapterFactory.cs`：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Sources;

public static class SourceAdapterFactory
{
    public static ISourceAdapter Create(string mode, string fixtureDir) => mode switch
    {
        "fixture" => new FixtureKsoaAdapter(fixtureDir),
        "real" => new RealKsoaAdapter(),
        _ => throw new ArgumentException($"未知来源适配器模式: {mode}（仅 fixture/real）", nameof(mode))
    };
}
```

- [ ] **Step 6: fixture 复制到输出 + OrgBootstrap + Program 接线**

`traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj` 追加 ItemGroup：

```xml
  <ItemGroup>
    <None Include="..\..\..\docs\contracts\fixtures\ksoa\*.json" CopyToOutputDirectory="PreserveNewest" LinkBase="fixtures\ksoa" />
  </ItemGroup>
```

`traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj` 追加同样的 ItemGroup（路径相同，两个工程距仓库根都是三层）。

`traceflow/src/TraceFlow.Infrastructure/Bootstrap/OrgBootstrap.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Bootstrap;

/// <summary>
/// 单门店引导（幂等）：仅当不存在 active 组织时插入 DEFAULT 组织（UX_organization_single_active 保证单活）；
/// KSOA 来源系统 IF NOT EXISTS。已有数据一律不动（不覆盖用户配置）。
/// 属基础设施种子而非业务变更，不写审计；web/worker 两种模式启动都会经过。
/// </summary>
public static class OrgBootstrap
{
    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE is_active = 1)
    INSERT INTO dbo.organization(org_code, name) VALUES (N'DEFAULT', N'默认门店');
IF NOT EXISTS (SELECT 1 FROM dbo.source_system WHERE system_code = N'KSOA')
    INSERT INTO dbo.source_system(system_code, name, adapter_contract_version, config_json)
    VALUES (N'KSOA', N'KSOA ERP/POS', N'ksoa-fixture-1', N'{""mode"":""fixture"",""fixtureDir"":""fixtures/ksoa""}');",
            cancellationToken: ct));
    }
}
```

`traceflow/src/TraceFlow.Host/Program.cs`：在 `var app = builder.Build();` 之后、`app.UseExceptionHandler(...)` 之前插入一行：

```csharp
await TraceFlow.Infrastructure.Bootstrap.OrgBootstrap.EnsureAsync(connectionString);
```

- [ ] **Step 7: 写 OrgBootstrap 集成测试并跑全部新测试**

`traceflow/tests/TraceFlow.IntegrationTests/Sources/OrgBootstrapTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Bootstrap;

namespace TraceFlow.IntegrationTests.Sources;

public class OrgBootstrapTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public OrgBootstrapTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Ensure_creates_default_org_and_ksoa_then_idempotent()
    {
        await OrgBootstrap.EnsureAsync(_fx.ConnectionString);
        await OrgBootstrap.EnsureAsync(_fx.ConnectionString);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.organization WHERE is_active=1"));
        Assert.Equal("DEFAULT", await c.ExecuteScalarAsync<string>("SELECT org_code FROM dbo.organization"));
        var cfg = await c.ExecuteScalarAsync<string>("SELECT config_json FROM dbo.source_system WHERE system_code=N'KSOA'");
        Assert.Contains("fixture", cfg);
        Assert.Equal("ksoa-fixture-1", await c.ExecuteScalarAsync<string>(
            "SELECT adapter_contract_version FROM dbo.source_system WHERE system_code=N'KSOA'"));
    }

    [Fact]
    public async Task Ensure_never_touches_existing_active_org()
    {
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'EXISTING',N'已配置门店')");
        }
        await OrgBootstrap.EnsureAsync(_fx.ConnectionString);
        await using var c2 = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(1, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.organization"));
        Assert.Equal("EXISTING", await c2.ExecuteScalarAsync<string>("SELECT org_code FROM dbo.organization"));
    }
}
```

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~FixtureKsoaAdapterTests|FullyQualifiedName~OrgBootstrapTests"`
Expected: PASS（10 个新测试）。Sql2008Fixture 位于命名空间 `TraceFlow.IntegrationTests`，用法与 M0 各测试类一致：`IClassFixture<Sql2008Fixture>`（每类独立测试库）。

- [ ] **Step 8: 写适配器合同文档**

`docs/contracts/ksoa-adapter-contract.md`（完整内容）：

```markdown
# KSOA 来源适配器合同 v1（M1）

状态：**fixture-only**。真实适配器（ksoa-real-0）在 G-ERP 取证门禁通过前一律抛
AdapterNotCertifiedException，不执行任何 SQL。本文件是 G-ERP 取证的核对清单。

## 端口契约

- `ISourceAdapter.FetchAsync(entity, windowStartUtc, windowEndUtc, ct)`，entity ∈ sale / purchase-in。
- 返回 `SourceDocumentDto`：单据键字符串、DocumentType（SALE/PURCHASE_IN）、UTC 业务时间、
  raw 单头 JSON、行列表（raw 行 JSON 原样）、键异常列表。
- 窗口语义由真实适配器负责（按 rq+ontime 过滤）；fixture 适配器读全量静态文件，
  重复由服务侧 content_hash 去重，不产生重复版本。

## 候选结构（未取证，仅 Mock）

| 实体 | 单头 | 明细 | 单据键 | 行键 | 数量 | 码 |
|---|---|---|---|---|---|---|
| sale | salemast | salemxls | lshh | plh | shl（正售/负退/零无事件） | TraceCode（可 null） |
| purchase-in | jh_rkhz | jh_rkmx | djbh | dj_sn | shl | 无（上游出库单匹配，M2） |

业务时间 = rq(日期) + ontime(时刻)，按 Asia/Shanghai → UTC（DB-02）。
批号 pihao、效期 sxrq、单位 dw、往来单位 dwbh、验收人 yanshr 为候选字段。

## 键异常规则（SRC-02，服务侧落 ingest_exception）

- MISSING_LINE_KEY：plh/dj_sn 为空 → 不造行号，raw 行进异常台账，单据 INVALID。
- DUPLICATE_LINE_KEY：同单内行键重复 → 首条保留，其余进异常，单据 INVALID。
- ORPHAN_LINE：明细行无对应单头 → 按孤儿单据键聚合进异常。
- CROSS_DAY_REUSE：同单据键跨业务日复用且内容变化 → 异常 + INVALID（DB 侧亦校验）。
- KEY_REUSE：同单据键跨单据类型（lshh 与 djbh 碰撞）→ 服务侧 DB 校验（Task 7）。
- BAD_BUSINESS_TIME：rq+ontime 无法解析 → 不猜时间，异常 + INVALID。
- 行键按单内唯一（UQ_line_key(snapshot_id, source_line_key)）；plh/dj_sn 跨单重复属正常。

## G-ERP 取证清单（真实适配器实现前必须逐项验证并记录证据）

1. 真实表名/列名与候选结构差异（salemast/salemxls/jh_rkhz/jh_rkmx 是否为实际对象）。
2. lshh/plh、djbh/dj_sn 的 NULL、重复、跨日复用、跨表关联实测（SRC-02）。
3. rq/ontime 的真实类型与时区语义；是否存在 datetime 单列替代。
4. shl 的计量单位与 jlgg 换算方向（采购数量换算，M2 前必须验证）。
5. TraceCode 字段真实名称与扫码绑定粒度（单盒）。
6. 只读账号权限与连接超时（ConnectTimeout=5）、轮询对 POS 性能影响。
7. 增量窗口依据（是否有修改时间列；无则全窗口回扫策略确认）。

取证完成后：更新本合同版本号 → 实现 RealKsoaAdapter → 用户显式批准 LIVE 门禁。
```

- [ ] **Step 9: 全量回归 + 提交**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（M0 45+87 加本任务新增；WAF 测试会经 Program 触发 OrgBootstrap，幂等种子不影响既有断言）。

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Application/Abstractions/ISourceAdapter.cs traceflow/src/TraceFlow.Infrastructure/Sources/ traceflow/src/TraceFlow.Infrastructure/Bootstrap/ traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj traceflow/tests/TraceFlow.IntegrationTests/Sources/ docs/contracts/ksoa-adapter-contract.md
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): source adapter port, KSOA fixture adapter, org bootstrap (T5)"
```

---

### Task 6: 配置中心 SettingsService + 设置端点（密钥只写、版本并发、ERP 连接测试）

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`
- Create: `traceflow/src/TraceFlow.Host/Endpoints/SettingsEndpoints.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（DI 注册 + MapSettingsEndpoints）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Settings/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 `RowVer.ToHex(byte[])`/`RowVer.FromHex(string)`（命名空间 `TraceFlow.Domain.Versioning`）；Task 2 `SyncPolicy(int PollSeconds,int RescanHours,int DailyRescanDays,int StabilizeSeconds,int RoundIntervalSeconds)` + `SyncPolicy.Default`（`TraceFlow.Domain.Sources`）；Task 3 实例 `IdempotencyExecutor.RunAsync(ctx, action)`（DI 注入）；Task 4 `SettingValidationException`/`SettingVersionException`（`TraceFlow.Application.Abstractions`，ApiErrors 已映射 422 VALIDATION / 409 VERSION_CONFLICT）；M0 `ISecretProtector.Protect/Unprotect`、`DpapiProtector`（`TraceFlow.Infrastructure.Security`）、`IAuditLog.AppendAsync(conn, tx, AuditEntry)`、`RequirePermissionAttribute`、`PermissionCatalog.SystemManage`。
- Produces:
  - `sealed record SettingView(long Id, string Key, string? ValueJson, bool IsSecret, bool HasSecretValue, string Version, string? UpdatedBy, DateTime? UpdatedAtUtc, string? Reason)`（未持久化键 Id=0、Version="0"）
  - `sealed record ConnectionTestResult(bool Ok, string Message)`
  - `sealed class SettingsService`（命名空间 `TraceFlow.Infrastructure.Settings`）ctor `(string connectionString, ISecretProtector protector)`：
    - `Task<IReadOnlyList<SettingView>> ListAsync(CancellationToken ct)`
    - `Task<SettingView> UpdateAsync(SqlConnection conn, SqlTransaction tx, string key, string? valueJson, string? secretValue, string expectedVersionHex, string reason, string actor, CancellationToken ct)`（"0"=新建；否则 ver 乐观并发；异常回滚由调用方事务承担；返回**同事务内**读回的视图——端点不得在事务提交前用 ListAsync 另开连接读同一行，会被未提交的 X 锁阻塞直至超时）
    - `Task<SyncPolicy> GetSyncPolicyAsync(CancellationToken ct)`
    - `Task<bool> IsChannelPausedAsync(CancellationToken ct)`
    - `Task<IReadOnlyList<string>> GetAuditPassValuesAsync(CancellationToken ct)`
    - `Task<(long MaxFileBytes, int MaxRows)> GetImportBoundsAsync(CancellationToken ct)`
    - `Task<string> GetSecretAsync(string key, CancellationToken ct)`
    - `Task<ConnectionTestResult> TestErpConnectionAsync(long configId, CancellationToken ct)`
  - `SettingsEndpoints.MapSettingsEndpoints(this WebApplication app, string connectionString)`；请求记录 `SettingUpdateRequest(string? ValueJson, string? SecretValue, string? ExpectedVersion, string? Reason)`、`ConnectionTestRequest(long ConfigId)`
  - 配置键目录（后续任务按这些键名消费）：`erp.connection`/`top.appkey`/`top.appsecret`（secret）、`sync.pollSeconds`[5,3600]默30、`sync.rescanHours`[1,720]默72、`sync.dailyRescanDays`[1,365]默30、`sync.stabilizeSeconds`[5,300]默10、`sync.roundIntervalSeconds`[5,60]默5、`sync.paused` bool 默false、`supplier.auditPassValues` string[] 默[]、`import.maxFileBytes`[1024,104857600]默20971520、`import.maxRows`[1,100000]默10000

- [ ] **Step 1: 写失败测试**

`traceflow/tests/TraceFlow.IntegrationTests/Settings/SettingsServiceTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.IntegrationTests.Settings;

[SupportedOSPlatform("windows")] // DpapiProtector 仅 Windows（与 M0 SecretProtectorTests 相同标注）
// 每测试独立库：sync.pollSeconds 会被多个测试以 "0" 新建，类共享库会产生顺序依赖（与 SourceSyncTests 同模式）
public class SettingsServiceTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();
    private SettingsService NewSvc() => new(_fx.ConnectionString, new DpapiProtector());

    private async Task UpdateAsync(string key, string? valueJson, string? secretValue, string ver, string reason = "测试")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await NewSvc().UpdateAsync(c, tx, key, valueJson, secretValue, ver, reason, "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Update_new_int_setting_creates_row_and_audit()
    {
        await UpdateAsync("sync.pollSeconds", "45", null, "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.pollSeconds");
        Assert.Equal("45", view.ValueJson);
        Assert.False(view.IsSecret);
        Assert.NotEqual("0", view.Version);
        Assert.Equal("tester", view.UpdatedBy);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'settings.update' AND object_id=N'sync.pollSeconds'"));
    }

    [Theory]
    [InlineData("4")]      // 下界外
    [InlineData("3601")]   // 上界外
    [InlineData("3.5")]    // 非整数
    [InlineData("\"30\"")] // 类型错
    public async Task Int_bounds_and_type_violations_throw_validation(string bad)
    {
        await Assert.ThrowsAsync<SettingValidationException>(() => UpdateAsync("sync.pollSeconds", bad, null, "0"));
    }

    [Fact]
    public async Task Stale_version_throws_conflict_and_correct_version_succeeds()
    {
        await UpdateAsync("sync.rescanHours", "72", null, "0");
        var v1 = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.rescanHours").Version;
        await UpdateAsync("sync.rescanHours", "48", null, v1);
        await Assert.ThrowsAsync<SettingVersionException>(() => UpdateAsync("sync.rescanHours", "24", null, v1)); // 旧 ver
        var v2 = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.rescanHours").Version;
        Assert.Equal("24", (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.rescanHours").ValueJson);
        await UpdateAsync("sync.rescanHours", "72", null, v2); // 新 ver 成功
    }

    [Fact]
    public async Task Secret_is_write_only_never_echoed_and_audit_has_no_plaintext()
    {
        await UpdateAsync("top.appsecret", null, "super-secret-value-123", "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "top.appsecret");
        Assert.True(view.IsSecret);
        Assert.True(view.HasSecretValue);
        Assert.Null(view.ValueJson); // 永不回显
        Assert.Equal("super-secret-value-123", await NewSvc().GetSecretAsync("top.appsecret", CancellationToken.None));
        await using var c = new SqlConnection(_fx.ConnectionString);
        var audits = await c.QueryAsync<string>(
            "SELECT ISNULL(before_digest,N'') + ISNULL(after_digest,N'') + ISNULL(reason,N'') FROM dbo.audit_log WHERE object_id=N'top.appsecret'");
        Assert.All(audits, a => Assert.DoesNotContain("super-secret-value-123", a));
        // 密钥键禁止明文 valueJson
        await Assert.ThrowsAsync<SettingValidationException>(() => UpdateAsync("top.appsecret", "\"plain\"", null, "0"));
    }

    [Fact]
    public async Task Sync_policy_defaults_then_reflects_updates()
    {
        var p0 = await NewSvc().GetSyncPolicyAsync(CancellationToken.None);
        Assert.Equal(SyncPolicy.Default, p0);
        await UpdateAsync("sync.stabilizeSeconds", "5", null, "0");
        await UpdateAsync("sync.roundIntervalSeconds", "5", null, "0");
        var p1 = await NewSvc().GetSyncPolicyAsync(CancellationToken.None);
        Assert.Equal(5, p1.StabilizeSeconds);
        Assert.Equal(5, p1.RoundIntervalSeconds);
        Assert.Equal(30, p1.PollSeconds); // 未改键保持默认
    }

    [Fact]
    public async Task Import_bounds_defaults_and_pause_toggle()
    {
        var (bytes, rows) = await NewSvc().GetImportBoundsAsync(CancellationToken.None);
        Assert.Equal(20971520L, bytes);
        Assert.Equal(10000, rows);
        Assert.False(await NewSvc().IsChannelPausedAsync(CancellationToken.None));
        await UpdateAsync("sync.paused", "true", null, "0");
        Assert.True(await NewSvc().IsChannelPausedAsync(CancellationToken.None));
        Assert.Empty(await NewSvc().GetAuditPassValuesAsync(CancellationToken.None)); // 默认空→供应商不自动确认（G-TOP）
    }

    [Fact]
    public async Task Unknown_key_throws_validation()
    {
        await Assert.ThrowsAsync<SettingValidationException>(() => UpdateAsync("no.such.key", "1", null, "0"));
    }

    [Fact]
    public async Task Connection_test_rejects_non_erp_config_and_missing_id()
    {
        await UpdateAsync("sync.pollSeconds", "30", null, "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "sync.pollSeconds");
        var svc = NewSvc();
        await Assert.ThrowsAsync<SettingValidationException>(() => svc.TestErpConnectionAsync(view.Id, CancellationToken.None));
        await Assert.ThrowsAsync<SettingValidationException>(() => svc.TestErpConnectionAsync(999999, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_test_with_stored_local_connection_succeeds()
    {
        // 用本机测试库连接串充当 ERP 连接（仅验证 SELECT 1 通路与 5 秒超时设置，不代表真实 KSOA 取证）
        await UpdateAsync("erp.connection", null, _fx.ConnectionString, "0");
        var view = (await NewSvc().ListAsync(CancellationToken.None)).Single(v => v.Key == "erp.connection");
        var r = await NewSvc().TestErpConnectionAsync(view.Id, CancellationToken.None);
        Assert.True(r.Ok);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SettingsServiceTests"`
Expected: 编译失败（SettingsService 不存在）。

- [ ] **Step 3: 实现 SettingsService**

`traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`：

```csharp
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
                new { k = key, vj = def.IsSecret ? null : valueJson, sp = protectedBytes, is = def.IsSecret ? 1 : 0,
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
                new { k = key, vj = def.IsSecret ? null : valueJson, sp = protectedBytes, is = def.IsSecret ? 1 : 0,
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
```

- [ ] **Step 4: 实现设置端点 + Program 接线**

`traceflow/src/TraceFlow.Host/Endpoints/SettingsEndpoints.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Host.Endpoints;

public static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapSettingsEndpoints(this WebApplication app, string connectionString)
    {
        // 用 Web（camelCase）序列化，与 PUT 返回的 SettingView  casing 一致（Host 无全局 camelCase 策略，Results.Ok 会输出 PascalCase 记录属性）
        app.MapGet("/api/v1/settings", async (HttpContext ctx, SettingsService svc) =>
            Results.Json(new { data = await svc.ListAsync(ctx.RequestAborted), requestId = ctx.TraceIdentifier }, Web))
            .WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        app.MapPut("/api/v1/settings/{key}", (HttpContext ctx, string key, IdempotencyExecutor idem, SettingsService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<SettingUpdateRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion))
                    return (422, JsonSerializer.Serialize(new { code = "VALIDATION", message = "expectedVersion 必填（新建传 \"0\"）", retryable = false, requestId = ctx.TraceIdentifier }));
                // 服务异常（SettingValidationException/SettingVersionException）直接抛出→事务回滚→ApiErrors 422/409
                var view = await svc.UpdateAsync(conn, tx, key, req.ValueJson, req.SecretValue, req.ExpectedVersion,
                    req.Reason ?? "", ctx.User.Identity!.Name!, ct);
                return (200, JsonSerializer.Serialize(new { data = view, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        // 连接测试无副作用，不需要幂等键；Web camelCase 输出 {ok,message}
        app.MapPost("/api/v1/settings/connections/test", async (HttpContext ctx, ConnectionTestRequest req, SettingsService svc) =>
            Results.Json(new { data = await svc.TestErpConnectionAsync(req.ConfigId, ctx.RequestAborted), requestId = ctx.TraceIdentifier }, Web))
            .WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
    }

    public sealed record SettingUpdateRequest(string? ValueJson, string? SecretValue, string? ExpectedVersion, string? Reason);
    public sealed record ConnectionTestRequest(long ConfigId);
}
```

`traceflow/src/TraceFlow.Host/Program.cs` 两处修改：
1. DI 区（`builder.Services.AddSingleton<IIdempotencyStore, IdempotencyStore>();` 之后）追加：

```csharp
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Settings.SettingsService(
    connectionString, sp.GetRequiredService<ISecretProtector>()));
```

2. `app.MapAdminEndpoints(connectionString);` 之后追加：

```csharp
    app.MapSettingsEndpoints(connectionString);
```

- [ ] **Step 5: 跑测试确认通过 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 12 个 SettingsServiceTests 事实，含 Theory 4 例）。

- [ ] **Step 6: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Infrastructure/Settings/ traceflow/src/TraceFlow.Host/Endpoints/SettingsEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Settings/
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): settings service with secret write-only, version concurrency, ERP connection test (T6)"
```

---

### Task 7: 来源同步服务核心（不可变版本 + 摘要去重 + 接入异常 + 检查点）

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncService.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/TestSeed.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceSyncTests.cs`

**Interfaces:**
- Consumes: Task 5 `ISourceAdapter.FetchAsync(entity, startUtc, endUtc, ct)`、`SourceDocumentDto/SourceLineDto/SourceKeyIssue`、`FixtureKsoaAdapter(fixtureDir)`；Task 2 `SourceHasher.LineHash(lineKey, productId, qty, unit, batch, expiry, traceCodeRaw, eventKind)`、`SourceHasher.DocumentHash(docKey, businessUtc, sortedLineHashes)`、`ExpiryParser.ParsePrecision(raw)`、`BusinessTimeParser.Shanghai`；Task 1 `CodeFormatStorage.ToToken(CodeFormat)`（`TraceFlow.Domain.Codes`）；Task 6 `SettingsService`（本任务仅透传构造参数，Task 8 消费稳定期参数）；Task 4 `AdapterNotCertifiedException`；M0 `TraceCodeNormalizer.Normalize(string?) → NormalizedCode(Raw, Normalized, Format)`、`Qty(decimal)`（越界抛 ArgumentOutOfRangeException）、`IAuditLog`、`AuditEntry`。
- Produces:
  - `sealed record SyncResult(int DocumentsSeen, int NewVersions, int Completed, int Issues, DateTime WindowEndUtc)`
  - `sealed class SourceSyncService`（`TraceFlow.Infrastructure.Sources`）ctor `(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)`；`Task<SyncResult> RunAsync(long orgId, string env, string entity, DateTime startUtc, DateTime endUtc, CancellationToken ct)`
  - `static class TestSeed { static Task<(long OrgId, long SysId)> EnsureAsync(SqlConnection c); }`（幂等种子 O1 组织 + KSOA 来源系统；后续集成测试共用）
  - 完整性状态机（本任务部分）：有异常→INVALID；无异常零行→DISCOVERED+EMPTY_DETAIL 异常；无异常有行→STABILIZING（rounds=1）。同摘要最新版本→无操作（不产生新版本）。COMPLETE/SOURCE_CHANGED 由 Task 8 补全。

- [ ] **Step 1: 写共享种子与失败测试**

`traceflow/tests/TraceFlow.IntegrationTests/Sources/TestSeed.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.IntegrationTests.Sources;

/// <summary>幂等共享种子：O1 组织 + KSOA 来源系统（每类独立测试库内 IF NOT EXISTS）。</summary>
public static class TestSeed
{
    public static async Task<(long OrgId, long SysId)> EnsureAsync(SqlConnection c)
    {
        var orgId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
    INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
SELECT id FROM dbo.organization WHERE org_code = N'O1'");
        var sysId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.source_system WHERE system_code = N'KSOA')
    INSERT INTO dbo.source_system(system_code,name,adapter_contract_version,config_json)
    VALUES(N'KSOA',N'KSOA ERP/POS',N'ksoa-fixture-1',N'{""mode"":""fixture""}');
SELECT id FROM dbo.source_system WHERE system_code = N'KSOA'");
        return (orgId, sysId);
    }
}
```

`traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceSyncTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")] // SettingsService 依赖 DpapiProtector
// 每测试独立测试库（xUnit 每方法新建类实例→IAsyncLifetime 各建一库）：
// 默认 fixture 的单据键（000123 等）与审计计数断言要求测试间完全隔离，类级共享库会依执行顺序而碎。
public class SourceSyncTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa");
    private static readonly DateTime Start = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    private SourceSyncService NewSvc(string? dir = null)
        => new(_fx.ConnectionString, new FixtureKsoaAdapter(dir ?? FixtureDir),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // 每测试私有 fixture 副本：与每测试独立库配合，保证自定义 fixture 测试互不干扰（目录用后即删）。
    private static string PrivateFixtures()
    {
        var d = TempDir();
        foreach (var f in Directory.GetFiles(FixtureDir, "*.json"))
            File.Copy(f, Path.Combine(d, Path.GetFileName(f)));
        return d;
    }

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    [Fact]
    public async Task Sale_ingest_per_line_codes_utc_time_checkpoint_and_audit() // AT-002
    {
        var dir = PrivateFixtures();
        try
        {
        var (orgId, sysId) = await SeedAsync();
        var r = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        Assert.True(r.DocumentsSeen >= 3);
        Assert.True(r.NewVersions >= 3);
        await using var c = new SqlConnection(_fx.ConnectionString);
        var snapId = await c.ExecuteScalarAsync<long>(
            "SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'000123' AND version=1");
        Assert.Equal(new DateTime(2026, 11, 20, 6, 32, 5), await c.ExecuteScalarAsync<DateTime>(
            "SELECT business_datetime FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
        Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
            "SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
        // 码随行走：plh=1 的行携带自己的码，plh=3 无码
        Assert.Equal("81234567890123456789", await c.ExecuteScalarAsync<string>(
            "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
        Assert.Equal("S8P20", await c.ExecuteScalarAsync<string>(
            "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
        Assert.Null(await c.ExecuteScalarAsync<string?>(
            "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
        Assert.Equal("EMPTY", await c.ExecuteScalarAsync<string>(
            "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
        // 检查点同事务落库
        Assert.Equal(End, await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_end FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'sale' AND checkpoint_kind=N'WINDOW'", new { s = sysId }));
        // 审计 env=MOCK
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'source.sync' AND env=N'MOCK' AND actor_kind=N'Worker'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Second_identical_run_dedups_by_hash_no_new_version()
    {
        var dir = PrivateFixtures();
        try
        {
        var (orgId, _) = await SeedAsync();
        var r1 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        var r2 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        Assert.True(r1.NewVersions > 0);
        Assert.Equal(0, r2.NewVersions);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(r1.NewVersions, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Missing_plh_doc_is_invalid_with_raw_exception_no_lines() // SRC-02
    {
        var dir = PrivateFixtures();
        try
        {
        var (orgId, _) = await SeedAsync();
        await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
            "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'000124'"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.source_line l JOIN dbo.source_snapshot s ON l.snapshot_id=s.id WHERE s.source_document_key=N'000124'"));
        var detail = await c.ExecuteScalarAsync<string>(
            "SELECT detail FROM dbo.ingest_exception WHERE source_document_key=N'000124' AND exception_type=N'MISSING_LINE_KEY' AND status=N'OPEN'");
        Assert.Contains("81234567890123456791", detail); // raw 行完整保留
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Raw_codes_preserved_verbatim_leading_zeros_and_control_chars() // AT-008 / SRC-04
    {
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "c.json"), """
            { "salemast": [{ "lshh": "C1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [
                { "lshh": "C1", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "00123456789012345678" },
                { "lshh": "C1", "plh": "2", "spid": "SP0001", "shl": 1, "TraceCode": "\u000281234567890123456789\u0003" },
                { "lshh": "C1", "plh": "3", "spid": "SP0001", "shl": 1, "TraceCode": "81234\u001f567890123456789" } ] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            var snapId = await c.ExecuteScalarAsync<long>(
                "SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'C1'");
            // 前导零不转数值、不截断；raw 原样
            Assert.Equal("00123456789012345678", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
            Assert.Equal("UNSUP", await c.ExecuteScalarAsync<string>(
                "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'1'", new { id = snapId }));
            // 边缘控制符规范化移除，raw 保留原控制符
            Assert.Equal("\u000281234567890123456789\u0003", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_raw FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'2'", new { id = snapId }));
            Assert.Equal("81234567890123456789", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_normalized FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'2'", new { id = snapId }));
            Assert.Equal("S8P20", await c.ExecuteScalarAsync<string>(
                "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'2'", new { id = snapId }));
            // 内部控制符属未验证情形→UNSUP 异常保存，不静默清洗
            Assert.Equal("UNSUP", await c.ExecuteScalarAsync<string>(
                "SELECT code_format FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
            Assert.Contains("\u001f", await c.ExecuteScalarAsync<string>(
                "SELECT trace_code_normalized FROM dbo.source_line WHERE snapshot_id=@id AND source_line_key=N'3'", new { id = snapId }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Overlong_doc_key_fails_whole_run_no_snapshot_no_checkpoint() // AT-006
    {
        var dir = TempDir();
        try
        {
            var longKey = new string('K', 130);
            await File.WriteAllTextAsync(Path.Combine(dir, "long.json"),
                "{ \"salemast\": [{ \"lshh\": \"" + longKey + "\", \"rq\": \"2026-11-20\", \"ontime\": \"08:00:00\" }], \"salemxls\": [] }");
            var (orgId, sysId) = await SeedAsync();
            await Assert.ThrowsAnyAsync<SqlException>(
                () => NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None));
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot"));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.sync_checkpoint WHERE source_system_id=@s", new { s = sysId }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Cross_day_reuse_across_runs_invalidates_new_version_old_intact() // AT-003
    {
        var dirA = TempDir(); var dirB = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dirA, "a.json"), """
            { "salemast": [{ "lshh": "R2", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R2", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            await File.WriteAllTextAsync(Path.Combine(dirB, "b.json"), """
            { "salemast": [{ "lshh": "R2", "rq": "2026-11-21", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "R2", "plh": "1", "spid": "SP0001", "shl": 2 }] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dirA).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await NewSvc(dirB).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'R2' AND version=1"));
            Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'R2' AND version=2"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'R2' AND exception_type=N'CROSS_DAY_REUSE'"));
        }
        finally { Directory.Delete(dirA, recursive: true); Directory.Delete(dirB, recursive: true); }
    }

    [Fact]
    public async Task Same_doc_key_across_document_types_is_key_reuse() // SRC-02 跨表键碰撞
    {
        var dirS = TempDir(); var dirP = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dirS, "s.json"), """
            { "salemast": [{ "lshh": "COLL1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "COLL1", "plh": "1", "spid": "SP0001", "shl": 1 }] }
            """);
            await File.WriteAllTextAsync(Path.Combine(dirP, "p.json"), """
            { "jh_rkhz": [{ "djbh": "COLL1", "rq": "2026-11-20", "ontime": "08:00:00", "dwbh": "DW0001" }],
              "jh_rkmx": [{ "djbh": "COLL1", "dj_sn": "1", "spid": "SP0001", "pihao": "P1", "shl": 5 }] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dirS).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await NewSvc(dirP).RunAsync(orgId, "MOCK", "purchase-in", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'COLL1' AND document_type=N'PURCHASE_IN'"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'COLL1' AND exception_type=N'KEY_REUSE'"));
        }
        finally { Directory.Delete(dirS, recursive: true); Directory.Delete(dirP, recursive: true); }
    }

    [Fact]
    public async Task Zero_line_doc_discovered_with_empty_detail_exception()
    {
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "empty.json"), """
            { "salemast": [{ "lshh": "E1", "rq": "2026-11-20", "ontime": "08:00:00" }], "salemxls": [] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("DISCOVERED", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'E1'"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'E1' AND exception_type=N'EMPTY_DETAIL'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Qty_out_of_range_becomes_issue_raw_preserved_line_not_silently_rounded() // SRC-05
    {
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "q.json"), """
            { "salemast": [{ "lshh": "Q1", "rq": "2026-11-20", "ontime": "08:00:00" }],
              "salemxls": [{ "lshh": "Q1", "plh": "1", "spid": "SP0001", "shl": 10000000000000 }] }
            """);
            var (orgId, _) = await SeedAsync();
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("INVALID", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'Q1'"));
            var detail = await c.ExecuteScalarAsync<string>(
                "SELECT detail FROM dbo.ingest_exception WHERE source_document_key=N'Q1' AND exception_type=N'QTY_RANGE'");
            Assert.Contains("10000000000000", detail);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.source_line l JOIN dbo.source_snapshot s ON l.snapshot_id=s.id WHERE s.source_document_key=N'Q1'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Live_env_refuses_sync_in_m1()
    {
        var (orgId, _) = await SeedAsync();
        await Assert.ThrowsAsync<AdapterNotCertifiedException>(
            () => NewSvc().RunAsync(orgId, "LIVE", "sale", Start, End, CancellationToken.None));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SourceSyncTests"`
Expected: 编译失败（SourceSyncService 不存在）。

- [ ] **Step 3: 实现 SourceSyncService**

`traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncService.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Quantities;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

public sealed record SyncResult(int DocumentsSeen, int NewVersions, int Completed, int Issues, DateTime WindowEndUtc);

/// <summary>
/// 来源同步（SYNC-01/03、SRC-01/02/04/05、DB-02）：
/// 抓取在事务外；落库整体一个事务——任何失败（含 docKey 超宽 SqlException）回滚全部，检查点不动（AT-006）。
/// 不可变版本：同 docKey 摘要相同→无操作；摘要变化→新版本（旧版本字节不动）。
/// 异常单据 INVALID 但 raw 完整保存；零行 DISCOVERED+EMPTY_DETAIL；正常 STABILIZING（Task 8 续稳定期）。
/// M1 仅 MOCK：LIVE 一律 AdapterNotCertifiedException（G-ERP 门禁）。
/// </summary>
public sealed class SourceSyncService
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly IAuditLog _audit;

    // settings 参数本任务不消费（Task 8 引入稳定期字段时再存 _settings）；
    // 不存未读字段——TreatWarningsAsErrors 下 CS0414 会编译失败。
    public SourceSyncService(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)
    {
        _cs = connectionString;
        _adapter = adapter;
        _audit = audit;
    }

    public async Task<SyncResult> RunAsync(long orgId, string env, string entity, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new AdapterNotCertifiedException("M1 来源同步仅允许 MOCK 通道；LIVE 须 G-ERP 取证门禁通过后开启");
        var docs = await _adapter.FetchAsync(entity, startUtc, endUtc, ct); // 事务外抓取（DB-01 只读来源库）
        var now = DateTime.UtcNow;
        int newVersions = 0, issuesTotal = 0;

        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var sysId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", transaction: tx, cancellationToken: ct));

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            var lineHashes = doc.Lines.Select(l => SourceHasher.LineHash(
                l.SourceLineKey ?? "", l.ProductSourceId, l.QuantityOriginal, l.UnitOriginal,
                l.BatchRaw, l.ExpiryRaw, l.TraceCodeRaw, l.EventKind)).ToList();
            var sorted = lineHashes.OrderBy(h => h, StringComparer.Ordinal).ToList();
            var docHash = SourceHasher.DocumentHash(doc.SourceDocumentKey, doc.BusinessDateTimeUtc, sorted);

            var latest = await c.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(@"
SELECT TOP 1 id, version, content_hash, completeness_status, business_datetime, document_type
FROM dbo.source_snapshot WITH (UPDLOCK, HOLDLOCK)
WHERE source_system_id=@sys AND source_document_key=@key ORDER BY version DESC",
                new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));
            if (latest is not null && latest.content_hash == docHash)
                continue; // 摘要去重：重复轮询不产生新版本

            var issues = new List<(string? LineKey, string Type, string Detail, string Raw)>();
            foreach (var ki in doc.KeyIssues)
                issues.Add((ki.SourceLineKey, ki.IssueKind, ki.Detail, ki.RawLineJson));

            // DB 级跨表键碰撞（KEY_REUSE）：同 docKey 出现过不同单据类型
            var otherType = await c.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT TOP 1 document_type FROM dbo.source_snapshot
WHERE source_system_id=@sys AND source_document_key=@key AND document_type<>@t",
                new { sys = sysId, key = doc.SourceDocumentKey, t = doc.DocumentType }, tx, cancellationToken: ct));
            if (otherType is not null)
                issues.Add((null, "KEY_REUSE", $"docKey={doc.SourceDocumentKey} 已用于 {otherType}，跨单据类型碰撞", doc.RawHeaderJson));

            // DB 级跨日复用（CROSS_DAY_REUSE）：摘要变化且中国业务日不同（适配器批内已查，这里查跨轮次）
            if (latest is not null && latest.business_datetime is not null && doc.BusinessDateTimeUtc is not null)
            {
                var oldDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(latest.business_datetime.Value, BusinessTimeParser.Shanghai));
                var newDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(doc.BusinessDateTimeUtc.Value, BusinessTimeParser.Shanghai));
                if (oldDay != newDay)
                    issues.Add((null, "CROSS_DAY_REUSE", $"docKey={doc.SourceDocumentKey} 跨业务日复用 {oldDay:yyyy-MM-dd}→{newDay:yyyy-MM-dd}", doc.RawHeaderJson));
            }

            // 数量域校验（SRC-05）：越界行不落库（decimal(18,6) 放不下），raw 进异常，不静默舍入
            var insertable = new List<(SourceLineDto Line, string Hash)>();
            var skippedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                try { _ = new Qty(l.QuantityOriginal); }
                catch (ArgumentOutOfRangeException)
                {
                    issues.Add((l.SourceLineKey, "QTY_RANGE", $"数量 {l.QuantityOriginal} 超出 decimal(18,6) 域", l.RawLineJson));
                    if (l.SourceLineKey is not null) skippedKeys.Add(l.SourceLineKey);
                    continue;
                }
                insertable.Add((l, lineHashes[i]));
            }

            var hasFatal = issues.Count > 0;
            if (!hasFatal && insertable.Count == 0)
                issues.Add((null, "EMPTY_DETAIL", "单据无明细行（迟到明细由后续新版本补全，AT-005）", doc.RawHeaderJson));
            var status = hasFatal ? "INVALID" : insertable.Count == 0 ? "DISCOVERED" : "STABILIZING";
            var version = (latest?.version ?? 0) + 1;

            var snapId = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,
    business_datetime,source_time_raw,source_time_zone,completeness_status,content_hash,raw_header,stabilize_rounds,last_round_at)
OUTPUT inserted.id
VALUES(@org,@sys,@key,@type,@ver,@bdt,@str,@stz,@status,@hash,@raw,@rounds,@roundAt)",
                new { org = orgId, sys = sysId, key = doc.SourceDocumentKey, type = doc.DocumentType, ver = version,
                      bdt = doc.BusinessDateTimeUtc, str = doc.SourceTimeRaw, stz = doc.SourceTimeZone,
                      status, hash = docHash, raw = doc.RawHeaderJson,
                      rounds = status == "STABILIZING" ? 1 : 0,
                      roundAt = status == "STABILIZING" ? now : (DateTime?)null }, tx, cancellationToken: ct));
            newVersions++;

            foreach (var (l, hash) in insertable)
            {
                var norm = TraceCodeNormalizer.Normalize(l.TraceCodeRaw);
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,
    batch_raw,expiry_raw,expiry_precision,trace_code_raw,trace_code_normalized,code_format,line_hash,event_kind,raw_line)
VALUES(@snap,@key,@pid,@qty,@unit,@batch,@expiry,@prec,@raw,@norm,@fmt,@hash,@evt,@rawLine)",
                    new { snap = snapId, key = l.SourceLineKey, pid = l.ProductSourceId, qty = l.QuantityOriginal,
                          unit = l.UnitOriginal, batch = l.BatchRaw, expiry = l.ExpiryRaw,
                          prec = ExpiryParser.ParsePrecision(l.ExpiryRaw), raw = l.TraceCodeRaw,
                          norm = norm.Normalized, fmt = CodeFormatStorage.ToToken(norm.Format),
                          hash, evt = l.EventKind, rawLine = l.RawLineJson }, tx, cancellationToken: ct));
            }

            foreach (var (lineKey, type, detail, raw) in issues)
            {
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,detail)
VALUES(@org,@sys,@key,@lineKey,@type,@detail)",
                    new { org = orgId, sys = sysId, key = doc.SourceDocumentKey, lineKey,
                          type, detail = JsonSerializer.Serialize(new { detail, raw }) }, tx, cancellationToken: ct));
                issuesTotal++;
            }
        }

        // 检查点 UPSERT（同事务；失败回滚则检查点不动，AT-007 窗口不丢）
        await c.ExecuteAsync(new CommandDefinition(@"
IF EXISTS (SELECT 1 FROM dbo.sync_checkpoint WHERE source_system_id=@sys AND entity=@entity AND checkpoint_kind=N'WINDOW')
    UPDATE dbo.sync_checkpoint SET window_start=@start, window_end=@end, last_success_at=@now
    WHERE source_system_id=@sys AND entity=@entity AND checkpoint_kind=N'WINDOW'
ELSE
    INSERT INTO dbo.sync_checkpoint(source_system_id,entity,checkpoint_kind,window_start,window_end,last_success_at)
    VALUES(@sys,@entity,N'WINDOW',@start,@end,@now)",
            new { sys = sysId, entity, start = startUtc, end = endUtc, now }, tx, cancellationToken: ct));

        await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "Worker", "source.sync", "sync_checkpoint",
            entity, null, null,
            JsonSerializer.Serialize(new { entity, documentsSeen = docs.Count, newVersions, issues = issuesTotal, windowEnd = endUtc }),
            null, null, env));
        await tx.CommitAsync(ct);
        return new SyncResult(docs.Count, newVersions, 0, issuesTotal, endUtc);
    }

    private sealed class SnapshotRow
    {
        public long id { get; set; }
        public int version { get; set; }
        public string content_hash { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public DateTime? business_datetime { get; set; }
        public string document_type { get; set; } = "";
    }
}
```

- [ ] **Step 4: 跑测试确认通过 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 10 个 SourceSyncTests 事实）。

- [ ] **Step 5: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncService.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/TestSeed.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceSyncTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): source sync with immutable versions, hash dedup, ingest exceptions, checkpoint (T7)"
```

---

### Task 8: 稳定期判定（两轮摘要一致 + 轮间隔 + COMPLETE/SOURCE_CHANGED + 迟到明细）

**Files:**
- Modify: `traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncService.cs`（整文件替换为下方最终版）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceStabilizationTests.cs`

**Interfaces:**
- Consumes: Task 7 `SourceSyncService` 全部现有行为（本任务只增不改语义：摘要去重/异常/检查点/审计保持）；Task 6 `SettingsService.GetSyncPolicyAsync(ct) → SyncPolicy`（StabilizeSeconds/RoundIntervalSeconds）；Task 5 fixture 适配器；0007 列 `source_snapshot.stabilize_rounds`/`last_round_at`。
- Produces: `SyncResult.Completed` 开始计数（本轮晋升 COMPLETE 的单据数）；完整性状态机补全：
  - 同摘要且最新 STABILIZING：距 last_round_at ≥ roundIntervalSeconds 才 rounds+1；满足「行数>0 且 docKey 无 OPEN 异常 且 rounds≥2 且 now−observed_at≥stabilizeSeconds」→ COMPLETE（SYNC-02）。
  - 已 COMPLETE 的单据摘要变化：旧版本→SOURCE_CHANGED + 台账 SOURCE_CHANGED 异常（立即 RESOLVED，evidence=新版本 id——它是变更标记而非待处理数据质量问题，新版本走自己的稳定期；AT-057）。旧版本关联 upload_task 的 payload 字节不动（M1 无任务生成逻辑，测试直接种子验证不可变）。
  - 迟到明细（AT-005）：零行 DISCOVERED 单据后续带行新版本到达时，旧 EMPTY_DETAIL 异常自动 RESOLVED（evidence=新版本 id），新版本可正常晋升。
  - 零行单据永不 COMPLETE；INVALID 单据不参与稳定期。

- [ ] **Step 1: 写失败测试**

`traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceStabilizationTests.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")]
public class SourceStabilizationTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public SourceStabilizationTests(Sql2008Fixture fx) => _fx = fx;

    private static readonly DateTime Start = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);

    private SourceSyncService NewSvc(string dir)
        => new(_fx.ConnectionString, new FixtureKsoaAdapter(dir),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tf-stab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // 稳定期测试统一 5 秒稳定期 + 5 秒轮间隔（目录允许的最小值），幂等写入
    private async Task UseFastStabilizationAsync()
    {
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        foreach (var (key, json) in new[] { ("sync.stabilizeSeconds", "5"), ("sync.roundIntervalSeconds", "5") })
        {
            var view = (await svc.ListAsync(CancellationToken.None)).Single(v => v.Key == key);
            if (view.ValueJson == json) continue;
            await using var c = new SqlConnection(_fx.ConnectionString);
            await c.OpenAsync();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            await svc.UpdateAsync(c, tx, key, json, null, view.Id == 0 ? "0" : view.Version, "稳定期测试", "tester", CancellationToken.None);
            await tx.CommitAsync();
        }
    }

    private async Task<long> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        return orgId;
    }

    private static string Doc(string key, string lines) => $$"""
    { "salemast": [{ "lshh": "{{key}}", "rq": "2026-11-20", "ontime": "08:00:00" }],
      "salemxls": [{{lines}}] }
    """;

    [Fact]
    public async Task Two_consistent_rounds_with_interval_promote_complete() // SYNC-02 / AT-004
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "s.json"),
                Doc("S1", """{ "lshh": "S1", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" }"""));
            var r1 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(0, r1.Completed);
            await using var c = new SqlConnection(_fx.ConnectionString);
            var snapId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'S1'");
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT stabilize_rounds FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
            // 立即第二轮：轮间隔不足，rounds 不增
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT stabilize_rounds FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
            await Task.Delay(6000); // 超过 5 秒轮间隔 + 5 秒稳定期
            var r3 = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(1, r3.Completed);
            Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
            Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT stabilize_rounds FROM dbo.source_snapshot WHERE id=@id", new { id = snapId }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Zero_line_doc_never_completes()
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "e.json"), Doc("E2", ""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await Task.Delay(6000);
            var r = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(0, r.Completed);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("DISCOVERED", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'E2'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Complete_doc_changed_marks_source_changed_and_task_payload_immutable() // AT-057
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "s.json");
            await File.WriteAllTextAsync(file,
                Doc("S2", """{ "lshh": "S2", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" }"""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await Task.Delay(6000);
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            var v1 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'S2' AND version=1");
            Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = v1 }));
            // 种子上传任务引用 v1（M1 无任务生成逻辑，直接 SQL 种子验证 payload 不可变）
            var payload = """{"codes":["81234567890123456789"]}""";
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var sourceHash = await c.ExecuteScalarAsync<string>("SELECT content_hash FROM dbo.source_snapshot WHERE id=@id", new { id = v1 });
            await c.ExecuteAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_json,payload_hash)
VALUES(@org,N'MOCK',N'STAB-TEST-1',N'RETAIL_321',@snap,@sh,@pj,@ph)",
                new { org = orgId, snap = v1, sh = sourceHash, pj = payload, ph = payloadHash });
            // 来源变化：shl 1→2
            await File.WriteAllTextAsync(file,
                Doc("S2", """{ "lshh": "S2", "plh": "1", "spid": "SP0001", "shl": 2, "TraceCode": "81234567890123456789" }"""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal("SOURCE_CHANGED", await c.ExecuteScalarAsync<string>("SELECT completeness_status FROM dbo.source_snapshot WHERE id=@id", new { id = v1 }));
            Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'S2' AND version=2"));
            // 任务 payload 字节不动、不产生重复业务
            Assert.Equal(payload, await c.ExecuteScalarAsync<string>("SELECT payload_json FROM dbo.upload_task WHERE event_key=N'STAB-TEST-1'"));
            Assert.Equal(payloadHash, await c.ExecuteScalarAsync<string>("SELECT payload_hash FROM dbo.upload_task WHERE event_key=N'STAB-TEST-1'"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task"));
            // SOURCE_CHANGED 台账：变更标记，立即 RESOLVED 且证据指向新版本
            var evidence = await c.ExecuteScalarAsync<string>(
                "SELECT resolution_evidence FROM dbo.ingest_exception WHERE source_document_key=N'S2' AND exception_type=N'SOURCE_CHANGED' AND status=N'RESOLVED'");
            Assert.Contains("snapshotId", evidence);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Late_lines_new_version_resolves_empty_detail_and_completes() // AT-005
    {
        await UseFastStabilizationAsync();
        var orgId = await SeedAsync();
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "s.json");
            await File.WriteAllTextAsync(file, Doc("L1", ""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            await using var c = new SqlConnection(_fx.ConnectionString);
            Assert.Equal("DISCOVERED", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'L1' AND version=1"));
            // 迟到明细
            await File.WriteAllTextAsync(file,
                Doc("L1", """{ "lshh": "L1", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" }"""));
            await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal("STABILIZING", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'L1' AND version=2"));
            Assert.Equal("RESOLVED", await c.ExecuteScalarAsync<string>(
                "SELECT status FROM dbo.ingest_exception WHERE source_document_key=N'L1' AND exception_type=N'EMPTY_DETAIL'"));
            await Task.Delay(6000);
            var r = await NewSvc(dir).RunAsync(orgId, "MOCK", "sale", Start, End, CancellationToken.None);
            Assert.Equal(1, r.Completed);
            Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>(
                "SELECT completeness_status FROM dbo.source_snapshot WHERE source_document_key=N'L1' AND version=2"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SourceStabilizationTests"`
Expected: FAIL（Completed 恒 0、无 SOURCE_CHANGED/COMPLETE 晋升、EMPTY_DETAIL 不 RESOLVED）。

- [ ] **Step 3: 整文件替换 SourceSyncService.cs（Task 7 修复版 + 稳定期）**

> **执行裁决（2026-11-22）**：下方代码已合并 T7 评审修复（a83e055，问题感知去重）——异常计算先于去重判定、命中时台账比对补记、InsertIssueAsync 单点写入、ExceptionRow 比对行。不得回退为"摘要命中即 continue"的旧形态；补记的 OPEN 异常阻塞 COMPLETE 晋升。skippedKeys 只写局部已按 T7 遗留项删除。

`traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncService.cs` 最终版：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Quantities;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

public sealed record SyncResult(int DocumentsSeen, int NewVersions, int Completed, int Issues, DateTime WindowEndUtc);

/// <summary>
/// 来源同步（SYNC-01/02/03、SRC-01/02/04/05、DB-02）：
/// 抓取在事务外；落库整体一个事务——任何失败（含 docKey 超宽 SqlException）回滚全部，检查点不动（AT-006）。
/// 不可变版本：同 docKey 摘要相同→稳定期轮次推进（不产生新版本）；摘要变化→新版本（旧版本字节不动）。
/// 摘要去重是问题感知的（T7 评审修复，必须保留）：content_hash 不含 KeyIssues/document_type，
/// 异常计算先于去重判定；命中时比对台账、只补记未记录的键质量异常（含跨类型 KEY_REUSE）；
/// 完全相同重跑→零写入。补记的 OPEN 异常同样阻塞 COMPLETE 晋升。
/// 稳定期（SYNC-02）：两轮摘要一致且轮间隔≥roundIntervalSeconds；行数>0、docKey 无 OPEN 异常、
/// rounds≥2、距 observed_at≥stabilizeSeconds → COMPLETE。零行永不 COMPLETE。
/// 已 COMPLETE 摘要变化→旧 SOURCE_CHANGED + 台账标记（立即 RESOLVED，证据=新版本）；任务 payload 不可变（AT-057）。
/// 迟到明细→新版本 + EMPTY_DETAIL 自动 RESOLVED（AT-005）。
/// 异常单据 INVALID 但 raw 完整保存。M1 仅 MOCK：LIVE 一律 AdapterNotCertifiedException（G-ERP 门禁）。
/// </summary>
public sealed class SourceSyncService
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public SourceSyncService(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)
    {
        _cs = connectionString;
        _adapter = adapter;
        _settings = settings;
        _audit = audit;
    }

    public async Task<SyncResult> RunAsync(long orgId, string env, string entity, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new AdapterNotCertifiedException("M1 来源同步仅允许 MOCK 通道；LIVE 须 G-ERP 取证门禁通过后开启");
        var policy = await _settings.GetSyncPolicyAsync(ct);
        var docs = await _adapter.FetchAsync(entity, startUtc, endUtc, ct); // 事务外抓取（DB-01 只读来源库）
        var now = DateTime.UtcNow;
        int newVersions = 0, completed = 0, issuesTotal = 0;

        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var sysId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", transaction: tx, cancellationToken: ct));

        // 异常单点写入：新版本路径与去重补记共用同一 detail 序列化，保证重跑比对逐字节一致。
        async Task InsertIssueAsync(string docKey, (string? LineKey, string Type, string Detail, string Raw) issue)
        {
            await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,detail)
VALUES(@org,@sys,@key,@lineKey,@type,@detail)",
                new { org = orgId, sys = sysId, key = docKey, lineKey = issue.LineKey,
                      type = issue.Type, detail = JsonSerializer.Serialize(new { detail = issue.Detail, raw = issue.Raw }) }, tx, cancellationToken: ct));
            issuesTotal++;
        }

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            var lineHashes = doc.Lines.Select(l => SourceHasher.LineHash(
                l.SourceLineKey ?? "", l.ProductSourceId, l.QuantityOriginal, l.UnitOriginal,
                l.BatchRaw, l.ExpiryRaw, l.TraceCodeRaw, l.EventKind)).ToList();
            var sorted = lineHashes.OrderBy(h => h, StringComparer.Ordinal).ToList();
            var docHash = SourceHasher.DocumentHash(doc.SourceDocumentKey, doc.BusinessDateTimeUtc, sorted);

            var latest = await c.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(@"
SELECT TOP 1 id, version, content_hash, completeness_status, business_datetime, document_type,
       observed_at, stabilize_rounds, last_round_at
FROM dbo.source_snapshot WITH (UPDLOCK, HOLDLOCK)
WHERE source_system_id=@sys AND source_document_key=@key ORDER BY version DESC",
                new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));

            // 异常计算先于去重判定（T7 评审修复）：同摘要不代表键质量异常已记录。
            var issues = new List<(string? LineKey, string Type, string Detail, string Raw)>();
            foreach (var ki in doc.KeyIssues)
                issues.Add((ki.SourceLineKey, ki.IssueKind, ki.Detail, ki.RawLineJson));

            // DB 级跨表键碰撞（KEY_REUSE）：同 docKey 出现过不同单据类型。
            // 摘要命中时也必须执行：content_hash 不含 document_type，跨类型同内容否则被去重吞掉。
            var otherType = await c.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT TOP 1 document_type FROM dbo.source_snapshot
WHERE source_system_id=@sys AND source_document_key=@key AND document_type<>@t",
                new { sys = sysId, key = doc.SourceDocumentKey, t = doc.DocumentType }, tx, cancellationToken: ct));
            if (otherType is not null)
                issues.Add((null, "KEY_REUSE", $"docKey={doc.SourceDocumentKey} 已用于 {otherType}，跨单据类型碰撞", doc.RawHeaderJson));

            // DB 级跨日复用（CROSS_DAY_REUSE）：摘要变化且中国业务日不同（适配器批内已查，这里查跨轮次）
            if (latest is not null && latest.business_datetime is not null && doc.BusinessDateTimeUtc is not null)
            {
                var oldDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(latest.business_datetime.Value, BusinessTimeParser.Shanghai));
                var newDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(doc.BusinessDateTimeUtc.Value, BusinessTimeParser.Shanghai));
                if (oldDay != newDay)
                    issues.Add((null, "CROSS_DAY_REUSE", $"docKey={doc.SourceDocumentKey} 跨业务日复用 {oldDay:yyyy-MM-dd}→{newDay:yyyy-MM-dd}", doc.RawHeaderJson));
            }

            // 数量域校验（SRC-05）：越界行不落库（decimal(18,6) 放不下），raw 进异常，不静默舍入
            var insertable = new List<(SourceLineDto Line, string Hash)>();
            for (var i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                try { _ = new Qty(l.QuantityOriginal); }
                catch (ArgumentOutOfRangeException)
                {
                    issues.Add((l.SourceLineKey, "QTY_RANGE", $"数量 {l.QuantityOriginal} 超出 decimal(18,6) 域", l.RawLineJson));
                    continue;
                }
                insertable.Add((l, lineHashes[i]));
            }

            var hasFatal = issues.Count > 0;
            if (!hasFatal && insertable.Count == 0)
                issues.Add((null, "EMPTY_DETAIL", "单据无明细行（迟到明细由后续新版本补全，AT-005）", doc.RawHeaderJson));

            if (latest is not null && latest.content_hash == docHash)
            {
                // 去重补记（T7 评审修复）：比对台账 (lineKey,type,detail)，只补记缺失异常；
                // 不产生新版本、不动既有版本字节；完全相同重跑→全部已记录→零写入。
                var recorded = await c.QueryAsync<ExceptionRow>(new CommandDefinition(@"
SELECT source_line_key, exception_type, detail FROM dbo.ingest_exception
WHERE source_system_id=@sys AND source_document_key=@key",
                    new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));
                var recordedSet = new HashSet<(string? LineKey, string Type, string? Detail)>(
                    recorded.Select(r => (r.source_line_key, r.exception_type, r.detail)));
                foreach (var issue in issues)
                {
                    var payload = JsonSerializer.Serialize(new { detail = issue.Detail, raw = issue.Raw });
                    if (!recordedSet.Add((issue.LineKey, issue.Type, payload)))
                        continue; // 同 (lineKey,type,detail) 已在台账→可证已记录，不重复
                    await InsertIssueAsync(doc.SourceDocumentKey, issue);
                }

                // 摘要一致：仅 STABILIZING 推进稳定期；DISCOVERED/INVALID/COMPLETE/SOURCE_CHANGED 无操作。
                // 补记先于晋升判定：新记录的 OPEN 异常立即阻塞 COMPLETE。
                if (latest.completeness_status == "STABILIZING")
                {
                    var rounds = latest.stabilize_rounds;
                    if (latest.last_round_at is null || (now - latest.last_round_at.Value).TotalSeconds >= policy.RoundIntervalSeconds)
                    {
                        rounds++;
                        await c.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.source_snapshot SET stabilize_rounds=@r, last_round_at=@now WHERE id=@id",
                            new { r = rounds, now, id = latest.id }, tx, cancellationToken: ct));
                    }
                    var lineCount = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.source_line WHERE snapshot_id=@id", new { id = latest.id }, tx, cancellationToken: ct));
                    var openIssues = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_system_id=@sys AND source_document_key=@key AND status=N'OPEN'",
                        new { sys = sysId, key = doc.SourceDocumentKey }, tx, cancellationToken: ct));
                    if (lineCount > 0 && openIssues == 0 && rounds >= 2 &&
                        (now - latest.observed_at).TotalSeconds >= policy.StabilizeSeconds)
                    {
                        await c.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.source_snapshot SET completeness_status=N'COMPLETE' WHERE id=@id",
                            new { id = latest.id }, tx, cancellationToken: ct));
                        completed++;
                    }
                }
                continue; // 摘要去重：重复轮询不产生新版本
            }

            var status = hasFatal ? "INVALID" : insertable.Count == 0 ? "DISCOVERED" : "STABILIZING";
            var version = (latest?.version ?? 0) + 1;

            var snapId = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,
    business_datetime,source_time_raw,source_time_zone,completeness_status,content_hash,raw_header,stabilize_rounds,last_round_at)
OUTPUT inserted.id
VALUES(@org,@sys,@key,@type,@ver,@bdt,@str,@stz,@status,@hash,@raw,@rounds,@roundAt)",
                new { org = orgId, sys = sysId, key = doc.SourceDocumentKey, type = doc.DocumentType, ver = version,
                      bdt = doc.BusinessDateTimeUtc, str = doc.SourceTimeRaw, stz = doc.SourceTimeZone,
                      status, hash = docHash, raw = doc.RawHeaderJson,
                      rounds = status == "STABILIZING" ? 1 : 0,
                      roundAt = status == "STABILIZING" ? now : (DateTime?)null }, tx, cancellationToken: ct));
            newVersions++;

            foreach (var (l, hash) in insertable)
            {
                var norm = TraceCodeNormalizer.Normalize(l.TraceCodeRaw);
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,
    batch_raw,expiry_raw,expiry_precision,trace_code_raw,trace_code_normalized,code_format,line_hash,event_kind,raw_line)
VALUES(@snap,@key,@pid,@qty,@unit,@batch,@expiry,@prec,@raw,@norm,@fmt,@hash,@evt,@rawLine)",
                    new { snap = snapId, key = l.SourceLineKey, pid = l.ProductSourceId, qty = l.QuantityOriginal,
                          unit = l.UnitOriginal, batch = l.BatchRaw, expiry = l.ExpiryRaw,
                          prec = ExpiryParser.ParsePrecision(l.ExpiryRaw), raw = l.TraceCodeRaw,
                          norm = norm.Normalized, fmt = CodeFormatStorage.ToToken(norm.Format),
                          hash, evt = l.EventKind, rawLine = l.RawLineJson }, tx, cancellationToken: ct));
            }

            foreach (var issue in issues)
                await InsertIssueAsync(doc.SourceDocumentKey, issue);

            // AT-057：已 COMPLETE 的单据来源变化→旧版本 SOURCE_CHANGED；台账标记立即 RESOLVED（证据=新版本），
            // 新版本走自己的稳定期。旧版本关联 upload_task 的 payload 由 M2 任务生成逻辑保证不可变，此处不触碰任务表。
            if (latest is not null && latest.completeness_status == "COMPLETE")
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.source_snapshot SET completeness_status=N'SOURCE_CHANGED' WHERE id=@id",
                    new { id = latest.id }, tx, cancellationToken: ct));
                await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,exception_type,detail,status,resolved_by,resolved_at,resolution_evidence)
VALUES(@org,@sys,@key,N'SOURCE_CHANGED',@detail,N'RESOLVED',N'SYSTEM',@now,@evidence)",
                    new { org = orgId, sys = sysId, key = doc.SourceDocumentKey,
                          detail = JsonSerializer.Serialize(new { oldVersion = latest.version, oldSnapshotId = latest.id, newHash = docHash }),
                          now, evidence = JsonSerializer.Serialize(new { snapshotId = snapId }) }, tx, cancellationToken: ct));
                issuesTotal++;
            }

            // AT-005：迟到明细到达→旧 EMPTY_DETAIL 自动 RESOLVED（证据=新版本），解除 COMPLETE 阻塞
            if (insertable.Count > 0)
            {
                await c.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.ingest_exception SET status=N'RESOLVED', resolved_by=N'SYSTEM', resolved_at=@now,
       resolution_evidence=@evidence
WHERE source_system_id=@sys AND source_document_key=@key AND exception_type=N'EMPTY_DETAIL' AND status=N'OPEN'",
                    new { sys = sysId, key = doc.SourceDocumentKey, now,
                          evidence = JsonSerializer.Serialize(new { snapshotId = snapId }) }, tx, cancellationToken: ct));
            }
        }

        // 检查点 UPSERT（同事务；失败回滚则检查点不动，AT-007 窗口不丢）
        await c.ExecuteAsync(new CommandDefinition(@"
IF EXISTS (SELECT 1 FROM dbo.sync_checkpoint WHERE source_system_id=@sys AND entity=@entity AND checkpoint_kind=N'WINDOW')
    UPDATE dbo.sync_checkpoint SET window_start=@start, window_end=@end, last_success_at=@now
    WHERE source_system_id=@sys AND entity=@entity AND checkpoint_kind=N'WINDOW'
ELSE
    INSERT INTO dbo.sync_checkpoint(source_system_id,entity,checkpoint_kind,window_start,window_end,last_success_at)
    VALUES(@sys,@entity,N'WINDOW',@start,@end,@now)",
            new { sys = sysId, entity, start = startUtc, end = endUtc, now }, tx, cancellationToken: ct));

        await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "Worker", "source.sync", "sync_checkpoint",
            entity, null, null,
            JsonSerializer.Serialize(new { entity, documentsSeen = docs.Count, newVersions, completed, issues = issuesTotal, windowEnd = endUtc }),
            null, null, env));
        await tx.CommitAsync(ct);
        return new SyncResult(docs.Count, newVersions, completed, issuesTotal, endUtc);
    }

    private sealed class SnapshotRow
    {
        public long id { get; set; }
        public int version { get; set; }
        public string content_hash { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public DateTime? business_datetime { get; set; }
        public string document_type { get; set; } = "";
        public DateTime observed_at { get; set; }
        public int stabilize_rounds { get; set; }
        public DateTime? last_round_at { get; set; }
    }

    // 去重补记的台账比对行：detail 为入库时的完整 JSON 载荷（与 InsertIssueAsync 序列化一致）。
    private sealed class ExceptionRow
    {
        public string? source_line_key { get; set; }
        public string exception_type { get; set; } = "";
        public string? detail { get; set; }
    }
}
```

- [ ] **Step 4: 跑新测试 + 全量回归（Task 7 的 SourceSyncTests 必须仍全绿）**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 4 个稳定期事实，含约 18 秒受控等待；SourceSyncTests 12 事实不受影响（含 T7 评审修复的 2 个去重补记回归）——其断言不依赖 rounds/COMPLETE）。

- [ ] **Step 5: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncService.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceStabilizationTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): stabilization rounds, COMPLETE/SOURCE_CHANGED, late-line resolution (T8)"
```

---

### Task 9: 同步调度器 + source-sync 作业处理器 + 运维端点（手动同步/作业查询/审计查询）

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncJobHandler.cs`
- Create: `traceflow/src/TraceFlow.Host/SyncSchedulerHostedService.cs`
- Create: `traceflow/src/TraceFlow.Host/Endpoints/OpsEndpoints.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（ISourceAdapter/SyncScheduler/IJobHandler DI + 调度器托管服务 + MapOpsEndpoints）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceSyncJobHandlerTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/M1ApiGateTests.cs`

**Interfaces:**
- Consumes: M0 `IJobQueue.EnqueueAsync(conn, tx, orgId, env, jobKind, intentKey, payloadJson, nextRunAtUtc, ct) → Task<long>`（同意图幂等）、`ClaimedJob(Id, JobKind, IntentKey, PayloadJson, FencingToken, LeaseUntilUtc, AttemptCount)`（**无 env/org 字段——payload 携带 {orgId,env,entity,trigger}**）、`IJobHandler { string Kind; Task HandleAsync(ClaimedJob, ct); }`（Worker 负责 Complete/Reschedule，处理器只做业务、失败抛异常）、`JobQueue(connectionString, IAuditLog)`（Task 4 后签名）；Task 2 `SyncWindowPolicy.Incremental/Daily`、`SyncPolicy`；Task 6 `SettingsService.GetSyncPolicyAsync/IsChannelPausedAsync`；Task 7/8 `SourceSyncService.RunAsync(orgId, env, entity, startUtc, endUtc, ct)`；Task 5 `ISourceAdapter`/`SourceAdapterFactory.Create(mode, fixtureDir)`；Task 3 实例 `IdempotencyExecutor`；`PermissionCatalog.JobRematch/TraceRead/AuditRead/SystemManage`。**EnvOptions 定义在 Host（Program.cs 全局命名空间），Infrastructure 不可引用——SyncScheduler 构造接收 `string env`。**
- Produces:
  - `sealed class SyncScheduler`（`TraceFlow.Infrastructure.Sources`）ctor `(string connectionString, IJobQueue queue, SettingsService settings, string env)`；`Task<int> TickAsync(DateTime nowUtc, CancellationToken ct)`（非 MOCK→0；无 active 组织→0；返回入队调用次数）
  - 意图键：`periodic:{entity}:{unix/PollSeconds}`、`daily:{entity}:{yyyyMMdd}`（job_kind=source-sync）；`match:periodic:{unix/300}`（job_kind=master-match）。payload=`{orgId,env,entity,trigger}`（match 的 entity=null）。暂停（sync.paused）跳过 source-sync，master-match 继续。
  - `sealed class SourceSyncJobHandler : IJobHandler` Kind=`"source-sync"`，ctor `(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)`；窗口：trigger=manual→payload 显式 startUtc/endUtc；daily→`SyncWindowPolicy.Daily`；periodic→`SyncWindowPolicy.Incremental(cp.window_end, now, policy)`（**停机缺口不截断，AT-007**）
  - `SyncSchedulerHostedService`（Host）：30 秒循环，**先 Delay 后 Tick**（避免短命测试宿主内立即产生后台作业）
  - `OpsEndpoints.MapOpsEndpoints(this WebApplication app, string connectionString, string env)`：
    - `POST /api/v1/sync-jobs`（job:rematch，幂等）→202 `{data:{jobId:string}}`；守卫：entity∈{sale,purchase-in}、end>start、end≤now+5min、窗口≤90 天、paused→422 CHANNEL_PAUSED、LIVE→422 ERP_NOT_CERTIFIED；意图 `manual:{entity}:{start:yyyyMMddHHmmss}:{end:yyyyMMddHHmmss}`
    - `GET /api/v1/jobs/{id:long}`（trace:read）→作业状态；404 NOT_FOUND
    - `GET /api/v1/audit-logs`（audit:read）→ROW_NUMBER 分页（page 默 1，pageSize 默 50 上限 200），过滤 action/objectType/objectId/dateStart/dateEnd，ORDER BY occurred_at DESC,id DESC
  - `SyncJobRequest(string Entity, DateTime Start, DateTime End, string? Filters)`（Filters 为 openapi 预留字段，M1 不参与窗口计算）

- [ ] **Step 1: 写失败测试（调度器 + 处理器）**

`traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")]
// 每测试独立测试库：Paused 测试改写共享设置、桶计数断言要求完全隔离。
public class SyncSchedulerTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private SyncScheduler NewSvc(string env = "MOCK")
        => new(_fx.ConnectionString, new JobQueue(_fx.ConnectionString, new AuditLog()),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), env);

    private async Task SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await TestSeed.EnsureAsync(c);
    }

    private async Task<int> JobCountAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job");
    }

    [Fact]
    public async Task Tick_enqueues_periodic_daily_and_match_intents_idempotent()
    {
        await SeedAsync();
        var now = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);
        var n = await NewSvc().TickAsync(now, CancellationToken.None);
        Assert.Equal(5, n); // 2 实体 × (periodic+daily) + 1 match
        Assert.Equal(5, await JobCountAsync());
        await NewSvc().TickAsync(now, CancellationToken.None); // 同意图桶→幂等
        Assert.Equal(5, await JobCountAsync());
        await using var c = new SqlConnection(_fx.ConnectionString);
        // unix(2026-11-22T08:00Z)/300 动态计算，不硬编码桶号
        var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE intent_key=@k", new { k = $"match:periodic:{unix / 300}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE intent_key=@k", new { k = $"daily:sale:{now:yyyyMMdd}" }));
    }

    [Fact]
    public async Task Next_poll_bucket_creates_new_periodic_intents()
    {
        await SeedAsync();
        var now = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);
        await NewSvc().TickAsync(now, CancellationToken.None);
        await NewSvc().TickAsync(now.AddSeconds(30), CancellationToken.None); // 默认 pollSeconds=30 → 新桶
        Assert.Equal(7, await JobCountAsync()); // +2 periodic（daily/match 桶未变）
    }

    [Fact]
    public async Task Live_env_tick_enqueues_nothing()
    {
        await SeedAsync();
        var n = await NewSvc("LIVE").TickAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, n);
        Assert.Equal(0, await JobCountAsync());
    }

    [Fact]
    public async Task Paused_channel_skips_source_sync_but_keeps_match()
    {
        await SeedAsync();
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
            await settings.UpdateAsync(c, tx, "sync.paused", "true", null, "0", "暂停测试", "tester", CancellationToken.None);
            await tx.CommitAsync();
        }
        await NewSvc().TickAsync(new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        await using var c2 = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'source-sync'"));
        Assert.Equal(1, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'master-match'"));
    }
}
```

`traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceSyncJobHandlerTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.IntegrationTests.Sources;

[SupportedOSPlatform("windows")]
public class SourceSyncJobHandlerTests : IAsyncLifetime
{
    private Sql2008Fixture _fx = null!;
    public async Task InitializeAsync() { _fx = new Sql2008Fixture(); await _fx.InitializeAsync(); }
    public Task DisposeAsync() => _fx.DisposeAsync();

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa");

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    private async Task<ClaimedJob> EnqueueAndClaimAsync(long orgId, string payload, string intent)
    {
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await queue.EnqueueAsync(c, tx, orgId, "MOCK", "source-sync", intent, payload, DateTime.UtcNow, CancellationToken.None);
        await tx.CommitAsync();
        var job = await queue.ClaimAsync("test-worker", "MOCK", new[] { "source-sync" }, TimeSpan.FromMinutes(5), CancellationToken.None);
        return job!;
    }

    private SourceSyncJobHandler NewHandler()
        => new(_fx.ConnectionString, new FixtureKsoaAdapter(FixtureDir),
               new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());

    [Fact]
    public async Task First_periodic_run_uses_daily_rescan_window()
    {
        var (orgId, sysId) = await SeedAsync();
        var before = DateTime.UtcNow;
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"sale","trigger":"periodic"}""", "h-first-" + Guid.NewGuid().ToString("N"));
        await NewHandler().HandleAsync(job, CancellationToken.None);
        await using var c = new SqlConnection(_fx.ConnectionString);
        var start = await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_start FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'sale' AND checkpoint_kind=N'WINDOW'", new { s = sysId });
        // 首次无检查点：now−30 天（允许测试执行秒级误差）
        Assert.True((before.AddDays(-30) - start).Duration() < TimeSpan.FromMinutes(1));
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.source_snapshot") >= 3);
    }

    [Fact]
    public async Task Stale_checkpoint_gap_never_truncated() // AT-007
    {
        var (orgId, sysId) = await SeedAsync();
        var oldEnd = DateTime.UtcNow.AddDays(-10); // 停机 10 天
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync(@"
INSERT INTO dbo.sync_checkpoint(source_system_id,entity,checkpoint_kind,window_start,window_end,last_success_at)
VALUES(@s,N'sale',N'WINDOW',@ws,@we,@we)", new { s = sysId, ws = oldEnd.AddDays(-1), we = oldEnd });
        }
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"sale","trigger":"periodic"}""", "h-gap-" + Guid.NewGuid().ToString("N"));
        await NewHandler().HandleAsync(job, CancellationToken.None);
        await using var c2 = new SqlConnection(_fx.ConnectionString);
        var start = await c2.ExecuteScalarAsync<DateTime>(
            "SELECT window_start FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'sale' AND checkpoint_kind=N'WINDOW'", new { s = sysId });
        // 起点=旧检查点终点−72h 回扫，而不是 now−72h（缺口全覆盖）
        Assert.Equal(oldEnd.AddHours(-72), start, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Manual_trigger_honors_explicit_window()
    {
        var (orgId, sysId) = await SeedAsync();
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var job = await EnqueueAndClaimAsync(orgId,
            $$"""{"orgId":{{orgId}},"env":"MOCK","entity":"purchase-in","trigger":"manual","startUtc":"{{start:O}}","endUtc":"{{end:O}}"}""",
            "h-manual-" + Guid.NewGuid().ToString("N"));
        await NewHandler().HandleAsync(job, CancellationToken.None);
        await using var c = new SqlConnection(_fx.ConnectionString);
        Assert.Equal(start, await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_start FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'purchase-in' AND checkpoint_kind=N'WINDOW'", new { s = sysId }));
        Assert.Equal(end, await c.ExecuteScalarAsync<DateTime>(
            "SELECT window_end FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=N'purchase-in' AND checkpoint_kind=N'WINDOW'", new { s = sysId }));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SyncSchedulerTests|FullyQualifiedName~SourceSyncJobHandlerTests"`
Expected: 编译失败（SyncScheduler/SourceSyncJobHandler 不存在）。

- [ ] **Step 3: 实现 SyncScheduler**

`traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// 周期调度（SYNC-04）：30 秒轮询桶 + 每日全量回扫 + 5 分钟匹配桶。
/// 意图键含时间桶→outbox UQ(org,env,kind,intent) 天然去重，多实例并发安全。
/// 仅 MOCK（M1）；暂停通道跳过 source-sync（master-match 为本地计算，继续）。
/// </summary>
public sealed class SyncScheduler
{
    private readonly string _cs;
    private readonly IJobQueue _queue;
    private readonly SettingsService _settings;
    private readonly string _env;

    public SyncScheduler(string connectionString, IJobQueue queue, SettingsService settings, string env)
    {
        _cs = connectionString;
        _queue = queue;
        _settings = settings;
        _env = env;
    }

    public async Task<int> TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (_env != "MOCK") return 0;
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        var orgId = await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ct));
        if (orgId is null) return 0;
        var policy = await _settings.GetSyncPolicyAsync(ct);
        var paused = await _settings.IsChannelPausedAsync(ct);
        var unix = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var n = 0;
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        if (!paused)
        {
            foreach (var entity in new[] { "sale", "purchase-in" })
            {
                var periodicPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, entity, trigger = "periodic" });
                await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "source-sync",
                    $"periodic:{entity}:{unix / policy.PollSeconds}", periodicPayload, nowUtc, ct);
                n++;
                var dailyPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, entity, trigger = "daily" });
                await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "source-sync",
                    $"daily:{entity}:{nowUtc:yyyyMMdd}", dailyPayload, nowUtc, ct);
                n++;
            }
        }
        var matchPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, entity = (string?)null, trigger = "periodic" });
        await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "master-match",
            $"match:periodic:{unix / 300}", matchPayload, nowUtc, ct);
        n++;
        await tx.CommitAsync(ct);
        return n;
    }
}
```

- [ ] **Step 4: 实现 SourceSyncJobHandler**

`traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncJobHandler.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Sources;

/// <summary>
/// source-sync 作业：窗口三态——manual 用 payload 显式窗口；daily 全量回扫；
/// periodic 增量（检查点终点−回扫窗；首次 now−每日回扫天数）。停机缺口绝不截断（AT-007）。
/// 失败抛异常→WorkerHostedService 指数退避重排（JOB-03）。
/// </summary>
public sealed class SourceSyncJobHandler : IJobHandler
{
    private readonly string _cs;
    private readonly ISourceAdapter _adapter;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public SourceSyncJobHandler(string connectionString, ISourceAdapter adapter, SettingsService settings, IAuditLog audit)
    {
        _cs = connectionString;
        _adapter = adapter;
        _settings = settings;
        _audit = audit;
    }

    public string Kind => "source-sync";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var j = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var root = j.RootElement;
        var orgId = root.GetProperty("orgId").GetInt64();
        var env = root.GetProperty("env").GetString() ?? "MOCK";
        var entity = root.GetProperty("entity").GetString()
            ?? throw new InvalidOperationException("source-sync payload 缺少 entity");
        var trigger = root.TryGetProperty("trigger", out var t) ? t.GetString() ?? "periodic" : "periodic";

        var policy = await _settings.GetSyncPolicyAsync(ct);
        var now = DateTime.UtcNow;
        DateTime start, end;
        if (trigger == "manual")
        {
            start = root.GetProperty("startUtc").GetDateTime();
            end = root.GetProperty("endUtc").GetDateTime();
        }
        else if (trigger == "daily")
        {
            (start, end) = SyncWindowPolicy.Daily(now, policy);
        }
        else
        {
            await using var c = new SqlConnection(_cs);
            await c.OpenAsync(ct);
            var sysId = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", cancellationToken: ct));
            var cpEnd = await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT window_end FROM dbo.sync_checkpoint WHERE source_system_id=@s AND entity=@e AND checkpoint_kind=N'WINDOW'",
                new { s = sysId, e = entity }, cancellationToken: ct));
            (start, end) = SyncWindowPolicy.Incremental(cpEnd, now, policy);
        }
        var svc = new SourceSyncService(_cs, _adapter, _settings, _audit);
        await svc.RunAsync(orgId, env, entity, start, end, ct);
    }
}
```

- [ ] **Step 5: 实现 SyncSchedulerHostedService + OpsEndpoints + Program 接线**

`traceflow/src/TraceFlow.Host/SyncSchedulerHostedService.cs`：

```csharp
using TraceFlow.Infrastructure.Sources;

namespace TraceFlow.Host;

/// <summary>SYNC-04：30 秒调度循环。先 Delay 后 Tick——短命测试宿主不产生后台作业；生产节奏不变。</summary>
public sealed class SyncSchedulerHostedService : BackgroundService
{
    private readonly SyncScheduler _scheduler;
    private readonly ILogger<SyncSchedulerHostedService> _logger;

    public SyncSchedulerHostedService(SyncScheduler scheduler, ILogger<SyncSchedulerHostedService> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                await _scheduler.TickAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "同步调度 Tick 异常，30 秒后重试");
            }
        }
    }
}
```

`traceflow/src/TraceFlow.Host/Endpoints/OpsEndpoints.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Host.Endpoints;

public static class OpsEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapOpsEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapPost("/api/v1/sync-jobs", (HttpContext ctx, IdempotencyExecutor idem, IJobQueue queue, SettingsService settings, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<SyncJobRequest>(ctx.Request.Body, Web, ct);
                if (req is null || req.Entity is not ("sale" or "purchase-in"))
                    return Err(ctx, 422, "VALIDATION", "entity 须为 sale 或 purchase-in");
                var start = NormalizeUtc(req.Start);
                var end = NormalizeUtc(req.End);
                var now = DateTime.UtcNow;
                if (end <= start)
                    return Err(ctx, 422, "VALIDATION", "end 必须晚于 start");
                if (end > now.AddMinutes(5))
                    return Err(ctx, 422, "VALIDATION", "end 不得超过当前时间 +5 分钟");
                if ((end - start).TotalDays > 90)
                    return Err(ctx, 422, "VALIDATION", "手动窗口不得超过 90 天");
                if (env != "MOCK")
                    return Err(ctx, 422, "ERP_NOT_CERTIFIED", "LIVE 通道须 G-ERP 取证门禁通过后开启");
                if (await settings.IsChannelPausedAsync(ct))
                    return Err(ctx, 422, "CHANNEL_PAUSED", "同步通道已暂停，请先恢复");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var intent = $"manual:{req.Entity}:{start:yyyyMMddHHmmss}:{end:yyyyMMddHHmmss}";
                var payload = JsonSerializer.Serialize(new { orgId, env, entity = req.Entity, trigger = "manual", startUtc = start, endUtc = end });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, "source-sync", intent, payload, now, ct);
                await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "sync.manual",
                    "outbox_job", jobId.ToString(), null, null,
                    JsonSerializer.Serialize(new { entity = req.Entity, start, end }), ctx.TraceIdentifier, null, "LIVE")); // 执行裁决：控制面人工操作审计 env="LIVE"（全局约定优先于简报的通道 env；作业自身审计仍用作业 env）
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.JobRematch));

        app.MapGet("/api/v1/jobs/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var row = await conn.QuerySingleOrDefaultAsync<JobRow>(
                "SELECT id,job_kind,intent_key,job_state,attempt_count,next_run_at,last_error,lease_owner,fencing_token FROM dbo.outbox_job WHERE id=@id",
                new { id });
            if (row is null)
                return Results.Json(new { code = "NOT_FOUND", message = "作业不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            return Results.Ok(new { data = new { jobId = row.id.ToString(), jobKind = row.job_kind, intentKey = row.intent_key,
                jobState = row.job_state, attemptCount = row.attempt_count, nextRunAtUtc = row.next_run_at,
                lastError = row.last_error, leaseOwner = row.lease_owner, fencingToken = row.fencing_token }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/audit-logs", async (HttpContext ctx, int? page, int? pageSize,
            string? action, string? objectType, string? objectId, DateTime? dateStart, DateTime? dateEnd) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var where = "WHERE 1=1";
            var args = new DynamicParameters();
            if (!string.IsNullOrEmpty(action)) { where += " AND action=@action"; args.Add("action", action); }
            if (!string.IsNullOrEmpty(objectType)) { where += " AND object_type=@objectType"; args.Add("objectType", objectType); }
            if (!string.IsNullOrEmpty(objectId)) { where += " AND object_id=@objectId"; args.Add("objectId", objectId); }
            if (dateStart is not null) { where += " AND occurred_at>=@dateStart"; args.Add("dateStart", NormalizeUtc(dateStart.Value)); }
            if (dateEnd is not null) { where += " AND occurred_at<=@dateEnd"; args.Add("dateEnd", NormalizeUtc(dateEnd.Value)); }
            await using var conn = new SqlConnection(connectionString);
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.audit_log {where}", args);
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<AuditRow>($@"
SELECT id,occurred_at,actor,actor_kind,action,object_type,object_id,reason,before_digest,after_digest,request_id,rule_version,env FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY occurred_at DESC, id DESC) AS rn,
           id,occurred_at,actor,actor_kind,action,object_type,object_id,reason,before_digest,after_digest,request_id,rule_version,env
    FROM dbo.audit_log {where}
) t WHERE rn BETWEEN @lo AND @hi ORDER BY rn", args);
            return Results.Ok(new { data = new {
                records = rows.Select(r => new { id = r.id, occurredAtUtc = r.occurred_at, actor = r.actor, actorKind = r.actor_kind,
                    action = r.action, objectType = r.object_type, objectId = r.object_id, reason = r.reason,
                    beforeDigest = r.before_digest, afterDigest = r.after_digest, requestId = r.request_id,
                    ruleVersion = r.rule_version, env = r.env }),
                total, page = p, pageSize = size }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.AuditRead));
    }

    private static DateTime NormalizeUtc(DateTime d)
        => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static (int, string) Err(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    public sealed record SyncJobRequest(string Entity, DateTime Start, DateTime End, string? Filters);

    private sealed class JobRow
    {
        public long id { get; set; }
        public string job_kind { get; set; } = "";
        public string intent_key { get; set; } = "";
        public string job_state { get; set; } = "";
        public int attempt_count { get; set; }
        public DateTime next_run_at { get; set; }
        public string? last_error { get; set; }
        public string? lease_owner { get; set; }
        public long fencing_token { get; set; }
    }

    private sealed class AuditRow
    {
        public long id { get; set; }
        public DateTime occurred_at { get; set; }
        public string actor { get; set; } = "";
        public string actor_kind { get; set; } = "";
        public string action { get; set; } = "";
        public string object_type { get; set; } = "";
        public string? object_id { get; set; }
        public string? reason { get; set; }
        public string? before_digest { get; set; }
        public string? after_digest { get; set; }
        public string? request_id { get; set; }
        public string? rule_version { get; set; }
        public string env { get; set; } = "";
    }
}
```

`traceflow/src/TraceFlow.Host/Program.cs` 三处修改：

1. SettingsService 注册之后追加：

```csharp
var sourceMode = builder.Configuration["TraceFlow:SourceMode"] ?? "fixture";
builder.Services.AddSingleton<ISourceAdapter>(TraceFlow.Infrastructure.Sources.SourceAdapterFactory.Create(
    sourceMode, Path.Combine(AppContext.BaseDirectory, "fixtures", "ksoa")));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Sources.SyncScheduler(
    connectionString, sp.GetRequiredService<IJobQueue>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), env));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Sources.SourceSyncJobHandler(
    connectionString, sp.GetRequiredService<ISourceAdapter>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IAuditLog>()));
```

2. `if (!webOnly) builder.Services.AddHostedService<WorkerHostedService>();` 改为：

```csharp
if (!webOnly)
{
    builder.Services.AddHostedService<WorkerHostedService>();
    builder.Services.AddHostedService<SyncSchedulerHostedService>();
}
```

3. `app.MapSettingsEndpoints(connectionString);` 之后追加：

```csharp
    app.MapOpsEndpoints(connectionString, env);
```

- [ ] **Step 6: 写 M1 API 门禁测试（WAF）**

`traceflow/tests/TraceFlow.IntegrationTests/Api/M1ApiGateTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class M1ApiGateTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public M1ApiGateTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await SeedUserAsync("m1viewer", "只读", "Test-Password-123", new[] { "trace:read" });
        await SeedUserAsync("m1admin", "管理员", "Admin-Password-123",
            new[] { "system:manage", "job:rematch", "audit:read", "trace:read", "master:match", "master:classify" });
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

    private async Task<(HttpClient client, string csrf)> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return (client, session2.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Anonymous_gets_401_on_all_m1_endpoints()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/jobs/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = DateTime.UtcNow.AddDays(-1), end = DateTime.UtcNow })).StatusCode);
    }

    [Fact]
    public async Task Viewer_lacks_settings_and_sync_and_audit_permissions()
    {
        var (client, _) = await LoginAsync("m1viewer", "Test-Password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = DateTime.UtcNow.AddDays(-1), end = DateTime.UtcNow })).StatusCode);
        // trace:read 允许查作业（不存在→404 而非 403）
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/jobs/999999")).StatusCode);
    }

    [Fact]
    public async Task Admin_mutations_require_csrf()
    {
        var (client, csrf) = await LoginAsync("m1admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var put = await client.PutAsJsonAsync("/api/v1/settings/sync.pollSeconds",
            new { valueJson = "30", expectedVersion = "0", reason = "无CSRF" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("CSRF_INVALID", await put.Content.ReadAsStringAsync());
        var post = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = DateTime.UtcNow.AddDays(-1), end = DateTime.UtcNow });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Settings_update_missing_expected_version_422()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PutAsJsonAsync("/api/v1/settings/sync.pollSeconds", new { valueJson = "30", reason = "缺版本" });
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
    }

    [Fact]
    public async Task Manual_sync_job_accepted_and_validated()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var now = DateTime.UtcNow;
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var ok = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = now.AddDays(-1), end = now });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JobAccepted>();
        Assert.False(string.IsNullOrEmpty(body!.Data.JobId));
        // 非法 entity
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var bad = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "inventory", start = now.AddDays(-1), end = now });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        // end 超过 now+5min
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var future = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = now.AddDays(-1), end = now.AddHours(1) });
        Assert.Equal((HttpStatusCode)422, future.StatusCode);
        // 窗口 >90 天
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var wide = await client.PostAsJsonAsync("/api/v1/sync-jobs",
            new { entity = "sale", start = now.AddDays(-120), end = now });
        Assert.Equal((HttpStatusCode)422, wide.StatusCode);
        // 作业可查（trace:read 语义，admin 亦有）；后台 Worker 可能已认领，只断言字段存在不锁定状态
        var jobResp = await client.GetAsync($"/api/v1/jobs/{body.Data.JobId}");
        Assert.Equal(HttpStatusCode.OK, jobResp.StatusCode);
        Assert.Contains("jobState", await jobResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Audit_logs_paginated_and_filterable()
    {
        var (client, _) = await LoginAsync("m1admin", "Admin-Password-123");
        var resp = await client.GetAsync("/api/v1/audit-logs?page=1&pageSize=5&action=auth.login");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("records", body);
        Assert.Contains("total", body);
    }

    private sealed record JobAccepted(JobData Data);
    private sealed record JobData(string JobId);
}
```

- [ ] **Step 7: 跑全部新测试 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿。M0 ApiGateTests 不受影响（调度器先 Delay 30 秒，短命测试宿主内不产生后台作业；dashboard 断言只查字段存在）。

- [ ] **Step 8: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs traceflow/src/TraceFlow.Infrastructure/Sources/SourceSyncJobHandler.cs traceflow/src/TraceFlow.Host/SyncSchedulerHostedService.cs traceflow/src/TraceFlow.Host/Endpoints/OpsEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SourceSyncJobHandlerTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/M1ApiGateTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): sync scheduler, source-sync handler, ops endpoints with gates (T9)"
```

---
### Task 10: CSV 导入（暂存/预览/提交）+ import-stage 作业 + 导入端点

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Master/ImportService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/ImportStageJobHandler.cs`
- Create: `traceflow/src/TraceFlow.Host/Endpoints/ImportEndpoints.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（DI 注册 ImportService/ImportStageJobHandler + MapImportEndpoints）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Master/ImportServiceTests.cs`

**Interfaces:**
- Consumes:
  - Task 1 `import_batch.staged_content nvarchar(max) NULL`（0007 新增列）；`RowVer.ToHex(byte[])`/`FromHex(string)`。
  - Task 2 `CsvReader.Parse(string text, IReadOnlyList<string> requiredColumns)`（表头必须**恰好**覆盖 requiredColumns，值已 Trim，空单元格为 `""`）；`CsvFormatException`；`ApprovalNoNormalizer.Normalize(string?)`（TraceFlow.Domain.Master / TraceFlow.Domain.Matching）。
  - Task 3 实例 `IdempotencyExecutor.RunAsync(ctx, action)`（DI 注入）。
  - Task 4 `SettingValidationException`/`SettingVersionException`/`ImportBoundsException`（ApiErrors 已映射 422 VALIDATION / 409 VERSION_CONFLICT / 422 IMPORT_BOUNDS）。
  - Task 6 `SettingsService.GetImportBoundsAsync(ct) → Task<(long MaxFileBytes, int MaxRows)>`、`ListAsync`、`UpdateAsync`（测试调上限用）。
  - Task 7 `TestSeed.EnsureAsync(SqlConnection) → Task<(long OrgId, long SysId)>`（命名空间 `TraceFlow.IntegrationTests.Sources`）。
  - M0 `IJobQueue.EnqueueAsync(conn, tx, orgId, env, jobKind, intentKey, payloadJson, nextRunAtUtc, ct) → Task<long>`（intent 重复→重选既有 job id）；`ClaimAsync(workerId, env, jobKinds, lease, ct)`；`CompleteAsync(jobId, workerId, fencingToken, ct)`；`ClaimedJob(long Id, string JobKind, string IntentKey, string? PayloadJson, long FencingToken, DateTime LeaseUntilUtc, int AttemptCount)`；`IJobHandler { string Kind; Task HandleAsync(ClaimedJob, ct); }`；`IAuditLog.AppendAsync(conn, tx?, AuditEntry)`；`AuditLog` 无参 ctor（TraceFlow.Infrastructure.Security）；`JobQueue` ctor `(string, IAuditLog)`（Task 4 后）；`RequirePermissionAttribute`/`PermissionCatalog.MasterMatch`（TraceFlow.Application.Security）。
- Produces:
  - `sealed class ImportService`（TraceFlow.Infrastructure.Master）ctor `(string connectionString, SettingsService settings, IJobQueue queue, IAuditLog audit)`：
    - `static IReadOnlyDictionary<string, IReadOnlyList<string>> Templates`（四种 fileKind 的固定 CSV 模板列，表头须恰好一致）。
    - `Task<(long BatchId, long JobId)> StageAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, string fileKind, string? fileName, string fileHash, string contentBase64, string actor, CancellationToken ct)`。
    - `static string? ValidateRow(string fileKind, IReadOnlyDictionary<string,string> row)`（null=OK；否则错误文案）。
    - `Task<int> CommitAsync(SqlConnection conn, SqlTransaction tx, long batchId, string env, string expectedVersionHex, string? reason, string actor, CancellationToken ct)`（返回应用行数）。
  - `sealed class ImportStageJobHandler : IJobHandler`（TraceFlow.Infrastructure.Jobs）ctor `(string connectionString)`；`Kind => "import-stage"`；payload `{orgId, env, batchId}`。
  - `ImportEndpoints.MapImportEndpoints(this WebApplication app, string connectionString, string env)`；请求记录 `ImportRequest(string? FileKind, string? FileHash, string? Content, string? FileName)`、`CommitImportRequest(string? ExpectedVersion, string? Reason)`。
  - 作业意图键：`import-stage:{batchId}`（kind import-stage）、`match:import:{batchId}`（kind master-match，提交时同事务入队，Task 12 消费）。
- 规则裁定（写入代码注释与契约文档）：
  - **暂存幂等**：同 `(org_id, file_kind, file_hash)` 已存在批次→直接返回其 batchId；STAGED 批次确保 stage 作业存在（intent 去重），COMMITTED/FAILED 批次不再产生作业（JobId=0，UI 跳过轮询直接查批次）。
  - **stage 作业可重跑**：先 DELETE 本批次 import_row 再全量重插（行由 staged_content 确定性推导）；租约失效重跑不产生重复行。
  - **批次状态**：stage 后 `ok==0`（含零数据行）→ FAILED（无可提交内容）；否则 STAGED。commit 仅接受 STAGED。
  - **MD-01 分类保护**：PRODUCT 的 UPDATE 语句**不含** trace_classification/classification_reason 两列；分类只能走 Task 11 专用端点。新 INSERT 可携带 CSV 分类（EXEMPT 必须带 reason，stage 行校验强制）。
  - **ENT-02**：ENTERPRISE 仅 INSERT（IF NOT EXISTS 按 ref_ent_id+ent_id）；已存在版本行永不更新——企业改名只能由平台同步产生新版本行（M2），导入不覆盖。
  - **审计 env 约定**：主数据/设置类审计（settings.update、master.import.*）env=`LIVE`（全局配置面，与 AuthEndpoints 一致）；通道作业审计（source.sync、master.match.auto）env=作业 env。
  - 行校验含**列宽检查**（与 0003 DDL 逐列一致），超长在 stage 记 ERROR 行而非 SQL 截断异常（AT-006 同精神：原始数据保留在 raw_json）。

- [ ] **Step 1: 写失败测试**

`traceflow/tests/TraceFlow.IntegrationTests/Master/ImportServiceTests.cs`（每测试独立库：上限设置测试会改 import.maxFileBytes/maxRows，类共享库产生顺序依赖）：

```csharp
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

public class ImportServiceTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private const string ProductHeader =
        "spid,display_code,barcode,name,approval_no,manufacturer,dosage_form,strength,package_spec,unit,trace_classification,classification_reason,manufacturer_identity";

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private ImportService NewSvc() => new(_fx.ConnectionString,
        new SettingsService(_fx.ConnectionString, new DpapiProtector()),
        new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());

    private async Task<(long OrgId, long BatchId, long JobId)> StageAsync(string kind, string csv,
        string? fileName = null, string? hashOverride = null, string? contentOverride = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var (batchId, jobId) = await NewSvc().StageAsync(c, tx, orgId, "MOCK", kind, fileName,
            hashOverride ?? Hash(csv), contentOverride ?? B64(csv), "tester", CancellationToken.None);
        await tx.CommitAsync();
        return (orgId, batchId, jobId);
    }

    private async Task RunStageJobAsync()
    {
        var queue = new JobQueue(_fx.ConnectionString, new AuditLog());
        var job = await queue.ClaimAsync("test-worker", "MOCK", new[] { "import-stage" }, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(job);
        await new ImportStageJobHandler(_fx.ConnectionString).HandleAsync(job!, CancellationToken.None);
        await queue.CompleteAsync(job!.Id, "test-worker", job.FencingToken, CancellationToken.None);
    }

    private async Task<string> BatchVersionAsync(long batchId)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        var ver = await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.import_batch WHERE id=@id", new { id = batchId });
        return RowVer.ToHex(ver);
    }

    private async Task CommitAsync(long batchId, string version, string? reason = "提交")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await NewSvc().CommitAsync(c, tx, batchId, "MOCK", version, reason, "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    private async Task SetSettingAsync(string key, string valueJson)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var view = (await svc.ListAsync(CancellationToken.None)).Single(v => v.Key == key);
        await svc.UpdateAsync(c, tx, key, valueJson, null, view.Id == 0 ? "0" : view.Version, "调整上限", "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, param);
    }

    [Fact]
    public async Task Stage_product_creates_batch_then_handler_writes_rows()
    {
        var csv = ProductHeader + "\n" +
            "P001,,6901234567890,阿莫西林胶囊,国药准字H12345678,华北制药,胶囊剂,0.25g,24粒,盒,REQUIRED,,\n" +
            "P002,,,,维生素C片,,,,,盒,EXEMPT,普通化学药豁免,\n" +
            "P003,,,,板蓝根颗粒,,,,,盒,EXEMPT,,\n";
        var (_, batchId, jobId) = await StageAsync("PRODUCT", csv);
        Assert.True(jobId > 0);
        Assert.Equal("STAGED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(3, await ScalarAsync<int>("SELECT total_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));

        await RunStageJobAsync();

        Assert.Equal(2, await ScalarAsync<int>("SELECT success_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT failed_rows FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        Assert.Equal(3, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id", new { id = batchId }));
        var error = await ScalarAsync<string?>("SELECT error FROM dbo.import_row WHERE batch_id=@id AND status=N'ERROR'", new { id = batchId });
        Assert.Contains("classification_reason", error);
        // 原始行完整保留（含 EXEMPT 空 reason 的缺陷行）
        var raw = await ScalarAsync<string>("SELECT raw_json FROM dbo.import_row WHERE batch_id=@id AND status=N'ERROR'", new { id = batchId });
        Assert.Contains("P003", raw);
    }

    [Fact]
    public async Task Stage_hash_mismatch_rejected()
    {
        var csv = ProductHeader + "\nP001,,,,药,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<SettingValidationException>(() => StageAsync("PRODUCT", csv, hashOverride: Hash("别的内容")));
    }

    [Fact]
    public async Task Stage_invalid_base64_rejected()
    {
        var csv = ProductHeader + "\nP001,,,,药,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<SettingValidationException>(() => StageAsync("PRODUCT", csv, contentOverride: "!!!不是Base64!!!"));
    }

    [Fact]
    public async Task Stage_oversize_rejected()
    {
        await SetSettingAsync("import.maxFileBytes", "1024");
        var csv = ProductHeader + "\nP001," + new string('9', 900) + ",,名,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<ImportBoundsException>(() => StageAsync("PRODUCT", csv));
    }

    [Fact]
    public async Task Stage_too_many_rows_rejected()
    {
        await SetSettingAsync("import.maxRows", "1");
        var csv = ProductHeader + "\nP001,,,,药一,,,,,盒,UNKNOWN,,\nP002,,,,药二,,,,,盒,UNKNOWN,,\n";
        await Assert.ThrowsAsync<ImportBoundsException>(() => StageAsync("PRODUCT", csv));
    }

    [Fact]
    public async Task Stage_same_hash_idempotent()
    {
        var csv = ProductHeader + "\nP001,,,,药,,,,,盒,UNKNOWN,,\n";
        var (_, batch1, job1) = await StageAsync("PRODUCT", csv);
        var (_, batch2, job2) = await StageAsync("PRODUCT", csv);
        Assert.Equal(batch1, batch2);
        Assert.Equal(job1, job2); // intent 去重→重选同一作业
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_batch"));
    }

    [Fact]
    public async Task Stage_header_only_batch_becomes_failed()
    {
        var (_, batchId, _) = await StageAsync("PRODUCT", ProductHeader + "\n");
        await RunStageJobAsync();
        Assert.Equal("FAILED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
        await Assert.ThrowsAsync<SettingValidationException>(() => CommitAsync(batchId, await BatchVersionAsync(batchId)));
    }

    [Fact]
    public async Task Commit_product_update_never_touches_classification()
    {
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            var (orgId, sysId) = await TestSeed.EnsureAsync(c);
            await c.ExecuteAsync(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification,classification_reason)
VALUES(@org,@sys,N'P100',N'旧名',N'REQUIRED',N'处方药')", new { org = orgId, sys = sysId });
        }
        var csv = ProductHeader + "\n" +
            "P100,,,,新名,,,,,盒,EXEMPT,试图覆盖,\n" +
            "P101,,,,新药,,,,,盒,EXEMPT,豁免理由,\n";
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));

        Assert.Equal("新名", await ScalarAsync<string>("SELECT name FROM dbo.product WHERE spid=N'P100'"));
        Assert.Equal("REQUIRED", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE spid=N'P100'"));
        Assert.Equal("处方药", await ScalarAsync<string>("SELECT classification_reason FROM dbo.product WHERE spid=N'P100'"));
        Assert.Equal("EXEMPT", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE spid=N'P101'"));
        Assert.Equal("豁免理由", await ScalarAsync<string>("SELECT classification_reason FROM dbo.product WHERE spid=N'P101'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id AND status=N'APPLIED'", new { id = batchId }));
        Assert.Equal("COMMITTED", await ScalarAsync<string>("SELECT status FROM dbo.import_batch WHERE id=@id", new { id = batchId }));
    }

    [Fact]
    public async Task Commit_version_conflict_and_double_commit()
    {
        var csv = ProductHeader + "\nP001,,,,药,,,,,盒,UNKNOWN,,\n";
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await Assert.ThrowsAsync<SettingVersionException>(() => CommitAsync(batchId, "0000000000000000"));
        var ver = await BatchVersionAsync(batchId);
        await CommitAsync(batchId, ver);
        // 提交后 ver 已变→旧版本冲突；用新版本再提交→状态守卫 422
        await Assert.ThrowsAsync<SettingVersionException>(() => CommitAsync(batchId, ver));
        await Assert.ThrowsAsync<SettingValidationException>(() => CommitAsync(batchId, await BatchVersionAsync(batchId)));
    }

    [Fact]
    public async Task Commit_enterprise_insert_only()
    {
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) VALUES(N'REF1',N'ENT1',N'旧名')");
        }
        var csv = "ref_ent_id,ent_id,name,credit_code,audit_status,role\n" +
            "REF1,ENT1,新名,91110000MA001,审核通过,SUPPLIER\n" +
            "REF2,ENT2,另一家,,,\n";
        var (_, batchId, _) = await StageAsync("ENTERPRISE", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));
        Assert.Equal("旧名", await ScalarAsync<string>("SELECT name FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF1' AND ent_id=N'ENT1'"));
        Assert.Equal("另一家", await ScalarAsync<string>("SELECT name FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF2' AND ent_id=N'ENT2'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version"));
    }

    [Fact]
    public async Task Commit_enqueues_match_job_and_audit()
    {
        var csv = ProductHeader + "\nP001,,,,药,,,,,盒,UNKNOWN,,\n";
        var (_, batchId, _) = await StageAsync("PRODUCT", csv);
        await RunStageJobAsync();
        await CommitAsync(batchId, await BatchVersionAsync(batchId));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'master-match' AND intent_key=@k",
            new { k = $"match:import:{batchId}" }));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.import.commit' AND object_id=@id",
            new { id = batchId.ToString() }));
    }

    [Fact]
    public async Task Commit_supplier_and_platform_drug_upsert()
    {
        var csv1 = "dwbh,name,credit_code,role\nD100,供应商甲,91110000MA002,SUPPLIER\n";
        var (_, b1, _) = await StageAsync("SUPPLIER", csv1);
        await RunStageJobAsync();
        await CommitAsync(b1, await BatchVersionAsync(b1));
        Assert.Equal("供应商甲", await ScalarAsync<string>("SELECT name FROM dbo.supplier WHERE dwbh=N'D100'"));

        var csv2 = "dwbh,name,credit_code,role\nD100,供应商乙,91110000MA002,SUPPLIER\n";
        var (_, b2, _) = await StageAsync("SUPPLIER", csv2);
        await RunStageJobAsync();
        await CommitAsync(b2, await BatchVersionAsync(b2));
        Assert.Equal("供应商乙", await ScalarAsync<string>("SELECT name FROM dbo.supplier WHERE dwbh=N'D100'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier"));

        var csv3 = "platform_drug_id,approval_no,manufacturer_identity,dosage_form,strength,package_spec,source_kind\n" +
            "PD100,国药准字H12345678,华北制药,胶囊剂,0.25g,24粒,MANUAL\n";
        var (_, b3, _) = await StageAsync("PLATFORM_DRUG", csv3);
        await RunStageJobAsync();
        await CommitAsync(b3, await BatchVersionAsync(b3));
        Assert.Equal("国药准字H12345678", await ScalarAsync<string>("SELECT approval_no_norm FROM dbo.platform_drug WHERE platform_drug_id=N'PD100'"));
        Assert.Equal("MANUAL", await ScalarAsync<string>("SELECT source_kind FROM dbo.platform_drug WHERE platform_drug_id=N'PD100'"));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~ImportServiceTests"`
Expected: FAIL 编译错误（ImportService/ImportStageJobHandler 不存在）。

- [ ] **Step 3: 实现 ImportService**

`traceflow/src/TraceFlow.Infrastructure/Master/ImportService.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Master;
using TraceFlow.Domain.Matching;
using TraceFlow.Domain.Versioning;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Master;

/// <summary>
/// MD-02/03：CSV 导入暂存/预览/提交。暂存按 (org,fileKind,fileHash) 幂等；提交仅应用 OK 行。
/// MD-01：PRODUCT 更新永不触碰 trace_classification/classification_reason（分类走专用端点）。
/// ENT-02：ENTERPRISE 仅插入——企业新版本只能来自平台同步，导入不覆盖既有版本行。
/// </summary>
public sealed class ImportService
{
    private readonly SettingsService _settings;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;

    // connectionString 参数保留（DI 形态一致）但不存储：所有读写走调用方传入的 conn/tx，
    // 存储未使用的私有字段会触发 CS0414（TreatWarningsAsErrors=true）。
    public ImportService(string connectionString, SettingsService settings, IJobQueue queue, IAuditLog audit)
    { _settings = settings; _queue = queue; _audit = audit; }

    /// <summary>四种导入模板（表头须恰好覆盖这些列，顺序不限；值可空，行校验另行把关）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Templates =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["PRODUCT"] = new[] { "spid", "display_code", "barcode", "name", "approval_no", "manufacturer",
                "dosage_form", "strength", "package_spec", "unit", "trace_classification", "classification_reason", "manufacturer_identity" },
            ["SUPPLIER"] = new[] { "dwbh", "name", "credit_code", "role" },
            ["PLATFORM_DRUG"] = new[] { "platform_drug_id", "approval_no", "manufacturer_identity",
                "dosage_form", "strength", "package_spec", "source_kind" },
            ["ENTERPRISE"] = new[] { "ref_ent_id", "ent_id", "name", "credit_code", "audit_status", "role" },
        };

    public async Task<(long BatchId, long JobId)> StageAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        string fileKind, string? fileName, string fileHash, string contentBase64, string actor, CancellationToken ct)
    {
        if (!Templates.TryGetValue(fileKind, out var columns))
            throw new SettingValidationException($"未知导入类型: {fileKind}");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { throw new SettingValidationException("content 不是合法 Base64"); }

        // 上限读取走独立连接（app_setting 与本事务无锁交集，安全）
        var (maxBytes, maxRows) = await _settings.GetImportBoundsAsync(ct);
        if (bytes.Length > maxBytes)
            throw new ImportBoundsException($"文件 {bytes.Length} 字节超过上限 {maxBytes}");
        var text = Encoding.UTF8.GetString(bytes);
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
        try { rows = CsvReader.Parse(text, columns); }
        catch (CsvFormatException ex) { throw new SettingValidationException(ex.Message); }
        if (rows.Count > maxRows)
            throw new ImportBoundsException($"数据行 {rows.Count} 超过上限 {maxRows}");
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, fileHash, StringComparison.OrdinalIgnoreCase))
            throw new SettingValidationException("fileHash 与内容 SHA-256 不一致");
        if (fileName is not null && fileName.Length > 260)
            throw new SettingValidationException("fileName 超过 260 字符");

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingBatch>(new CommandDefinition(
            "SELECT id,status FROM dbo.import_batch WHERE org_id=@org AND file_kind=@kind AND file_hash=@hash",
            new { org = orgId, kind = fileKind, hash = actualHash }, tx, cancellationToken: ct));
        long batchId;
        if (existing is not null)
        {
            batchId = existing.id;
            if (existing.status != "STAGED") return (batchId, 0); // 已提交/失败批次不再产生作业
        }
        else
        {
            batchId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.import_batch(org_id,file_kind,file_name,file_hash,total_rows,status,staged_by,staged_content)
OUTPUT inserted.id
VALUES(@org,@kind,@name,@hash,@total,N'STAGED',@by,@content)",
                new { org = orgId, kind = fileKind, name = fileName ?? "upload.csv", hash = actualHash,
                      total = rows.Count, by = actor, content = text }, tx, cancellationToken: ct));
        }
        var jobId = await _queue.EnqueueAsync(conn, tx, orgId, env, "import-stage", $"import-stage:{batchId}",
            JsonSerializer.Serialize(new { orgId, env, batchId }), null, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.import.stage", "import_batch",
            batchId.ToString(), null, null,
            JsonSerializer.Serialize(new { fileKind, totalRows = rows.Count, fileHash = actualHash }), null, null, "LIVE"));
        return (batchId, jobId);
    }

    /// <summary>行校验（列宽与 0003 DDL 逐列一致）；null=OK。超长记 ERROR 行而非 SQL 截断异常。</summary>
    public static string? ValidateRow(string fileKind, IReadOnlyDictionary<string, string> r)
    {
        string? Req(string col, int max) =>
            r[col].Length == 0 ? $"{col} 必填" : r[col].Length > max ? $"{col} 超过 {max} 字符" : null;
        string? Opt(string col, int max) => r[col].Length > max ? $"{col} 超过 {max} 字符" : null;
        string? First(params string?[] errors) => errors.FirstOrDefault(e => e is not null);
        switch (fileKind)
        {
            case "PRODUCT":
                var cls = r["trace_classification"].Length == 0 ? "UNKNOWN" : r["trace_classification"];
                if (cls is not ("UNKNOWN" or "REQUIRED" or "EXEMPT")) return $"trace_classification 非法: {cls}";
                if (cls == "EXEMPT" && r["classification_reason"].Length == 0) return "EXEMPT 必须填写 classification_reason";
                return First(Req("spid", 64), Req("name", 200), Opt("display_code", 64), Opt("barcode", 64),
                    Opt("approval_no", 100), Opt("manufacturer", 200), Opt("manufacturer_identity", 128),
                    Opt("dosage_form", 64), Opt("strength", 100), Opt("package_spec", 100), Opt("unit", 32),
                    Opt("classification_reason", 500));
            case "SUPPLIER":
                if (r["role"].Length > 0 && r["role"] is not ("SUPPLIER" or "SHIPPER" or "OWNER" or "DELIVERER"))
                    return $"role 非法: {r["role"]}";
                return First(Req("dwbh", 64), Req("name", 200), Opt("credit_code", 64));
            case "PLATFORM_DRUG":
                return First(Req("platform_drug_id", 64), Req("source_kind", 32), Opt("approval_no", 100),
                    Opt("manufacturer_identity", 128), Opt("dosage_form", 64), Opt("strength", 100), Opt("package_spec", 100));
            case "ENTERPRISE":
                return First(Req("ref_ent_id", 64), Req("ent_id", 64), Req("name", 200),
                    Opt("credit_code", 64), Opt("audit_status", 32), Opt("role", 32));
            default:
                return $"未知导入类型: {fileKind}";
        }
    }

    public async Task<int> CommitAsync(SqlConnection conn, SqlTransaction tx, long batchId, string env,
        string expectedVersionHex, string? reason, string actor, CancellationToken ct)
    {
        var batch = await conn.QuerySingleOrDefaultAsync<BatchRow>(new CommandDefinition(
            "SELECT id,org_id,file_kind,status,ver FROM dbo.import_batch WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
            new { id = batchId }, tx, cancellationToken: ct));
        if (batch is null) throw new SettingValidationException("批次不存在");
        if (!batch.ver.SequenceEqual(RowVer.FromHex(expectedVersionHex)))
            throw new SettingVersionException("批次版本冲突，请刷新后重试");
        if (batch.status != "STAGED") throw new SettingValidationException("批次状态不是 STAGED，无法提交");

        var rows = (await conn.QueryAsync<ImportRow>(new CommandDefinition(
            "SELECT id,row_number,raw_json FROM dbo.import_row WHERE batch_id=@b AND status=N'OK' ORDER BY row_number",
            new { b = batchId }, tx, cancellationToken: ct))).ToList();
        var sysId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'", null, tx, cancellationToken: ct));

        var applied = 0;
        foreach (var row in rows)
        {
            var r = JsonSerializer.Deserialize<Dictionary<string, string>>(row.raw_json)!;
            switch (batch.file_kind)
            {
                case "PRODUCT": await ApplyProductAsync(conn, tx, batch.org_id, sysId, r, ct); break;
                case "SUPPLIER": await ApplySupplierAsync(conn, tx, batch.org_id, sysId, r, ct); break;
                case "PLATFORM_DRUG": await ApplyPlatformDrugAsync(conn, tx, r, ct); break;
                case "ENTERPRISE": await ApplyEnterpriseAsync(conn, tx, r, ct); break;
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.import_row SET status=N'APPLIED' WHERE id=@id", new { id = row.id }, tx, cancellationToken: ct));
            applied++;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.import_batch SET status=N'COMMITTED', committed_by=@by, committed_at=SYSUTCDATETIME() WHERE id=@id",
            new { id = batchId, by = actor }, tx, cancellationToken: ct));
        // 提交即触发自动匹配（Task 12 消费 master-match）；同事务保证不丢
        await _queue.EnqueueAsync(conn, tx, batch.org_id, env, "master-match", $"match:import:{batchId}",
            JsonSerializer.Serialize(new { orgId = batch.org_id, env, entity = (string?)null, trigger = "import" }), null, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.import.commit", "import_batch",
            batchId.ToString(), reason, JsonSerializer.Serialize(new { status = "STAGED" }),
            JsonSerializer.Serialize(new { status = "COMMITTED", appliedRows = applied }), null, null, "LIVE"));
        return applied;
    }

    private static string? N(string v) => v.Length == 0 ? null : v;

    private static async Task ApplyProductAsync(SqlConnection conn, SqlTransaction tx, long orgId, long sysId,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        var cls = r["trace_classification"].Length == 0 ? "UNKNOWN" : r["trace_classification"];
        var rawJson = JsonSerializer.Serialize(r);
        // MD-01：UPDATE 刻意不含 trace_classification/classification_reason——分类只能走 Task 11 专用端点
        var updated = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product SET display_code=@dc, barcode=@bc, name=@name, approval_no_raw=@ar, approval_no_norm=@an,
       manufacturer_raw=@mr, manufacturer_identity=@mi, dosage_form=@df, strength=@st, package_spec=@ps, unit=@unit,
       raw_json=@raw, updated_at=SYSUTCDATETIME()
WHERE org_id=@org AND source_system_id=@sys AND spid=@spid",
            new { org = orgId, sys = sysId, spid = r["spid"], dc = N(r["display_code"]), bc = N(r["barcode"]),
                  name = r["name"], ar = N(r["approval_no"]), an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                  mr = N(r["manufacturer"]), mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]),
                  st = N(r["strength"]), ps = N(r["package_spec"]), unit = N(r["unit"]), raw = rawJson },
            tx, cancellationToken: ct));
        if (updated == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,display_code,barcode,name,approval_no_raw,approval_no_norm,
       manufacturer_raw,manufacturer_identity,dosage_form,strength,package_spec,unit,trace_classification,classification_reason,raw_json)
VALUES(@org,@sys,@spid,@dc,@bc,@name,@ar,@an,@mr,@mi,@df,@st,@ps,@unit,@cls,@cr,@raw)",
                new { org = orgId, sys = sysId, spid = r["spid"], dc = N(r["display_code"]), bc = N(r["barcode"]),
                      name = r["name"], ar = N(r["approval_no"]), an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                      mr = N(r["manufacturer"]), mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]),
                      st = N(r["strength"]), ps = N(r["package_spec"]), unit = N(r["unit"]),
                      cls, cr = N(r["classification_reason"]), raw = rawJson }, tx, cancellationToken: ct));
    }

    private static async Task ApplySupplierAsync(SqlConnection conn, SqlTransaction tx, long orgId, long sysId,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        var rawJson = JsonSerializer.Serialize(r);
        var updated = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier SET name=@name, credit_code=@cc, role=@role, raw_json=@raw
WHERE org_id=@org AND source_system_id=@sys AND dwbh=@dwbh",
            new { org = orgId, sys = sysId, dwbh = r["dwbh"], name = r["name"],
                  cc = N(r["credit_code"]), role = N(r["role"]), raw = rawJson }, tx, cancellationToken: ct));
        if (updated == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code,role,raw_json)
VALUES(@org,@sys,@dwbh,@name,@cc,@role,@raw)",
                new { org = orgId, sys = sysId, dwbh = r["dwbh"], name = r["name"],
                      cc = N(r["credit_code"]), role = N(r["role"]), raw = rawJson }, tx, cancellationToken: ct));
    }

    private static async Task ApplyPlatformDrugAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        var identityJson = JsonSerializer.Serialize(r);
        var updated = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.platform_drug SET approval_no_norm=@an, manufacturer_identity=@mi, dosage_form=@df,
       strength=@st, package_spec=@ps, identity_json=@ij, source_kind=@sk
WHERE platform_drug_id=@pdid",
            new { pdid = r["platform_drug_id"], an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                  mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]), st = N(r["strength"]),
                  ps = N(r["package_spec"]), ij = identityJson, sk = r["source_kind"] }, tx, cancellationToken: ct));
        if (updated == 0)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.platform_drug(platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec,identity_json,source_kind)
VALUES(@pdid,@an,@mi,@df,@st,@ps,@ij,@sk)",
                new { pdid = r["platform_drug_id"], an = ApprovalNoNormalizer.Normalize(N(r["approval_no"])),
                      mi = N(r["manufacturer_identity"]), df = N(r["dosage_form"]), st = N(r["strength"]),
                      ps = N(r["package_spec"]), ij = identityJson, sk = r["source_kind"] }, tx, cancellationToken: ct));
    }

    private static async Task ApplyEnterpriseAsync(SqlConnection conn, SqlTransaction tx,
        IReadOnlyDictionary<string, string> r, CancellationToken ct)
    {
        // ENT-02：仅插入。既有 (ref_ent_id,ent_id) 版本行永不更新（改名→平台同步新版本行，M2）
        await conn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.platform_enterprise_version WHERE ref_ent_id=@ref AND ent_id=@ent)
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
VALUES(@ref,@ent,@name,@cc,@audit,@role)",
            new { @ref = r["ref_ent_id"], ent = r["ent_id"], name = r["name"],
                  cc = N(r["credit_code"]), audit = N(r["audit_status"]), role = N(r["role"]) }, tx, cancellationToken: ct));
    }

    private sealed record ExistingBatch(long id, string status);
    private sealed record BatchRow(long id, long org_id, string file_kind, string status, byte[] ver);
    private sealed record ImportRow(long id, int row_number, string raw_json);
}
```

注意：`batch.ver.SequenceEqual(...)` 是值比较（byte[] 的 `==` 是引用比较，禁止）。ImportService 不存储 connectionString（避免 CS0414）；上限读取经 SettingsService 独立连接，与导入事务无锁交集。

- [ ] **Step 4: 实现 ImportStageJobHandler**

`traceflow/src/TraceFlow.Infrastructure/Jobs/ImportStageJobHandler.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Master;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>
/// import-stage：重解析 staged_content → import_row OK/ERROR + 计数。
/// 先删后插保证租约失效重跑幂等（行由 staged_content 确定性推导）。
/// ok==0（含零数据行）→ FAILED；否则 STAGED 等待人工提交。
/// </summary>
public sealed class ImportStageJobHandler : IJobHandler
{
    private readonly string _cs;
    public ImportStageJobHandler(string connectionString) => _cs = connectionString;
    public string Kind => "import-stage";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        var batchId = payload.RootElement.GetProperty("batchId").GetInt64();
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var batch = await conn.QuerySingleOrDefaultAsync<Batch>(new CommandDefinition(
            "SELECT file_kind,status,staged_content FROM dbo.import_batch WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
            new { id = batchId }, tx, cancellationToken: ct));
        if (batch is null || batch.status != "STAGED") { await tx.CommitAsync(ct); return; } // 已提交/失败：重跑无操作
        if (batch.staged_content is null) throw new InvalidOperationException($"批次 {batchId} 缺少 staged_content");

        var rows = CsvReader.Parse(batch.staged_content, ImportService.Templates[batch.file_kind]);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.import_row WHERE batch_id=@b", new { b = batchId }, tx, cancellationToken: ct));
        var ok = 0; var failed = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var error = ImportService.ValidateRow(batch.file_kind, rows[i]);
            if (error is null) ok++; else failed++;
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.import_row(batch_id,row_number,raw_json,status,error) VALUES(@b,@rn,@raw,@status,@error)",
                new { b = batchId, rn = i + 1, raw = JsonSerializer.Serialize(rows[i]),
                      status = error is null ? "OK" : "ERROR", error }, tx, cancellationToken: ct));
        }
        var status = ok == 0 ? "FAILED" : "STAGED";
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.import_batch SET total_rows=@total, success_rows=@ok, failed_rows=@failed, status=@status WHERE id=@id",
            new { id = batchId, total = rows.Count, ok, failed, status }, tx, cancellationToken: ct));
        await new AuditLog().AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "master.import.staged",
            "import_batch", batchId.ToString(), null, null,
            JsonSerializer.Serialize(new { totalRows = rows.Count, successRows = ok, failedRows = failed, status }), null, null, "LIVE"));
        await tx.CommitAsync(ct);
    }

    private sealed record Batch(string file_kind, string status, string? staged_content);
}
```

- [ ] **Step 5: 实现导入端点 + Program 接线**

`traceflow/src/TraceFlow.Host/Endpoints/ImportEndpoints.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Domain.Versioning;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Master;

namespace TraceFlow.Host.Endpoints;

public static class ImportEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapImportEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapPost("/api/v1/imports", (HttpContext ctx, IdempotencyExecutor idem, ImportService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ImportRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.FileKind) || string.IsNullOrEmpty(req.FileHash) || string.IsNullOrEmpty(req.Content))
                    return (422, Err(ctx, "VALIDATION", "fileKind/fileHash/content 必填"));
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", null, tx, cancellationToken: ct));
                if (orgId == 0) return (422, Err(ctx, "VALIDATION", "无启用组织"));
                var (batchId, jobId) = await svc.StageAsync(conn, tx, orgId, env, req.FileKind, req.FileName,
                    req.FileHash, req.Content, ctx.User.Identity!.Name!, ct);
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString(), batchId = batchId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        // 预览定位：按 (fileKind,fileHash) 找批次（UI 上传后拿 batchId 也可直接查详情）
        app.MapGet("/api/v1/imports", async (HttpContext ctx, string? fileKind, string? fileHash) =>
        {
            if (string.IsNullOrEmpty(fileKind) || string.IsNullOrEmpty(fileHash))
                return Results.Json(new { code = "VALIDATION", message = "fileKind/fileHash 必填", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var b = await conn.QuerySingleOrDefaultAsync<BatchView>(new CommandDefinition(@"
SELECT id,file_kind,file_name,file_hash,total_rows,success_rows,failed_rows,status,staged_by,staged_at,committed_by,committed_at,ver
FROM dbo.import_batch
WHERE org_id=(SELECT id FROM dbo.organization WHERE is_active=1) AND file_kind=@kind AND file_hash=@hash",
                // T10 评审修复（controller ruling）：file_hash 为 BIN2 大写存储，POST 接受任意大小写；
                // GET 必须归一化大写，否则小写 hex 客户端暂存成功后预览定位 404（写收读拒不对称）。
                new { kind = fileKind, hash = fileHash.ToUpperInvariant() }, cancellationToken: ctx.RequestAborted));
            if (b is null)
                return Results.Json(new { code = "NOT_FOUND", message = "批次不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            return Results.Ok(new { data = ToView(b), requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapGet("/api/v1/imports/{id:long}", async (HttpContext ctx, long id, int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var b = await conn.QuerySingleOrDefaultAsync<BatchView>(new CommandDefinition(
                "SELECT id,file_kind,file_name,file_hash,total_rows,success_rows,failed_rows,status,staged_by,staged_at,committed_by,committed_at,ver FROM dbo.import_batch WHERE id=@id",
                new { id }, cancellationToken: ctx.RequestAborted));
            if (b is null)
                return Results.Json(new { code = "NOT_FOUND", message = "批次不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.import_row WHERE batch_id=@id", new { id }, cancellationToken: ctx.RequestAborted));
            var rows = await conn.QueryAsync<RowView>(new CommandDefinition(@"
SELECT row_number,status,error,raw_json FROM (
  SELECT row_number,status,error,raw_json, ROW_NUMBER() OVER (ORDER BY row_number) AS rn
  FROM dbo.import_row WHERE batch_id=@id) t WHERE rn BETWEEN @lo AND @hi",
                new { id, lo = (p - 1) * ps + 1, hi = p * ps }, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new
            {
                data = new
                {
                    batch = ToView(b),
                    rows = rows.Select(r => new { rowNumber = r.row_number, status = r.status, error = r.error, rawJson = r.raw_json }),
                    total, page = p, pageSize = ps,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/imports/{id:long}/commit", (HttpContext ctx, long id, IdempotencyExecutor idem, ImportService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<CommitImportRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion))
                    return (422, Err(ctx, "VALIDATION", "expectedVersion 必填"));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.import_batch WHERE id=@id", new { id }, tx, cancellationToken: ct));
                if (exists == 0) return (404, Err(ctx, "NOT_FOUND", "批次不存在"));
                var applied = await svc.CommitAsync(conn, tx, id, env, req.ExpectedVersion, req.Reason, ctx.User.Identity!.Name!, ct);
                return (200, JsonSerializer.Serialize(new { data = new { appliedRows = applied, status = "COMMITTED" }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));
    }

    private static string Err(HttpContext ctx, string code, string message) =>
        JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier });

    private static object ToView(BatchView b) => new
    {
        batchId = b.id.ToString(), fileKind = b.file_kind, fileName = b.file_name, fileHash = b.file_hash,
        totalRows = b.total_rows, successRows = b.success_rows, failedRows = b.failed_rows, status = b.status,
        version = RowVer.ToHex(b.ver), stagedBy = b.staged_by, stagedAt = b.staged_at,
        committedBy = b.committed_by, committedAt = b.committed_at,
    };

    public sealed record ImportRequest(string? FileKind, string? FileHash, string? Content, string? FileName);
    public sealed record CommitImportRequest(string? ExpectedVersion, string? Reason);
    public sealed record BatchView(long id, string file_kind, string file_name, string file_hash, int total_rows,
        int success_rows, int failed_rows, string status, string staged_by, DateTime staged_at,
        string? committed_by, DateTime? committed_at, byte[] ver);
    public sealed record RowView(int row_number, string status, string? error, string raw_json);
}
```

`traceflow/src/TraceFlow.Host/Program.cs` 两处修改：
1. DI 区（SettingsService 注册之后）追加：

```csharp
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Master.ImportService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.ImportStageJobHandler(connectionString));
```

2. `app.MapSettingsEndpoints(connectionString);` 之后追加：

```csharp
    app.MapImportEndpoints(connectionString, env);
```

- [ ] **Step 6: 跑测试确认通过 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 12 个 ImportServiceTests 事实）。M1ApiGateTests 的 WAF 主机此时已注册 import-stage handler，但无导入请求→无干扰。

- [ ] **Step 7: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Infrastructure/Master/ImportService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/ImportStageJobHandler.cs traceflow/src/TraceFlow.Host/Endpoints/ImportEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Master/ImportServiceTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): CSV import staging/preview/commit with classification protection and enterprise insert-only (T10)"
```

---
### Task 11: 商品/供应商列表端点 + 追溯分类端点

**Files:**
- Create: `traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（MapMasterEndpoints）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Master/ClassificationTests.cs`

**Interfaces:**
- Consumes: Task 1 `RowVer.ToHex/FromHex`；Task 3 `IdempotencyExecutor`（DI）；Task 4 `SettingVersionException`（409 VERSION_CONFLICT）；M0 `RequirePermissionAttribute`、`PermissionCatalog.TraceRead`（"trace:read"）/`MasterClassify`（"master:classify"）、`IAuditLog`/`AuditLog`；0003 DDL（product/supplier/product_mapping/supplier_mapping/platform_drug/platform_enterprise_version 列名与宽度）；0006 `upload_task.state` 终态为 `N'Succeeded'`/`N'Failed'`（其余 10 态均非终态）。
- Produces:
  - `MasterEndpoints.MapMasterEndpoints(this WebApplication app, string connectionString)`（Task 13 在同一文件追加 confirm/disable 端点）。
  - `GET /api/v1/products`（trace:read）：查询参数 `keyword`（name/spid/display_code/barcode LIKE）、`classification`、`mappingState`（7 态或 `NONE`）、`page`（默认 1）、`pageSize`（默认 50，上限 200）。响应 `data.records[]`：`productId/spid/displayCode/barcode/name/approvalNoRaw/approvalNoNorm/manufacturerRaw/manufacturerIdentity/dosageForm/strength/packageSpec/unit/traceClassification/classificationReason/updatedAt/version/mappingState`，及 `mapping`（无映射为 null）：`mappingId/state/isLocked/mappingVersion/version/platformDrugId/approvalNoNorm/manufacturerIdentity/dosageForm/strength/packageSpec`（平台侧五项，供 UI-02 并排比对）。`data.total/page/pageSize`。
  - `GET /api/v1/suppliers`（trace:read）：`keyword`（name/dwbh/credit_code LIKE）、`mappingState`、分页同上。records：`supplierId/dwbh/name/creditCode/role/version/mappingState` + `mapping`：`mappingId/state/isLocked/mappingVersion/version/refEntId/entId/platformName/creditCode/auditStatus/role`。
  - `POST /api/v1/products/{id:long}/trace-policy`（master:classify，幂等）：请求 `TracePolicyRequest(string? Classification, string? Reason, string? ExpectedVersion)`；classification ∈ REQUIRED/EXEMPT（UNKNOWN 不允许手工设置——只能由系统初始态流转），reason 必填 ≤500；404/422/409；响应 `data:{productId,traceClassification,classificationReason,version,affectedNonTerminalTasks}`。
  - 映射选取规则（列表 OUTER APPLY）：优先有效映射（AUTO_CONFIRMED/MANUAL_CONFIRMED），否则最新一条（id DESC）——CANDIDATE/CONFLICT 等非有效态也要在列表可见（UI-11 确认入口）。
- 诚实性声明（写入端点注释与 M1 门禁报告）：`affectedNonTerminalTasks` 仅**统计**经 source_snapshot→source_line.product_source_id=spid 关联的非终态 upload_task 数量；M1 尚无上传任务产生路径，恒为 0。分类变更对已生成任务的失效/重阻断接线在 M2 上传管线实现（AT-057 的 SOURCE_CHANGED 路径已在 Task 8 落地）。

- [ ] **Step 1: 写失败测试（WAF）**

`traceflow/tests/TraceFlow.IntegrationTests/Master/ClassificationTests.cs`（每测试独立库：分类/审计断言依赖干净计数。WAF 启动时 OrgBootstrap 种子 DEFAULT 组织+KSOA，**不再用 TestSeed**——两个 active 组织会违反 UX_organization_single_active）：

```csharp
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Versioning;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

[SupportedOSPlatform("windows")]
public class ClassificationTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        // 触发宿主启动（OrgBootstrap 种子 DEFAULT 组织 + KSOA 来源系统）
        using var warm = _factory.CreateClient();
        (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        await SeedUserAsync("clsadmin", "分类管理员", "Cls-Password-123", new[] { "master:classify", "master:match", "trace:read" });
        await SeedUserAsync("clsviewer", "只读", "Cls-Password-123", new[] { "trace:read" });
    }

    public async Task DisposeAsync() { _factory.Dispose(); await _fx.DisposeAsync(); }

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

    private async Task<(HttpClient client, string csrf)> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return (client, session2.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<(long OrgId, long SysId)> OrgAsync()
    {
        await using var c = await _fx.OpenAsync();
        var orgId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1");
        var sysId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'");
        return (orgId, sysId);
    }

    private async Task<long> SeedProductAsync(string spid, string name, string cls = "UNKNOWN", string? reason = null)
    {
        var (orgId, sysId) = await OrgAsync();
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification,classification_reason)
OUTPUT inserted.id VALUES(@org,@sys,@spid,@name,@cls,@reason)",
            new { org = orgId, sys = sysId, spid, name, cls, reason });
    }

    private async Task<string> ProductVersionAsync(long id)
    {
        await using var c = await _fx.OpenAsync();
        return RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product WHERE id=@id", new { id }));
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, param);
    }

    [Fact]
    public async Task Products_list_paginates_and_filters()
    {
        await SeedProductAsync("CP1", "阿莫西林胶囊", "REQUIRED", "处方药");
        await SeedProductAsync("CP2", "维生素C片", "EXEMPT", "化学药豁免");
        await SeedProductAsync("CP3", "板蓝根颗粒");
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");

        var page1 = await client.GetAsync("/api/v1/products?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var body1 = await page1.Content.ReadAsStringAsync();
        Assert.Contains("\"total\":3", body1);
        Assert.Contains("CP1", body1);
        Assert.DoesNotContain("CP3", body1); // 第 2 页才有

        var kw = await client.GetAsync("/api/v1/products?keyword=阿莫西林");
        Assert.Contains("CP1", await kw.Content.ReadAsStringAsync());
        Assert.Contains("\"total\":1", await kw.Content.ReadAsStringAsync());

        var cls = await client.GetAsync("/api/v1/products?classification=EXEMPT");
        var clsBody = await cls.Content.ReadAsStringAsync();
        Assert.Contains("CP2", clsBody);
        Assert.DoesNotContain("CP1", clsBody);

        var none = await client.GetAsync("/api/v1/products?mappingState=NONE");
        Assert.Contains("\"total\":3", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Products_list_includes_active_mapping_identity()
    {
        var pid = await SeedProductAsync("CP10", "匹配药");
        var (orgId, _) = await OrgAsync();
        await using (var c = await _fx.OpenAsync())
        {
            var drugId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec,source_kind)
OUTPUT inserted.id VALUES(N'PD-EXT-1',N'国药准字H99999999',N'某药厂',N'片剂',N'10mg',N'12片',N'MANUAL')");
            await c.ExecuteAsync(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,rule_version)
VALUES(@org,@pid,@drug,N'AUTO_CONFIRMED',N'match-1')", new { org = orgId, pid, drug = drugId });
        }
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");
        var body = await (await client.GetAsync("/api/v1/products?keyword=匹配药")).Content.ReadAsStringAsync();
        Assert.Contains("\"mappingState\":\"AUTO_CONFIRMED\"", body);
        Assert.Contains("PD-EXT-1", body);
        Assert.Contains("国药准字H99999999", body);
        Assert.Contains("某药厂", body);
        Assert.Contains("\"isLocked\":false", body);
    }

    [Fact]
    public async Task Trace_policy_updates_and_audits()
    {
        var pid = await SeedProductAsync("CP20", "分类药", "REQUIRED", "处方药");
        var ver = await ProductVersionAsync(pid);
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy",
            new { classification = "EXEMPT", reason = "化学药豁免", expectedVersion = ver });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"traceClassification\":\"EXEMPT\"", body);
        Assert.Contains("\"affectedNonTerminalTasks\":0", body); // M1 无上传任务产生路径（诚实声明）
        Assert.Equal("EXEMPT", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE id=@id", new { id = pid }));
        Assert.Equal("化学药豁免", await ScalarAsync<string>("SELECT classification_reason FROM dbo.product WHERE id=@id", new { id = pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.classify' AND object_id=@id", new { id = pid.ToString() }));
        Assert.NotEqual(ver, await ProductVersionAsync(pid));
    }

    [Fact]
    public async Task Trace_policy_validation_and_concurrency()
    {
        var pid = await SeedProductAsync("CP21", "守卫药");
        var ver = await ProductVersionAsync(pid);
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");

        async Task<HttpStatusCode> Post(object payload)
        {
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return (await client.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy", payload)).StatusCode;
        }
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "FOO", reason = "x", expectedVersion = ver }));
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "UNKNOWN", reason = "x", expectedVersion = ver }));
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "EXEMPT", reason = "", expectedVersion = ver }));
        Assert.Equal((HttpStatusCode)422, await Post(new { classification = "EXEMPT", reason = "缺版本" }));
        Assert.Equal(HttpStatusCode.Conflict, await Post(new { classification = "EXEMPT", reason = "旧版本", expectedVersion = "0000000000000000" }));
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var missing = await client.PostAsJsonAsync("/api/v1/products/999999/trace-policy",
            new { classification = "EXEMPT", reason = "不存在", expectedVersion = ver });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        // 未被任何非法请求改动
        Assert.Equal("UNKNOWN", await ScalarAsync<string>("SELECT trace_classification FROM dbo.product WHERE id=@id", new { id = pid }));
    }

    [Fact]
    public async Task Trace_policy_requires_permission()
    {
        var pid = await SeedProductAsync("CP22", "权限药");
        var ver = await ProductVersionAsync(pid);
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy",
            new { classification = "EXEMPT", reason = "匿名", expectedVersion = ver })).StatusCode);
        var (viewer, _) = await LoginAsync("clsviewer", "Cls-Password-123");
        viewer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/products/{pid}/trace-policy",
            new { classification = "EXEMPT", reason = "越权", expectedVersion = ver })).StatusCode);
        // 列表 trace:read 可访问
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/suppliers")).StatusCode);
    }

    [Fact]
    public async Task Suppliers_list_filters_and_mapping()
    {
        var (orgId, sysId) = await OrgAsync();
        await using (var c = await _fx.OpenAsync())
        {
            var sid = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code,role)
OUTPUT inserted.id VALUES(@org,@sys,N'D200',N'华东医药供应链',N'91310000MA1FL0001X',N'SUPPLIER')",
                new { org = orgId, sys = sysId });
            await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@org,@sys,N'D201',N'无关供应商')",
                new { org = orgId, sys = sysId });
            var evId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
OUTPUT inserted.id VALUES(N'REF-D200',N'ENT-D200',N'华东医药（平台名）',N'91310000MA1FL0001X',N'审核通过',N'SUPPLIER')");
            await c.ExecuteAsync(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state)
VALUES(@org,@sid,@ev,N'AUTO_CONFIRMED')", new { org = orgId, sid, ev = evId });
        }
        var (client, _) = await LoginAsync("clsadmin", "Cls-Password-123");
        var body = await (await client.GetAsync("/api/v1/suppliers?keyword=华东")).Content.ReadAsStringAsync();
        Assert.Contains("\"total\":1", body);
        Assert.Contains("D200", body);
        Assert.Contains("REF-D200", body);
        Assert.Contains("ENT-D200", body);
        Assert.Contains("华东医药（平台名）", body);
        Assert.Contains("审核通过", body);
        Assert.Contains("\"mappingState\":\"AUTO_CONFIRMED\"", body);
        var none = await (await client.GetAsync("/api/v1/suppliers?mappingState=NONE")).Content.ReadAsStringAsync();
        Assert.Contains("D201", none);
        Assert.DoesNotContain("D200", none);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~ClassificationTests"`
Expected: FAIL（/api/v1/products 等路由不存在→404，断言失败）。

- [ ] **Step 3: 实现 MasterEndpoints**

`traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs`：

```csharp
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Domain.Versioning;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Host.Endpoints;

public static class MasterEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapMasterEndpoints(this WebApplication app, string connectionString)
    {
        app.MapGet("/api/v1/products", async (HttpContext ctx, string? keyword, string? classification, string? mappingState, int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var where = new StringBuilder("WHERE p.org_id=@org");
            var par = new DynamicParameters();
            par.Add("org", orgId);
            if (!string.IsNullOrWhiteSpace(keyword))
            { where.Append(" AND (p.name LIKE @kw OR p.spid LIKE @kw OR p.display_code LIKE @kw OR p.barcode LIKE @kw)"); par.Add("kw", $"%{keyword}%"); }
            if (!string.IsNullOrWhiteSpace(classification))
            { where.Append(" AND p.trace_classification=@cls"); par.Add("cls", classification); }
            if (!string.IsNullOrWhiteSpace(mappingState))
            { where.Append(" AND ISNULL(pm.state,N'NONE')=@ms"); par.Add("ms", mappingState); }
            // 优先有效映射，否则最新一条（CANDIDATE/CONFLICT 也要可见，UI-11 确认入口）
            const string from = @"
FROM dbo.product p
OUTER APPLY (
  SELECT TOP 1 pm.id, pm.state, pm.is_locked, pm.version, pm.ver, pm.platform_drug_id
  FROM dbo.product_mapping pm WHERE pm.product_id=p.id
  ORDER BY CASE WHEN pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED') THEN 0 ELSE 1 END, pm.id DESC
) pm
LEFT JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {from} {where}", par, cancellationToken: ctx.RequestAborted));
            par.Add("lo", (p - 1) * ps + 1); par.Add("hi", p * ps);
            var rows = await conn.QueryAsync<ProductRow>(new CommandDefinition($@"
SELECT * FROM (
  SELECT p.id, p.spid, p.display_code, p.barcode, p.name, p.approval_no_raw, p.approval_no_norm,
         p.manufacturer_raw, p.manufacturer_identity, p.dosage_form, p.strength, p.package_spec, p.unit,
         p.trace_classification, p.classification_reason, p.updated_at, p.ver,
         pm.id AS mapping_id, pm.state AS mapping_state, pm.is_locked, pm.version AS mapping_version, pm.ver AS mapping_ver,
         pd.platform_drug_id AS platform_drug_key, pd.approval_no_norm AS p_approval_no_norm,
         pd.manufacturer_identity AS p_manufacturer_identity, pd.dosage_form AS p_dosage_form,
         pd.strength AS p_strength, pd.package_spec AS p_package_spec,
         ROW_NUMBER() OVER (ORDER BY p.id) AS rn
  {from} {where}) t WHERE rn BETWEEN @lo AND @hi", par, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new
            {
                data = new
                {
                    records = rows.Select(r => new
                    {
                        productId = r.id.ToString(), spid = r.spid, displayCode = r.display_code, barcode = r.barcode,
                        name = r.name, approvalNoRaw = r.approval_no_raw, approvalNoNorm = r.approval_no_norm,
                        manufacturerRaw = r.manufacturer_raw, manufacturerIdentity = r.manufacturer_identity,
                        dosageForm = r.dosage_form, strength = r.strength, packageSpec = r.package_spec, unit = r.unit,
                        traceClassification = r.trace_classification, classificationReason = r.classification_reason,
                        updatedAt = r.updated_at, version = RowVer.ToHex(r.ver),
                        mappingState = r.mapping_state ?? "NONE",
                        mapping = r.mapping_id is null ? null : new
                        {
                            mappingId = r.mapping_id.Value.ToString(), state = r.mapping_state, isLocked = r.is_locked!.Value,
                            mappingVersion = r.mapping_version!.Value, version = RowVer.ToHex(r.mapping_ver!),
                            platformDrugId = r.platform_drug_key, approvalNoNorm = r.p_approval_no_norm,
                            manufacturerIdentity = r.p_manufacturer_identity, dosageForm = r.p_dosage_form,
                            strength = r.p_strength, packageSpec = r.p_package_spec,
                        },
                    }),
                    total, page = p, pageSize = ps,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/suppliers", async (HttpContext ctx, string? keyword, string? mappingState, int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ctx.RequestAborted);
            var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.organization WHERE is_active=1", cancellationToken: ctx.RequestAborted));
            var where = new StringBuilder("WHERE s.org_id=@org");
            var par = new DynamicParameters();
            par.Add("org", orgId);
            if (!string.IsNullOrWhiteSpace(keyword))
            { where.Append(" AND (s.name LIKE @kw OR s.dwbh LIKE @kw OR s.credit_code LIKE @kw)"); par.Add("kw", $"%{keyword}%"); }
            if (!string.IsNullOrWhiteSpace(mappingState))
            { where.Append(" AND ISNULL(sm.state,N'NONE')=@ms"); par.Add("ms", mappingState); }
            const string from = @"
FROM dbo.supplier s
OUTER APPLY (
  SELECT TOP 1 sm.id, sm.state, sm.is_locked, sm.version, sm.ver, sm.enterprise_version_id
  FROM dbo.supplier_mapping sm WHERE sm.supplier_id=s.id
  ORDER BY CASE WHEN sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED') THEN 0 ELSE 1 END, sm.id DESC
) sm
LEFT JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(*) {from} {where}", par, cancellationToken: ctx.RequestAborted));
            par.Add("lo", (p - 1) * ps + 1); par.Add("hi", p * ps);
            var rows = await conn.QueryAsync<SupplierRow>(new CommandDefinition($@"
SELECT * FROM (
  SELECT s.id, s.dwbh, s.name, s.credit_code, s.role, s.ver,
         sm.id AS mapping_id, sm.state AS mapping_state, sm.is_locked, sm.version AS mapping_version, sm.ver AS mapping_ver,
         ev.ref_ent_id, ev.ent_id, ev.name AS platform_name, ev.credit_code AS platform_credit_code,
         ev.audit_status, ev.role AS platform_role,
         ROW_NUMBER() OVER (ORDER BY s.id) AS rn
  {from} {where}) t WHERE rn BETWEEN @lo AND @hi", par, cancellationToken: ctx.RequestAborted));
            return Results.Ok(new
            {
                data = new
                {
                    records = rows.Select(r => new
                    {
                        supplierId = r.id.ToString(), dwbh = r.dwbh, name = r.name, creditCode = r.credit_code,
                        role = r.role, version = RowVer.ToHex(r.ver),
                        mappingState = r.mapping_state ?? "NONE",
                        mapping = r.mapping_id is null ? null : new
                        {
                            mappingId = r.mapping_id.Value.ToString(), state = r.mapping_state, isLocked = r.is_locked!.Value,
                            mappingVersion = r.mapping_version!.Value, version = RowVer.ToHex(r.mapping_ver!),
                            refEntId = r.ref_ent_id, entId = r.ent_id, platformName = r.platform_name,
                            creditCode = r.platform_credit_code, auditStatus = r.audit_status, role = r.platform_role,
                        },
                    }),
                    total, page = p, pageSize = ps,
                },
                requestId = ctx.TraceIdentifier,
            });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        // MD-01 分类专用端点：reason 必填；UPDATE 只动分类两列。
        // 诚实声明：affectedNonTerminalTasks 仅统计（M1 无任务产生路径，恒 0）；
        // 分类变更对已生成任务的失效/重阻断接线在 M2 上传管线实现。
        app.MapPost("/api/v1/products/{id:long}/trace-policy", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<TracePolicyRequest>(ctx.Request.Body, Web, ct);
                if (req is null || req.Classification is not ("REQUIRED" or "EXEMPT")
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500
                    || string.IsNullOrEmpty(req.ExpectedVersion))
                    return (422, Err(ctx, "VALIDATION", "classification 须为 REQUIRED/EXEMPT，reason 必填且不超过 500 字，expectedVersion 必填"));
                var prod = await conn.QuerySingleOrDefaultAsync<ProductPolicyRow>(new CommandDefinition(
                    "SELECT id,org_id,source_system_id,spid,trace_classification,classification_reason,ver FROM dbo.product WITH (UPDLOCK, HOLDLOCK) WHERE id=@id",
                    new { id }, tx, cancellationToken: ct));
                if (prod is null) return (404, Err(ctx, "NOT_FOUND", "商品不存在"));
                if (!prod.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("商品版本冲突，请刷新后重试");
                var before = JsonSerializer.Serialize(new { classification = prod.trace_classification, reason = prod.classification_reason });
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.product SET trace_classification=@cls, classification_reason=@reason, updated_at=SYSUTCDATETIME() WHERE id=@id",
                    new { id, cls = req.Classification, reason = req.Reason }, tx, cancellationToken: ct));
                // 经快照→来源行 spid 关联的非终态任务数（终态=Succeeded/Failed，0006 CK_task_state）
                var affected = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(DISTINCT t.id)
FROM dbo.upload_task t
JOIN dbo.source_snapshot ss ON ss.id=t.source_snapshot_id
JOIN dbo.source_line sl ON sl.snapshot_id=ss.id
WHERE ss.org_id=@org AND ss.source_system_id=@sys AND sl.product_source_id=@spid
  AND t.state NOT IN (N'Succeeded',N'Failed')",
                    new { org = prod.org_id, sys = prod.source_system_id, spid = prod.spid }, tx, cancellationToken: ct));
                var newVer = await conn.ExecuteScalarAsync<byte[]>(new CommandDefinition(
                    "SELECT ver FROM dbo.product WHERE id=@id", new { id }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "master.classify",
                    "product", id.ToString(), req.Reason, before,
                    JsonSerializer.Serialize(new { classification = req.Classification, reason = req.Reason }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        productId = id.ToString(), traceClassification = req.Classification,
                        classificationReason = req.Reason, version = RowVer.ToHex(newVer),
                        affectedNonTerminalTasks = affected,
                    },
                    requestId = ctx.TraceIdentifier,
                }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterClassify));
    }

    private static string Err(HttpContext ctx, string code, string message) =>
        JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier });

    public sealed record TracePolicyRequest(string? Classification, string? Reason, string? ExpectedVersion);

    public sealed record ProductRow(long id, string spid, string? display_code, string? barcode, string name,
        string? approval_no_raw, string? approval_no_norm, string? manufacturer_raw, string? manufacturer_identity,
        string? dosage_form, string? strength, string? package_spec, string? unit,
        string trace_classification, string? classification_reason, DateTime updated_at, byte[] ver,
        long? mapping_id, string? mapping_state, bool? is_locked, int? mapping_version, byte[]? mapping_ver,
        string? platform_drug_key, string? p_approval_no_norm, string? p_manufacturer_identity,
        string? p_dosage_form, string? p_strength, string? p_package_spec);

    public sealed record SupplierRow(long id, string dwbh, string name, string? credit_code, string? role, byte[] ver,
        long? mapping_id, string? mapping_state, bool? is_locked, int? mapping_version, byte[]? mapping_ver,
        string? ref_ent_id, string? ent_id, string? platform_name, string? platform_credit_code,
        string? audit_status, string? platform_role);

    private sealed record ProductPolicyRow(long id, long org_id, long source_system_id, string spid,
        string trace_classification, string? classification_reason, byte[] ver);
}
```

`traceflow/src/TraceFlow.Host/Program.cs`：`app.MapImportEndpoints(connectionString, env);` 之后追加：

```csharp
    app.MapMasterEndpoints(connectionString);
```

- [ ] **Step 4: 跑测试确认通过 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 6 个 ClassificationTests 事实）。

- [ ] **Step 5: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Master/ClassificationTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): product/supplier list endpoints and trace classification with audit (T11)"
```

---
### Task 12: 自动匹配作业（商品五项/供应商 ENT 规则）+ 前缀索引重建

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Master/MatchingService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Master/PrefixIndexService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/MasterMatchJobHandler.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（注册 MasterMatchJobHandler）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Master/MatchingServiceTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Master/SupplierMatchingTests.cs`

**Interfaces:**
- Consumes:
  - Task 2 `DrugIdentity(string? ApprovalNoNorm, string? ManufacturerIdentity, string? DosageForm, string? Strength, string? PackageSpec)`；`DrugIdentityMatcher.Evaluate(local, platform) → IdentityMatch(Equal, Missing, Conflicts)`（`IsFullMatch`=Equal.Count==5、`HasConflict`、`HasOverlap`；Trim 后 Ordinal 比较；**缺字段≠通配**）；`MappingStates`（7 常量 + `Active` 列表）；`ApprovalNoNormalizer`（导入侧已规范化，匹配侧直接比 norm 列）。
  - Task 6 `SettingsService.GetAuditPassValuesAsync(ct)`（默认空数组→供应商**永不自动确认**，G-TOP 诚实）。
  - Task 7 `TestSeed.EnsureAsync`；0003 DDL（product_mapping 有 rule_version/confirmed_by/at；supplier_mapping **没有**这三列；history 表列差异同理）；0002 `source_line.trace_code_normalized`/`code_format`（0007 后 token `S8P20`）；`drug_prefix_index` **无 org 列**（单门店全局索引，重建=全删全插）。
  - M0 `IJobHandler`/`ClaimedJob`；`IAuditLog`/`AuditLog`；`AuditEntry(Actor,ActorKind,Action,ObjectType,ObjectId?,Reason?,BeforeDigest?,AfterDigest?,RequestId?,RuleVersion?,Env)`。
- Produces:
  - `sealed class MatchingService`（TraceFlow.Infrastructure.Master）ctor `(string connectionString, SettingsService settings, IAuditLog audit)`；`Task<MatchResult> RunAsync(long orgId, string env, CancellationToken ct)`；`sealed record MatchResult(int ProductsAutoConfirmed, int ProductsCandidate, int ProductsConflict, int ProductsUnresolved, int ProductsReviewRequired, int SuppliersAutoConfirmed, int SuppliersCandidate, int SuppliersConflict, int SuppliersUnresolved, int SuppliersReviewRequired, int PrefixRows, int PrefixConflicts)`。
  - `static class PrefixIndexService`：`Task<(int Rows, int Conflicts)> RebuildAsync(SqlConnection conn, SqlTransaction tx, long orgId, CancellationToken ct)`（在匹配同事务内调用）。
  - `sealed class MasterMatchJobHandler : IJobHandler`（TraceFlow.Infrastructure.Jobs）ctor `(string connectionString, SettingsService settings, IAuditLog audit)`；`Kind => "master-match"`；payload `{orgId, env, entity, trigger}`（entity 可为 null）。
  - 审计：每次运行一条 `master.match.auto`（SYSTEM/Worker，objectType organization，objectId orgId，RuleVersion "match-1"，Env=作业 env，afterDigest=计数摘要）。
- 算法规则（裁定，写入代码注释）：
  - **商品**：`is_locked` 映射整体跳过（AT-016）。有效映射（AUTO/MANUAL_CONFIRMED）只做**五项复核**：平台身份变化→REVIEW_REQUIRED+history（AT-017），不再重新寻配。非有效映射：候选=批准文号 norm 非空且 Ordinal 相等的平台药；逐项 Evaluate。
    - 恰 1 个全项一致 **且双向唯一**（该平台药无其他有效映射；本组织内五项身份完全相同的商品数==1）→ AUTO_CONFIRMED（AT-012/013/015）。
    - 全项一致候选 >1，或恰 1 但双向不唯一 → CONFLICT。
    - 无全项一致但存在字段矛盾 → CONFLICT；仅部分重叠（无矛盾）→ CANDIDATE；零重叠 → UNRESOLVED。
    - **无任何候选**（本商品批准文号为空或平台无此文号）→ 不建映射行（列表 mappingState=NONE≙未解析）；已有非有效行降级 UNRESOLVED（保留原 pair）。
    - 写入冲突（2601/2627：UX_pmap_active_platform/UX_pmap_pair 竞态）→ 降级 CONFLICT（AT-015 并发兜底）。
  - **供应商**（ENT-01/02）：候选=统一社会信用代码非空且精确相等；名称相同**仅作候选**（CANDIDATE），绝不自动。信用代码候选 ≥2 → CONFLICT（AT-019）。恰 1 且 `audit_status ∈ supplier.auditPassValues`（默认空→不自动）且双向唯一 → AUTO_CONFIRMED；审核通过但不唯一 → CONFLICT；审核未通过 → CANDIDATE。角色（role）两侧独立存储，不参与匹配条件（AT-021）。有效映射复核：信用代码不再精确相等→REVIEW_REQUIRED；企业改名产生新版本行不影响既有映射（AT-020，映射钉在 enterprise_version_id）。
  - **前缀索引**（MATCH-03/AT-018）：S8P20 来源码前 7 位 × 有效映射平台药，GROUP BY 去重；同前缀 >1 平台药 → 该前缀全部行 conflict_state=CONFLICT。前缀不是药品外键，绝不据此自动绑定。**资格规则（T12 controller ruling，T7 携带项闭环）：仅 completeness_status=COMPLETE 快照的明细行入索引**——INVALID 版本保留明细行供审计，不得污染诊断索引。
  - 单门店数据量（千级商品）采用**一次事务内全量加载+内存匹配**；写入逐行 UPSERT。M1 每商品至多一条映射行（历史在 history 表）；UX_pmap_pair 防未来多行竞态。

- [ ] **Step 1: 写失败测试（商品侧）**

`traceflow/tests/TraceFlow.IntegrationTests/Master/MatchingServiceTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

public class MatchingServiceTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    private async Task<long> SeedProductAsync(string spid, string? approval = "国药准字H11111111",
        string? manufacturer = "华北制药", string? form = "胶囊剂", string? strength = "0.25g", string? spec = "24粒")
    {
        var (org, sys) = await SeedAsync();
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec)
OUTPUT inserted.id VALUES(@org,@sys,@spid,@name,@an,@mi,@df,@st,@ps)",
            new { org, sys, spid, name = $"商品{spid}", an = approval, mi = manufacturer, df = form, st = strength, ps = spec });
    }

    private async Task<long> SeedDrugAsync(string pdid, string? approval = "国药准字H11111111",
        string? manufacturer = "华北制药", string? form = "胶囊剂", string? strength = "0.25g", string? spec = "24粒")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec,source_kind)
OUTPUT inserted.id VALUES(@pdid,@an,@mi,@df,@st,@ps,N'MANUAL')",
            new { pdid, an = approval, mi = manufacturer, df = form, st = strength, ps = spec });
    }

    private async Task<MatchingService.MatchResult> RunAsync()
    {
        var (org, _) = await SeedAsync();
        var svc = new MatchingService(_fx.ConnectionString,
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());
        return await svc.RunAsync(org, "MOCK", CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, param);
    }

    private async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, param);
    }

    [Fact]
    public async Task Full_match_bidirectional_unique_auto_confirms()
    {
        var pid = await SeedProductAsync("MP1");
        await SeedDrugAsync("PD-M1");
        var result = await RunAsync();
        Assert.Equal(1, result.ProductsAutoConfirmed);
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal("match-1", await ScalarAsync<string>("SELECT rule_version FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Contains("\"bidirectionalUnique\":true", await ScalarAsync<string>("SELECT evidence_json FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE product_id=@pid AND state=N'AUTO_CONFIRMED'", new { pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.match.auto'"));
    }

    [Fact]
    public async Task Missing_field_never_wildcards()
    {
        var pid = await SeedProductAsync("MP2", manufacturer: null, form: null, strength: null, spec: null);
        await SeedDrugAsync("PD-M2");
        var result = await RunAsync();
        Assert.Equal(0, result.ProductsAutoConfirmed);
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Field_conflict_marks_conflict()
    {
        var pid = await SeedProductAsync("MP3", strength: "0.5g");
        await SeedDrugAsync("PD-M3");
        await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Two_full_candidates_conflict()
    {
        var pid = await SeedProductAsync("MP4");
        await SeedDrugAsync("PD-M4A");
        await SeedDrugAsync("PD-M4B");
        await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')"));
    }

    [Fact]
    public async Task Bidirectional_unique_blocks_identical_twins()
    {
        var p1 = await SeedProductAsync("MP5A");
        var p2 = await SeedProductAsync("MP5B"); // 五项完全相同的双胞胎
        await SeedDrugAsync("PD-M5");
        await RunAsync();
        // 无法区分→双方 CONFLICT，平台药不被任何一方独占
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid = p1 }));
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid = p2 }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')"));
    }

    [Fact]
    public async Task Locked_mapping_untouched()
    {
        var pid = await SeedProductAsync("MP6", strength: "0.25g");
        var drugId = await SeedDrugAsync("PD-M6", strength: "9.99g"); // 身份已不同
        var (org, _) = await SeedAsync();
        await ExecAsync(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,is_locked,version)
VALUES(@org,@pid,@drug,N'MANUAL_CONFIRMED',1,7)", new { org, pid, drug = drugId });
        await RunAsync();
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(7, await ScalarAsync<int>("SELECT version FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Active_mapping_revalidation_detects_platform_change()
    {
        var pid = await SeedProductAsync("MP7");
        var drugId = await SeedDrugAsync("PD-M7");
        await RunAsync(); // AUTO_CONFIRMED
        await ExecAsync("UPDATE dbo.platform_drug SET strength=N'9.99g' WHERE id=@id", new { id = drugId });
        var result = await RunAsync();
        Assert.Equal(1, result.ProductsReviewRequired);
        Assert.Equal("REVIEW_REQUIRED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE product_id=@pid AND state=N'REVIEW_REQUIRED'", new { pid }));
    }

    [Fact]
    public async Task No_candidates_creates_no_row()
    {
        var pid = await SeedProductAsync("MP8", approval: "国药准字H00000000");
        await SeedDrugAsync("PD-M8");
        var result = await RunAsync();
        Assert.Equal(1, result.ProductsUnresolved);
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping WHERE product_id=@pid", new { pid }));
    }

    [Fact]
    public async Task Prefix_index_rebuild_and_conflict()
    {
        await SeedProductAsync("MP9A");
        await SeedProductAsync("MP9B", approval: "国药准字H22222222");
        var d1 = await SeedDrugAsync("PD-M9A");
        var d2 = await SeedDrugAsync("PD-M9B", approval: "国药准字H22222222");
        var (org, sys) = await SeedAsync();
        await RunAsync(); // 两个 AUTO_CONFIRMED
        // 两个 S8P20 来源码共享前 7 位 8111111，分属两个已映射商品
        await ExecAsync(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash,completeness_status)
OUTPUT inserted.id VALUES(@org,@sys,N'PM-DOC-1',N'sale',1,REPLICATE(N'a',64),N'COMPLETE')", new { org, sys });
        var snapId = await ScalarAsync<long>("SELECT id FROM dbo.source_snapshot WHERE source_document_key=N'PM-DOC-1'");
        await ExecAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind,trace_code_normalized,code_format)
VALUES(@snap,N'1',N'MP9A',1,REPLICATE(N'b',64),N'SALE',N'81111112222333344445',N'S8P20');
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind,trace_code_normalized,code_format)
VALUES(@snap,N'2',N'MP9B',1,REPLICATE(N'c',64),N'SALE',N'81111119999888877776',N'S8P20');", new { snap = snapId });
        var result = await RunAsync();
        Assert.Equal(2, result.PrefixRows);
        Assert.Equal(1, result.PrefixConflicts); // 一个冲突前缀（两行）
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.drug_prefix_index WHERE prefix=N'8111111' AND conflict_state=N'CONFLICT'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.drug_prefix_index WHERE prefix=N'8111111' AND platform_drug_id=@d", new { d = d1 }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.drug_prefix_index WHERE prefix=N'8111111' AND platform_drug_id=@d", new { d = d2 }));
    }
}
```

- [ ] **Step 2: 写失败测试（供应商侧）**

`traceflow/tests/TraceFlow.IntegrationTests/Master/SupplierMatchingTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

public class SupplierMatchingTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    public async Task InitializeAsync() => await _fx.InitializeAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private async Task<(long OrgId, long SysId)> SeedAsync()
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await TestSeed.EnsureAsync(c);
    }

    private async Task<long> SeedSupplierAsync(string dwbh, string name = "华东医药", string? credit = "91310000MA1FL0001X", string? role = "SUPPLIER")
    {
        var (org, sys) = await SeedAsync();
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code,role)
OUTPUT inserted.id VALUES(@org,@sys,@dwbh,@name,@cc,@role)",
            new { org, sys, dwbh, name, cc = credit, role });
    }

    private async Task<long> SeedEntAsync(string refId, string entId, string name = "华东医药（平台）",
        string? credit = "91310000MA1FL0001X", string? audit = "审核通过", string? role = "SUPPLIER")
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
OUTPUT inserted.id VALUES(@ref,@ent,@name,@cc,@audit,@role)",
            new { @ref = refId, ent = entId, name, cc = credit, audit, role });
    }

    private async Task SetAuditPassAsync(string json)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var view = (await svc.ListAsync(CancellationToken.None)).Single(v => v.Key == "supplier.auditPassValues");
        await svc.UpdateAsync(c, tx, "supplier.auditPassValues", json, null, view.Id == 0 ? "0" : view.Version, "审核白名单", "tester", CancellationToken.None);
        await tx.CommitAsync();
    }

    private async Task<MatchingService.MatchResult> RunAsync()
    {
        var (org, _) = await SeedAsync();
        var svc = new MatchingService(_fx.ConnectionString,
            new SettingsService(_fx.ConnectionString, new DpapiProtector()), new AuditLog());
        return await svc.RunAsync(org, "MOCK", CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = new SqlConnection(_fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, param);
    }

    [Fact]
    public async Task Credit_exact_audit_pass_unique_auto_confirms()
    {
        var sid = await SeedSupplierAsync("SD1");
        await SeedEntAsync("REF-SD1", "ENT-SD1");
        await SetAuditPassAsync("[\"审核通过\"]");
        var result = await RunAsync();
        Assert.Equal(1, result.SuppliersAutoConfirmed);
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Default_empty_pass_set_never_auto_confirms()
    {
        var sid = await SeedSupplierAsync("SD2");
        await SeedEntAsync("REF-SD2", "ENT-SD2");
        var result = await RunAsync(); // 未配置审核通过值（G-TOP：真实平台枚举未取证前不自动）
        Assert.Equal(0, result.SuppliersAutoConfirmed);
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Multiple_credit_matches_conflict()
    {
        var sid = await SeedSupplierAsync("SD3");
        await SeedEntAsync("REF-SD3A", "ENT-SD3A");
        await SeedEntAsync("REF-SD3B", "ENT-SD3B");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync();
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Name_only_is_candidate_never_auto()
    {
        var sid = await SeedSupplierAsync("SD4", name: "同名企业", credit: null);
        await SeedEntAsync("REF-SD4", "ENT-SD4", name: "同名企业", credit: "91999999MA9999999X");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync();
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Rename_new_version_mapping_stays()
    {
        var sid = await SeedSupplierAsync("SD5");
        var ev1 = await SeedEntAsync("REF-SD5", "ENT-SD5-V1", name: "旧名");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync(); // AUTO_CONFIRMED → ev1
        await SeedEntAsync("REF-SD5", "ENT-SD5-V2", name: "新名"); // 改名→新版本行（AT-020）
        await RunAsync();
        Assert.Equal(ev1, await ScalarAsync<long>("SELECT enterprise_version_id FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }

    [Fact]
    public async Task Roles_are_independent()
    {
        var sid = await SeedSupplierAsync("SD6", role: "SHIPPER");
        await SeedEntAsync("REF-SD6", "ENT-SD6", role: "SUPPLIER");
        await SetAuditPassAsync("[\"审核通过\"]");
        await RunAsync();
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
        Assert.Equal("SHIPPER", await ScalarAsync<string>("SELECT role FROM dbo.supplier WHERE id=@sid", new { sid }));
        Assert.Equal("SUPPLIER", await ScalarAsync<string>("SELECT role FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF-SD6'"));
    }

    [Fact]
    public async Task Locked_supplier_mapping_untouched()
    {
        var sid = await SeedSupplierAsync("SD7", credit: "91111111MA1111111X");
        var ev = await SeedEntAsync("REF-SD7", "ENT-SD7", credit: "91222222MA2222222X");
        var (org, _) = await SeedAsync();
        await using (var c = new SqlConnection(_fx.ConnectionString))
        {
            await c.OpenAsync();
            await c.ExecuteAsync(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,is_locked,version)
VALUES(@org,@sid,@ev,N'MANUAL_CONFIRMED',1,5)", new { org, sid, ev });
        }
        await RunAsync();
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
        Assert.Equal(5, await ScalarAsync<int>("SELECT version FROM dbo.supplier_mapping WHERE supplier_id=@sid", new { sid }));
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~MatchingServiceTests|FullyQualifiedName~SupplierMatchingTests"`
Expected: FAIL 编译错误（MatchingService/PrefixIndexService 不存在）。

- [ ] **Step 4: 实现 MatchingService**

`traceflow/src/TraceFlow.Infrastructure/Master/MatchingService.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Matching;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Master;

/// <summary>
/// MATCH-01..05 / ENT-01/02 自动匹配（单门店千级数据：一次事务全量加载+内存匹配，逐行 UPSERT）。
/// 缺字段≠通配；双向唯一才自动；is_locked 跳过；有效映射只做身份复核；写入竞态降级 CONFLICT。
/// </summary>
public sealed class MatchingService
{
    private readonly string _cs;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public MatchingService(string connectionString, SettingsService settings, IAuditLog audit)
    { _cs = connectionString; _settings = settings; _audit = audit; }

    public sealed record MatchResult(int ProductsAutoConfirmed, int ProductsCandidate, int ProductsConflict,
        int ProductsUnresolved, int ProductsReviewRequired, int SuppliersAutoConfirmed, int SuppliersCandidate,
        int SuppliersConflict, int SuppliersUnresolved, int SuppliersReviewRequired, int PrefixRows, int PrefixConflicts);

    public async Task<MatchResult> RunAsync(long orgId, string env, CancellationToken ct)
    {
        var passValues = await _settings.GetAuditPassValuesAsync(ct); // 独立连接，事务前读取
        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var p = await MatchProductsAsync(conn, tx, orgId, ct);
        var s = await MatchSuppliersAsync(conn, tx, orgId, passValues, ct);
        var (prefixRows, prefixConflicts) = await PrefixIndexService.RebuildAsync(conn, tx, orgId, ct);
        await _audit.AppendAsync(conn, tx, new AuditEntry("SYSTEM", "Worker", "master.match.auto", "organization",
            orgId.ToString(), null, null,
            // T12 评审前修复（controller ruling）：值元组被 STJ 序列化为 {}（字段非属性）→具名匿名对象投影保留计数
            JsonSerializer.Serialize(new
            {
                products = new { auto = p.auto, candidate = p.candidate, conflict = p.conflict, unresolved = p.unresolved, review = p.review },
                suppliers = new { auto = s.auto, candidate = s.candidate, conflict = s.conflict, unresolved = s.unresolved, review = s.review },
                prefixRows, prefixConflicts
            }),
            null, "match-1", env));
        await tx.CommitAsync(ct);
        return new MatchResult(p.auto, p.candidate, p.conflict, p.unresolved, p.review,
            s.auto, s.candidate, s.conflict, s.unresolved, s.review, prefixRows, prefixConflicts);
    }

    private static DrugIdentity Local(ProductRec r) => new(r.approval_no_norm, r.manufacturer_identity, r.dosage_form, r.strength, r.package_spec);
    private static DrugIdentity Platform(DrugRec r) => new(r.approval_no_norm, r.manufacturer_identity, r.dosage_form, r.strength, r.package_spec);
    private static string IdentityKey(DrugIdentity d) => string.Join("|",
        d.ApprovalNoNorm?.Trim() ?? "", d.ManufacturerIdentity?.Trim() ?? "", d.DosageForm?.Trim() ?? "",
        d.Strength?.Trim() ?? "", d.PackageSpec?.Trim() ?? "");

    private async Task<(int auto, int candidate, int conflict, int unresolved, int review)> MatchProductsAsync(
        SqlConnection conn, SqlTransaction tx, long orgId, CancellationToken ct)
    {
        var products = (await conn.QueryAsync<ProductRec>(new CommandDefinition(
            "SELECT id,spid,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec FROM dbo.product WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var drugs = (await conn.QueryAsync<DrugRec>(new CommandDefinition(
            "SELECT id,platform_drug_id,approval_no_norm,manufacturer_identity,dosage_form,strength,package_spec FROM dbo.platform_drug",
            null, tx, cancellationToken: ct))).ToList();
        var maps = (await conn.QueryAsync<PMapRec>(new CommandDefinition(
            "SELECT id,product_id,platform_drug_id,state,is_locked,version FROM dbo.product_mapping WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var latest = maps.GroupBy(m => m.product_id).ToDictionary(g => g.Key,
            g => g.OrderBy(m => MappingStates.Active.Contains(m.state) ? 0 : 1).ThenByDescending(m => m.id).First());
        var localKeyCounts = products.GroupBy(x => IdentityKey(Local(x))).ToDictionary(g => g.Key, g => g.Count());
        int auto = 0, candidate = 0, conflict = 0, unresolved = 0, review = 0;

        foreach (var prod in products)
        {
            var local = Local(prod);
            latest.TryGetValue(prod.id, out var map);
            if (map is not null && map.is_locked) continue; // AT-016：人工锁定不可被自动改写
            if (map is not null && MappingStates.Active.Contains(map.state))
            {
                // AT-017：有效映射只复核五项身份；平台侧变化→REVIEW_REQUIRED
                var drug = drugs.FirstOrDefault(d => d.id == map.platform_drug_id);
                var m = drug is null ? null : DrugIdentityMatcher.Evaluate(local, Platform(drug));
                if (m is null || !m.IsFullMatch)
                {
                    var ev = JsonSerializer.Serialize(new { reason = "platform-identity-changed", equal = m?.Equal, missing = m?.Missing, conflicts = m?.Conflicts });
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.product_mapping SET state=N'REVIEW_REQUIRED', evidence_json=@ev, version=version+1 WHERE id=@id",
                        new { id = map.id, ev }, tx, cancellationToken: ct));
                    await WriteProductHistoryAsync(conn, tx, map.id, prod.id, map.platform_drug_id, MappingStates.ReviewRequired, ev, ct);
                    review++;
                }
                continue;
            }

            var candidates = prod.approval_no_norm is null
                ? new List<(DrugRec drug, IdentityMatch match)>()
                : drugs.Where(d => d.approval_no_norm is not null
                        && string.Equals(d.approval_no_norm, prod.approval_no_norm, StringComparison.Ordinal))
                    .Select(d => (d, DrugIdentityMatcher.Evaluate(local, Platform(d)))).ToList();
            var full = candidates.Where(x => x.match.IsFullMatch).ToList();
            string state; long? target = null; var bidirectionalUnique = false;
            if (full.Count == 1)
            {
                var chosen = full[0].d;
                var drugTaken = maps.Any(m => MappingStates.Active.Contains(m.state) && m.platform_drug_id == chosen.id && m.product_id != prod.id);
                bidirectionalUnique = !drugTaken && localKeyCounts[IdentityKey(local)] == 1;
                target = chosen.id;
                state = bidirectionalUnique ? MappingStates.AutoConfirmed : MappingStates.Conflict;
            }
            else if (full.Count > 1) { state = MappingStates.Conflict; target = full[0].d.id; }
            else if (candidates.Any(x => x.match.HasConflict)) { state = MappingStates.Conflict; target = candidates.First(x => x.match.HasConflict).d.id; }
            else if (candidates.Any(x => x.match.HasOverlap)) { state = MappingStates.Candidate; target = candidates.First(x => x.match.HasOverlap).d.id; }
            else if (candidates.Count > 0) { state = MappingStates.Unresolved; target = candidates[0].d.id; }
            else
            {
                // 无候选：不建行；既有非有效行降级 UNRESOLVED（保留原 pair）
                if (map is not null && map.state != MappingStates.Unresolved)
                {
                    var ev0 = JsonSerializer.Serialize(new { reason = "no-candidates" });
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.product_mapping SET state=N'UNRESOLVED', evidence_json=@ev, version=version+1 WHERE id=@id",
                        new { id = map.id, ev = ev0 }, tx, cancellationToken: ct));
                    await WriteProductHistoryAsync(conn, tx, map.id, prod.id, map.platform_drug_id, MappingStates.Unresolved, ev0, ct);
                }
                unresolved++;
                continue;
            }

            var best = full.Count > 0 ? full[0].match
                : candidates.FirstOrDefault(x => x.match.HasConflict).match
                ?? candidates.FirstOrDefault(x => x.match.HasOverlap).match
                ?? candidates[0].match;
            var evidence = JsonSerializer.Serialize(new
            {
                equal = best.Equal, missing = best.Missing, conflicts = best.Conflicts, bidirectionalUnique,
                candidates = candidates.Select(x => new { platformDrugId = x.d.platform_drug_id, equal = x.match.Equal, missing = x.match.Missing, conflicts = x.match.Conflicts }),
            });
            var (mapId, finalState) = await UpsertProductMappingAsync(conn, tx, orgId, prod.id, target!.Value, state, evidence, map, ct);
            if (map is null || map.state != finalState)
                await WriteProductHistoryAsync(conn, tx, mapId, prod.id, target.Value, finalState, evidence, ct);
            switch (finalState)
            {
                case MappingStates.AutoConfirmed: auto++; break;
                case MappingStates.Candidate: candidate++; break;
                case MappingStates.Conflict: conflict++; break;
                default: unresolved++; break;
            }
        }
        return (auto, candidate, conflict, unresolved, review);
    }

    private static async Task<(long Id, string State)> UpsertProductMappingAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, long productId, long drugId, string state, string evidence, PMapRec? existing, CancellationToken ct)
    {
        try
        {
            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,rule_version,evidence_json)
OUTPUT inserted.id VALUES(@org,@pid,@drug,@state,N'match-1',@ev)",
                    new { org = orgId, pid = productId, drug = drugId, state, ev = evidence }, tx, cancellationToken: ct));
                return (id, state);
            }
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product_mapping SET platform_drug_id=@drug, state=@state, rule_version=N'match-1', evidence_json=@ev, version=version+1
WHERE id=@id",
                new { id = existing.id, drug = drugId, state, ev = evidence }, tx, cancellationToken: ct));
            return (existing.id, state);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // AT-015 并发兜底：有效唯一/对唯一竞态→降级 CONFLICT（语句级错误，事务仍可用）
            var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.product_mapping WHERE product_id=@pid ORDER BY id DESC",
                new { pid = productId }, tx, cancellationToken: ct));
            if (existingId is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.product_mapping SET state=N'CONFLICT', evidence_json=@ev, version=version+1 WHERE id=@id",
                    new { id = existingId.Value, ev = evidence }, tx, cancellationToken: ct));
                return (existingId.Value, MappingStates.Conflict);
            }
            var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state,rule_version,evidence_json)
OUTPUT inserted.id VALUES(@org,@pid,@drug,N'CONFLICT',N'match-1',@ev)",
                new { org = orgId, pid = productId, drug = drugId, ev = evidence }, tx, cancellationToken: ct));
            return (newId, MappingStates.Conflict);
        }
    }

    private static async Task WriteProductHistoryAsync(SqlConnection conn, SqlTransaction tx, long mappingId,
        long productId, long drugId, string state, string evidence, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product_mapping_history(mapping_id,product_id,platform_drug_id,state,rule_version,evidence_json,changed_by,reason)
VALUES(@mid,@pid,@drug,@state,N'match-1',@ev,N'SYSTEM',N'auto-match')",
            new { mid = mappingId, pid = productId, drug = drugId, state, ev = evidence }, tx, cancellationToken: ct));

    private async Task<(int auto, int candidate, int conflict, int unresolved, int review)> MatchSuppliersAsync(
        SqlConnection conn, SqlTransaction tx, long orgId, IReadOnlyList<string> passValues, CancellationToken ct)
    {
        var suppliers = (await conn.QueryAsync<SupplierRec>(new CommandDefinition(
            "SELECT id,dwbh,name,credit_code,role FROM dbo.supplier WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var ents = (await conn.QueryAsync<EntRec>(new CommandDefinition(
            "SELECT id,ref_ent_id,ent_id,name,credit_code,audit_status,role FROM dbo.platform_enterprise_version",
            null, tx, cancellationToken: ct))).ToList();
        var maps = (await conn.QueryAsync<SMapRec>(new CommandDefinition(
            "SELECT id,supplier_id,enterprise_version_id,state,is_locked,version FROM dbo.supplier_mapping WHERE org_id=@org",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        var latest = maps.GroupBy(m => m.supplier_id).ToDictionary(g => g.Key,
            g => g.OrderBy(m => MappingStates.Active.Contains(m.state) ? 0 : 1).ThenByDescending(m => m.id).First());
        var creditCounts = suppliers.Where(s => s.credit_code is not null)
            .GroupBy(s => s.credit_code!.Trim()).ToDictionary(g => g.Key, g => g.Count());
        int auto = 0, candidate = 0, conflict = 0, unresolved = 0, review = 0;

        foreach (var sup in suppliers)
        {
            latest.TryGetValue(sup.id, out var map);
            if (map is not null && map.is_locked) continue; // AT-016
            if (map is not null && MappingStates.Active.Contains(map.state))
            {
                // ENT-01 复核：信用代码不再精确相等→REVIEW_REQUIRED；改名新版本行不影响（AT-020 映射钉在版本 id）
                var ev = ents.FirstOrDefault(e => e.id == map.enterprise_version_id);
                var same = ev is not null && string.Equals(sup.credit_code?.Trim(), ev.credit_code?.Trim(), StringComparison.Ordinal);
                if (!same)
                {
                    var evJson = JsonSerializer.Serialize(new { reason = "credit-code-changed", local = sup.credit_code, platform = ev?.credit_code });
                    await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.supplier_mapping SET state=N'REVIEW_REQUIRED', evidence_json=@ev, version=version+1 WHERE id=@id",
                        new { id = map.id, ev = evJson }, tx, cancellationToken: ct));
                    await WriteSupplierHistoryAsync(conn, tx, map.id, sup.id, map.enterprise_version_id, MappingStates.ReviewRequired, evJson, ct);
                    review++;
                }
                continue;
            }

            var creditCandidates = sup.credit_code is null
                ? new List<EntRec>()
                : ents.Where(e => e.credit_code is not null && string.Equals(e.credit_code.Trim(), sup.credit_code.Trim(), StringComparison.Ordinal)).ToList();
            string state; long? target = null;
            if (creditCandidates.Count >= 2) { state = MappingStates.Conflict; target = creditCandidates[0].id; } // AT-019
            else if (creditCandidates.Count == 1)
            {
                var ev = creditCandidates[0];
                var auditPassed = ev.audit_status is not null && passValues.Contains(ev.audit_status);
                var evTaken = maps.Any(m => MappingStates.Active.Contains(m.state) && m.enterprise_version_id == ev.id && m.supplier_id != sup.id);
                var unique = !evTaken && creditCounts[sup.credit_code!.Trim()] == 1;
                target = ev.id;
                if (!auditPassed) state = MappingStates.Candidate; // 默认空白名单→永不自动（G-TOP）
                else state = unique ? MappingStates.AutoConfirmed : MappingStates.Conflict;
            }
            else
            {
                // 名称仅候选（ENT-01），绝不自动
                var nameCandidates = ents.Where(e => e.name.Trim().Length > 0
                    && string.Equals(e.name.Trim(), sup.name.Trim(), StringComparison.Ordinal)).ToList();
                if (nameCandidates.Count > 0) { state = MappingStates.Candidate; target = nameCandidates[0].id; }
                else
                {
                    if (map is not null && map.state != MappingStates.Unresolved)
                    {
                        var ev0 = JsonSerializer.Serialize(new { reason = "no-candidates" });
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE dbo.supplier_mapping SET state=N'UNRESOLVED', evidence_json=@ev, version=version+1 WHERE id=@id",
                            new { id = map.id, ev = ev0 }, tx, cancellationToken: ct));
                        await WriteSupplierHistoryAsync(conn, tx, map.id, sup.id, map.enterprise_version_id, MappingStates.Unresolved, ev0, ct);
                    }
                    unresolved++;
                    continue;
                }
            }

            var evidence = JsonSerializer.Serialize(new
            {
                creditCode = sup.credit_code, targetEntId = ents.First(e => e.id == target!.Value).ent_id,
                auditStatus = ents.First(e => e.id == target.Value).audit_status,
                nameOnly = creditCandidates.Count == 0,
                candidates = creditCandidates.Select(e => new { entId = e.ent_id, refEntId = e.ref_ent_id, auditStatus = e.audit_status }),
            });
            var (mapId, finalState) = await UpsertSupplierMappingAsync(conn, tx, orgId, sup.id, target!.Value, state, evidence, map, ct);
            if (map is null || map.state != finalState)
                await WriteSupplierHistoryAsync(conn, tx, mapId, sup.id, target.Value, finalState, evidence, ct);
            switch (finalState)
            {
                case MappingStates.AutoConfirmed: auto++; break;
                case MappingStates.Candidate: candidate++; break;
                case MappingStates.Conflict: conflict++; break;
                default: unresolved++; break;
            }
        }
        return (auto, candidate, conflict, unresolved, review);
    }

    private static async Task<(long Id, string State)> UpsertSupplierMappingAsync(SqlConnection conn, SqlTransaction tx,
        long orgId, long supplierId, long entVersionId, string state, string evidence, SMapRec? existing, CancellationToken ct)
    {
        try
        {
            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,evidence_json)
OUTPUT inserted.id VALUES(@org,@sid,@ev,@state,@evid)",
                    new { org = orgId, sid = supplierId, ev = entVersionId, state, evid = evidence }, tx, cancellationToken: ct));
                return (id, state);
            }
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier_mapping SET enterprise_version_id=@ev, state=@state, evidence_json=@evid, version=version+1
WHERE id=@id",
                new { id = existing.id, ev = entVersionId, state, evid = evidence }, tx, cancellationToken: ct));
            return (existing.id, state);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT TOP 1 id FROM dbo.supplier_mapping WHERE supplier_id=@sid ORDER BY id DESC",
                new { sid = supplierId }, tx, cancellationToken: ct));
            if (existingId is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.supplier_mapping SET state=N'CONFLICT', evidence_json=@ev, version=version+1 WHERE id=@id",
                    new { id = existingId.Value, ev = evidence }, tx, cancellationToken: ct));
                return (existingId.Value, MappingStates.Conflict);
            }
            var newId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state,evidence_json)
OUTPUT inserted.id VALUES(@org,@sid,@ev,N'CONFLICT',@evid)",
                new { org = orgId, sid = supplierId, ev = entVersionId, evid = evidence }, tx, cancellationToken: ct));
            return (newId, MappingStates.Conflict);
        }
    }

    private static async Task WriteSupplierHistoryAsync(SqlConnection conn, SqlTransaction tx, long mappingId,
        long supplierId, long entVersionId, string state, string evidence, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping_history(mapping_id,supplier_id,enterprise_version_id,state,evidence_json,changed_by,reason)
VALUES(@mid,@sid,@ev,@state,@evid,N'SYSTEM',N'auto-match')",
            new { mid = mappingId, sid = supplierId, ev = entVersionId, state, evid = evidence }, tx, cancellationToken: ct));

    private sealed record ProductRec(long id, string spid, string? approval_no_norm, string? manufacturer_identity,
        string? dosage_form, string? strength, string? package_spec);
    private sealed record DrugRec(long id, string platform_drug_id, string? approval_no_norm, string? manufacturer_identity,
        string? dosage_form, string? strength, string? package_spec);
    private sealed record PMapRec(long id, long product_id, long platform_drug_id, string state, bool is_locked, int version);
    private sealed record SupplierRec(long id, string dwbh, string name, string? credit_code, string? role);
    private sealed record EntRec(long id, string ref_ent_id, string ent_id, string name, string? credit_code, string? audit_status, string? role);
    private sealed record SMapRec(long id, long supplier_id, long enterprise_version_id, string state, bool is_locked, int version);
}
```

注意：`candidates.FirstOrDefault(x => x.match.HasConflict).match` 对值元组列表返回 default 元组（match 为 null）时用 `??` 兜底——值元组的 `.match` 是引用类型 IdentityMatch，default 元组该字段为 null，`??` 链成立。`best` 的 `??` 链最后一项 `candidates[0].match` 非 null（此分支 candidates.Count>0）。

- [ ] **Step 5: 实现 PrefixIndexService + MasterMatchJobHandler + Program 接线**

`traceflow/src/TraceFlow.Infrastructure/Master/PrefixIndexService.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Master;

/// <summary>
/// MATCH-03/AT-018：S8P20 来源码前 7 位 × 有效映射平台药。前缀不是药品外键——
/// 同前缀多药仅标 CONFLICT 供人工排查，绝不据此自动绑定。单门店全局索引：全删全插。
/// </summary>
public static class PrefixIndexService
{
    public static async Task<(int Rows, int Conflicts)> RebuildAsync(SqlConnection conn, SqlTransaction tx, long orgId, CancellationToken ct)
    {
        var pairs = (await conn.QueryAsync<PrefixPair>(new CommandDefinition(@"
SELECT LEFT(sl.trace_code_normalized,7) AS prefix, pm.platform_drug_id
FROM dbo.source_line sl
JOIN dbo.source_snapshot ss ON ss.id=sl.snapshot_id
JOIN dbo.product p ON p.org_id=ss.org_id AND p.source_system_id=ss.source_system_id AND p.spid=sl.product_source_id
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
WHERE ss.org_id=@org AND sl.code_format=N'S8P20' AND sl.trace_code_normalized IS NOT NULL
  AND ss.completeness_status=N'COMPLETE' -- T12 评审前修复（T7 携带项闭环）：INVALID 版本保留明细行，前缀索引仅取 COMPLETE 稳定快照
GROUP BY LEFT(sl.trace_code_normalized,7), pm.platform_drug_id",
            new { org = orgId }, tx, cancellationToken: ct))).ToList();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM dbo.drug_prefix_index", null, tx, cancellationToken: ct));
        var conflictPrefixes = pairs.GroupBy(x => x.prefix).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in pairs)
            await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.drug_prefix_index(prefix,platform_drug_id,evidence_json,conflict_state)
VALUES(@prefix,@drug,@ev,@cs)",
                new { prefix = pair.prefix, drug = pair.platform_drug_id,
                      ev = JsonSerializer.Serialize(new { source = "source_line", orgId }),
                      cs = conflictPrefixes.Contains(pair.prefix) ? "CONFLICT" : "OK" }, tx, cancellationToken: ct));
        return (pairs.Count, conflictPrefixes.Count);
    }

    private sealed record PrefixPair(string prefix, long platform_drug_id);
}
```

`traceflow/src/TraceFlow.Infrastructure/Jobs/MasterMatchJobHandler.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Master;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Jobs;

/// <summary>master-match：周期/每日/导入提交/人工重匹配共用（payload.entity 可为 null）。</summary>
public sealed class MasterMatchJobHandler : IJobHandler
{
    private readonly string _cs;
    private readonly SettingsService _settings;
    private readonly IAuditLog _audit;

    public MasterMatchJobHandler(string connectionString, SettingsService settings, IAuditLog audit)
    { _cs = connectionString; _settings = settings; _audit = audit; }

    public string Kind => "master-match";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        var orgId = payload.RootElement.GetProperty("orgId").GetInt64();
        var env = payload.RootElement.GetProperty("env").GetString()!;
        await new MatchingService(_cs, _settings, _audit).RunAsync(orgId, env, ct);
    }
}
```

`traceflow/src/TraceFlow.Host/Program.cs`：ImportStageJobHandler 注册之后追加：

```csharp
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.MasterMatchJobHandler(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IAuditLog>()));
```

- [ ] **Step 6: 跑测试确认通过 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 9 个 MatchingServiceTests + 7 个 SupplierMatchingTests 事实）。M1ApiGateTests 的 WAF 主机现在会执行 master-match 作业（空主数据→空跑，无副作用）。

- [ ] **Step 7: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Infrastructure/Master/MatchingService.cs traceflow/src/TraceFlow.Infrastructure/Master/PrefixIndexService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/MasterMatchJobHandler.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Master/MatchingServiceTests.cs traceflow/tests/TraceFlow.IntegrationTests/Master/SupplierMatchingTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): auto matching with five-item identity, ENT rules, prefix conflict index (T12)"
```

---
### Task 13: 人工确认/解除映射端点（双向唯一/强冲突/在途任务守卫）

> **T13 评审前修复（controller ruling，AT-015/MD-01 并发闭环）**：T12 的 MatchingService 全部 8 处 UPDATE 站点（upsert ×2、2601/2627 fallback ×2、重验 ×2、无候选降级 ×2）已加守卫 `is_locked=0 AND state/version=加载值`，0 行→跳过写入+历史+计数（下轮重读）；fallback 先 UPDLOCK+HOLDLOCK 重选当前行，locked/active 则跳过 CONFLICT 降级。T12 代码块以仓库 b9bac3e 为准（本计划 T12 块未含守卫）。确定性测试用影子 org 座位锁定行使 INSERT 真实触发 2601（fallback 重选无 org 过滤——M2 前建议补 org_id 谓词）；UPDATE 守卫真并发无测试缝，逻辑轨迹经评审独立确认。详见 task-13-report.md Fix Round 1。

**Files:**
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs`（追加 4 个端点）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Master/MappingConfirmTests.cs`
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Api/M1ApiGateTests.cs`（追加映射端点门禁事实）

**Interfaces:**
- Consumes: Task 11 `MasterEndpoints`（同文件追加）、`Err`/`Web` 私有助手；Task 1 `RowVer`；Task 3 `IdempotencyExecutor`；Task 4 `SettingVersionException`（409）；Task 2 `MappingStates`；M0 `PermissionCatalog.MasterMatch`（"master:match"）、`AuditLog`/`AuditEntry`；0003 `product_mapping`（有 confirmed_by/at）/`supplier_mapping`（无）/history 表；0006 `upload_task` 终态 Succeeded/Failed。
- Produces（均 master:match + 幂等）：
  - `POST /api/v1/product-mappings/{id:long}/confirm`、`POST /api/v1/supplier-mappings/{id:long}/confirm`：请求 `ConfirmMappingRequest(string? TargetId, string? ExpectedVersion, string? Reason, string[]? EvidenceIds)`。
  - `POST /api/v1/product-mappings/{id:long}/disable`、`POST /api/v1/supplier-mappings/{id:long}/disable`：请求 `DisableMappingRequest(string? ExpectedVersion, string? Reason)`。
  - 响应 `data:{mappingId,state,version,mappingVersion}`（version=新 rowversion hex，mappingVersion=新 int 版本）。
- 规则（裁定，写入注释）：
  - **确认**：UPDLOCK 读映射→404；ver 不符→409 VERSION_CONFLICT；`state==CONFLICT`→409 `STRONG_CONFLICT`（强冲突须先解决，不能直接确认，MATCH-05）；`targetId` 必须等于映射既有平台标识（商品=platform_drug.platform_drug_id 外部串；供应商=platform_enterprise_version.ent_id），不等→422（**确认不允许改绑**，改绑走重新匹配/导入）；reason 必填 ≤500。置 MANUAL_CONFIRMED + is_locked=1 + version+1（商品另写 confirmed_by/at）+ history + audit `master.mapping.confirm`。有效唯一竞态 2601/2627→409 `MAPPING_ACTIVE_CONFLICT`。
  - **解除**：UPDLOCK→404；ver→409；**非终态上传任务守卫**：商品经 snapshot→source_line.product_source_id=spid 关联、供应商经 org 下采购类任务（PURCHASE_IN_102/PURCHASE_RETURN_202）保守阻断（M1 无供应商级任务关联结构，M2 载荷契约落地后细化到 dwbh）；存在非终态任务→409 `TASKS_IN_FLIGHT`。置 DISABLED + is_locked=0 + version+1 + history + audit `master.mapping.disable`。
  - 审计 env=LIVE（主数据面）；changed_by/actor=当前用户名。

- [ ] **Step 1: 写失败测试**

`traceflow/tests/TraceFlow.IntegrationTests/Master/MappingConfirmTests.cs`（WAF，每测试独立库；OrgBootstrap 种子 DEFAULT 组织，不用 TestSeed）：

```csharp
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Versioning;
using Xunit;

namespace TraceFlow.IntegrationTests.Master;

[SupportedOSPlatform("windows")]
public class MappingConfirmTests : IAsyncLifetime
{
    private readonly Sql2008Fixture _fx = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _fx.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        using var warm = _factory.CreateClient();
        (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
        await SeedUserAsync("mapadmin", "映射管理员", "Map-Password-123", new[] { "master:match", "trace:read" });
    }

    public async Task DisposeAsync() { _factory.Dispose(); await _fx.DisposeAsync(); }

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

    private async Task<(HttpClient client, string csrf)> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = user, password = pass })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return (client, session2.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private async Task<(long OrgId, long SysId)> OrgAsync()
    {
        await using var c = await _fx.OpenAsync();
        return (await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1"),
                await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.source_system WHERE system_code=N'KSOA'"));
    }

    private async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        return await c.ExecuteScalarAsync<T>(sql, param);
    }

    private async Task ExecAsync(string sql, object? param = null)
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(sql, param);
    }

    // 建商品+平台药+指定态映射，返回 (mappingId, productSpid, drugKey, verHex)
    private async Task<(long MapId, string Spid, string DrugKey, string Ver)> SeedProductMappingAsync(string state, string spid = "CF1", string drugKey = "PD-CF1")
    {
        var (org, sys) = await OrgAsync();
        var pid = await ScalarAsync<long>(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@org,@sys,@spid,@name)",
            new { org, sys, spid, name = $"商品{spid}" });
        var drugId = await ScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT inserted.id VALUES(@k,N'MANUAL')", new { k = drugKey });
        var mapId = await ScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT inserted.id VALUES(@org,@pid,@drug,@state)",
            new { org, pid, drug = drugId, state });
        var ver = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        return (mapId, spid, drugKey, ver);
    }

    private async Task<(long MapId, string EntId, string Ver)> SeedSupplierMappingAsync(string state, string dwbh = "CF-D1", string entId = "ENT-CF1")
    {
        var (org, sys) = await OrgAsync();
        var sid = await ScalarAsync<long>(@"
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) OUTPUT inserted.id VALUES(@org,@sys,@dwbh,@name)",
            new { org, sys, dwbh, name = $"供应商{dwbh}" });
        var evId = await ScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT inserted.id VALUES(@ref,@ent,@name)",
            new { @ref = $"REF-{entId}", ent = entId, name = "平台企业" });
        var mapId = await ScalarAsync<long>(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT inserted.id VALUES(@org,@sid,@ev,@state)",
            new { org, sid, ev = evId, state });
        var ver = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        return (mapId, entId, ver);
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Confirm_candidate_sets_manual_locked()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm",
            Json(new { targetId = drugKey, expectedVersion = ver, reason = "人工核对五项一致" }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.True(await ScalarAsync<bool>("SELECT is_locked FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal("mapadmin", await ScalarAsync<string>("SELECT confirmed_by FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.product_mapping_history WHERE mapping_id=@id AND state=N'MANUAL_CONFIRMED'", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.mapping.confirm' AND object_id=@id", new { id = mapId.ToString() }));
    }

    [Fact]
    public async Task Confirm_strong_conflict_rejected()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("CONFLICT");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm",
            Json(new { targetId = drugKey, expectedVersion = ver, reason = "试图确认强冲突" }));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("STRONG_CONFLICT", await resp.Content.ReadAsStringAsync());
        Assert.Equal("CONFLICT", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Confirm_wrong_target_rejected_no_rebind()
    {
        var (mapId, _, _, ver) = await SeedProductMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm",
            Json(new { targetId = "PD-OTHER", expectedVersion = ver, reason = "试图改绑" }));
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Confirm_version_conflict_and_validation_and_404()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        async Task<HttpStatusCode> Post(long id, object body)
        {
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return (await client.PostAsync($"/api/v1/product-mappings/{id}/confirm", Json(body))).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Conflict, await Post(mapId, new { targetId = drugKey, expectedVersion = "0000000000000000", reason = "旧版本" }));
        Assert.Equal((HttpStatusCode)422, await Post(mapId, new { targetId = drugKey, expectedVersion = ver, reason = "" }));
        Assert.Equal((HttpStatusCode)422, await Post(mapId, new { targetId = drugKey, reason = "缺版本" }));
        Assert.Equal(HttpStatusCode.NotFound, await Post(999999, new { targetId = drugKey, expectedVersion = ver, reason = "不存在" }));
    }

    [Fact]
    public async Task Disable_active_then_reconfirm()
    {
        var (mapId, _, drugKey, ver) = await SeedProductMappingAsync("AUTO_CONFIRMED");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var dis = await client.PostAsync($"/api/v1/product-mappings/{mapId}/disable", Json(new { expectedVersion = ver, reason = "误配解除" }));
        Assert.Equal(HttpStatusCode.OK, dis.StatusCode);
        Assert.Equal("DISABLED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.False(await ScalarAsync<bool>("SELECT is_locked FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'master.mapping.disable' AND object_id=@id", new { id = mapId.ToString() }));
        // 解除后可重新确认（re-enable 路径）
        var ver2 = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var re = await client.PostAsync($"/api/v1/product-mappings/{mapId}/confirm", Json(new { targetId = drugKey, expectedVersion = ver2, reason = "重新确认" }));
        Assert.Equal(HttpStatusCode.OK, re.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Disable_blocked_by_inflight_task()
    {
        var (mapId, spid, _, ver) = await SeedProductMappingAsync("AUTO_CONFIRMED", spid: "CF-INFLIGHT");
        var (org, sys) = await OrgAsync();
        var snapId = await ScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
OUTPUT inserted.id VALUES(@org,@sys,N'CF-DOC',N'sale',1,REPLICATE(N'a',64))", new { org, sys });
        await ExecAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind)
VALUES(@snap,N'1',@spid,1,REPLICATE(N'b',64),N'SALE')", new { snap = snapId, spid });
        await ExecAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
VALUES(@org,N'MOCK',N'CF-EVENT-1',N'RETAIL_321',@snap,REPLICATE(N'c',64),REPLICATE(N'd',64),N'Blocked')", new { org, snap = snapId });
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var blocked = await client.PostAsync($"/api/v1/product-mappings/{mapId}/disable", Json(new { expectedVersion = ver, reason = "在途解除" }));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("TASKS_IN_FLIGHT", await blocked.Content.ReadAsStringAsync());
        Assert.Equal("AUTO_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        // 任务终态后放行
        await ExecAsync("UPDATE dbo.upload_task SET state=N'Succeeded' WHERE event_key=N'CF-EVENT-1'");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var ok = await client.PostAsync($"/api/v1/product-mappings/{mapId}/disable", Json(new { expectedVersion = ver, reason = "终态后解除" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Supplier_confirm_and_disable()
    {
        var (mapId, entId, ver) = await SeedSupplierMappingAsync("CANDIDATE");
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var conf = await client.PostAsync($"/api/v1/supplier-mappings/{mapId}/confirm", Json(new { targetId = entId, expectedVersion = ver, reason = "信用代码核对一致" }));
        Assert.Equal(HttpStatusCode.OK, conf.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        Assert.True(await ScalarAsync<bool>("SELECT is_locked FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        var ver2 = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var dis = await client.PostAsync($"/api/v1/supplier-mappings/{mapId}/disable", Json(new { expectedVersion = ver2, reason = "解除" }));
        Assert.Equal(HttpStatusCode.OK, dis.StatusCode);
        Assert.Equal("DISABLED", await ScalarAsync<string>("SELECT state FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Confirm_active_unique_race_rejected()
    {
        var (org, sys) = await OrgAsync();
        var drugId = await ScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT inserted.id VALUES(N'PD-RACE',N'MANUAL')");
        var p1 = await ScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@org,@sys,N'RACE1',N'药一')", new { org, sys });
        var p2 = await ScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@org,@sys,N'RACE2',N'药二')", new { org, sys });
        await ExecAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@org,@p,@drug,N'AUTO_CONFIRMED')", new { org, p = p1, drug = drugId });
        var m2 = await ScalarAsync<long>("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT inserted.id VALUES(@org,@p,@drug,N'CANDIDATE')", new { org, p = p2, drug = drugId });
        var ver = RowVer.ToHex(await ScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = m2 }));
        var (client, _) = await LoginAsync("mapadmin", "Map-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync($"/api/v1/product-mappings/{m2}/confirm", Json(new { targetId = "PD-RACE", expectedVersion = ver, reason = "抢占已被占用的平台药" }));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("MAPPING_ACTIVE_CONFLICT", await resp.Content.ReadAsStringAsync());
        Assert.Equal("CANDIDATE", await ScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = m2 }));
    }
}
```

注：测试文件顶部需 `using System.Text.Json;`（Json 助手用 JsonSerializer）。

- [ ] **Step 2: 运行确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~MappingConfirmTests"`
Expected: FAIL（confirm/disable 路由不存在→404/405）。

- [ ] **Step 3: 在 MasterEndpoints 追加端点**

在 `MasterEndpoints.cs` 的 `MapMasterEndpoints` 方法内（trace-policy 端点之后、方法闭合 `}` 之前）追加。文件顶部 using 增加 `using TraceFlow.Domain.Matching;`。

```csharp
        app.MapPost("/api/v1/product-mappings/{id:long}/confirm", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.TargetId) || string.IsNullOrEmpty(req.ExpectedVersion)
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "targetId/expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<PMapFull>(new CommandDefinition(@"
SELECT pm.id, pm.state, pm.version, pm.ver, pd.platform_drug_id AS drug_key
FROM dbo.product_mapping pm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE pm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                if (map.state == MappingStates.Conflict)
                    return (409, Err(ctx, "STRONG_CONFLICT", "强冲突映射须先解决冲突，不能直接确认"));
                if (!string.Equals(map.drug_key, req.TargetId, StringComparison.Ordinal))
                    return (422, Err(ctx, "VALIDATION", "targetId 与既有平台药不一致——确认不允许改绑"));
                var actor = ctx.User.Identity!.Name!;
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product_mapping SET state=N'MANUAL_CONFIRMED', is_locked=1, confirmed_by=@by, confirmed_at=SYSUTCDATETIME(), version=version+1
WHERE id=@id", new { id, by = actor }, tx, cancellationToken: ct));
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    return (409, Err(ctx, "MAPPING_ACTIVE_CONFLICT", "该平台药已被其他有效映射占用（双向唯一）"));
                }
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "product_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product_mapping_history(mapping_id,product_id,platform_drug_id,state,rule_version,evidence_json,changed_by,reason)
SELECT @id,product_id,platform_drug_id,N'MANUAL_CONFIRMED',rule_version,evidence_json,@by,@reason FROM dbo.product_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.confirm", "product_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "MANUAL_CONFIRMED", targetId = req.TargetId }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "MANUAL_CONFIRMED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/supplier-mappings/{id:long}/confirm", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.TargetId) || string.IsNullOrEmpty(req.ExpectedVersion)
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "targetId/expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<SMapFull>(new CommandDefinition(@"
SELECT sm.id, sm.state, sm.version, sm.ver, ev.ent_id AS ent_key
FROM dbo.supplier_mapping sm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE sm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                if (map.state == MappingStates.Conflict)
                    return (409, Err(ctx, "STRONG_CONFLICT", "强冲突映射须先解决冲突，不能直接确认"));
                if (!string.Equals(map.ent_key, req.TargetId, StringComparison.Ordinal))
                    return (422, Err(ctx, "VALIDATION", "targetId 与既有企业版本 ent_id 不一致——确认不允许改绑"));
                var actor = ctx.User.Identity!.Name!;
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier_mapping SET state=N'MANUAL_CONFIRMED', is_locked=1, version=version+1
WHERE id=@id", new { id }, tx, cancellationToken: ct));
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    return (409, Err(ctx, "MAPPING_ACTIVE_CONFLICT", "该企业版本已被其他有效映射占用（双向唯一）"));
                }
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "supplier_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping_history(mapping_id,supplier_id,enterprise_version_id,state,evidence_json,changed_by,reason)
SELECT @id,supplier_id,enterprise_version_id,N'MANUAL_CONFIRMED',evidence_json,@by,@reason FROM dbo.supplier_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.confirm", "supplier_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "MANUAL_CONFIRMED", targetId = req.TargetId }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "MANUAL_CONFIRMED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/product-mappings/{id:long}/disable", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<DisableMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion) || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<PMapDisable>(new CommandDefinition(@"
SELECT pm.id, pm.org_id, pm.state, pm.ver, p.spid, p.source_system_id
FROM dbo.product_mapping pm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.product p ON p.id=pm.product_id
WHERE pm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                var inflight = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(DISTINCT t.id) FROM dbo.upload_task t
JOIN dbo.source_snapshot ss ON ss.id=t.source_snapshot_id
JOIN dbo.source_line sl ON sl.snapshot_id=ss.id
WHERE ss.org_id=@org AND ss.source_system_id=@sys AND sl.product_source_id=@spid AND t.state NOT IN (N'Succeeded',N'Failed')",
                    new { org = map.org_id, sys = map.source_system_id, spid = map.spid }, tx, cancellationToken: ct));
                if (inflight > 0) return (409, Err(ctx, "TASKS_IN_FLIGHT", $"存在 {inflight} 个非终态上传任务，禁止解除"));
                var actor = ctx.User.Identity!.Name!;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.product_mapping SET state=N'DISABLED', is_locked=0, version=version+1 WHERE id=@id", new { id }, tx, cancellationToken: ct));
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "product_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.product_mapping_history(mapping_id,product_id,platform_drug_id,state,rule_version,evidence_json,changed_by,reason)
SELECT @id,product_id,platform_drug_id,N'DISABLED',rule_version,evidence_json,@by,@reason FROM dbo.product_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.disable", "product_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "DISABLED" }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "DISABLED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/supplier-mappings/{id:long}/disable", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<DisableMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedVersion) || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<SMapDisable>(new CommandDefinition(
                    "SELECT id,org_id,state,ver FROM dbo.supplier_mapping WITH (UPDLOCK, HOLDLOCK) WHERE id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                // M1 保守守卫：org 下任何在途采购类任务都阻断供应商解除（M2 细化到 dwbh 级关联）
                var inflight = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.upload_task WHERE org_id=@org AND task_type IN (N'PURCHASE_IN_102',N'PURCHASE_RETURN_202') AND state NOT IN (N'Succeeded',N'Failed')",
                    new { org = map.org_id }, tx, cancellationToken: ct));
                if (inflight > 0) return (409, Err(ctx, "TASKS_IN_FLIGHT", $"存在 {inflight} 个非终态采购任务，禁止解除"));
                var actor = ctx.User.Identity!.Name!;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.supplier_mapping SET state=N'DISABLED', is_locked=0, version=version+1 WHERE id=@id", new { id }, tx, cancellationToken: ct));
                var (newVer, newVersion) = await ReadMappingVersionAsync(conn, tx, "supplier_mapping", id, ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.supplier_mapping_history(mapping_id,supplier_id,enterprise_version_id,state,evidence_json,changed_by,reason)
SELECT @id,supplier_id,enterprise_version_id,N'DISABLED',evidence_json,@by,@reason FROM dbo.supplier_mapping WHERE id=@id",
                    new { id, by = actor, reason = req.Reason }, tx, cancellationToken: ct));
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.disable", "supplier_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "DISABLED" }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "DISABLED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));
```

在 `MasterEndpoints` 类内（`Err` 助手附近）追加私有版本读取助手与记录类型：

```csharp
    private static async Task<(string Ver, int Version)> ReadMappingVersionAsync(SqlConnection conn, SqlTransaction tx, string table, long id, CancellationToken ct)
    {
        // table 仅取本文件内两个常量字面量（product_mapping/supplier_mapping），无注入面
        var row = await conn.QuerySingleAsync<VerRow>(new CommandDefinition(
            $"SELECT ver,version FROM dbo.{table} WHERE id=@id", new { id }, tx, cancellationToken: ct));
        return (RowVer.ToHex(row.ver), row.version);
    }

    public sealed record ConfirmMappingRequest(string? TargetId, string? ExpectedVersion, string? Reason, string[]? EvidenceIds);
    public sealed record DisableMappingRequest(string? ExpectedVersion, string? Reason);
    public sealed record PMapFull(long id, string state, int version, byte[] ver, string drug_key);
    public sealed record SMapFull(long id, string state, int version, byte[] ver, string ent_key);
    public sealed record PMapDisable(long id, long org_id, string state, byte[] ver, string spid, long source_system_id);
    public sealed record SMapDisable(long id, long org_id, string state, byte[] ver);
    private sealed record VerRow(byte[] ver, int version);
```

- [ ] **Step 4: 追加 M1ApiGateTests 门禁事实**

在 `M1ApiGateTests.cs` 的 `Audit_logs_paginated_and_filterable` 事实之后、`private sealed record JobAccepted` 之前追加：

```csharp
    [Fact]
    public async Task Mapping_endpoints_gated()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/product-mappings/1/confirm",
            new { targetId = "X", expectedVersion = "0", reason = "x" })).StatusCode);
        var (viewer, _) = await LoginAsync("m1viewer", "Test-Password-123");
        viewer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/product-mappings/1/confirm",
            new { targetId = "X", expectedVersion = "0", reason = "x" })).StatusCode);
        var (admin, _) = await LoginAsync("m1admin", "Admin-Password-123");
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var noCsrf = await admin.PostAsJsonAsync("/api/v1/supplier-mappings/1/disable", new { expectedVersion = "0", reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Contains("CSRF_INVALID", await noCsrf.Content.ReadAsStringAsync());
    }
```

（m1admin 种子已在 Task 9 含 `master:match`/`master:classify`；若执行顺序导致缺失，按 Task 9 InitializeAsync 的权限数组补齐。）

- [ ] **Step 5: 跑测试确认通过 + 全量回归**

Run: `cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（新增 8 个 MappingConfirmTests + 1 个 M1ApiGateTests 事实）。

- [ ] **Step 6: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs traceflow/tests/TraceFlow.IntegrationTests/Master/MappingConfirmTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/M1ApiGateTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): manual mapping confirm/disable with strong-conflict and in-flight guards (T13)"
```

---
### Task 14: 前端商品映射页 + 导入向导（UI-02/03、NFR-02）

**Files:**
- Create: `traceflow/src/TraceFlow.Web/src/m1types.ts`（M1 API 形状类型，Task 15 复用）
- Create: `traceflow/src/TraceFlow.Web/src/pages/ProductsPage.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/pages/ImportWizard.tsx`
- Modify: `traceflow/src/TraceFlow.Web/src/App.tsx`（挂 `/products`、`/imports` 路由）
- Modify: `traceflow/src/TraceFlow.Web/src/components/AppShell.tsx`（products ready=true；新增导入导航项）

**Interfaces:**
- Consumes: Task 11 `GET /api/v1/products`（`data.records[]` 形状见下 m1types）、`POST /api/v1/products/{id}/trace-policy`；Task 13 `POST /api/v1/product-mappings/{id}/confirm`、`/disable`；Task 10 `POST /api/v1/imports`（202 `{jobId,batchId}`）、`GET /api/v1/imports?fileKind&fileHash`（BatchView）、`GET /api/v1/imports/{id}`（`{batch,rows,total,page,pageSize}`）、`POST /api/v1/imports/{id}/commit`（`{appliedRows,status}`）；Task 9 `GET /api/v1/jobs/{id}`（`{jobState,...}`）；M0 `api<T>`（返回 `json.data`，非 GET 自动带 X-CSRF-TOKEN）。
- Produces: `m1types.ts` 导出 `Product/ProductMapping/Supplier/SupplierMapping/Page<T>/ImportBatch/ImportRow/ImportDetail/JobStatus`（Task 15 SuppliersPage/SettingsPage 复用 `Supplier/Page`）。`ProductsPage`/`ImportWizard` 默认导出组件。
- 规则：
  - **NFR-02**：对话框组件（ClassifyDialog/ConfirmDialog/DisableDialog）定义为**模块顶层函数**，禁止在 ProductsPage 函数体内再定义组件（避免每次渲染重挂载丢状态）；独立请求用 `Promise.all` 并行；列表分页 pageSize=50。
  - **UI-02 确认并排比对**：确认对话框用列表 join 返回的本地五项 vs `mapping` 平台五项并排展示，缺字段标"（缺）"，不一致行高亮；targetId 取 `mapping.platformDrugId`（不改绑），expectedVersion 取 `mapping.version`。
  - **UI-03 分类**：reason 必填 ≤500；classification 仅 REQUIRED/EXEMPT；expectedVersion 取 `product.version`。
  - 所有写操作带 `Idempotency-Key: crypto.randomUUID()`；409 VERSION_CONFLICT 提示刷新；错误用 `ApiError.message`。
  - 导入哈希：客户端对**文件原始字节**（ArrayBuffer）做 SHA-256 与 Base64（与服务端 `SHA256.HashData(bytes)` 一致，含 BOM 字节），不用 `file.text()`（会剥 BOM 致哈希不符）。

- [ ] **Step 1: 写类型文件 m1types.ts**

`traceflow/src/TraceFlow.Web/src/m1types.ts`：

```ts
export interface ProductMapping {
  mappingId: string; state: string; isLocked: boolean; mappingVersion: number; version: string;
  platformDrugId: string; approvalNoNorm: string | null; manufacturerIdentity: string | null;
  dosageForm: string | null; strength: string | null; packageSpec: string | null;
}
export interface Product {
  productId: string; spid: string; displayCode: string | null; barcode: string | null; name: string;
  approvalNoRaw: string | null; approvalNoNorm: string | null; manufacturerRaw: string | null;
  manufacturerIdentity: string | null; dosageForm: string | null; strength: string | null;
  packageSpec: string | null; unit: string | null; traceClassification: string; classificationReason: string | null;
  updatedAt: string; version: string; mappingState: string; mapping: ProductMapping | null;
}
export interface SupplierMapping {
  mappingId: string; state: string; isLocked: boolean; mappingVersion: number; version: string;
  refEntId: string; entId: string; platformName: string; creditCode: string | null;
  auditStatus: string | null; role: string | null;
}
export interface Supplier {
  supplierId: string; dwbh: string; name: string; creditCode: string | null; role: string | null;
  version: string; mappingState: string; mapping: SupplierMapping | null;
}
export interface Page<T> { records: T[]; total: number; page: number; pageSize: number; }
export interface ImportBatch {
  batchId: string; fileKind: string; fileName: string; fileHash: string; totalRows: number;
  successRows: number; failedRows: number; status: string; version: string;
  stagedBy: string; stagedAt: string; committedBy: string | null; committedAt: string | null;
}
export interface ImportRow { rowNumber: number; status: string; error: string | null; rawJson: string; }
export interface ImportDetail { batch: ImportBatch; rows: ImportRow[]; total: number; page: number; pageSize: number; }
export interface JobStatus {
  jobId: string; jobKind: string; intentKey: string; jobState: string; attemptCount: number;
  nextRunAtUtc: string; lastError: string | null; leaseOwner: string | null; fencingToken: number;
}
```

- [ ] **Step 2: 写 ProductsPage.tsx（含三个顶层对话框组件）**

`traceflow/src/TraceFlow.Web/src/pages/ProductsPage.tsx`：

```tsx
import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { Page, Product } from '../m1types';

const CLASSIFICATIONS = ['ALL', 'UNKNOWN', 'REQUIRED', 'EXEMPT'];
const MAPPING_STATES = ['ALL', 'NONE', 'UNRESOLVED', 'CANDIDATE', 'AUTO_CONFIRMED', 'MANUAL_CONFIRMED', 'CONFLICT', 'REVIEW_REQUIRED', 'DISABLED'];
const PAGE_SIZE = 50;

function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function ProductsPage() {
  const [keyword, setKeyword] = useState('');
  const [classification, setClassification] = useState('ALL');
  const [mappingState, setMappingState] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<Product> | null>(null);
  const [error, setError] = useState('');
  const [classifyTarget, setClassifyTarget] = useState<Product | null>(null);
  const [confirmTarget, setConfirmTarget] = useState<Product | null>(null);
  const [disableTarget, setDisableTarget] = useState<Product | null>(null);

  const load = useCallback(async () => {
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (keyword.trim()) qs.set('keyword', keyword.trim());
    if (classification !== 'ALL') qs.set('classification', classification);
    if (mappingState !== 'ALL') qs.set('mappingState', mappingState);
    try { setData(await api<Page<Product>>(`/api/v1/products?${qs.toString()}`)); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, [keyword, classification, mappingState, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>商品映射</h1>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={keyword} onChange={(e) => { setKeyword(e.target.value); setPage(1); }} placeholder="名称/编码/条码" />
        <select value={classification} onChange={(e) => { setClassification(e.target.value); setPage(1); }}>
          {CLASSIFICATIONS.map((c) => <option key={c} value={c}>{c === 'ALL' ? '全部分类' : c}</option>)}
        </select>
        <select value={mappingState} onChange={(e) => { setMappingState(e.target.value); setPage(1); }}>
          {MAPPING_STATES.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部映射态' : s}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr><th>SPID</th><th>名称</th><th>批准文号</th><th>分类</th><th>映射态</th><th>平台药</th><th>操作</th></tr>
            </thead>
            <tbody>
              {data.records.map((p) => (
                <tr key={p.productId}>
                  <td>{p.spid}</td><td>{p.name}</td><td>{p.approvalNoNorm ?? '（缺）'}</td>
                  <td>{p.traceClassification}</td><td>{p.mappingState}</td>
                  <td>{p.mapping?.platformDrugId ?? '—'}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    <button onClick={() => setClassifyTarget(p)}>分类</button>{' '}
                    {p.mapping && p.mappingState !== 'DISABLED' && <button onClick={() => setConfirmTarget(p)}>确认映射</button>}{' '}
                    {p.mapping && (p.mappingState === 'AUTO_CONFIRMED' || p.mappingState === 'MANUAL_CONFIRMED') &&
                      <button onClick={() => setDisableTarget(p)}>解除</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center' }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
            <span>第 {data.page} / {totalPages} 页 · 共 {data.total} 条</span>
            <button disabled={page >= totalPages} onClick={() => setPage(page + 1)}>下一页</button>
          </div>
        </>
      )}
      {classifyTarget && <ClassifyDialog product={classifyTarget} onClose={() => setClassifyTarget(null)} onDone={() => { setClassifyTarget(null); void load(); }} />}
      {confirmTarget && confirmTarget.mapping && <ConfirmDialog product={confirmTarget} onClose={() => setConfirmTarget(null)} onDone={() => { setConfirmTarget(null); void load(); }} />}
      {disableTarget && disableTarget.mapping && <DisableDialog product={disableTarget} onClose={() => setDisableTarget(null)} onDone={() => { setDisableTarget(null); void load(); }} />}
    </section>
  );
}

function ClassifyDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const [cls, setCls] = useState(product.traceClassification === 'EXEMPT' ? 'EXEMPT' : 'REQUIRED');
  const [reason, setReason] = useState(product.classificationReason ?? '');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写分类理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/products/${product.productId}/trace-policy`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ classification: cls, reason, expectedVersion: product.version }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>追溯分类 · {product.name}</h3>
        <p>
          <select value={cls} onChange={(e) => setCls(e.target.value)}>
            <option value="REQUIRED">REQUIRED 需追溯</option>
            <option value="EXEMPT">EXEMPT 豁免</option>
          </select>
        </p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={3} style={{ width: '100%' }} placeholder="分类理由（必填，≤500 字）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>保存</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

type FiveKey = 'approvalNoNorm' | 'manufacturerIdentity' | 'dosageForm' | 'strength' | 'packageSpec';
const FIVE: { key: FiveKey; label: string }[] = [
  { key: 'approvalNoNorm', label: '批准文号' }, { key: 'manufacturerIdentity', label: '厂家身份' },
  { key: 'dosageForm', label: '剂型' }, { key: 'strength', label: '规格' }, { key: 'packageSpec', label: '包装' },
];

function ConfirmDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const m = product.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写确认理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/product-mappings/${m.mappingId}/confirm`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: m.platformDrugId, expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>确认映射 · {product.name}</h3>
        <p>平台药：{m.platformDrugId}（确认不改绑；强冲突须先解决）</p>
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>字段</th><th>本地</th><th>平台</th><th>比对</th></tr></thead>
          <tbody>
            {FIVE.map(({ key, label }) => {
              const local = product[key] ?? '';
              const platform = m[key] ?? '';
              const same = local !== '' && platform !== '' && local === platform;
              const missing = local === '' || platform === '';
              return (
                <tr key={key} style={{ background: missing ? '#fff3cd' : same ? '#d4edda' : '#f8d7da' }}>
                  <td>{label}</td><td>{local || '（缺）'}</td><td>{platform || '（缺）'}</td>
                  <td>{missing ? '缺字段' : same ? '一致' : '冲突'}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="确认理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>确认（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function DisableDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const m = product.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写解除理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/product-mappings/${m.mappingId}/disable`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>解除映射 · {product.name}</h3>
        <p>存在非终态上传任务时将被阻断（TASKS_IN_FLIGHT）。</p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%' }} placeholder="解除理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>解除</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 640, width: '90%', maxHeight: '85vh', overflow: 'auto' };
```

- [ ] **Step 3: 写 ImportWizard.tsx**

`traceflow/src/TraceFlow.Web/src/pages/ImportWizard.tsx`：

```tsx
import { useState } from 'react';
import { api, ApiError } from '../api';
import type { ImportBatch, ImportDetail, JobStatus } from '../m1types';

const KINDS = [
  { value: 'PRODUCT', label: '商品档案（13 列）' },
  { value: 'SUPPLIER', label: '供应商档案（4 列）' },
  { value: 'PLATFORM_DRUG', label: '平台药品（7 列）' },
  { value: 'ENTERPRISE', label: '平台企业版本（6 列）' },
];

function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf)).map((b) => b.toString(16).padStart(2, '0')).join('');
}
function base64(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf); let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export default function ImportWizard() {
  const [kind, setKind] = useState('PRODUCT');
  const [file, setFile] = useState<{ name: string; hash: string; content: string } | null>(null);
  const [batch, setBatch] = useState<ImportBatch | null>(null);
  const [detail, setDetail] = useState<ImportDetail | null>(null);
  const [phase, setPhase] = useState<'select' | 'staging' | 'preview' | 'committed'>('select');
  const [error, setError] = useState('');
  const [applied, setApplied] = useState(0);

  async function onFile(f: File | undefined) {
    setError(''); setFile(null); setBatch(null); setDetail(null); setPhase('select');
    if (!f) return;
    try {
      const buf = await f.arrayBuffer();
      const hash = hex(await crypto.subtle.digest('SHA-256', buf));
      setFile({ name: f.name, hash, content: base64(buf) });
    } catch (e) { setError(errMsg(e)); }
  }

  async function upload() {
    if (!file) { setError('请先选择 CSV 文件'); return; }
    setPhase('staging'); setError('');
    try {
      const accepted = await api<{ jobId: string; batchId: string }>('/api/v1/imports', {
        method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() },
        body: JSON.stringify({ fileKind: kind, fileHash: file.hash, content: file.content, fileName: file.name }),
      });
      // 轮询暂存作业至终态（DONE/FAILED/CANCELLED），最多 60 次 × 1s
      let job: JobStatus | null = null;
      for (let i = 0; i < 60; i++) {
        job = await api<JobStatus>(`/api/v1/jobs/${accepted.jobId}`);
        if (job.jobState === 'DONE' || job.jobState === 'FAILED' || job.jobState === 'CANCELLED') break;
        await sleep(1000);
      }
      if (!job || job.jobState !== 'DONE') { setError(`暂存未完成：${job?.jobState ?? '超时'} ${job?.lastError ?? ''}`); setPhase('select'); return; }
      const b = await api<ImportBatch>(`/api/v1/imports?fileKind=${kind}&fileHash=${file.hash}`);
      const d = await api<ImportDetail>(`/api/v1/imports/${b.batchId}?page=1&pageSize=50`);
      setBatch(b); setDetail(d); setPhase('preview');
    } catch (e) { setError(errMsg(e)); setPhase('select'); }
  }

  async function commit() {
    if (!batch) return;
    setError('');
    try {
      const res = await api<{ appliedRows: number; status: string }>(`/api/v1/imports/${batch.batchId}/commit`, {
        method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() },
        body: JSON.stringify({ expectedVersion: batch.version, reason: '导入向导提交' }),
      });
      setApplied(res.appliedRows); setPhase('committed');
    } catch (e) { setError(errMsg(e)); }
  }

  return (
    <section>
      <h1>主数据导入</h1>
      <p>
        <select value={kind} onChange={(e) => { setKind(e.target.value); setFile(null); setBatch(null); setDetail(null); setPhase('select'); }} disabled={phase === 'staging'}>
          {KINDS.map((k) => <option key={k.value} value={k.value}>{k.label}</option>)}
        </select>{' '}
        <input type="file" accept=".csv,text/csv" onChange={(e) => void onFile(e.target.files?.[0])} disabled={phase === 'staging'} />
      </p>
      {file && <p>已选：{file.name} · SHA-256 {file.hash.slice(0, 16)}…</p>}
      {error && <p role="alert">{error}</p>}
      {phase === 'select' && <button disabled={!file} onClick={() => void upload()}>上传暂存</button>}
      {phase === 'staging' && <p>暂存校验中…（轮询作业状态）</p>}
      {phase === 'preview' && batch && detail && (
        <>
          <p>批次 {batch.batchId} · 状态 {batch.status} · 总 {batch.totalRows} 行 · 合格 {batch.successRows} · 错误 {batch.failedRows}</p>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>行号</th><th>状态</th><th>错误</th><th>原始</th></tr></thead>
            <tbody>
              {detail.rows.map((r) => (
                <tr key={r.rowNumber} style={{ background: r.status === 'ERROR' ? '#f8d7da' : undefined }}>
                  <td>{r.rowNumber}</td><td>{r.status}</td><td>{r.error ?? ''}</td>
                  <td style={{ maxWidth: 320, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.rawJson}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p style={{ color: '#666' }}>仅展示前 50 行；提交只应用 OK 行，ERROR 行跳过。</p>
          <button onClick={() => void commit()} disabled={batch.status !== 'STAGED'}>提交应用 OK 行</button>
        </>
      )}
      {phase === 'committed' && <p role="status">已提交，应用 {applied} 行。匹配作业已入队（master-match）。</p>}
    </section>
  );
}
```

- [ ] **Step 4: 挂路由 + 导航**

`App.tsx`：在 import 区追加 `import ProductsPage from './pages/ProductsPage';` 与 `import ImportWizard from './pages/ImportWizard';`；在 `<Route path="/" .../>` 之后追加：

```tsx
        <Route path="/products" element={<ProductsPage />} />
        <Route path="/imports" element={<ImportWizard />} />
```

`AppShell.tsx`：把 NAV 中 `{ to: '/products', label: '商品映射', ready: false }` 改为 `ready: true`；在 products 项之后插入 `{ to: '/imports', label: '主数据导入', ready: true },`。

- [ ] **Step 5: 构建验证（tsc 0 错误）**

Run: `cd "/e/Viding/Retail Trace Code/traceflow/src/TraceFlow.Web" && npm install && npm run build`
Expected: `tsc -b` 0 错误，`vite build` 产出 `../TraceFlow.Host/wwwroot`。类型已用 `import type { CSSProperties }` 与共享 `FiveKey` 字面量联合，无 `React` 命名空间依赖、无键类型断言。

- [ ] **Step 6: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Web/src/m1types.ts traceflow/src/TraceFlow.Web/src/pages/ProductsPage.tsx traceflow/src/TraceFlow.Web/src/pages/ImportWizard.tsx traceflow/src/TraceFlow.Web/src/App.tsx traceflow/src/TraceFlow.Web/src/components/AppShell.tsx
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): products mapping page + import wizard UI (T14)"
```

注：`wwwroot` 构建产物若被 .gitignore 忽略则不提交；若未忽略，仅提交源码，构建产物由 Host 静态托管在部署时生成（执行时检查 `git status`，不要把 wwwroot 大批产物混入本次提交）。

---

### Task 15: 前端供应商映射页 + 设置页 + api 健壮性 + 路由/代理（UI-10/11/13、NFR-02）

**Files:**
- Create: `traceflow/src/TraceFlow.Web/src/pages/SuppliersPage.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/pages/SettingsPage.tsx`
- Modify: `traceflow/src/TraceFlow.Web/src/m1types.ts`（追加 `SettingView`/`ConnectionTestResult`）
- Modify: `traceflow/src/TraceFlow.Web/src/api.ts`（非 JSON 2xx 守卫 + 401 回调）
- Modify: `traceflow/src/TraceFlow.Web/src/App.tsx`（挂 `/suppliers`、`/settings`；注册 401 处理器）
- Modify: `traceflow/src/TraceFlow.Web/src/components/AppShell.tsx`（suppliers/settings ready=true；health active 守卫；登出容错）
- Modify: `traceflow/src/TraceFlow.Web/vite.config.ts`（代理目标 5080→5152，与 launchSettings http 一致）

**Interfaces:**
- Consumes: Task 11 `GET /api/v1/suppliers`（records 形状见 m1types.Supplier）；Task 13 `POST /api/v1/supplier-mappings/{id}/confirm`、`/disable`；Task 6 `GET /api/v1/settings`（`data=SettingView[]`，**camelCase**：id/key/valueJson/isSecret/hasSecretValue/version/updatedBy/updatedAtUtc/reason）、`PUT /api/v1/settings/{key}`（`data=SettingView`）、`POST /api/v1/settings/connections/test`（`data={ok,message}`，body `{configId:number}`）；Task 14 `m1types`/`idem`/`errMsg` 模式。
- Produces: `SuppliersPage`/`SettingsPage` 默认导出；`api.ts` 导出 `setUnauthorizedHandler(fn:()=>void):void`；m1types 追加 `SettingView`/`ConnectionTestResult`。
- 规则：
  - **UI-10 供应商确认**：targetId=`mapping.entId`（不改绑），expectedVersion=`mapping.version`；并排展示信用代码（精确）/名称（仅候选）/审核状态/角色；强冲突 CONFLICT 由后端 409 STRONG_CONFLICT 拒绝，前端提示。
  - **UI-11 设置**：非密钥键编辑 `valueJson`（JSON 文本）；密钥键（isSecret）**只写** `secretValue`（password 输入，不回显明文，列表显示 hasSecretValue 布尔）；expectedVersion=`setting.version`（未持久化 id=0 时 version="0"）；reason 必填。连接测试仅对已保存的 `erp.connection`（id≠0）可用，body `{configId:row.id}`。`sync.paused` 提供暂停开关（PUT valueJson "true"/"false"）。
  - **UI-13/api 健壮性**：`api()` 对 2xx 非 JSON 抛 `ApiError(NON_JSON)`；401 触发 `unauthorizedHandler`（App 重置会话回登录页）；登出失败仍回登录页（try/finally）。
  - **NFR-02**：对话框为模块顶层组件；AppShell health 请求带 active 守卫防卸载后 setState；列表分页 50。
  - 写操作带 `Idempotency-Key: crypto.randomUUID()`（连接测试除外，无副作用）。

- [ ] **Step 1: m1types.ts 追加设置类型**

在 `m1types.ts` 末尾追加：

```ts
export interface SettingView {
  id: number; key: string; valueJson: string | null; isSecret: boolean; hasSecretValue: boolean;
  version: string; updatedBy: string | null; updatedAtUtc: string | null; reason: string | null;
}
export interface ConnectionTestResult { ok: boolean; message: string; }
```

- [ ] **Step 2: 重写 api.ts（守卫 + 401 回调）**

`traceflow/src/TraceFlow.Web/src/api.ts` 全文替换为：

```ts
export interface SessionData {
  authenticated: boolean;
  username?: string;
  permissions?: string[];
  csrfToken: string;
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryable: boolean) {
    super(message);
  }
}

let csrfToken = '';
let unauthorizedHandler: (() => void) | null = null;

export function setUnauthorizedHandler(fn: () => void): void { unauthorizedHandler = fn; }

export async function loadSession(): Promise<SessionData> {
  const resp = await fetch('/api/v1/auth/session', { credentials: 'include' });
  const json = await resp.json();
  csrfToken = json.data.csrfToken;
  return json.data as SessionData;
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers);
  if (method !== 'GET' && method !== 'HEAD') headers.set('X-CSRF-TOKEN', csrfToken);
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const resp = await fetch(path, { ...init, headers, credentials: 'include' });
  if (resp.status === 401) unauthorizedHandler?.();
  const json: unknown = await resp.json().catch(() => null);
  if (!resp.ok) {
    const e = json as { code?: string; message?: string; retryable?: boolean } | null;
    throw new ApiError(resp.status, e?.code ?? 'HTTP_' + resp.status, e?.message ?? '请求失败', e?.retryable ?? false);
  }
  if (json === null || typeof json !== 'object') {
    throw new ApiError(resp.status, 'NON_JSON', '成功响应不是合法 JSON', false);
  }
  return (json as { data: T }).data;
}
```

- [ ] **Step 3: 写 SuppliersPage.tsx**

`traceflow/src/TraceFlow.Web/src/pages/SuppliersPage.tsx`：

```tsx
import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { Page, Supplier } from '../m1types';

const MAPPING_STATES = ['ALL', 'NONE', 'UNRESOLVED', 'CANDIDATE', 'AUTO_CONFIRMED', 'MANUAL_CONFIRMED', 'CONFLICT', 'REVIEW_REQUIRED', 'DISABLED'];
const PAGE_SIZE = 50;
function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function SuppliersPage() {
  const [keyword, setKeyword] = useState('');
  const [mappingState, setMappingState] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<Supplier> | null>(null);
  const [error, setError] = useState('');
  const [confirmTarget, setConfirmTarget] = useState<Supplier | null>(null);
  const [disableTarget, setDisableTarget] = useState<Supplier | null>(null);

  const load = useCallback(async () => {
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (keyword.trim()) qs.set('keyword', keyword.trim());
    if (mappingState !== 'ALL') qs.set('mappingState', mappingState);
    try { setData(await api<Page<Supplier>>(`/api/v1/suppliers?${qs.toString()}`)); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, [keyword, mappingState, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>供应商映射</h1>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={keyword} onChange={(e) => { setKeyword(e.target.value); setPage(1); }} placeholder="名称/单位编号/信用代码" />
        <select value={mappingState} onChange={(e) => { setMappingState(e.target.value); setPage(1); }}>
          {MAPPING_STATES.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部映射态' : s}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>单位编号</th><th>名称</th><th>信用代码</th><th>映射态</th><th>平台企业</th><th>审核</th><th>操作</th></tr></thead>
            <tbody>
              {data.records.map((s) => (
                <tr key={s.supplierId}>
                  <td>{s.dwbh}</td><td>{s.name}</td><td>{s.creditCode ?? '（缺）'}</td>
                  <td>{s.mappingState}</td><td>{s.mapping?.platformName ?? '—'}</td>
                  <td>{s.mapping?.auditStatus ?? '—'}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    {s.mapping && s.mappingState !== 'DISABLED' && <button onClick={() => setConfirmTarget(s)}>确认映射</button>}{' '}
                    {s.mapping && (s.mappingState === 'AUTO_CONFIRMED' || s.mappingState === 'MANUAL_CONFIRMED') &&
                      <button onClick={() => setDisableTarget(s)}>解除</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center' }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
            <span>第 {data.page} / {totalPages} 页 · 共 {data.total} 条</span>
            <button disabled={page >= totalPages} onClick={() => setPage(page + 1)}>下一页</button>
          </div>
        </>
      )}
      {confirmTarget && confirmTarget.mapping && <ConfirmDialog supplier={confirmTarget} onClose={() => setConfirmTarget(null)} onDone={() => { setConfirmTarget(null); void load(); }} />}
      {disableTarget && disableTarget.mapping && <DisableDialog supplier={disableTarget} onClose={() => setDisableTarget(null)} onDone={() => { setDisableTarget(null); void load(); }} />}
    </section>
  );
}

function ConfirmDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: () => void }) {
  const m = supplier.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const creditSame = !!supplier.creditCode && !!m.creditCode && supplier.creditCode === m.creditCode;
  async function submit() {
    if (!reason.trim()) { setError('请填写确认理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/supplier-mappings/${m.mappingId}/confirm`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: m.entId, expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>确认供应商映射 · {supplier.name}</h3>
        <p>平台企业 ent_id：{m.entId}（确认不改绑）· 审核状态：{m.auditStatus ?? '（未知）'}</p>
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>字段</th><th>本地</th><th>平台</th><th>比对</th></tr></thead>
          <tbody>
            <tr style={{ background: creditSame ? '#d4edda' : '#f8d7da' }}>
              <td>信用代码（精确）</td><td>{supplier.creditCode ?? '（缺）'}</td><td>{m.creditCode ?? '（缺）'}</td>
              <td>{creditSame ? '一致' : '不一致/缺'}</td>
            </tr>
            <tr><td>名称（仅候选）</td><td>{supplier.name}</td><td>{m.platformName}</td><td>—</td></tr>
            <tr><td>角色</td><td>{supplier.role ?? '（缺）'}</td><td>{m.role ?? '（缺）'}</td><td>独立</td></tr>
          </tbody>
        </table>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="确认理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>确认（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function DisableDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: () => void }) {
  const m = supplier.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写解除理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/supplier-mappings/${m.mappingId}/disable`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>解除供应商映射 · {supplier.name}</h3>
        <p>存在非终态采购任务时将被阻断（TASKS_IN_FLIGHT）。</p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%' }} placeholder="解除理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>解除</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 640, width: '90%', maxHeight: '85vh', overflow: 'auto' };
```

- [ ] **Step 4: 写 SettingsPage.tsx**

`traceflow/src/TraceFlow.Web/src/pages/SettingsPage.tsx`：

```tsx
import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { ConnectionTestResult, SettingView } from '../m1types';

function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function SettingsPage() {
  const [settings, setSettings] = useState<SettingView[] | null>(null);
  const [error, setError] = useState('');
  const [editTarget, setEditTarget] = useState<SettingView | null>(null);
  const [testMsg, setTestMsg] = useState('');
  const [busyKey, setBusyKey] = useState('');

  const load = useCallback(async () => {
    try { setSettings(await api<SettingView[]>('/api/v1/settings')); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, []);
  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);

  async function testConnection(row: SettingView) {
    setTestMsg(''); setBusyKey(row.key);
    try {
      const r = await api<ConnectionTestResult>('/api/v1/settings/connections/test', {
        method: 'POST', body: JSON.stringify({ configId: row.id }),
      });
      setTestMsg(`${r.ok ? '✅' : '❌'} ${r.message}`);
    } catch (e) { setTestMsg(errMsg(e)); }
    finally { setBusyKey(''); }
  }

  async function togglePause(row: SettingView) {
    const next = row.valueJson === 'true' ? 'false' : 'true';
    setBusyKey(row.key); setError('');
    try {
      await api(`/api/v1/settings/${row.key}`, {
        method: 'PUT', headers: idem(),
        body: JSON.stringify({ valueJson: next, expectedVersion: row.version, reason: '切换渠道暂停' }),
      });
      await load();
    } catch (e) { setError(errMsg(e)); }
    finally { setBusyKey(''); }
  }

  return (
    <section>
      <h1>系统设置</h1>
      {error && <p role="alert">{error}</p>}
      {testMsg && <p role="status">{testMsg}</p>}
      {settings === null ? <p>加载中…</p> : (
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>键</th><th>值</th><th>版本</th><th>更新者</th><th>理由</th><th>操作</th></tr></thead>
          <tbody>
            {settings.map((s) => (
              <tr key={s.key}>
                <td>{s.key}</td>
                <td style={{ maxWidth: 280, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.isSecret ? (s.hasSecretValue ? '••••（已保存，只写）' : '（未设置）') : (s.valueJson ?? '（默认）')}
                </td>
                <td>{s.version}</td><td>{s.updatedBy ?? '—'}</td><td>{s.reason ?? '—'}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button onClick={() => setEditTarget(s)}>编辑</button>{' '}
                  {s.key === 'erp.connection' && <button disabled={s.id === 0 || busyKey === s.key} onClick={() => void testConnection(s)}>连接测试</button>}{' '}
                  {s.key === 'sync.paused' && <button disabled={busyKey === s.key} onClick={() => void togglePause(s)}>{s.valueJson === 'true' ? '恢复渠道' : '暂停渠道'}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editTarget && <SettingEditDialog setting={editTarget} onClose={() => setEditTarget(null)} onDone={() => { setEditTarget(null); void load(); }} />}
    </section>
  );
}

function SettingEditDialog({ setting, onClose, onDone }: { setting: SettingView; onClose: () => void; onDone: () => void }) {
  const [valueJson, setValueJson] = useState(setting.valueJson ?? '');
  const [secretValue, setSecretValue] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写修改理由'); return; }
    if (setting.isSecret && !secretValue) { setError('密钥不能为空'); return; }
    if (!setting.isSecret && !valueJson.trim()) { setError('值不能为空'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/settings/${setting.key}`, {
        method: 'PUT', headers: idem(),
        body: JSON.stringify({
          valueJson: setting.isSecret ? null : valueJson,
          secretValue: setting.isSecret ? secretValue : null,
          expectedVersion: setting.version, reason,
        }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>编辑设置 · {setting.key}</h3>
        {setting.isSecret ? (
          <>
            <p>密钥只写：输入新值保存，不会回显已存明文。{setting.hasSecretValue ? '当前已保存密钥。' : '当前未设置。'}</p>
            <input type="password" value={secretValue} onChange={(e) => setSecretValue(e.target.value)} style={{ width: '100%' }} placeholder="新密钥/连接串" autoComplete="new-password" />
          </>
        ) : (
          <>
            <p>JSON 值（按配置类型校验）：</p>
            <textarea value={valueJson} onChange={(e) => setValueJson(e.target.value)} rows={4} style={{ width: '100%', fontFamily: 'monospace' }} />
          </>
        )}
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="修改理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>保存</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 640, width: '90%', maxHeight: '85vh', overflow: 'auto' };
```

- [ ] **Step 5: App.tsx 路由 + 401 处理器**

`App.tsx` 全文替换为：

```tsx
import { useEffect, useState } from 'react';
import { Route, Routes } from 'react-router-dom';
import { loadSession, setUnauthorizedHandler, type SessionData } from './api';
import AppShell from './components/AppShell';
import LoginPage from './pages/LoginPage';
import DashboardPage from './pages/DashboardPage';
import ProductsPage from './pages/ProductsPage';
import ImportWizard from './pages/ImportWizard';
import SuppliersPage from './pages/SuppliersPage';
import SettingsPage from './pages/SettingsPage';

export default function App() {
  const [session, setSession] = useState<SessionData | null>(null);

  useEffect(() => {
    setUnauthorizedHandler(() => setSession({ authenticated: false, csrfToken: '' }));
    loadSession().then(setSession).catch(() => setSession({ authenticated: false, csrfToken: '' }));
  }, []);

  if (session === null) return <p>加载中…</p>;
  if (!session.authenticated) return <LoginPage onLoggedIn={async () => setSession(await loadSession())} />;
  return (
    <AppShell session={session} onLoggedOut={async () => setSession(await loadSession())}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/products" element={<ProductsPage />} />
        <Route path="/imports" element={<ImportWizard />} />
        <Route path="/suppliers" element={<SuppliersPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="*" element={<p>该模块在后续阶段交付（见路线图），当前未接入。</p>} />
      </Routes>
    </AppShell>
  );
}
```

- [ ] **Step 6: AppShell.tsx 导航/health 守卫/登出容错**

`AppShell.tsx`：
1. NAV 中 `{ to: '/suppliers', label: '供应商映射', ready: false }` → `ready: true`；`{ to: '/settings', label: '设置', ready: false }` → `ready: true`。
2. health useEffect 替换为带 active 守卫：

```tsx
  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);
```

3. handleLogout 替换为容错版：

```tsx
  async function handleLogout() {
    try { await api('/api/v1/auth/logout', { method: 'POST' }); }
    catch { /* 登出请求失败也回到登录页 */ }
    finally { await onLoggedOut(); }
  }
```

- [ ] **Step 7: vite.config.ts 代理端口对齐**

`vite.config.ts` 中 `server: { proxy: { '/api': 'http://localhost:5080' } }` 改为 `server: { proxy: { '/api': 'http://localhost:5152' } }`（与 `TraceFlow.Host/Properties/launchSettings.json` http profile 的 5152 一致）。

- [ ] **Step 8: 构建验证（tsc 0 错误）**

Run: `cd "/e/Viding/Retail Trace Code/traceflow/src/TraceFlow.Web" && npm run build`
Expected: `tsc -b` 0 错误，`vite build` 成功。

- [ ] **Step 9: 提交**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/src/TraceFlow.Web/src/pages/SuppliersPage.tsx traceflow/src/TraceFlow.Web/src/pages/SettingsPage.tsx traceflow/src/TraceFlow.Web/src/m1types.ts traceflow/src/TraceFlow.Web/src/api.ts traceflow/src/TraceFlow.Web/src/App.tsx traceflow/src/TraceFlow.Web/src/components/AppShell.tsx traceflow/src/TraceFlow.Web/vite.config.ts
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m1): suppliers/settings pages, api hardening, routes and dev proxy (T15)"
```

---
### Task 16: 契约/文档回写 + M1 门禁报告 + 全量双变体回归

**Files:**
- Modify: `docs/contracts/openapi.yaml`（M1 真实形状回写 + 新增 5 路径）
- Modify: `docs/contracts/data-dictionary.md`（0007 增量）
- Modify: `docs/contracts/adapter-facts.md`（M1 fixture 通道状态）
- Modify: `docs/contracts/source-mapping.md`（M1 落地注记）
- Create: `docs/contracts/ksoa-adapter-contract.md`（适配器合同定稿：fixture 形状 + 取证清单）
- Modify: `docs/traceability/matrix.md`（M1 需求行证据回写）
- Create: `docs/delivery/M1-gate-report.md`
- Create: `docs/delivery/M1-rulings-and-carryforwards.md`

**Interfaces:**
- Consumes: Task 1–15 全部实际端点形状/测试名/迁移；M0 `docs/delivery/M0-gate-report.md` 口径（降级声明模板）。
- Produces: 与代码一致的 OpenAPI/数据字典/追踪矩阵；M1 门禁报告（诚实声明 2008 R2 未验证、G-ERP fixture-only）。
- 规则（诚实口径，强制）：
  - **2008 R2 未验证**：所有 DB 集成证据标注"SQL 2019 集成（授权降级；2008 R2 待补验）"；AT-070 仍为未满足门禁；不伪造 10.50.x 记录。
  - **G-ERP fixture-only**：M1 来源同步仅经 `FixtureKsoaAdapter`（本地 JSON fixture）验证；真实 KSOA 只读连接/键唯一性取证未做，真实 ERP 同步通道禁用（`RealKsoaAdapter` 抛 `AdapterNotCertifiedException`，LIVE 环境拒绝同步）。
  - **G-TOP 未取证**：平台药品/企业真实下载未实现（M2）；M1 平台主数据仅经 CSV 导入积累（source_kind=MANUAL/IMPORT）。
  - 矩阵测试名必须为实际存在的测试（上方 Task 1–15 已定义）；未实现项标"计划/未实现"。

- [ ] **Step 1: openapi.yaml 回写（6 处编辑 + 2 处插入）**

**(a) info.description 段**：把第 8–9 行

```
    M0 已实现端点（/auth/*、/admin/*、/dashboard、/health）按真实代码形状描述
    （traceflow/src/TraceFlow.Host/Endpoints/*.cs）；M1+ 端点为计划契约，尚未实现。
```

替换为

```
    M0 已实现端点（/auth/*、/admin/*、/dashboard、/health）与 M1 已实现端点
    （/products、/suppliers、/imports*、/product-mappings/*、/supplier-mappings/*、
    /products/{id}/trace-policy、/sync-jobs、/jobs/{id}、/audit-logs、/settings*）
    按真实代码形状描述（traceflow/src/TraceFlow.Host/Endpoints/*.cs）；M2+ 端点为计划契约，尚未实现。
```

**(b) ImportRequest.fileKind**：把

```
        fileKind: { type: string, description: 导入文件类型（如平台药品映射导出） }
```

替换为

```
        fileKind: { type: string, enum: [PRODUCT, SUPPLIER, PLATFORM_DRUG, ENTERPRISE], description: 导入类型（决定 CSV 列模板与目标表） }
```

**(c) 新增 schemas**：在 `  responses:` 行之前（即 `JobAccepted` 块末尾 `        requestId: { type: string }` 之后）插入以下完整块：

```yaml
    # ---- M1 已实现端点真实响应形状（T16 回写） ----
    SettingView:
      type: object
      required: [id, key, isSecret, hasSecretValue, version]
      properties:
        id: { type: integer, format: int64, description: app_setting 本地 ID；未持久化为 0 }
        key: { type: string }
        valueJson: { type: string, nullable: true, description: 非密钥配置 JSON 文本；密钥键恒 null }
        isSecret: { type: boolean }
        hasSecretValue: { type: boolean, description: 是否已保存密钥密文（不回显明文） }
        version: { type: string, description: rowversion 字符串；未持久化为 "0" }
        updatedBy: { type: string, nullable: true }
        updatedAtUtc: { type: string, format: date-time, nullable: true }
        reason: { type: string, nullable: true }
    SettingsListResponse:
      type: object
      required: [data, requestId]
      properties:
        data: { type: array, items: { $ref: '#/components/schemas/SettingView' } }
        requestId: { type: string }
    SettingUpdateResponse:
      type: object
      required: [data, requestId]
      properties:
        data: { $ref: '#/components/schemas/SettingView' }
        requestId: { type: string }
    ConnectionTestResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [ok, message]
          properties:
            ok: { type: boolean }
            message: { type: string, description: 连接结果描述（不回显凭据/连接串） }
        requestId: { type: string }
    ImportBatchView:
      type: object
      required: [batchId, fileKind, fileName, fileHash, totalRows, successRows, failedRows, status, version]
      properties:
        batchId: { type: string }
        fileKind: { type: string, enum: [PRODUCT, SUPPLIER, PLATFORM_DRUG, ENTERPRISE] }
        fileName: { type: string }
        fileHash: { type: string }
        totalRows: { type: integer }
        successRows: { type: integer }
        failedRows: { type: integer }
        status: { type: string, enum: [STAGED, COMMITTED, FAILED] }
        version: { type: string, description: rowversion 字符串 }
        stagedBy: { type: string }
        stagedAt: { type: string, format: date-time }
        committedBy: { type: string, nullable: true }
        committedAt: { type: string, format: date-time, nullable: true }
    ImportAccepted:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [jobId, batchId]
          properties:
            jobId: { type: string, description: 'import-stage 作业 ID；已提交/失败批次返回 "0"（不产生作业）' }
            batchId: { type: string }
        requestId: { type: string }
    ImportPreviewResponse:
      type: object
      required: [data, requestId]
      properties:
        data: { $ref: '#/components/schemas/ImportBatchView' }
        requestId: { type: string }
    ImportDetailResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [batch, rows, total, page, pageSize]
          properties:
            batch: { $ref: '#/components/schemas/ImportBatchView' }
            rows:
              type: array
              items:
                type: object
                required: [rowNumber, status, rawJson]
                properties:
                  rowNumber: { type: integer }
                  status: { type: string, enum: [OK, ERROR, APPLIED] }
                  error: { type: string, nullable: true }
                  rawJson: { type: string }
            total: { type: integer }
            page: { type: integer }
            pageSize: { type: integer }
        requestId: { type: string }
    ImportCommitResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [appliedRows, status]
          properties:
            appliedRows: { type: integer }
            status: { type: string, enum: [COMMITTED] }
        requestId: { type: string }
    MappingActionResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [mappingId, state, version, mappingVersion]
          properties:
            mappingId: { type: string }
            state: { type: string, enum: [MANUAL_CONFIRMED, DISABLED] }
            version: { type: string, description: 新 rowversion 字符串 }
            mappingVersion: { type: integer, description: 新 int 乐观版本 }
        requestId: { type: string }
    DisableMappingRequest:
      type: object
      required: [expectedVersion, reason]
      properties:
        expectedVersion: { type: string }
        reason: { type: string, maxLength: 500 }
    TracePolicyResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [productId, traceClassification, classificationReason, version, affectedNonTerminalTasks]
          properties:
            productId: { type: string }
            traceClassification: { type: string, enum: [REQUIRED, EXEMPT] }
            classificationReason: { type: string }
            affectedNonTerminalTasks: { type: integer, description: 关联非终态任务数；M1 恒 0（无任务产生路径），失效接线 M2 }
            version: { type: string, description: 新商品 rowversion 字符串 }
        requestId: { type: string }
    ProductMappingView:
      type: object
      required: [mappingId, state, isLocked, mappingVersion, version, platformDrugId]
      properties:
        mappingId: { type: string }
        state: { type: string }
        isLocked: { type: boolean }
        mappingVersion: { type: integer }
        version: { type: string }
        platformDrugId: { type: string }
        approvalNoNorm: { type: string, nullable: true }
        manufacturerIdentity: { type: string, nullable: true }
        dosageForm: { type: string, nullable: true }
        strength: { type: string, nullable: true }
        packageSpec: { type: string, nullable: true }
    ProductsResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [records, total, page, pageSize]
          properties:
            records:
              type: array
              items:
                type: object
                required: [productId, spid, name, traceClassification, version, mappingState]
                properties:
                  productId: { type: string }
                  spid: { type: string }
                  displayCode: { type: string, nullable: true }
                  barcode: { type: string, nullable: true }
                  name: { type: string }
                  approvalNoRaw: { type: string, nullable: true }
                  approvalNoNorm: { type: string, nullable: true }
                  manufacturerRaw: { type: string, nullable: true }
                  manufacturerIdentity: { type: string, nullable: true }
                  dosageForm: { type: string, nullable: true }
                  strength: { type: string, nullable: true }
                  packageSpec: { type: string, nullable: true }
                  unit: { type: string, nullable: true }
                  traceClassification: { type: string, enum: [UNKNOWN, REQUIRED, EXEMPT] }
                  classificationReason: { type: string, nullable: true }
                  updatedAt: { type: string, format: date-time }
                  version: { type: string }
                  mappingState: { type: string, description: 7 态或 NONE（无映射） }
                  mapping: { nullable: true, allOf: [{ $ref: '#/components/schemas/ProductMappingView' }] }
            total: { type: integer }
            page: { type: integer }
            pageSize: { type: integer }
        requestId: { type: string }
    SupplierMappingView:
      type: object
      required: [mappingId, state, isLocked, mappingVersion, version, refEntId, entId, platformName]
      properties:
        mappingId: { type: string }
        state: { type: string }
        isLocked: { type: boolean }
        mappingVersion: { type: integer }
        version: { type: string }
        refEntId: { type: string }
        entId: { type: string }
        platformName: { type: string }
        creditCode: { type: string, nullable: true }
        auditStatus: { type: string, nullable: true }
        role: { type: string, nullable: true }
    SuppliersResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [records, total, page, pageSize]
          properties:
            records:
              type: array
              items:
                type: object
                required: [supplierId, dwbh, name, version, mappingState]
                properties:
                  supplierId: { type: string }
                  dwbh: { type: string }
                  name: { type: string }
                  creditCode: { type: string, nullable: true }
                  role: { type: string, nullable: true }
                  version: { type: string }
                  mappingState: { type: string }
                  mapping: { nullable: true, allOf: [{ $ref: '#/components/schemas/SupplierMappingView' }] }
            total: { type: integer }
            page: { type: integer }
            pageSize: { type: integer }
        requestId: { type: string }
    JobStatusResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [jobId, jobKind, intentKey, jobState, attemptCount, fencingToken]
          properties:
            jobId: { type: string }
            jobKind: { type: string }
            intentKey: { type: string }
            jobState: { type: string, enum: [PENDING, RUNNING, DONE, FAILED, CANCELLED] }
            attemptCount: { type: integer }
            nextRunAtUtc: { type: string, format: date-time }
            lastError: { type: string, nullable: true }
            leaseOwner: { type: string, nullable: true }
            fencingToken: { type: integer, format: int64 }
        requestId: { type: string }
```

**(d) GET /imports 合并进既有 `/imports:` 路径项**：在 `  /imports:` 行之后、`    post:` 行之前插入（同一路径项下新增 get 方法，勿新建重复 `/imports:` 键）：

```yaml
    get:
      x-phase: M1
      description: 按 (fileKind,fileHash) 定位批次预览（权限 master:match）。上传暂存完成后返回批次摘要与版本。
      parameters:
        - { name: fileKind, in: query, required: true, schema: { type: string, enum: [PRODUCT, SUPPLIER, PLATFORM_DRUG, ENTERPRISE] } }
        - { name: fileHash, in: query, required: true, schema: { type: string } }
      responses:
        '200': { description: 批次摘要, content: { application/json: { schema: { $ref: '#/components/schemas/ImportPreviewResponse' } } } }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
        '422': { $ref: '#/components/responses/Unprocessable' }
```

**(e) 新增 4 个路径**：在 `  /upstream-bills:` 行之前插入：

```yaml
  /imports/{id}:
    get:
      x-phase: M1
      description: 导入批次详情+逐行预览（权限 master:match，MD-03）。ROW_NUMBER 分页；行含 OK/ERROR/APPLIED 与错误原因。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/Page' }
        - { $ref: '#/components/parameters/PageSize' }
      responses:
        '200': { description: 批次与逐行预览, content: { application/json: { schema: { $ref: '#/components/schemas/ImportDetailResponse' } } } }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
  /product-mappings/{id}/disable:
    post:
      x-phase: M1
      description: 解除商品映射（权限 master:match）。expectedVersion+reason；存在非终态上传任务阻断（409 TASKS_IN_FLIGHT）；解除后 is_locked=0，可重新确认。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/IdempotencyKey' }
        - { $ref: '#/components/parameters/CsrfToken' }
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/DisableMappingRequest' } } }
      responses:
        '200': { description: 映射已解除, content: { application/json: { schema: { $ref: '#/components/schemas/MappingActionResponse' } } } }
        '400': { $ref: '#/components/responses/BadRequest' }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
        '409': { description: 版本冲突/在途任务, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
        '422': { $ref: '#/components/responses/Unprocessable' }
  /supplier-mappings/{id}/disable:
    post:
      x-phase: M1
      description: 解除供应商映射（权限 master:match）。org 下非终态采购任务阻断（409 TASKS_IN_FLIGHT，M1 保守守卫；M2 细化到 dwbh 级）。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/IdempotencyKey' }
        - { $ref: '#/components/parameters/CsrfToken' }
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/DisableMappingRequest' } } }
      responses:
        '200': { description: 映射已解除, content: { application/json: { schema: { $ref: '#/components/schemas/MappingActionResponse' } } } }
        '400': { $ref: '#/components/responses/BadRequest' }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
        '409': { description: 版本冲突/在途任务, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
        '422': { $ref: '#/components/responses/Unprocessable' }
  /settings:
    get:
      x-phase: M1
      description: 设置列表（权限 system:manage，UI-10）。密钥键仅返回 isSecret/hasSecretValue，绝不回显明文（NFR-05）；含未持久化键默认值（id=0, version="0"）。响应 camelCase。
      responses:
        '200': { description: 设置列表, content: { application/json: { schema: { $ref: '#/components/schemas/SettingsListResponse' } } } }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
```

**(f) 既有 M1 路径响应引用替换**（按唯一 description 锚点替换 '200'/'202' 行）：

| 路径·方法 | 旧（唯一锚点） | 新 |
| --- | --- | --- |
| /products GET | `'200': { description: 分页商品, content: { application/json: { schema: { $ref: '#/components/schemas/PageMeta' } } } }` | `'200': { description: 分页商品, content: { application/json: { schema: { $ref: '#/components/schemas/ProductsResponse' } } } }` |
| /suppliers GET | `'200': { description: 分页供应商, content: { application/json: { schema: { $ref: '#/components/schemas/PageMeta' } } } }` | `'200': { description: 分页供应商, content: { application/json: { schema: { $ref: '#/components/schemas/SuppliersResponse' } } } }` |
| /imports POST | `'202': { description: 已入队暂存校验作业, content: { application/json: { schema: { $ref: '#/components/schemas/JobAccepted' } } } }` | `'202': { description: 已入队暂存校验作业, content: { application/json: { schema: { $ref: '#/components/schemas/ImportAccepted' } } } }` |
| /imports/{id}/commit POST | `'200': { description: 提交结果（成功/失败行数与失败原因）, content: { application/json: { schema: { $ref: '#/components/schemas/EmptyDataResponse' } } } }` | `'200': { description: 提交结果（应用行数）, content: { application/json: { schema: { $ref: '#/components/schemas/ImportCommitResponse' } } } }` |
| /product-mappings/{id}/confirm POST | `'200': { description: 映射已确认（MANUAL_CONFIRMED，锁定）, content: { application/json: { schema: { $ref: '#/components/schemas/EmptyDataResponse' } } } }` | `'200': { description: 映射已确认（MANUAL_CONFIRMED，锁定）, content: { application/json: { schema: { $ref: '#/components/schemas/MappingActionResponse' } } } }` |
| /supplier-mappings/{id}/confirm POST | `'200': { description: 映射已确认, content: { application/json: { schema: { $ref: '#/components/schemas/EmptyDataResponse' } } } }` | `'200': { description: 映射已确认, content: { application/json: { schema: { $ref: '#/components/schemas/MappingActionResponse' } } } }` |
| /products/{id}/trace-policy POST | `'200': { description: 分类已变更（含受影响任务提示）, content: { application/json: { schema: { $ref: '#/components/schemas/EmptyDataResponse' } } } }` | `'200': { description: 分类已变更（含受影响任务提示）, content: { application/json: { schema: { $ref: '#/components/schemas/TracePolicyResponse' } } } }` |
| /jobs/{id} GET | `'200': { description: 作业详情, content: { application/json: { schema: { type: object, properties: { data: { type: object }, requestId: { type: string } } } } } }` | `'200': { description: 作业详情, content: { application/json: { schema: { $ref: '#/components/schemas/JobStatusResponse' } } } }` |
| /settings/connections/test POST | `'200': { description: 连接健康结果（不回显凭据/连接串）, content: { application/json: { schema: { $ref: '#/components/schemas/EmptyDataResponse' } } } }` | `'200': { description: 连接健康结果（不回显凭据/连接串）, content: { application/json: { schema: { $ref: '#/components/schemas/ConnectionTestResponse' } } } }` |
| /settings/{key} PUT | `'200': { description: 设置已更新（敏感值不回显）, content: { application/json: { schema: { $ref: '#/components/schemas/EmptyDataResponse' } } } }` | `'200': { description: 设置已更新（敏感值不回显）, content: { application/json: { schema: { $ref: '#/components/schemas/SettingUpdateResponse' } } } }` |

注：/audit-logs GET 仍用 PageMeta（records 为审计行匿名对象，形状已在 description 描述），保持不变。

- [ ] **Step 2: data-dictionary.md 追加 0007 增量**

在 `## 环境隔离（Live/Mock）` 段之前插入新段：

```markdown
## 0007 迁移增量（M1，`0007_m1_source_master.sql`）

不修改 0001–0006 任何字节（MigrationRunner 校验摘要）；0007 以 ALTER/CREATE 增量演进：

| 对象 | 变更 | 用途/约束 |
| --- | --- | --- |
| source_line.code_format | nvarchar(16)→nvarchar(32) NOT NULL DEFAULT N'EMPTY' | CodeFormat 持久化 token（EMPTY/S8P20/UNSUP），M0 携带项 1 |
| trace_code.format | nvarchar(16)→nvarchar(32) NOT NULL | 同上（无默认约束，直接 ALTER COLUMN） |
| source_snapshot.stabilize_rounds | int NOT NULL DEFAULT 0 | 稳定期轮次计数（SYNC-02） |
| source_snapshot.last_round_at | datetime2(3) NULL | 上一轮观测时间（轮间隔≥5s 判定） |
| idempotency_record | UQ_idempotency_key→UQ_idempotency_user_key(idem_key, user_id) | 幂等键用户作用域（不同用户同键独立） |
| product_mapping | UX_pmap_pair UNIQUE(product_id, platform_drug_id) | 映射对唯一（M1 每商品至多一条映射行，防多行竞态） |
| supplier_mapping | UX_smap_pair UNIQUE(supplier_id, enterprise_version_id) | 同上 |
| import_batch.staged_content | nvarchar(max) NULL | 暂存 CSV 原文，供 import-stage 作业重解析（MD-03） |

CodeFormat token 映射由 `TraceFlow.Domain.Codes.CodeFormatStorage` 统一（禁 enum.ToString 落库）；rowversion 经 `TraceFlow.Domain.Versioning.RowVer.ToHex/FromHex`（16 位大写 hex）。
```

并把文件第 3 行 `…0006_upload.sql`（HEAD 73f3d6f）。` 更新为 `…0006_upload.sql` + M1 增量 `0007_m1_source_master.sql`。`（HEAD 以实际 M1 末提交为准）。

- [ ] **Step 3: adapter-facts.md 状态更新**

把标题 `# KSOA/TOP 适配事实矩阵（M0 初版）` 改为 `# KSOA/TOP 适配事实矩阵（M0 初版 + M1 更新）`；在表格之后追加段：

```markdown
## M1 落地状态（fixture-only，真实通道仍禁用）

- **来源同步通道**：M1 实现 `ISourceAdapter` 抽象 + `FixtureKsoaAdapter`（读 `docs/contracts/fixtures/ksoa/*.json`）+ `RealKsoaAdapter`（抛 `AdapterNotCertifiedException`）。`AdapterFactory` 按配置路由：fixture 模式可用，real 模式拒绝。LIVE 环境 `SourceSyncService`/`SyncScheduler` 拒绝同步（`Live_env_refuses_sync_in_m1`、`Live_env_tick_enqueues_nothing`）。**真实 KSOA 只读连接与键唯一性取证（G-ERP）仍未做**，上表所有"待取证"项不变。
- **主数据积累**：M1 平台药品/企业版本仅经 CSV 导入（`ImportService`，source_kind=MANUAL/IMPORT），非平台实时下载（G-TOP 未取证，M2 实现 listupout/药品下载）。
- **匹配/分类/设置/审计**：M1 已实现并集成测试（见 matrix.md M1 行）；均运行于 SQL 2019 授权降级，2008 R2 待补验。
- **POS 采码/退货/TOP 合同**：仍待取证，对应生产通道禁用不变。
```

- [ ] **Step 4: source-mapping.md 追加 M1 注记**

在文件末尾追加：

```markdown
## 3. M1 落地注记

- 归一化内部契约五对象已由 `SourceSyncService` + `FixtureKsoaAdapter` 落地（fixture 形状）：SourceDocument→source_snapshot、SourceLine→source_line（raw+normalized 双存、line_hash、event_kind 拆分）；Product/Supplier 档案经 CSV 导入落地（spid/dwbh 稳定键）。
- 候选组合键 `(lshh,plh)`/`(djbh,dj_sn)` 在 fixture 中按 SRC-02 验证：plh 空→ingest_exception（不造行号）、跨日复用/跨表键碰撞→INVALID 新版本（旧快照保留）。**真实 KSOA 列名/类型/唯一性仍以 G-ERP 取证为准，上表候选状态不变。**
- 业务时间经 `BusinessTimeParser.Shanghai`（Asia/Shanghai→UTC）；效期精度 `ExpiryParser.ParsePrecision`（DAY/MONTH/UNKNOWN）；数量 `Qty` decimal(18,6) 越界→issue（不静默舍入）。
```

- [ ] **Step 5: 新建 ksoa-adapter-contract.md**

`docs/contracts/ksoa-adapter-contract.md`：

```markdown
# KSOA 适配器合同（M1 定稿：fixture 形状 + 取证清单）

状态：**fixture-only**。真实 KSOA 只读适配（G-ERP）未取证，`RealKsoaAdapter` 抛 `AdapterNotCertifiedException`，LIVE 同步禁用。本合同固定 M1 内部接口与 fixture 形状，取证后仅替换适配器实现，不改内部契约。

## 1. 内部接口（TraceFlow.Application.Sources）

- `ISourceAdapter.FetchAsync(string entity, DateTime startUtc, DateTime endUtc, CancellationToken ct) → Task<IReadOnlyList<SourceDocumentDto>>`
- `SourceDocumentDto`：SourceDocumentKey, DocumentType, BusinessDateTimeUtc(DateTime?), SourceTimeRaw, SourceTimeZone, Lines(IReadOnlyList<SourceLineDto>), Issues(IReadOnlyList<SourceKeyIssue>)
- `SourceLineDto`：SourceLineKey, ProductSourceId, QuantityOriginal(decimal), UnitOriginal, BatchRaw, ExpiryRaw, TraceCodeRaw, EventKind
- `SourceKeyIssue`：Kind, Detail, RawJson（键缺陷留证，不造行）
- entity 有界枚举：`sale`/`purchase-in`；未知 entity 抛 ArgumentException。

## 2. Fixture 形状（docs/contracts/fixtures/ksoa/*.json）

| fixture | 覆盖 |
| --- | --- |
| sale-normal.json | 正常销售：lshh 头键 + plh 行键 + 逐行 TraceCode |
| sale-missing-plh.json | plh 空→SourceKeyIssue（不造行号） |
| sale-mixed-return.json | 正负 shl 混单→SALE/CONSUMER_RETURN 分事件不抵消 |
| purchase-in.json | 入库：djbh 头键 + dj_sn 行键 + pihao 批号 + PURCHASE_IN 事件 |

fixture 为 **Mock 形状**，非真实 KSOA schema 事实；列名/类型以 G-ERP 取证为准。

## 3. G-ERP 取证清单（取证前真实通道禁用）

1. 脱敏 schema：salemast/salemxls/jh_rkhz/jh_rkmx 列名与类型（尤其 lshh/plh/djbh/dj_sn/spid/shl/TraceCode/pihao/rq/ontime）。
2. 键唯一性查询：`(lshh,plh)`/`(djbh,dj_sn)` 的 NULL/重复/跨日复用/跨表碰撞实测。
3. 稳定提交约定：is_fpwc/yishj 类型含义（不作写回标记、不假设递增）。
4. 主档来源表（SRC-03，不猜表名）。
5. jlgg/sxrq/dw 所属对象与换算方向/精度（UnitRatio 证据）。
6. 时间样例：rq/ontime 归属与时区（Asia/Shanghai 解析验证）。
7. 只读账号权限验证（AT-001）：确认适配器账号仅 SELECT，无写权限。
8. POS 逐盒扫码落库样例（G-POS，SCOPE-09 冲突裁决）。

取证完成后：更新 adapter-facts.md 证据列与日期、固定 source-mapping.md 候选键、实现 RealKsoaAdapter、解除 LIVE 同步门禁。
```

- [ ] **Step 6: matrix.md M1 证据回写**

标题改为 `# TraceFlow 需求追踪矩阵（M0–M1）`；基线行改为：

```
基线：PRD V1.0（`docs/PRD-TraceFlow-单门店-V1.0.md`）+ 验收清单（AT-*）+ 开发交接说明。代码基线：分支 `m1-source-master`（自 m0-foundation），HEAD 以 M1 末提交为准（Task 1–16）。
```

"验证运行"行替换为本次 Step 9 全量回归的真实输出数字（照抄，不估算）：Release 构建 0 警告 0 错误；单元测试 N/N；集成测试默认 collation N/N + BIN2 变体 N/N（2019 降级）；前端 tsc+vite 0 错误。门禁证据口径段保持不变（2008 R2 待补验）。

**按首列需求 ID 整行替换以下各行**（其余行不动；测试名均为 Task 1–15 实际定义的测试）：

| 需求 ID | 实现模块/物理对象 | 测试 | 门禁证据 | 阶段/状态 |
| --- | --- | --- | --- | --- |
| SCOPE-02 | adapter-facts.md + ksoa-adapter-contract.md；ISourceAdapter + FixtureKsoaAdapter + RealKsoaAdapter（抛 AdapterNotCertifiedException）+ AdapterFactory 路由 | FixtureKsoaAdapterTests.Real_adapter_throws_not_certified_and_factory_routes_modes；SourceSyncTests.Live_env_refuses_sync_in_m1 | G-ERP 未取证（fixture-only）；SQL 2019 集成（授权降级；2008 R2 待补验） | M1 fixture 通道完成；真实 ERP 同步禁用 |
| SCOPE-05 | Web/src/pages 六页（Login/Dashboard/Products/Suppliers/Imports/Settings），无独立扫码页面 | npm run build（tsc 0 错误） | — | M1 完成（六页）；十页闭环 M4 |
| SCOPE-08 | MatchingService/SupplierMatchingService + MappingEndpoints（confirm/disable）+ 0007 UX_pmap_pair/UX_smap_pair + 双向有效唯一索引 | MatchingServiceTests.Full_match_bidirectional_unique_auto_confirms、Bidirectional_unique_blocks_identical_twins；MappingConfirmTests.Confirm_active_unique_race_rejected；MasterConstraintTests（2 映射唯一用例） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（匹配+确认/解除）；入库分配 M2 |
| SCOPE-10 | SourceSyncService 版本不可变 + SourceStabilizationService SOURCE_CHANGED 检测 | SourceStabilizationTests.Complete_doc_changed_marks_source_changed_and_task_payload_immutable；SourceSyncTests.Cross_day_reuse_across_runs_invalidates_new_version_old_intact；MasterConstraintTests.Snapshot_version_unique_and_history_appendable | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（变化检测） |
| DEF-06 | source_line CK_line_event + FixtureKsoaAdapter 正负 shl 分事件（不抵消）+ SourceSync 落库 | FixtureKsoaAdapterTests.Mixed_sign_receipt_splits_events_without_netting；SourceSyncTests.Sale_ingest_per_line_codes_utc_time_checkpoint_and_audit；AT-042（生产上报待 M3） | G-ERP/G-RETURN 未取证；SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（fixture 事件拆分）；生产上报 M3 |
| SRC-01 | 连接串仅部署配置；DpapiProtector；SettingsService erp.connection 密钥只写；FixtureKsoaAdapter（真实只读未取证） | SettingsServiceTests.Secret_is_write_only_never_echoed_and_audit_has_no_plaintext；DpapiProtectorTests（2 用例）；AT-001（真实 ERP 只读账号取证待做） | G-ERP 未取证 | M1 完成（凭据管理）；只读账号取证待做 |
| SRC-02 | SourceSyncService 键校验（plh 空→INVALID 不造行；跨类型/跨日复用→新版本旧保留）+ 0002 UQ_snapshot_key_version/UQ_line_key | SourceSyncTests.Missing_plh_doc_is_invalid_with_raw_exception_no_lines、Same_doc_key_across_document_types_is_key_reuse、Cross_day_reuse_across_runs_invalidates_new_version_old_intact；FixtureKsoaAdapterTests.Missing_plh_becomes_issue_with_raw_preserved_not_fabricated_line、Duplicate_line_key_and_orphan_line_become_issues、Same_doc_key_across_business_days_flags_cross_day_reuse | SQL 2019 集成（授权降级；2008 R2 待补验）；G-ERP 真实键唯一性取证待做 | M1 fixture 验证完成；真实取证待做 |
| SRC-03 | ImportService（PRODUCT/SUPPLIER 主档导入，不猜 KSOA 表名）+ product UNKNOWN 阻断 + trace-policy | ImportServiceTests.Commit_product_update_never_touches_classification、Commit_supplier_and_platform_drug_upsert；ClassificationTests.Trace_policy_updates_and_audits | SQL 2019 集成（授权降级；2008 R2 待补验）；G-ERP 主档来源取证待做 | M1 导入通道完成；真实主档来源取证待做 |
| SRC-04 | TraceCodeNormalizer + CodeFormatStorage 持久化 token（0007 code_format nvarchar(32)）+ source_line raw+normalized 双存 | TraceCodeNormalizerTests（7 用例）；CodeFormatStorageTests（4 用例）；SourceSyncTests.Raw_codes_preserved_verbatim_leading_zeros_and_control_chars | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（接入双存） |
| SRC-05 | Qty/UnitRatio + ExpiryParser/BusinessTimeParser/LineHash + SourceSync 接入（越界→issue 不静默舍入） | QtyTests（8 用例）、UnitRatioTests（5 用例）；SourceValueObjectTests.Expiry_precision、Business_time_shanghai_to_utc、Line_hash_deterministic_and_sensitive；SourceSyncTests.Qty_out_of_range_becomes_issue_raw_preserved_line_not_silently_rounded | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（接入换算；单位换算应用 M2） |
| SYNC-01 | SourceSyncService 重叠窗口 + content_hash 摘要去重 + sync_checkpoint 持久检查点 | SourceSyncTests.Second_identical_run_dedups_by_hash_no_new_version；SourceValueObjectTests.Incremental_window_overlaps_checkpoint、Document_hash_order_insensitive_via_sorted_inputs | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（fixture 通道） |
| SYNC-02 | SourceStabilizationService（稳定期 10 秒/轮间隔≥5 秒/两轮摘要一致）+ 0007 stabilize_rounds/last_round_at | SourceStabilizationTests.Two_consistent_rounds_with_interval_promote_complete、Zero_line_doc_never_completes、Late_lines_new_version_resolves_empty_detail_and_completes | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| SYNC-04 | SyncScheduler（轮询 30 秒/回扫 72h/每日 30 天）+ SourceSyncJobHandler 检查点补拉不截断 | SyncSchedulerTests.Tick_enqueues_periodic_daily_and_match_intents_idempotent、Next_poll_bucket_creates_new_periodic_intents；SourceSyncJobHandlerTests.First_periodic_run_uses_daily_rescan_window、Stale_checkpoint_gap_never_truncated；SourceValueObjectTests.Daily_window_30_days、First_run_uses_daily_lookback、Gap_backfill_never_truncates | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（fixture 通道） |
| SYNC-05 | SourceStabilizationService SOURCE_CHANGED（旧快照保留+任务 payload 不可变） | SourceStabilizationTests.Complete_doc_changed_marks_source_changed_and_task_payload_immutable（AT-057） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（检测+保留）；依赖暂停接线 M2/M3 |
| MD-01 | ClassificationService trace-policy（REQUIRED/EXEMPT+reason+版本并发）+ ProductsPage ClassifyDialog | ClassificationTests.Trace_policy_updates_and_audits、Trace_policy_validation_and_concurrency、Trace_policy_requires_permission；ImportServiceTests.Commit_product_update_never_touches_classification | SQL 2019 集成（授权降级；2008 R2 待补验）；npm run build | M1 完成 |
| MD-02 | ImportService PLATFORM_DRUG/ENTERPRISE 导入积累（source_kind=IMPORT）；真实平台下载 M2 | ImportServiceTests.Commit_supplier_and_platform_drug_upsert、Commit_enterprise_insert_only | SQL 2019 集成（授权降级；2008 R2 待补验）；G-TOP 未取证 | M1 导入积累完成；平台下载 M2 |
| MD-03 | ImportService 暂存/预览/提交（20MB/10000 行上限）+ ImportWizard + 0007 import_batch.staged_content | ImportServiceTests（12 用例：Stage_product_creates_batch_then_handler_writes_rows、Stage_same_hash_idempotent、Stage_hash_mismatch_rejected、Stage_invalid_base64_rejected、Stage_oversize_rejected、Stage_too_many_rows_rejected、Stage_header_only_batch_becomes_failed、Commit_product_update_never_touches_classification、Commit_supplier_and_platform_drug_upsert、Commit_enterprise_insert_only、Commit_version_conflict_and_double_commit、Commit_enqueues_match_job_and_audit） | SQL 2019 集成（授权降级；2008 R2 待补验）；npm run build | M1 完成 |
| MATCH-01 | DrugIdentityMatcher 五字段（Trim+Ordinal，缺字段不通配）+ MatchingService AUTO_CONFIRMED | DrugIdentityMatcherTests.All_five_equal_is_full_match、Trim_before_compare、Missing_field_never_wildcards、Empty_string_counts_as_missing_not_equal；MatchingServiceTests.Full_match_bidirectional_unique_auto_confirms、Missing_field_never_wildcards（AT-012/014） | 单元；SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| MATCH-02 | ApprovalNoNormalizer（raw+norm 双存）；条码仅辅助、单字段不自动绑定 | DrugIdentityMatcherTests.Same_name_different_package_conflicts_not_full、Zero_overlap_has_no_equal_no_conflict；ApprovalNoNormalizerTests.Normalize_keeps_letters_digits_version；MatchingServiceTests.Field_conflict_marks_conflict（AT-013） | 单元；SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| MATCH-03 | MatchingService 双向唯一 + drug_prefix_index 重建/冲突 + 0007 UX_pmap_pair | MatchingServiceTests.Bidirectional_unique_blocks_identical_twins、Prefix_index_rebuild_and_conflict；MappingConfirmTests.Confirm_active_unique_race_rejected；MasterConstraintTests（2 用例）（AT-015/018） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| MATCH-04 | MappingStates 7 态流转 + confirm/disable + is_locked + 重验 | MatchingServiceTests.Locked_mapping_untouched、Active_mapping_revalidation_detects_platform_change；MappingConfirmTests.Confirm_candidate_sets_manual_locked、Disable_active_then_reconfirm（AT-016/017） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| MATCH-05 | MappingEndpoints confirm（五字段差异/targetId 校验/强冲突拒绝）+ ProductsPage ConfirmDialog | MappingConfirmTests.Confirm_strong_conflict_rejected、Confirm_wrong_target_rejected_no_rebind、Confirm_version_conflict_and_validation_and_404（AT-015）；npm run build | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| ENT-01 | SupplierMatchingService 信用代码精确+审核状态核对+名称仅候选+空通过集不自动 | SupplierMatchingTests.Credit_exact_audit_pass_unique_auto_confirms、Multiple_credit_matches_conflict、Name_only_is_candidate_never_auto、Default_empty_pass_set_never_auto_confirms（AT-019） | SQL 2019 集成（授权降级；2008 R2 待补验）；G-TOP 企业库真实取证待做 | M1 完成（导入企业库匹配） |
| ENT-02 | platform_enterprise_version 名称版本化（改名映射保留）+ 角色独立存储 | SupplierMatchingTests.Rename_new_version_mapping_stays、Roles_are_independent（AT-020/021） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| ENT-03 | 代码无 saveent 调用能力；无默认供应商自动确认路径 | SupplierMatchingTests.Default_empty_pass_set_never_auto_confirms、Locked_supplier_mapping_untouched（AT-019） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（负约束+匹配） |
| JOB-05 | SettingsService sync.paused + SyncScheduler 暂停跳过来源同步（保留匹配）；LIVE 环境不入队 | SettingsServiceTests.Import_bounds_defaults_and_pause_toggle；SyncSchedulerTests.Paused_channel_skips_source_sync_but_keeps_match、Live_env_tick_enqueues_nothing | SQL 2019 集成（授权降级；2008 R2 待补验）；G-TOP/G-DEPLOY 未取证 | M1 完成（来源通道暂停）；平台通道暂停 M3 |
| UI-02 | Web/src/pages/ProductsPage.tsx（筛选/分页/分类/确认/解除对话框，顶层组件） | ClassificationTests.Products_list_paginates_and_filters、Products_list_includes_active_mapping_identity；npm run build | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成；深链/全待办 M4 |
| UI-03 | Web/src/pages/SuppliersPage.tsx（信用代码精确对比/名称仅候选/角色独立展示） | ClassificationTests.Suppliers_list_filters_and_mapping；npm run build | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| UI-10 | Web/src/pages/SettingsPage.tsx + SettingsEndpoints（密钥只写/连接测试/渠道暂停） | SettingsServiceTests（9 用例）；M1ApiGateTests.Viewer_lacks_settings_and_sync_and_audit_permissions；npm run build | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| UI-11 | 映射确认版本 409（ConfirmDialog/DisableDialog expectedVersion）；补配逐行差异 M2 | MappingConfirmTests.Confirm_version_conflict_and_validation_and_404；AT-067（分配重配待 M2） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 部分完成（映射版本确认）；补配 UI M2/M4 |
| UI-13 | ImportWizard 批量预览（ERROR 行红显/批次版本提交）；导出 M4 | ImportServiceTests.Stage_product_creates_batch_then_handler_writes_rows；npm run build；AT-074（导出待 M4） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 部分完成（导入预览）；导出 M4 |
| API-01 | ApiErrors 统一错误 + M1 端点 data/requestId 包络（记录型响应 Results.Json Web camelCase）+ 外部 ID/rowversion 字符串 | ApiGateTests（7 用例）；M1ApiGateTests.Anonymous_gets_401_on_all_m1_endpoints、Mapping_endpoints_gated、Audit_logs_paginated_and_filterable、Manual_sync_job_accepted_and_validated | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（已实现端点）；全量契约 M2+ |
| API-02 | IdempotencyExecutor + 0007 用户作用域幂等键 + 查询串参与请求哈希 | IdempotencyHardeningTests.Same_key_different_users_are_independent、Query_string_participates_in_hash、Concurrent_same_key_loser_rolls_back_and_replays_winner、Concurrent_same_key_different_hash_loser_gets_conflict_and_rolls_back；IdempotencyStoreTests（2 用例）；ApiGateTests.Idempotent_user_create_same_key_same_body_replays_different_body_409（AT-066） | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（加固） |
| AUD-01 | AuditLog + 同步/导入/分类/映射/设置全留痕 + GET /audit-logs 分页过滤 | PermissionAuditTests.Audit_append_stores_before_after_and_actor；SourceSyncTests.Sale_ingest_per_line_codes_utc_time_checkpoint_and_audit；ClassificationTests.Trace_policy_updates_and_audits；SettingsServiceTests.Update_new_int_setting_creates_row_and_audit；M1ApiGateTests.Audit_logs_paginated_and_filterable | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（已实现动作）；分配/导出审计 M2/M4 |
| DB-01 | db/migrations/0001–0007（外部 ID/码 nvarchar+BIN2；0007 code_format 拓宽/token 化） | Sql2008CompatibilityTests、MasterConstraintTests、CodeConstraintTests、Migration0007Tests.Format_columns_widened_and_default_token、Mapping_pair_unique_indexes_exist | SQL 2019 集成（授权降级；2008 R2 待补验）；默认 collation + BIN2 双变体运行 | M1 完成 |
| DB-02 | db/migrations（datetime2(3)/rowversion/decimal(18,6)）；RowVer.ToHex/FromHex；BusinessTimeParser.Shanghai | QtyTests、UnitRatioTests；RowVerTests.Roundtrip_16_upper_hex、Invalid_hex_throws_FormatException；SourceValueObjectTests.Business_time_shanghai_to_utc、Business_time_date_only_midnight、Invalid_time_returns_null；Sql2008CompatibilityTests | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成 |
| DB-03 | db/migrations/*.sql（含 0007）+ 内嵌 SQL（ROW_NUMBER 分页，无高版本语法；0007 入白名单） | Sql2008CompatibilityTests.No_high_version_syntax_in_any_migration_or_embedded_sql、No_high_version_syntax_in_migration_scripts_full_constraint_list、Row_number_pagination_works_on_2008r2；Migration0007Tests（6 用例）；AT-070（2008 R2 实 instance 待补验） | 静态语法门禁通过；SQL 2019 集成（授权降级；2008 R2 待补验）；双 collation 变体 | M1 完成（语法层）；2008 R2 运行时门禁未满足 |
| NFR-02 | Web/src（React 18+TS：顶层对话框组件、轮询 active 守卫清理、分页 50/页、交互在事件处理） | npm run build（tsc -b 0 错误 + vite 产物） | — | M1 完成（六页）；其余页面 M4 |
| NFR-04 | FixtureKsoaAdapter 分页拉取（≤500 头/页）；真实 ERP 慢查询记录未取证 | FixtureKsoaAdapterTests（8 用例）；AT-075（性能基准待 M4） | G-ERP 未取证 | M1 部分完成（fixture 分页）；真实 ERP 取证待做 |
| NFR-05 | DpapiProtector + app_setting 密文/明文互斥 + SettingsService 密钥只写/连接测试不回显凭据 | DpapiProtectorTests（2 用例）；SettingsServiceTests.Secret_is_write_only_never_echoed_and_audit_has_no_plaintext、Connection_test_with_stored_local_connection_succeeds、Connection_test_rejects_non_erp_config_and_missing_id | SQL 2019 集成（授权降级；2008 R2 待补验） | M1 完成（设置层） |
| G-ERP | adapter-facts.md + source-mapping.md + ksoa-adapter-contract.md + fixtures/ksoa/*（fixture 通道已实现，候选列未取证） | FixtureKsoaAdapterTests（8 用例）；SourceSyncTests.Live_env_refuses_sync_in_m1 | 未取证（fixture-only） | 真实 ERP 同步禁用；取证清单见 ksoa-adapter-contract.md §3 |

覆盖统计段更新：需求行合计仍 **114**；追加一句"M1 已实现行（来源同步 fixture 通道/稳定期/导入/分类/匹配/确认解除/设置/审计/四页 UI）均给出实际测试名；G-ERP 仍 fixture-only，真实通道禁用；2008 R2 运行时门禁（AT-070）未满足。"

- [ ] **Step 7: 新建 M1-gate-report.md（真实输出，诚实口径）**

`docs/delivery/M1-gate-report.md`。结构仿 M0 报告；**所有命令输出必须照抄 Step 9 真实运行结果，禁止估算/伪造**。静态部分全文如下，输出段以 `（照抄真实输出）` 标记处粘贴实际结果：

```markdown
# M1 门禁报告（TraceFlow 来源同步与主数据匹配）

日期：（运行当日）。分支：`m1-source-master`，代码基线 HEAD（git rev-parse --short HEAD 实际值）（Task 1–16）。
配套：docs/contracts/openapi.yaml（M1 回写）、data-dictionary.md（0007）、adapter-facts.md、source-mapping.md、ksoa-adapter-contract.md、docs/traceability/matrix.md（M0–M1）。

## 结论（不标记 M1 全门禁通过）

**代码与测试完成（2019 降级运行全绿，双 collation 变体），2008 R2 实 instance 门禁与 G-ERP 取证门禁未满足。**

> **⚠ 降级声明（用户授权）：2008 R2 真实实例验证未完成（用户授权开发期降级至 SQL Server 2019；交付前必须真实 2008 R2 补验全部集成测试，AT-070）。**
> 本报告不声称 2008 R2 兼容性已验证，不伪造 10.50.x 版本记录。
> **G-ERP fixture-only：M1 来源同步仅经 FixtureKsoaAdapter（本地 JSON fixture）验证；真实 KSOA 只读连接、键唯一性、主档来源取证未做，真实 ERP 同步通道禁用（RealKsoaAdapter 抛 AdapterNotCertifiedException，LIVE 环境拒绝同步）。**

## ① 测试实例真实版本（SELECT @@VERSION 实际输出）

命令：`sqlcmd -S localhost -E -C -Q "SET NOCOUNT ON; SELECT @@VERSION" -W -f 65001`
（照抄真实输出）
实际引擎版本与降级开关说明（同 M0 报告口径：TRACEFLOW_TEST_DB_ALLOW_NEWER=1 显式授权；负向验证不设开关时门禁拒绝）。

## ② 全量验证命令与真实结果

### 1. 解决方案构建
`dotnet build traceflow/TraceFlow.sln -c Release`
（照抄真实输出尾部；要求 0 警告 0 错误）

### 2. 单元测试
`dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --no-build`
（照抄真实输出；列出新增测试类：CodeFormatStorageTests/RowVerTests/DrugIdentityMatcherTests/ApprovalNoNormalizerTests/SourceValueObjectTests/CsvReaderTests）

### 3. 集成测试（授权降级，默认 collation）
`TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build`
（照抄真实输出；列出 M1 新增测试类与用例数）

### 4. 集成测试（授权降级，BIN2 变体）
同上命令追加 `TRACEFLOW_TEST_DB_BIN2=1`（Sql2008Fixture 以 COLLATE Latin1_General_BIN2 建库）。
（照抄真实输出；两变体均须全绿）

### 5. 前端构建
`cd traceflow/src/TraceFlow.Web && npm run build`
（照抄真实输出尾部；tsc 0 错误，vite 产物至 TraceFlow.Host/wwwroot）

## ③ 迁移 applied 列表与 checksum（临时库真实运行）

同 M0 报告流程：CREATE DATABASE TraceFlow_GateReport_<随机> → DbMigrator 运行 → 查询 dbo.schema_version（应含 0001–0007 共 7 行 success=1）→ DROP → 残留核查 `SELECT name FROM sys.databases WHERE name LIKE 'TraceFlow%'` 须 0 行。
（照抄真实 applied 输出与 schema_version 7 行）

## ④ 未满足项清单

### 取证门禁
| 门禁 | 状态 | 禁用后果 |
| --- | --- | --- |
| G-ERP | 未取证（fixture-only） | 真实 ERP 同步禁用；KSOA 列映射仍"候选"；取证清单 ksoa-adapter-contract.md §3 |
| G-TOP | 未取证 | 真实平台发送/药品企业下载禁用；M1 平台主数据仅 CSV 导入积累 |
| G-POS | 未取证 | 销售生产自动上报禁用 |
| G-RETURN | 未取证 | 退货生产上报禁用（M1 仅 fixture 事件拆分） |
| G-OPENING | 未取证 | 期初歧义业务阻断 |
| G-DEPLOY | 未取证 | 不启用生产发送 |

### 运行时门禁
- **2008 R2 真实实例验证未完成（AT-070）**：全部 DB 证据来自 SQL 2019（授权降级，双 collation 变体）。交付前必须在真实 10.50.x 实例重跑全部集成测试并替换本报告 ①③ 证据。静态语法门禁通过不能替代运行时验证。

### M2–M4 功能缺口（汇总，详见 matrix.md）
- M2：上游下载（UP-01/02、SYNC-03）、入库关联与码级分配（IN-01…09）、资格冻结与自动 102 完整业务事务、补配 UI（UI-11 剩余）、trace-policy 受影响任务失效接线（M1 affectedNonTerminalTasks 恒 0）。
- M3：销售核验/自动 321（SALE-01…05）、退货确认（RET-02…04）、平台发送协议/限流/回查（TOP-01…04、JOB-03/04、IDEM-03/05）、平台通道暂停（JOB-05 剩余）。
- M4：十页闭环剩余（UI-04…09）、日对账（REC-01/03）、CSV 导出（DEF-07）、性能基准（NFR-03/04）、连续运行≥7 天、备份恢复（NFR-07/08）、完整告警（NFR-06）。

## 缺口与边界（必须原样声明）
- M1 来源同步为 fixture 通道；真实 KSOA 只读适配待 G-ERP 取证后实现（接口已固定，仅替换适配器）。
- 供应商映射解除的在途任务守卫为 org 级保守判定（M1）；M2 细化到 dwbh 级。
- trace-policy 的 affectedNonTerminalTasks M1 恒 0（无任务产生路径）；失效接线 M2。
- 连续运行/备份恢复/性能基准属 M4，M1 不声称。
```

- [ ] **Step 8: 新建 M1-rulings-and-carryforwards.md**

`docs/delivery/M1-rulings-and-carryforwards.md`：

```markdown
# M1 裁决与携带项（交接 M2）

## 裁决（执行期如偏离须记录新裁决）
1. **每商品至多一条映射行**：product_mapping 以 UX_pmap_pair(product_id,platform_drug_id) + 既有过滤唯一索引保证；历史版本在 product_mapping_history。UpsertProductMappingAsync UPDATE 既有行而非多行并存。
2. **供应商解除在途守卫 org 级保守**：M1 无 dwbh→任务直连路径，按 org 下非终态 PURCHASE_IN_102/PURCHASE_RETURN_202 计数阻断；M2 入库分配落地后细化到供应商级。
3. **affectedNonTerminalTasks 恒 0**：M1 无任务产生路径，trace-policy 返回 0 并如实标注；M2 接线真实失效计数。
4. **导入 jobId "0" 约定**：已提交/失败批次重复上传返回 jobId="0"（不产生作业），batchId 仍返回；前端 ImportWizard 以 jobId!=="0" 决定是否轮询。
5. **幂等键用户作用域**：0007 将 UQ 改为 (idem_key,user_id)；请求哈希含查询串；并发败者回滚并重放胜者/409。
6. **BIN2 双变体**：集成套件默认 collation 与 TRACEFLOW_TEST_DB_BIN2=1 各跑一遍；外部 ID/码列显式 BIN2，库级 collation 仅影响未显式指定列。
7. **记录型响应 camelCase**：Host 无全局 JSON 命名策略；返回 C# 记录的端点一律 Results.Json(..., Web)；匿名对象端点保持字面 camelCase。
8. **CodeFormat 持久化 token**：EMPTY/S8P20/UNSUP（CodeFormatStorage），禁 enum.ToString 落库；0007 拓宽列并迁移 'Empty'→'EMPTY'。

## 携带项（M2 必须处理）
- RealKsoaAdapter 实现 + G-ERP 取证清单（ksoa-adapter-contract.md §3）；取证前 LIVE 同步保持禁用。
- trace-policy 失效接线（受影响非终态任务计数与阻断）。
- 供应商解除守卫细化到 dwbh 级。
- upload-dispatch/bill-query 处理器（M0 仅表结构与恢复路径）。
- 2008 R2 真实实例补验（AT-070，交付前；替换 M0/M1 门禁报告 ①③ 证据）。
- SYNC-03 上游下载分页落库同事务（0004 download_progress 就绪）。
```

- [ ] **Step 9: 全量回归（双 collation 变体，真实运行）**

全部命令在仓库根执行；每条输出照抄入 Step 7 报告：

```bash
dotnet build traceflow/TraceFlow.sln -c Release
# 期望：0 警告 0 错误（TreatWarningsAsErrors）

dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --no-build
# 期望：全绿（M0 87 + M1 新增单元用例）

TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build
# 期望：全绿（默认 collation；M0 45 + M1 新增集成用例）

TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
TRACEFLOW_TEST_DB_ALLOW_NEWER=1 TRACEFLOW_TEST_DB_BIN2=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build
# 期望：全绿（BIN2 变体；用例数与上一致）

cd traceflow/src/TraceFlow.Web && npm run build && cd ../../..
# 期望：tsc 0 错误，vite 产物至 TraceFlow.Host/wwwroot
```

任一失败：修复后重跑全部五项（不得只重跑失败项声称全绿）。运行后核查无 TraceFlow_Test_* 残留库。

- [ ] **Step 10: 提交（仅本次文档与代码，身份旗标）**

```bash
git status --porcelain   # 先核查：仅提交 M1 相关文件；用户未跟踪文件（PRD 原件/docs/research/docs/review/retail_tracecode/zww-md）绝不加入
git add docs/contracts/openapi.yaml docs/contracts/data-dictionary.md docs/contracts/adapter-facts.md \
  docs/contracts/source-mapping.md docs/contracts/ksoa-adapter-contract.md \
  docs/traceability/matrix.md docs/delivery/M1-gate-report.md docs/delivery/M1-rulings-and-carryforwards.md
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com \
  commit -m "docs(m1): contract write-back, traceability evidence, M1 gate report (2008 R2 pending, G-ERP fixture-only)"
```

注：wwwroot 构建产物不入本提交（Task 14/15 已约定）；若 Step 9 修复了代码，代码修复随对应 Task 提交，本步仅文档。

---

## 计划自审记录（writing-plans self-review）

1. **Spec 覆盖**：AT-001（fixture 通道+取证清单，真实只读待取证）/002/003（T7 键校验）/004（T8 稳定期）/005（T8 迟行）/006（T7 超长键整批失败）/007（T9 窗口补拉）/009/010/011（T11 分类）/012/013/014（T2+T12 匹配）/015（T12 双向唯一+T13 强冲突）/016/017（T12 锁定/重验）/018（T12 前缀索引）/019/020/021（T12 供应商）/057（T8 SOURCE_CHANGED）；SRC-01…05、SYNC-01/02/04/05、MD-01/02/03、MATCH-01…05、ENT-01/02/03、JOB-05（来源通道）、UI-02/03/10/13(部分)/11(部分)、API-01/02、AUD-01、NFR-02/04(部分)/05、DB-01/02/03（0007）均有对应 Task。M0 携带项 9 条（编号依 `docs/delivery/M0-rulings-and-carryforwards.md`）：1 format 加宽/token（T1）、2 授权管线顺序复审（T9 M1ApiGateTests）、3 幂等用户作用域+query hash+DI+2627 语义（T3）、4 输入验证（T4）、5 租约回收审计+GrantAsync 2601（T4）、6 expectedVersion 要素对齐（T9）、7 BIN2 变体+CI 字面量修正（T4）、8 代理端口 5152+前端壳抛光（T15）、9 静态门禁根断言+卫生（T4）——全部落位。
2. **占位符扫描**：无 TBD/TODO/"类似 Task N"；门禁报告输出段为"照抄真实运行输出"指令（运行期产物，非代码占位）。
3. **类型一致性**：ClaimedJob/IJobQueue/IJobHandler/AuditEntry/ISecretProtector 签名与 M0 一致；SettingView/ImportBatchView/MappingActionResponse 等在 T6/T10/T13 定义、T14/T15 TS 类型与 T16 OpenAPI 回写逐字段对齐；RowVer hex 16 位大写、byte[] 比较一律 SequenceEqual；Results.Json(..., Web) 用于全部记录型响应。