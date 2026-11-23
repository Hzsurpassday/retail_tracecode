# TraceFlow M3 零售上传闭环 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 M2 基础上交付路线图 M3：销售核验管道（绑定摘要/规则版本/逐码平台核验/入库依赖）、零售 321 与消费者退货 116 的发送协议（send_attempt 短事务→HTTP→T3、fencing、受理未知）、billstatus 回查与逐码三态（含 UNKNOWN）、部分成功、发送恢复闭环、退货首次人工确认，以及 UI-06/07/09 页面。

**Architecture:** 沿用单体 + 持久任务引擎。`sale-verify` 作业对 COMPLETE 的 SALE 快照执行本地规则 + 逐码 Fake 平台核验，结论写 `verification`/`verification_code`，通过后在 T2 同事务创建 `RETAIL_321`/`CONSUMER_RETURN_116` 任务、码占用、不可变载荷、多分片 `bill_code` 与 `upload-dispatch` outbox。`upload-dispatch` 处理器执行 §3.2 发送协议：短事务落 `upload_attempt` 并置 Dispatching → 事务外 HTTP → token 守卫 T3 写回；`bill-query` 按退避回查 billstatus，逐码写 `code_result`（SUCCESS/FAILED/UNKNOWN），24h 或最大次数转 ManualAction。退货 116 首次上报强制人工确认（Blocked + RETURN_CONFIRM_REQUIRED），确认事务内建立前序关系、码占用与发送任务。真实平台仍被 G-TOP 门禁拒绝；M3 端到端证据全部来自 MOCK + Fake 网关 + 故障注入。

**Tech Stack:** .NET 8 / ASP.NET Core 8 / Dapper 2.x / Microsoft.Data.SqlClient 5.x / xUnit / React 18 + TypeScript + Vite 5（全部沿用 M0–M2，无新依赖）。

**Spec:** `docs/superpowers/specs/2026-11-22-retail-pharmacy-trace-design.md`（已批设计）。同时必读：`docs/PRD-TraceFlow-单门店-V1.0.md`（§9 状态机、§15.2 M3 范围）、`docs/PRD-TraceFlow-验收与代码审查清单.md`（AT-035…056、AT-061、AT-068）、`docs/delivery/M2-rulings-and-carryforwards.md`、`docs/contracts/openapi.yaml`、`docs/contracts/top-contract.md`、`docs/traceability/matrix.md`。

**分支：** 从 `m2-upstream-allocation`（HEAD `cb9bb8c`，PR #3 开放）创建 `m3-sale-upload`。M0–M2 已交付语义是既成事实接口，除本计划显式列出的修改点外不得改动。

## Global Constraints

（M0–M2 逐字沿用）

- 业务库兼容 **SQL Server 2008 R2**：禁止 OFFSET/FETCH、SEQUENCE、JSON_VALUE/OPENJSON、STRING_AGG、temporal table、CREATE OR ALTER、IIF/CONCAT/FORMAT/TRY_CONVERT、THROW、DROP IF EXISTS、TRIM、NOLOCK；分页用 ROW_NUMBER/键集；全部 SQL 在真实 2008 R2 实例验证（AT-070，仍待交付前补验）。
- 数据访问只用 **Dapper + 手写 T-SQL**，不用 EF Core。
- KSOA 源库**只读**；迁移工具只连 TraceFlow 目标库。
- 外部 ID/单号/码一律字符串，索引键列 `COLLATE Latin1_General_BIN2`；数量 `decimal(18,6)`；内部 UTC `datetime2(3)`；`rowversion` 序列化为 16 位大写十六进制字符串；内部 int64 ID 以 JSON number 返回，外部 ID/数量/rowversion 以字符串返回。
- AppKey/AppSecret/连接密码不落 Git、日志、前端、错误响应；凭据 DPAPI 加密；密文不可解密→停调用+告警，无默认凭据兜底。
- 上传状态机按 `UploadStateMachine` 唯一转换表；逐码结果必须含 UNKNOWN；非法转换拒绝并告警。
- 普通测试一律 Fake 平台/fixture；**不得向正式平台上传虚构交易、不得写 ERP 库**；真实平台调用仅限经用户授权的真实业务。
- 匿名拒绝、逐请求权限校验；审计不可变；控制面/人工操作审计 env 恒 `"LIVE"`，自动作业审计 env 用作业 env。
- 前端遵循 NFR-02 React 规范；时间显示 Asia/Shanghai；API 前缀 `/api/v1`；错误统一 `code/message/fieldErrors/retryable/requestId`。
- 未取证能力显示"未接入/未验证"并禁用发送；交付报告不得声称 2008 R2 或真实平台合同已验证。
- **测试引擎**（Git Bash，仓库根 `E:\Viding\Retail Trace Code`）：
  `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
  BIN2 变体追加 `TRACEFLOW_TEST_DB_BIN2=1`；单元测试 `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`；构建 `dotnet build traceflow/TraceFlow.sln -c Release`；前端 `cd traceflow/src/TraceFlow.Web && npm run build`。
- **提交规则**：每个 Task 末尾提交；`git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit`（不改 git config）；只提交本任务文件；既有未跟踪用户文件（PRD 原文、docs/research、docs/review、retail_tracecode/、zww-md/）一律不动。
- **迁移纪律**：0001–0008 字节不可变（checksum 门禁）；M3 DDL 全部进 `0009_m3_sale_upload.sql`；新文件以换行结尾；`TreatWarningsAsErrors` 0 警告。
- **fixture 纪律**：`docs/contracts/fixtures/ksoa/` 既有文件不改；M3 允许在 `docs/contracts/fixtures/top/` 新增方法响应样例，且测试优先用委托网关精确注入。

### M3 附加约束（本阶段裁定，执行者必须遵守）

- **G-TOP 门禁不变**：`TraceFlow:TopMode` 默认 `fixture`；`FakeTopGateway` 只接受 `MOCK`；`RealTopGateway` 继续抛 `TopNotCertifiedException`。`uploadretail`、`query.billstatus`、`query.codedetail` 的入参与语义全部是**候选合同值**，集中登记在 `docs/contracts/top-contract.md` 并标注 `_verified:false`。任何真实环境发送被禁用。
- **发送顺序不可交换**：认领 → 短事务（`upload_attempt` + part/task 置 `Dispatching`）→ HTTP → T3（租约+token 守卫）。禁止"先 HTTP 后记账"，禁止客户端透明重试上传 POST。
- **M3 退货范围裁定**：消费者退货 116 由 POS 负数行（`event_kind=CONSUMER_RETURN`）自动识别并**强制首次人工确认**；采购退货 202 的任务类型、确认与发送通道已实现并有测试，但 KSOA 侧采购退货来源字段未取证（G-ERP），UI 明示"采购退货采集未接入"，不伪造入口。负数行永不自动上传。
- **EXEMPT 按依据跳过**：`trace_classification=EXEMPT` 且 `classification_reason` 非空的行不进入发送载荷、不计核验失败；`UNKNOWN` 分类一律阻断（`UNKNOWN_CLASSIFICATION`），不得默认豁免。
- **退货确认权限**：`return:confirm`；受控重试 `upload:retry`；受理未知人工核对 `upload:verify`。后端逐请求校验。
- **受控重试合同门禁**：`top.retryPathCertified` 设置默认 `false`；为 false 时端点 422 `RETRY_PATH_NOT_CERTIFIED`，UI 禁用并说明原因。测试中可显式置 true 走通状态机，但不得作为平台事实宣传。
- **billstatus 判定（候选，G-TOP 取证后修订）**：`response_success=false` → 按 `TopResponseParser` 拒绝；`result_type="1"` → Processing（上传成功≠处理成功）；`result_type="2"` → 全部成功；`result_type="3"` 或 `sub_process_flag="52"` → 部分成功并解析逐码；`result_type="4"` → 确定失败；无 `result_type`/`model` 为空 → ResultUnresolved（继续回查）。逐码 `code_list[].status`：`"1"`=SUCCESS、`"2"`=FAILED、缺失/其他=UNKNOWN，**绝不把缺失码当成功**。
- **依赖判定**：销售码的入库依赖满足 = 同 org/env 下该码存在 `PURCHASE_IN` 事件且其 `upload_task.state='Succeeded'`，或平台 `query.codedetail` 返回 `instore=true`（历史平台证据）。否则 `dependency_state='WAIT'`、verification `UNRESOLVED`，任务 Blocked + `WAIT_DEPENDENCY`；采购入库任务成功时重新入队 `sale-verify`。
- **多分片（IDEM-03）**：`upload.part.maxCodes` 默认 200；同一来源行的全部码必须落在同一分片；`bill_code` 用 `BillCodeGenerator.Generate(contract, taskId, partIndex)`；任务状态由分片聚合（`TaskStateAggregator`），部分成功/失败不得整单重新排队。
- **任务终态释放规则**：M3 不自动释放任何码占用；失败/部分成功仅记录结果与差异，释放审批属 M4。

## File Structure（M3 新增/修改）

```
traceflow/
  db/migrations/0009_m3_sale_upload.sql          # T1：verification_code/return_confirmation/part 回查列/前置任务列
  src/TraceFlow.Domain/
    Sales/SaleRules.cs                            # T2：纯规则（分类、码存在、重复码、负数行拆分）
    Sales/ReturnRules.cs                          # T6：前序候选唯一性/退货载荷规则
    Uploads/TaskStateAggregator.cs                # T4：分片→任务状态聚合
    Top/BillStatusParser.cs                       # T5：result_type/sub_process_flag/逐码解析
  src/TraceFlow.Application/Abstractions/
    ITopGateway.cs                                # 不改（方法名字符串传参）
    ServiceExceptions.cs                          # T2/T5/T6/T7：新异常码
  src/TraceFlow.Infrastructure/
    Sales/SaleVerificationService.cs              # T2
    Sales/RetailTaskFactory.cs                    # T3
    Sales/ReturnConfirmService.cs                 # T6
    Uploads/UploadDispatchService.cs              # T4
    Uploads/BillQueryService.cs                   # T5
    Jobs/SaleVerifyJobHandler.cs                  # T2
    Jobs/UploadDispatchJobHandler.cs              # T4
    Jobs/BillQueryJobHandler.cs                   # T5
    Jobs/SourceSyncJobHandler.cs                  # T2 修改（COMPLETE→sale-verify）
    Sources/SyncScheduler.cs                      # T2/T5 修改（sale-verify 兜底桶、bill-query 兜底桶）
    Settings/SettingsService.cs                   # T1/T4/T5/T7 修改（M3 键）
  src/TraceFlow.Host/
    Endpoints/SalesEndpoints.cs                   # T7
    Endpoints/UploadEndpoints.cs                  # T7
    Security/ApiErrors.cs                         # T7 修改（新异常映射）
    Program.cs                                    # T2/T4/T5 修改（处理器注册）
  src/TraceFlow.Web/src/
    pages/SalesPage.tsx, ReturnsPage.tsx, UploadCenterPage.tsx   # T8
    App.tsx, components/Layout.tsx（或现有导航文件）              # T8 修改
  tests/TraceFlow.UnitTests/
    Sales/SaleRulesTests.cs, Uploads/TaskStateAggregatorTests.cs, Top/BillStatusParserTests.cs   # T2/T4/T5
  tests/TraceFlow.IntegrationTests/
    Sales/SaleVerificationTests.cs, Sales/RetailTaskTests.cs, Sales/ReturnConfirmTests.cs       # T2/T3/T6
    Uploads/UploadDispatchTests.cs, Uploads/BillQueryTests.cs, Uploads/FaultInjectionTests.cs    # T4/T5/T10
    Api/SalesApiTests.cs, Api/UploadApiTests.cs                                                 # T7
  docs/contracts/fixtures/top/
    upload-retail-accepted.json, billstatus-success.json, billstatus-partial.json, codedetail-instore.json  # T2/T5
```

---

### Task 1: 迁移 0009 + M3 设置键

**Files:**
- Create: `traceflow/db/migrations/0009_m3_sale_upload.sql`
- Modify: `traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Db/MigrationTests.cs`（既有迁移测试文件；新增 0009 断言）

**Interfaces:**
- Produces: 表 `dbo.verification_code`、`dbo.return_confirmation`；列 `upload_task.predecessor_task_id`、`upload_part.next_query_at/query_count/first_sent_at`；设置读取器 `GetSaleVerifySettingsAsync()`、`GetPartMaxCodesAsync()`、`GetQueryPolicyAsync()`、`GetRetryPathCertifiedAsync()`。

- [ ] **Step 1: 写迁移**

`0009_m3_sale_upload.sql` 全文（GO 分批，2008 R2 语法）：

```sql
-- 0009 M3：销售核验逐码结论/退货确认/回查调度/前置任务
ALTER TABLE dbo.upload_task ADD predecessor_task_id bigint NULL
  CONSTRAINT FK_task_predecessor REFERENCES dbo.upload_task(id);
GO
ALTER TABLE dbo.upload_part ADD
  next_query_at datetime2(3) NULL,
  query_count int NOT NULL CONSTRAINT DF_part_qcount DEFAULT 0,
  first_sent_at datetime2(3) NULL;
GO
CREATE TABLE dbo.verification_code (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_verification_code PRIMARY KEY,
  verification_id bigint NOT NULL CONSTRAINT FK_vcode_verif REFERENCES dbo.verification(id),
  org_id bigint NOT NULL,
  env char(4) NOT NULL,
  code_id bigint NOT NULL CONSTRAINT FK_vcode_code REFERENCES dbo.trace_code(id),
  source_line_id bigint NOT NULL,
  result nvarchar(8) NOT NULL,
  reason nvarchar(500) NULL,
  evidence_json nvarchar(max) NULL,
  checked_at datetime2(3) NOT NULL CONSTRAINT DF_vcode_at DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_vcode UNIQUE (verification_id, code_id),
  CONSTRAINT CK_vcode_result CHECK (result IN (N'PASS',N'FAIL',N'UNKNOWN'))
);
GO
CREATE TABLE dbo.return_confirmation (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_return_confirmation PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_retconf_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  task_id bigint NOT NULL CONSTRAINT FK_retconf_task REFERENCES dbo.upload_task(id),
  decision nvarchar(16) NOT NULL,
  relation_kind nvarchar(16) NOT NULL,
  predecessor_task_id bigint NULL,
  evidence_json nvarchar(max) NOT NULL,
  confirmed_by nvarchar(64) NOT NULL,
  confirmed_at datetime2(3) NOT NULL CONSTRAINT DF_retconf_at DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_retconf_task UNIQUE (task_id),
  CONSTRAINT CK_retconf_decision CHECK (decision IN (N'CONFIRMED',N'REJECTED')),
  CONSTRAINT CK_retconf_relation CHECK (relation_kind IN (N'SALE',N'PURCHASE_IN'))
);
```

- [ ] **Step 2: 设置键**（`SettingsService` 新增读取器，沿用现有键值缓存/解密模式）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `sale.verify.ruleVersion` | `M3-1` | 核验规则版本，写入 `verification.rule_version` 与任务快照 |
| `sale.verify.maxCodesPerRun` | `200` | 单次核验最多平台调用码数，余量下次 |
| `upload.part.maxCodes` | `200` | 分片码数上限 |
| `top.query.backoffSeconds` | `30` | 回查退避基数（指数） |
| `top.query.maxAttempts` | `20` | 回查最大次数 |
| `top.query.manualAfterHours` | `24` | 超过转 ManualAction |
| `top.query.consistencyWindowSeconds` | `600` | 受理未知"未受理"证据所需延迟窗口 |
| `top.dispatch.leaseSeconds` | `120` | 发送作业租约 |
| `top.retryPathCertified` | `false` | 受控重试合同门禁 |

- [ ] **Step 3: 迁移测试**

`MigrationTests` 断言：`0009` 记录存在且 checksum 非空；`verification_code`/`return_confirmation` 可插入最小值；`upload_part.next_query_at` 可空；`0001–0008` 校验和与 M2 gate report 一致（不得变化）。

- [ ] **Step 4: 运行**

Run: `dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter FullyQualifiedName~MigrationTests`（环境变量同 Global Constraints）
Expected: PASS。再跑 `dotnet build traceflow/TraceFlow.sln -c Release` 0 警告。

- [ ] **Step 5: 提交**

```bash
git add traceflow/db/migrations/0009_m3_sale_upload.sql traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs traceflow/tests/TraceFlow.IntegrationTests/Db/MigrationTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): migration 0009 and settings for sale verification and upload pipeline"
```

---

### Task 2: 销售核验规则与作业（SALE-01/02/03/04）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Sales/SaleRules.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Sales/SaleVerificationService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/SaleVerifyJobHandler.cs`
- Modify: `traceflow/src/TraceFlow.Infrastructure/Jobs/SourceSyncJobHandler.cs`（快照 COMPLETE 后入队）
- Modify: `traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs`（失败兜底：近 24h 未 PASSED 的 SALE 快照）
- Create: `docs/contracts/fixtures/top/codedetail-instore.json`
- Test: `traceflow/tests/TraceFlow.UnitTests/Sales/SaleRulesTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sales/SaleVerificationTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SaleLineInput(long SourceLineId, string SourceLineKey, string ProductSourceId, decimal Quantity, string? CodeNormalized, string EventKind, string Classification, string? ClassificationReason);`
  - `public sealed record SaleRuleResult(bool Ok, string? FailCode, IReadOnlyList<SaleLineInput> SaleLines, IReadOnlyList<SaleLineInput> ReturnLines, IReadOnlyList<string> DuplicateCodes, IReadOnlyList<SaleLineInput> MissingCodeLines, IReadOnlyList<SaleLineInput> UnknownClassLines);`
  - `public static SaleRuleResult Evaluate(IReadOnlyList<SaleLineInput> lines)`（纯函数，无 IO）
  - `public sealed class SaleVerificationService { Task<long?> VerifySnapshotAsync(long snapshotId, CancellationToken ct); }` 返回创建的 verification id。
  - outbox `job_kind="sale-verify"`，`intent_key="snapshot:{snapshotId}"`，payload `{"orgId":..,"env":"MOCK","snapshotId":..}`。

- [ ] **Step 1: 写规则失败测试**

```csharp
public class SaleRulesTests
{
    private static SaleLineInput Line(long id, decimal qty, string? code, string cls = "REQUIRED", string? reason = null, string kind = "SALE")
        => new(id, $"L{id}", "SP0001", qty, code, kind, cls, reason);

    [Fact] public void Required_line_without_code_fails_with_MISSING_CODE()
        => Assert.Equal("MISSING_CODE", SaleRules.Evaluate(new[] { Line(1, 1, null) }).FailCode);

    [Fact] public void Same_code_twice_in_document_fails_with_DUPLICATE_CODE()
        => Assert.Equal("DUPLICATE_CODE", SaleRules.Evaluate(new[] { Line(1, 1, "81234567890123456789"), Line(2, 1, "81234567890123456789") }).FailCode);

    [Fact] public void Exempt_line_with_reason_is_skipped_and_not_uploaded()
    {
        var r = SaleRules.Evaluate(new[] { Line(1, 2, null, "EXEMPT", "OTC 不纳入追溯") });
        Assert.True(r.Ok);
        Assert.Empty(r.SaleLines);
    }

    [Fact] public void Exempt_without_reason_is_UNKNOWN_CLASSIFICATION()
        => Assert.Equal("UNKNOWN_CLASSIFICATION", SaleRules.Evaluate(new[] { Line(1, 2, null, "EXEMPT") }).FailCode);

    [Fact] public void Unknown_classification_blocks()
        => Assert.Equal("UNKNOWN_CLASSIFICATION", SaleRules.Evaluate(new[] { Line(1, 1, "81234567890123456789", "UNKNOWN") }).FailCode);

    [Fact] public void Negative_line_goes_to_return_bucket_and_never_to_sale()
    {
        var r = SaleRules.Evaluate(new[] { Line(1, 1, "81234567890123456789"), Line(2, -1, "81234567890123456790", kind: "CONSUMER_RETURN") });
        Assert.True(r.Ok);
        Assert.Single(r.SaleLines);
        Assert.Single(r.ReturnLines);
    }

    [Fact] public void Unsupported_code_format_fails_with_CODE_FORMAT()
        => Assert.Equal("CODE_FORMAT", SaleRules.Evaluate(new[] { Line(1, 1, "12345") }).FailCode);
}
```

- [ ] **Step 2: 实现 `SaleRules`**

规则顺序（短路，错误码即 `verification.result_json` 的 `failCode`）：
1. 任一参与行 `Classification` 不在 REQUIRED/EXEMPT/UNKNOWN，或 UNKNOWN，或 EXEMPT 无原因 → `UNKNOWN_CLASSIFICATION`；带回 `UnknownClassLines`。
2. REQUIRED 行码为空/空白 → `MISSING_CODE`；码不满足 8 开头 20 位数字（复用 `TraceCodeNormalizer`/`CodeFormat` 现有判定） → `CODE_FORMAT`。
3. 同一单据内规范化码重复（跨正负行也算） → `DUPLICATE_CODE`。
4. 正数行（`EventKind=="SALE"` 且 `Quantity>0`）入 `SaleLines`；负数行（`EventKind=="CONSUMER_RETURN"` 或 `Quantity<0`）入 `ReturnLines`；EXEMPT 行不入任一列表。
5. 一张单据同时有正负行是合法的（DEF-01 拆两个任务，见 Task 3/6）。

- [ ] **Step 3: 集成测试（核验服务）**

数据准备沿用 `SourceSyncTests` 的临时 fixture 目录 + `TraceFlow_Test_*` 库模式；通过 `SourceSyncJobHandler` 落一条 COMPLETE 的 SALE 快照（`sale-normal.json`），插入 `product`/`product_mapping`（AUTO_CONFIRMED）与对应 `platform_drug`，然后直接调用 `SaleVerificationService.VerifySnapshotAsync`。

测试方法（全部 `[Fact]`）：
- `Passed_sale_creates_verification_rows_and_sale_task_ready`
- `Unknown_classification_blocks_with_UNKNOWN_CLASSIFICATION_and_no_task`
- `Missing_code_blocks_with_MISSING_CODE`
- `Dependency_wait_blocks_with_WAIT_DEPENDENCY`（码无 PURCHASE_IN 成功任务，Fake `codedetail` 返回 `instore=false`）
- `Platform_instore_evidence_satisfies_dependency`（`codedetail` 返回 `instore=true`）
- `Purchase_success_reenqueues_sale_verify`（把依赖码的 PURCHASE_IN 任务置 Succeeded 后调用 `OnPurchaseOutcome` 入队；断言 outbox 有 `sale-verify`）
- `Rerun_same_snapshot_is_idempotent`（两次调用，`verification` 只有一条 is_current，任务仍一条）
- `Max_codes_per_run_defers_remainder_to_next_job`

- [ ] **Step 4: 实现核验服务**

`VerifySnapshotAsync` 流程（单连接，分事务）：
1. 读快照（COMPLETE、`document_type='SALE'`），否则返回 null。
2. 读行 + 商品映射（`state IN (AUTO_CONFIRMED,MANUAL_CONFIRMED)`）+ `product.trace_classification/classification_reason`；缺映射视为 UNKNOWN。
3. `SaleRules.Evaluate`。
4. 本地失败：写 `verification`（`FAILED` 或 `UNRESOLVED`；`FAILED` 用于 MISSING_CODE/DUPLICATE_CODE/CODE_FORMAT/UNKNOWN_CLASSIFICATION）+ `result_json`；若已存在任务，更新 `state=Blocked,state_reason=错误码`；无任务则不建。
5. 本地通过：码去重后逐码（受 `maxCodesPerRun` 限制）：
   - 依赖检查（SQL）：`PURCHASE_IN` 事件的 task `Succeeded`；否则调用 `TopClientService.CallAsync(env,"alibaba.alihealth.drugtrace.top.lsyd.query.codedetail", {code, ref_ent_id})`；候选响应 `model.instore==true` 视为满足；网络/限流异常 → 该码 UNKNOWN，整体 `WAIT_RETRY`。
   - 平台核验：同一调用结果中 `model.exists==true` 且 `instore==true` → PASS；`exists==false` → FAIL（`PLATFORM_NOT_FOUND`）；无法判定 → UNKNOWN。
6. 任一并发受限/限流 → `verification.state='WAIT_RETRY'`，outbox 用 `next_run_at = now + backoff` 重排。
7. 全部 PASS → `PASSED`；写 `verification_code`；T2 调 `RetailTaskFactory`（Task 3）创建任务；写 `upload_task.verification_state='PASSED'`。
8. 有 FAIL → `FAILED`；任务 Blocked + `VERIFY_FAILED:<码>`；有 UNKNOWN → `UNRESOLVED` + 任务 Blocked + `WAIT_VERIFY`（不得发送）。
9. `verification.is_current`：插入前把同 `(org,env,event_key)` 的旧行置 0（同一事务）；`event_key=KSOA:{docKey}:SALE`。

- [ ] **Step 5: 作业与调度接线**

- `SaleVerifyJobHandler`：payload 取 `snapshotId`，调服务；`WAIT_RETRY` 用 `RescheduleAsync(now + backoff)`；其他 `CompleteAsync`。
- `SourceSyncJobHandler`：快照晋升 COMPLETE 且 `document_type='SALE'` 时，用 `IJobQueue.EnqueueAsync` 入队 `sale-verify`（同事务）。
- `SyncScheduler.TickAsync` 增加兜底：每 5 分钟查最近 24h 内 COMPLETE 且无 PASSED/FAILED 当前 verification 的快照，逐条入队（intent `snapshot:{id}:retry:{unix/300}`）。
- `Program.cs` 注册 `SaleVerifyJobHandler` 到 Worker 的 job-kind 列表。

- [ ] **Step 6: 运行**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter FullyQualifiedName~SaleRulesTests`
Run: `TRACEFLOW_TEST_DB=... TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter FullyQualifiedName~SaleVerificationTests`
Expected: PASS；既有套件不回归。

- [ ] **Step 7: 提交**

```bash
git add traceflow/src/TraceFlow.Domain/Sales traceflow/src/TraceFlow.Infrastructure/Sales/SaleVerificationService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/SaleVerifyJobHandler.cs traceflow/src/TraceFlow.Infrastructure/Jobs/SourceSyncJobHandler.cs traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs traceflow/src/TraceFlow.Host/Program.cs docs/contracts/fixtures/top/codedetail-instore.json traceflow/tests/TraceFlow.UnitTests/Sales traceflow/tests/TraceFlow.IntegrationTests/Sales/SaleVerificationTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): sale verification pipeline with platform evidence and dependency gates"
```

---

### Task 3: 零售任务工厂与 T2（SALE-05、IDEM-03、DEF-01）

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Sales/RetailTaskFactory.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sales/RetailTaskTests.cs`

**Interfaces:**
- Consumes: `SaleVerificationService` 结论；`IJobQueue`；`SettingsService.GetBillContractAsync()`；`CodeClaimWriter` 现有写入模式（若方法签名不适用，新增 `BindRetailAsync(conn,tx,codeId,taskId,ct)`）。
- Produces:
  - `public sealed record RetailTaskContext(long OrgId, string Env, long SnapshotId, string DocKey, DateTime? BusinessDateTimeUtc, string SourceHash, string MappingDigest, string RuleVersion, IReadOnlyList<RetailLine> Lines, string TaskType);`
  - `public sealed record RetailLine(long SourceLineId, string SourceLineKey, string ProductSourceId, string PlatformDrugId, decimal Quantity, string? Unit, string? BatchNo, IReadOnlyList<(long CodeId, string CodeNormalized)> Codes);`
  - `public sealed record RetailTaskResult(long TaskId, IReadOnlyList<long> PartIds, bool Reused);`
  - `public Task<RetailTaskResult> CreateAsync(SqlConnection conn, SqlTransaction tx, RetailTaskContext ctx, CancellationToken ct)`；调用方负责事务边界与提交。

- [ ] **Step 1: 测试**

- `Creates_one_task_one_part_and_dispatch_outbox`：断言 `upload_task` 一行 `task_type='RETAIL_321'`、state `Ready`、`verification_state='PASSED'`、payload_hash 64 hex；`upload_part` 一行 `bill_code` 前缀 TF；`outbox_job` 一行 `upload-dispatch` intent `part:{id}`；`code_event` 每码一条 `SALE/OUT`。
- `Splits_parts_at_max_codes_and_keeps_line_codes_together`：3 行 150 码/行、`maxCodes=200` → 3 个 part；同一行码不跨 part；bill_code 后缀 `P1/P2`。
- `Same_event_key_reuses_task_and_parts`：连续调用两次 → 任务一条、part 数不变、outbox 不重复。
- `Active_retail_claim_blocks_second_task`：同码已被 `code_claim(event_scope='RETAIL:321',ACTIVE)` 占用 → 抛 `RetailConflictException("CODE_CONTENDED")`，事务回滚无任务。
- `Cancelled_task_reactivates_without_new_bill_code`（复用 InboundTaskFactory R7a 语义）。
- `Event_key_is_document_level_not_line_level`（DEF-01）：同一单两行 → 一个任务，`event_key=KSOA:{docKey}:SALE`。

- [ ] **Step 2: 实现工厂**

- `event_key = $"KSOA:{ctx.DocKey}:{ctx.TaskType}"`；`task_type ∈ {RETAIL_321, CONSUMER_RETURN_116}`。
- UPDLOCK/HOLDLOCK 查既有任务；复用时校验 `upload_attempt(UPLOAD)` 为 0，否则 `REACTIVATE_REFUSED`（同 M2）。
- 分片：按行顺序装填，行码数 > `maxCodes` → 422 `PART_TOO_LARGE`（单行不可拆）；part payload 为该分片的行/码子集；`code_set_hash=SourceHasher.CodeSetHash(part 码集合)`。
- 任务 payload（全单不可变）：`{eventKey, taskType, businessDateTimeUtc, ruleVersion, lines:[{sourceLineKey,productSourceId,platformDrugId,quantity,unit,batchNo,codes[]}]}`，`payload_hash=SHA256(payloadJson)`。
- `mapping_version_snapshot` = 排序拼接后的 mapping id/version 摘要（与 M2 同法）。
- 码占用：每码 `code_claim` scope `RETAIL:321`（116 用 `RETURN:116`），`event_scope` 与 M2 的 `PURCHASE_IN` 分开；已有 ACTIVE → 冲突回滚。
- `code_event`：321 写 `SALE/OUT`；116 写 `CONSUMER_RETURN/IN`，`predecessor_event_id` 由调用方（Task 6）传入，工厂签名增加可选 `IReadOnlyDictionary<long,long> PredecessorEventByCodeId`；`source_line_id=0` 的退货行使用 0。
- outbox：每 part 一条 `upload-dispatch`，payload `{orgId,env,taskId,partId,trigger:"sale-verify"}`。

- [ ] **Step 3: 运行**

Run: `dotnet test ... --filter FullyQualifiedName~RetailTaskTests`
Expected: PASS。

- [ ] **Step 4: 提交**

```bash
git add traceflow/src/TraceFlow.Infrastructure/Sales/RetailTaskFactory.cs traceflow/tests/TraceFlow.IntegrationTests/Sales/RetailTaskTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): retail 321 task factory with part splitting and T2 claims"
```

---

### Task 4: 发送协议 upload-dispatch（IDEM-04、§3.2、DEF-01）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Uploads/TaskStateAggregator.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Uploads/UploadDispatchService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/UploadDispatchJobHandler.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Uploads/TaskStateAggregatorTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Uploads/UploadDispatchTests.cs`

**Interfaces:**
- Consumes: `ITopGateway`（经 `TopClientService`）、`IJobQueue`、`ISendRecoveryService.AppendStaleEvidenceAsync`。
- Produces:
  - `public static UploadState Aggregate(IReadOnlyCollection<UploadState> partStates)`
  - `public sealed class UploadDispatchService { Task<DispatchOutcome> DispatchAsync(long partId, string workerId, long fencingToken, CancellationToken ct); }`
  - `public enum DispatchOutcome { Accepted, AcceptanceUnknown, Failed, RateLimited, AuthPaused, StaleRejected }`
  - outbox `bill-query` intent `query:part:{partId}`。

- [ ] **Step 1: 聚合器单测**

```csharp
[Theory]
[InlineData(new[] { UploadState.Succeeded, UploadState.Succeeded }, UploadState.Succeeded)]
[InlineData(new[] { UploadState.Succeeded, UploadState.Processing }, UploadState.Processing)]
[InlineData(new[] { UploadState.Succeeded, UploadState.PartialSuccess }, UploadState.PartialSuccess)]
[InlineData(new[] { UploadState.AcceptanceUnknown, UploadState.Failed }, UploadState.AcceptanceUnknown)]
[InlineData(new[] { UploadState.Failed, UploadState.Failed }, UploadState.Failed)]
[InlineData(new[] { UploadState.Dispatching, UploadState.Ready }, UploadState.Dispatching)]
public void Aggregates_parts_with_strict_severity(UploadState[] parts, UploadState expected)
    => Assert.Equal(expected, TaskStateAggregator.Aggregate(parts));
```

优先级（高→低）：`AcceptanceUnknown > Dispatching > Processing > ResultUnresolved > PartialSuccess > Failed > Succeeded > RetryWait > Accepted > ManualAction`；`ManualAction` 在任一分片存在时覆盖除 AcceptanceUnknown 外的所有（需继续回查/人工）。空集合抛 `ArgumentException`。

- [ ] **Step 2: 发送流程实现**

`DispatchAsync`：
1. 读 part/task（若 part 非 `Ready` 或 `RetryWait` → `StaleRejected`）。
2. **短事务**：写入 `upload_attempt(task_id,part_id,attempt_no=max+1,attempt_kind='UPLOAD',env,lease_owner,fencing_token)`；`upload_part.state='Dispatching', first_sent_at=COALESCE(first_sent_at,SYSUTCDATETIME())`；`upload_task.state='Dispatching'`；提交。
3. **事务外**：根据 `task_type` 调 `TopClientService.CallAsync`：
   - `RETAIL_321` → `alibaba.alihealth.drugtrace.top.lsyd.uploadretail`，参数 `{bill_code, ref_ent_id, ent_id, bill_type:"321", bill_time, detail:payload}`；
   - `CONSUMER_RETURN_116` → 同方法 `bill_type:"116"`；
   - `PURCHASE_IN_102` → `uploadinoutbill`（M2 已建任务，M3 开始真正发送）；
   - `PURCHASE_RETURN_202` → `uploadinoutbill`（fixture 支持）。
4. **T3（新连接+事务）**：
   - 守卫：`SELECT ... FROM upload_part WITH (UPDLOCK) WHERE id=@id AND state='Dispatching'` 且 `outbox_job RUNNING/lease_owner=worker/fencing_token=@token`；不满足 → `AppendStaleEvidenceAsync` 只留证，返回 `StaleRejected`。
   - 更新 `upload_attempt`（finished_at/http_status/request_id/platform_task_id/raw_response 脱敏/error_kind）。
   - `TopOutcome` 映射（`UploadStateMachine` 已有边）：
     - `Success` → `ReceiptAccepted`；
     - `PartialEvidence` → `PartialEvidence`（保存 resultJson）；
     - `BusinessRejected` → `Rejected`；
     - `TransportUncertain/SystemError` → `TransportUncertain`（受理未知）；
     - `DuplicateBillCode` → `DuplicateBillCode`（受理未知，仅触发回查）；
     - `RateLimited` → 由 `TopClientService` 抛 `TopRateLimitedException`：part 保持 `Dispatching` 会误判坏；**先**在同一 T3 事务写 attempt error，按"请求可能已发出"处理 → `TransportUncertain`，并把 outbox 重排 `now + 平台等待/退避`（保守，避免丢受理）。
     - `AuthPaused` → 同上受理未知 + outbox 重排 5 分钟。
   - 分片状态推进后，用 `TaskStateAggregator` 重算任务状态（`upload_task.state`）。
   - 受理成功/部分成功/受理未知 → 入队 `bill-query`（intent `query:part:{id}`，`next_run_at=now+5s`）。
5. Handler `UploadDispatchJobHandler`：`ClaimAsync` 拿到 job payload → 调服务 → `CompleteAsync`/`RescheduleAsync`；`StaleRejected` 时 `FailAsync("stale")`。

- [ ] **Step 3: 集成测试（委托网关）**

`UploadDispatchTests` 构造 `UploadDispatchService` 时注入 `FakeTopGateway`（委托按 method 返回）：
- `Successful_upload_sets_accepted_and_schedules_query`
- `Timeout_sets_acceptance_unknown_and_schedules_query`（gateway 返回 `TransportOk=false`）
- `Duplicate_bill_code_moves_to_acceptance_unknown_only`
- `Business_rejection_sets_failed_without_query`
- `Partial_flag_sets_partial_and_records_no_success`
- `Attempt_row_written_before_http`（委托里查库断言 `upload_attempt` 已存在且 part=Dispatching）
- `Stale_worker_cannot_write_back`（先手动使 job fencing+1，再调 T3 内部方法；断言 attempt `is_stale_evidence=1`、part 状态不变）
- `Rate_limit_keeps_acceptance_unknown`（网关抛 `TopRateLimitedException`）
- `Crash_before_http_leaves_dispatching_then_recovery_queries_first`（写 attempt 后抛异常，调 `SendRecoveryService.RecoverAsync` → part AcceptanceUnknown + bill-query）

- [ ] **Step 4: 运行**

Run: `dotnet test ... --filter "FullyQualifiedName~TaskStateAggregatorTests|FullyQualifiedName~UploadDispatchTests"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add traceflow/src/TraceFlow.Domain/Uploads/TaskStateAggregator.cs traceflow/src/TraceFlow.Infrastructure/Uploads traceflow/src/TraceFlow.Infrastructure/Jobs/UploadDispatchJobHandler.cs traceflow/tests/TraceFlow.UnitTests/Uploads/TaskStateAggregatorTests.cs traceflow/tests/TraceFlow.IntegrationTests/Uploads/UploadDispatchTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): crash-determinable upload dispatch protocol with fencing and receipt states"
```

---

### Task 5: billstatus 回查与逐码三态（TOP-02/03/04、JOB-04）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Top/BillStatusParser.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Uploads/BillQueryService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/BillQueryJobHandler.cs`
- Create: `docs/contracts/fixtures/top/billstatus-success.json`, `billstatus-partial.json`
- Test: `traceflow/tests/TraceFlow.UnitTests/Top/BillStatusParserTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Uploads/BillQueryTests.cs`

**Interfaces:**
- Produces:
  - `public enum BillStatusKind { Processing, AllSuccess, Partial, Failed, Unresolved }`
  - `public sealed record CodeStatus(string Code, string Result, string? Reason);`（Result ∈ SUCCESS/FAILED/UNKNOWN）
  - `public static BillStatusParseResult Parse(string? resultJson)`；`BillStatusParseResult(BillStatusKind Kind, IReadOnlyList<CodeStatus> Codes, string? Note)`
  - outbox `bill-query` 消费方；24h/20 次后 `ManualEscalate` 入 ManualAction 并写 `reconciliation_difference(ACCEPTANCE_UNKNOWN)`（M4 对账复用）。

- [ ] **Step 1: 解析器单测**

```csharp
[Fact] public void Result_type_1_is_processing()
    => Assert.Equal(BillStatusKind.Processing, BillStatusParser.Parse(Read("result-type-1.json")).Kind);

[Fact] public void Result_type_3_or_flag_52_is_partial_with_per_code_evidence()
{
    var r = BillStatusParser.Parse(Read("partial-flag-52.json"));
    Assert.Equal(BillStatusKind.Partial, r.Kind);
}

[Fact] public void Missing_code_status_is_UNKNOWN_not_success()
{
    var r = BillStatusParser.Parse("""{"result_type":"2","code_list":[{"code":"8...1","status":"1"},{"code":"8...2"}]}""");
    Assert.Equal(new[] { "SUCCESS", "UNKNOWN" }, r.Codes.Select(c => c.Result));
}

[Fact] public void Missing_model_is_unresolved_not_success()
    => Assert.Equal(BillStatusKind.Unresolved, BillStatusParser.Parse("{\"foo\":1}").Kind);
```

解析规则按 Global Constraints 的候选映射实现；任何 JSON 异常 → `Unresolved`。`result_type="3"` 且 `code_list` 为空也是 `Partial`（证据不足不得升级成功）。

- [ ] **Step 2: 服务实现**

`BillQueryService.QueryPartAsync(partId, workerId, token, ct)`：
1. 读 part（`Dispatching/Accepted/Processing/AcceptanceUnknown/PartialSuccess/ResultUnresolved` 才查；终态直接返回）。
2. 短事务：写 `upload_attempt(attempt_kind='QUERY', attempt_no=max+1)` 并提交（查询超时只影响查询，不改变受理状态）。
3. 调 `TopClientService.CallAsync(env,"...query.billstatus",{bill_code, ref_ent_id})`；限流 → outbox 重排退避，part/task 状态不变。
4. T3 守卫（同 Task 4）：按当前状态选事件 `QueryProcessing/QueryAllSuccess/QueryPartial/QueryFailed/QueryUnresolved`，用 `UploadStateMachine.TryGetNext`；非法转换只告警不改状态。
5. 写 `code_result`（`attempt_id` 为本次 QUERY attempt；UNKNOWN 照写）；`part.query_count+1`。
6. 下一动作：
   - `AllSuccess` → part Succeeded；任务聚合后若全部成功 → Succeeded，并 **入队依赖它的 sale-verify**（查 `code_event` 同码 SALE 事件对应任务的 `dependency_state='WAIT'`）。
   - `Partial` → part PartialSuccess；任务 PartialSuccess；**不重排整单**，仅保留逐码失败证据，入队 M4 差异前先写 `reconciliation_difference` 即可（M3 允许直写）。
   - `Processing` → `next_query_at = now + backoff(query_count)`；入队下一次 bill-query。
   - `Unresolved` → `ResultUnresolved` + 退避；超过 20 次或 24h → `ObservationExpired` → ManualAction。
   - `Failed` → part/task Failed。
7. 退避：`min(top.query.backoffSeconds * 2^min(query_count,5), 600)` + 0..5s 抖动；不得硬撞。

- [ ] **Step 3: 测试**

- `Processing_then_success_walks_processing_to_succeeded`（两次查询委托）
- `Partial_saves_success_and_failed_codes_without_requeue`
- `Unknown_codes_are_persisted_as_UNKNOWN`
- `Query_timeout_does_not_change_acceptance_state`
- `Twenty_attempts_escalates_to_manual_action`
- `Purchase_success_triggers_dependent_sale_verify`
- `Non_terminal_part_only_queried`（Succeeded 再次调用零写入）

- [ ] **Step 4: 运行与提交**

Run: `dotnet test ... --filter "FullyQualifiedName~BillStatusParserTests|FullyQualifiedName~BillQueryTests"`
Expected: PASS。

```bash
git add traceflow/src/TraceFlow.Domain/Top/BillStatusParser.cs traceflow/src/TraceFlow.Infrastructure/Uploads/BillQueryService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/BillQueryJobHandler.cs traceflow/src/TraceFlow.Host/Program.cs docs/contracts/fixtures/top/billstatus-success.json docs/contracts/fixtures/top/billstatus-partial.json traceflow/tests/TraceFlow.UnitTests/Top/BillStatusParserTests.cs traceflow/tests/TraceFlow.IntegrationTests/Uploads/BillQueryTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): billstatus polling with per-code tri-state, backoff and manual escalation"
```

---

### Task 6: 退货 116 识别、首次人工确认与前序关系（RET-01/02/04、DEF-02）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Sales/ReturnRules.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Sales/ReturnConfirmService.cs`
- Modify: `traceflow/src/TraceFlow.Infrastructure/Sales/SaleVerificationService.cs`（负数行建 Blocked 退货任务）
- Create: `docs/contracts/fixtures/top/upload-retail-accepted.json`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Sales/ReturnConfirmTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ReturnCandidate(long CodeId, long SourceLineId, long? PredecessorEventId, string? Error);`
  - `public static IReadOnlyList<ReturnCandidate> ResolvePredecessors(IReadOnlyList<(long CodeId, DateTime? OccurredAt, IReadOnlyList<long> SaleEventIds)> sales, IReadOnlyList<long> returnedEventIds)`（纯函数：唯一未退回的最近 SALE 事件；0 条→`PREDECESSOR_MISSING`，>1 条同码未退回→`PREDECESSOR_AMBIGUOUS`，已退回→`ALREADY_RETURNED`）
  - `public sealed class ReturnConfirmService { Task<ReturnConfirmResult> ConfirmAsync(long taskId, ReturnConfirmCommand cmd, CancellationToken ct); }`
  - `ReturnConfirmCommand(long ExpectedVersion, string Decision, IReadOnlyList<long> PredecessorEventIds, string EvidenceJson, string IdemKey, string Actor)`
  - `ReturnConfirmResult(long TaskId, string State, bool Idempotent)`
- Consumes: `RetailTaskFactory`（116 分支）；`IIdempotencyStore`。

- [ ] **Step 1: 规则单测（合并到 `SaleRulesTests` 或新建 `ReturnRulesTests`）**

- `Single_unreturned_sale_event_is_predecessor`
- `No_sale_event_is_PREDECESSOR_MISSING`
- `Already_returned_code_is_ALREADY_RETURNED`
- `Two_unreturned_sales_is_PREDECESSOR_AMBIGUOUS`
- `Negative_sale_line_creates_blocked_116_task_without_claims`（集成）

- [ ] **Step 2: 服务实现**

`ConfirmAsync`：
1. 幂等：`IIdempotencyStore` 命中同一 key → 返回原结果（`Idempotent=true`）；key 冲突不同任务 → 409。
2. 读任务：`task_type IN (CONSUMER_RETURN_116,PURCHASE_RETURN_202)`、state `Blocked`、`state_reason='RETURN_CONFIRM_REQUIRED'`、`version=@expectedVersion`，否则 409 `VERSION_CONFLICT`/422 `NOT_RETURNABLE`。
3. `decision='REJECTED'`：写 `return_confirmation`、任务保持 Blocked + `state_reason='RETURN_CONFIRM_REJECTED'`、审计；不建占用/outbox。
4. `decision='CONFIRMED'`：
   - 校验提交的 `predecessorEventIds` 与服务端计算结果一致（顺序无关），不一致 → 422 `PREDECESSOR_MISMATCH`；无 → 422 `PREDECESSOR_MISSING`/`AMBIGUOUS`/`ALREADY_RETURNED`。
   - 校验 116 的前序任务为 `Succeeded`（平台已有销售流水）或证据 JSON 含平台回执；202 的前序为 PURCHASE_IN 任务（成功或平台证据）。
   - T3 同事务：`return_confirmation(CONFIRMED)` + `RetailTaskFactory.CreateAsync`（116，`PredecessorEventByCodeId`）+ 任务 `Blocked→Ready`（`EligibilitySatisfied`）+ outbox 入队 + 审计。
   - 任务 `predecessor_task_id` 取前序事件所属任务（唯一）。
5. 权限由端点校验 `return:confirm`。

- [ ] **Step 3: 接入核验服务**

`SaleVerificationService` 对 `ReturnLines`（正数单中的负数行）**不进入 321 载荷**；创建/复用 `CONSUMER_RETURN_116` 任务，`state='Blocked'`、`state_reason='RETURN_CONFIRM_REQUIRED'`、`verification_state='PASSED'`，**不创建 outbox、不占用码**；核验结论记录前序候选到 `result_json.returnCandidates`。

- [ ] **Step 4: 测试**

- `Signals_mixed_return_fixture_creates_321_and_blocked_116`
- `Confirm_creates_claims_events_and_dispatch_outbox`
- `Reject_keeps_blocked_and_writes_no_claim`
- `Repeat_same_confirmation_is_idempotent`
- `Stale_version_is_409`
- `Predecessor_missing_blocks_confirmation`
- `Confirmed_116_payload_contains_original_sale_bill`（前序任务 part.bill_code）
- `202_confirmation_works_when_fixture_task_seeded`（fixture 直接建 202 任务 → 确认 → Ready）

- [ ] **Step 5: 运行与提交**

Run: `dotnet test ... --filter "FullyQualifiedName~ReturnConfirmTests|FullyQualifiedName~ReturnRulesTests"`
Expected: PASS。

```bash
git add traceflow/src/TraceFlow.Domain/Sales/ReturnRules.cs traceflow/src/TraceFlow.Infrastructure/Sales/ReturnConfirmService.cs traceflow/src/TraceFlow.Infrastructure/Sales/SaleVerificationService.cs docs/contracts/fixtures/top/upload-retail-accepted.json traceflow/tests/TraceFlow.IntegrationTests/Sales/ReturnConfirmTests.cs traceflow/tests/TraceFlow.UnitTests/Sales/ReturnRulesTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): consumer return detection with mandatory first confirmation and predecessor checks"
```

---

### Task 7: API — POS 销售、退货、上传中心（UI-06/07/09 后端）

**Files:**
- Create: `traceflow/src/TraceFlow.Host/Endpoints/SalesEndpoints.cs`
- Create: `traceflow/src/TraceFlow.Host/Endpoints/UploadEndpoints.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（映射端点）
- Modify: `traceflow/src/TraceFlow.Host/Security/ApiErrors.cs`（新异常→状态码）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/SalesApiTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/UploadApiTests.cs`

**Interfaces（写回 openapi.yaml 由 Task 9 完成，端点形状以本表为准）：**

| 方法 | 路径 | 权限 | 说明 |
| --- | --- | --- | --- |
| GET | `/api/v1/pos-sales?page&pageSize&dateFrom&dateTo&env` | 登录 | 销售快照列表（含核验/任务摘要） |
| GET | `/api/v1/pos-sales/{snapshotId}` | 登录 | 行 + 码 + verification + task 钻取 |
| GET | `/api/v1/returns?page&pageSize&state` | 登录 | 退货任务列表（116/202，含前序候选） |
| POST | `/api/v1/returns/{taskId}/confirm` | `return:confirm` | body `{expectedVersion,decision,predecessorEventIds[],evidence,idemKey}` |
| GET | `/api/v1/upload-tasks?state&taskType&env&page&pageSize` | 登录 | 上传任务列表（成功率按"全部成功"口径） |
| GET | `/api/v1/upload-tasks/{id}` | 登录 | parts + attempts + code_result + timeline |
| POST | `/api/v1/upload-tasks/{id}/manual-verify` | `upload:verify` | 受理未知人工核对（ACCEPTED/PROCESSING/NOT_ACCEPTED） |
| POST | `/api/v1/upload-tasks/{id}/controlled-retry` | `upload:retry` | `top.retryPathCertified=false` → 422 |

错误码映射：`RetailConflictException`→409；`AllocationValidationException`→422；`TopNotCertifiedException`→422 `TOP_NOT_CERTIFIED`；`ChannelPausedException`→503；`TopRateLimitedException`→429（带 `retryAfter`）；`RETURN_*`/`PREDECESSOR_*`→422；`VERSION_CONFLICT`→409。

- [ ] **Step 1: 测试（HTTP 层）**

- `Sales_list_filters_by_date_and_env_and_masks_secrets`
- `Sale_detail_includes_verification_and_task_state`
- `Return_confirm_requires_return_permission`（操作员 403）
- `Return_confirm_is_idempotent_by_idem_key`
- `Upload_list_uses_final_success_rate_not_partial`
- `Upload_detail_exposes_attempts_and_code_results_without_raw_secrets`
- `Manual_verify_rejects_NOT_ACCEPTED_before_consistency_window`（422）
- `Manual_verify_ACCEPTED_requires_platform_evidence`（422）
- `Controlled_retry_disabled_returns_422_RETRY_PATH_NOT_CERTIFIED`
- `Unknown_task_is_404`

- [ ] **Step 2: 实现端点**

沿用 `InboundEndpoints` 模式：`MapGroup("/api/v1")`、`RequirePermission`、`Results.Json(camelCase)`、分页 ROW_NUMBER（2008 R2）。列表响应：
```json
{"items":[{"id":12,"eventKey":"KSOA:000123:SALE","taskType":"RETAIL_321","state":"Succeeded","verificationState":"PASSED","partCount":2,"codeCount":300,"succeededCodes":298,"failedCodes":2,"unknownCodes":0,"createdAt":"..."}],"page":1,"pageSize":20,"total":1}
```
`manual-verify` 状态机：仅 `ManualAction` 任务可提交；`ACCEPTED/PROCESSING` 需证据 JSON 含 `platformBillCode` 且与任务 part.bill_code 相等；`NOT_ACCEPTED` 需当前时间 ≥ `last_sent_at + consistencyWindowSeconds` 且 `contractEvidence` 非空；通过后按事件推进并写审计（env LIVE）。
`controlled-retry`：读 `top.retryPathCertified`；false → 422；true → 仅 `Failed` → `ControlledRetryApproved` → RetryWait，并重置 outbox `upload-dispatch`（attempt_count=0），审计。

- [ ] **Step 3: 运行与提交**

Run: `dotnet test ... --filter "FullyQualifiedName~SalesApiTests|FullyQualifiedName~UploadApiTests"`
Expected: PASS，既有 API 测试不回归。

```bash
git add traceflow/src/TraceFlow.Host/Endpoints/SalesEndpoints.cs traceflow/src/TraceFlow.Host/Endpoints/UploadEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/src/TraceFlow.Host/Security/ApiErrors.cs traceflow/tests/TraceFlow.IntegrationTests/Api/SalesApiTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/UploadApiTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): POS sales, returns and upload center APIs"
```

---

### Task 8: 前端 UI-06/07/09

**Files:**
- Create: `traceflow/src/TraceFlow.Web/src/pages/SalesPage.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/pages/ReturnsPage.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/pages/UploadCenterPage.tsx`
- Modify: 现有路由/导航（`App.tsx` 与布局组件，参照 `InboundsPage` 的现有注册方式）
- Test: `traceflow/src/TraceFlow.Web/src/__tests__/SalesPage.test.tsx`、`ReturnsPage.test.tsx`、`UploadCenterPage.test.tsx`（沿用现有 vitest/Testing Library 模式）

**要求：**
- 三页均显示当前环境徽标（复用现有组件）与 Asia/Shanghai 时间格式（`fmtShanghai`）。
- SalesPage：日期/环境筛选、分页表格（单号、时间、行数、码数、核验状态、任务状态）、点击行钻取（码列表 + 核验逐码 + 任务链接）；核验失败原因中文映射。
- ReturnsPage：状态页签（待确认/已确认/已驳回），待确认行展示前序销售候选（单号/时间/码），确认对话框要求选择前序并勾选"首次上报，确认后符合资格将自动上传"，提交携带 `expectedVersion` 与 `idemKey`；驳回要求填写证据。202 标注"采购退货采集未接入（G-ERP）"。
- UploadCenterPage：状态/类型筛选、任务表（状态、分片进度、失败/UNKNOWN 码数）、抽屉展示 attempts 与逐码结果（UNKNOWN 单列，不并入成功）；`ManualAction` 行提供"人工核对"表单；受控重试按钮在未取证时禁用并显示原因。
- 空态/加载态/错误态（role="alert"）齐全；不显示 AppKey/AppSecret 或任何原始响应中的敏感字段。

- [ ] **Step 1: 组件测试**（mock fetch，断言筛选、分页、确认流程、UNKNOWN 不并入成功、禁用重试）
- [ ] **Step 2: 实现三页 + 导航**
- [ ] **Step 3: 构建** `cd traceflow/src/TraceFlow.Web && npm run build`（0 error；0 新增警告）
- [ ] **Step 4: 浏览器验证**：按 M2 方式启动后端（MOCK）与 Vite，用 chrome-devtools 打开三页，走 列表→钻取→确认→上传中心 路径并截图存档（`.superpowers/sdd/2026-11-23-m3-sale-upload/shots/`）。
- [ ] **Step 5: 提交**

```bash
git add traceflow/src/TraceFlow.Web/src/pages/SalesPage.tsx traceflow/src/TraceFlow.Web/src/pages/ReturnsPage.tsx traceflow/src/TraceFlow.Web/src/pages/UploadCenterPage.tsx traceflow/src/TraceFlow.Web/src/App.tsx traceflow/src/TraceFlow.Web/src/__tests__/
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m3): sales, returns and upload center pages"
```

---

### Task 9: 故障注入套件（AT-068）

**Files:**
- Create: `traceflow/tests/TraceFlow.IntegrationTests/Uploads/FaultInjectionTests.cs`

**必须覆盖（每条断言"本地保障"，不宣称平台恰好一次）：**

1. `Kill_before_http_attempt_leaves_evidence_then_recovery_queries_first`：写 attempt 后抛 `SimulatedCrashException`（不执行 T3）；`SendRecoveryService.RecoverAsync` → part `AcceptanceUnknown`，outbox `upload-dispatch` FAILED、`bill-query` PENDING；不得再次 UPLOAD。
2. `Kill_after_http_before_t3_leaves_dispatching_and_recovery_never_reuploads`：网关记录"已发出"后抛异常；恢复后只有 QUERY 尝试。
3. `Kill_during_t3_rolls_back_and_attempt_stays_open`：T3 内模拟异常 → attempt.finished_at NULL；恢复路径走受理未知。
4. `Late_worker_write_rejected_but_response_preserved`：token 失效后写回 → part 状态不变、attempt `is_stale_evidence=1` 且 `raw_response` 保留。
5. `Duplicate_bill_code_error_only_triggers_query`：重复号响应 → AcceptanceUnknown → QUERY 调用；无第二次 UPLOAD。
6. `Crash_before_receipt_commit_keeps_accepted_unknown_and_no_task_success`。
7. `Same_business_intent_creates_exactly_one_task_and_one_part_bill_code`（并发 4 线程调 `RetailTaskFactory`；断言任务 1、part 唯一、bill_code 稳定）。
8. `Mock_success_never_advances_live_state`：env=Live 任务 + Fake 网关 → `TopNotCertifiedException`，任务/码状态不变。

每用例使用独立 `TraceFlow_Test_*` 数据库（沿用测试基类），不得触碰 `TraceFlow_Dev`/`TraceFlow_M2Manual`/`TraceFlow_M2Gate`。

- [ ] **Step 1–3**：按上表逐条编写、实现缺失钩子（如 `UploadDispatchService` 的可注入 `beforeSend/afterSend/beforeCommit` 委托，仅测试用 internal），运行
  `dotnet test ... --filter FullyQualifiedName~FaultInjectionTests`
- [ ] **Step 4**：BIN2 变体再跑一遍
- [ ] **Step 5**：提交 `test(m3): crash-injection and fencing guarantees`

---

### Task 10: 契约与矩阵回写、M3 门禁报告、全量回归

**Files:**
- Modify: `docs/contracts/openapi.yaml`（Task 7 的 8 个端点 + M3 字段；保持 2008 R2 无关）
- Modify: `docs/contracts/top-contract.md`（M3 候选：uploadretail/116、billstatus 映射、codedetail、`_verified:false`）
- Modify: `docs/contracts/data-dictionary.md`（0009 三张表/列）
- Modify: `docs/traceability/matrix.md`（SALE-01..05、RET-01/02/04、TOP-02/03/04、IDEM-03/04/05、JOB-04、UI-06/07/09、DEF-01/02 真实测试名；AT-035…056/061/068）
- Create: `docs/delivery/M3-gate-report.md`
- Modify: `docs/delivery/M2-rulings-and-carryforwards.md`（M3 裁定追加）或新建 `M3-rulings-and-carryforwards.md`
- Create: `docs/superpowers/plans/2026-11-23-m4-closure.md`（本任务只写 M4 计划骨架：基于 M3 实际接口，不预先伪造细节；M4 执行前按路线图规则细化）

**步骤：**

- [ ] **Step 1**：全量单元 + 集成（默认与 BIN2）+ 前端构建（命令见 Global Constraints），记录真实耗时与计数。
- [ ] **Step 2**：核对迁移 checksum 0001–0009；确认 0001–0008 未变。
- [ ] **Step 3**：写 gate report：@@VERSION、五次运行输出、测试类/用例计数（`dotnet test --list-tests` 实测，不用 grep 猜）、浏览器截图路径、明确声明——2008 R2 未验证（AT-070 待交付前）、G-TOP 未取证、发送全部 MOCK、202 采集未接入、`top.retryPathCertified=false`。
- [ ] **Step 4**：矩阵更新后统计真实覆盖率，缺口按需求 ID 列出进入 M4/携带项。
- [ ] **Step 5**：提交（仅上述文件）。

---

## 验收映射（M3 完成定义）

| 验收 | 证据 |
| --- | --- |
| 零售核验通过才入队；UNKNOWN/依赖未满足阻断；EXEMPT 按依据跳过（§5.3-2） | Task 2 测试 + 上传中心页面 |
| 消费者退货首次人工确认；负数行不自动上传（§5.3-3） | Task 6 测试 + 退货页面 |
| 崩溃恢复：未知受理不盲目重发、迟到写回被拒（AT-068） | Task 9 套件 |
| 部分成功逐码证据、UNKNOWN 保留（TOP-03） | Task 5 + code_result |
| 多分片单号稳定（IDEM-03） | Task 3/9 |
| 2008 R2 | **未验证**，报告如实标注 |
| 真实平台合同 | **未取证**，全部 MOCK |