# TraceFlow M2 上游下载与入库分配 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 M1 基础上交付 PRD §15.2 的 M2 范围：上游出库单下载（列表/详情分页完整性）、往来单位/企业版本同步、入库自动关联（行级证据唯一才自动）、数量/码级分配与占用冻结、人工补配及确认（同样资格复查）、入库资格冻结与 T2 同事务建 102 任务（发送属 M3），并关闭 M1 携带的优先修复项。

**Architecture:** 沿用单体（Kestrel + UseWindowsService + 持久任务引擎）。新增 `ITopGateway` 端口：M2 只交付 Fake 网关（G-TOP 未取证，真实网关占位拒绝）；TOP 响应解析（TOP-01）、持久共享限流预算（JOB-03）、授权错误按范围暂停（§3.3）为真实实现并以 Fake 驱动测试。上游下载以 SYNC-03 同事务分页落库，列表/详情进度分离，详情未全部成功不 COMPLETE。入库匹配/补配/确认共用同一资格规则（§1.1 唯一实现处）；资格结论+码占用冻结+不可变载荷+稳定单号+任务入队同事务（T2），HTTP 在事务外。M2 创建的任务停在 Ready/PENDING，`upload-dispatch`/`bill-query` 处理器属 M3。

**Tech Stack:** .NET 8 / ASP.NET Core 8 / Dapper 2.x / Microsoft.Data.SqlClient 5.x / xUnit / React 18 + TypeScript + Vite 5（全部沿用 M0/M1，无新依赖）。

**Spec:** `docs/superpowers/specs/2026-11-22-retail-pharmacy-trace-design.md`（已批设计）。同时必读：`docs/PRD-TraceFlow-单门店-V1.0.md`、`docs/PRD-TraceFlow-验收与代码审查清单.md`、`docs/delivery/M1-rulings-and-carryforwards.md`（M1 裁定与携带项）、`docs/contracts/openapi.yaml`（M2 计划契约）、`docs/contracts/ksoa-adapter-contract.md`。

**分支：** 从 `m1-source-master`（HEAD 691679d，PR #2 待合并）创建 `m2-upstream-allocation`。M0/M1 代码是既成事实接口，本计划逐字消费；不得修改 M0/M1 已交付语义（除本计划显式列出的加固点）。

## Global Constraints

（与 M0/M1 计划共用，逐字沿用）

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
- **测试引擎**：用户已授权开发期用本机 SQL Server 2019 降级测试。集成测试命令统一为（Git Bash，仓库根 `E:\Viding\Retail Trace Code`）：
  `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
  BIN2 变体追加 `TRACEFLOW_TEST_DB_BIN2=1`。交付报告必须如实标注"2008 R2 未验证，交付前补验 AT-070"。禁止声称 2008 R2 运行时已验证。
- **rowversion 序列化约定**：`Convert.ToHexString(bytes)`（16 位大写十六进制，无连字符）。`RowVer.FromHex` 校验失败抛 `FormatException`→端点 422。新建对象 expectedVersion 哨兵值为字符串 `"0"`。
- **CodeFormat 持久化 token**：`Empty→"EMPTY"`、`Supported8Prefix20→"S8P20"`、`Unsupported→"UNSUP"`（`CodeFormatStorage`）。禁止 `enum.ToString()` 直接落库。
- **审计 env 约定（M0 M-8）**：控制面/人工操作审计 env 恒 `"LIVE"`；自动作业审计 env 用作业 env。
- **提交规则**：每个 Task 末尾提交；`git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit`（不改 git config）；只提交本任务文件；仓库中既有未跟踪用户文件（PRD 原文、docs/research、docs/review 等）一律不动。
- **新文件一律以换行符结尾**；`TreatWarningsAsErrors` 已开（traceflow/Directory.Build.props），0 警告才能过。
- **内部 int64 ID 以 JSON number 返回**（M1 裁定 9）；外部 ID/金额/数量/rowversion 以字符串返回（API-01）。record 响应用 `Results.Json(..., new JsonSerializerOptions(JsonSerializerDefaults.Web))` 或匿名对象 camelCase 手写投影（沿用 M1 端点模式）。

### M2 附加约束（本阶段裁定，执行者必须遵守）

- **G-TOP 门禁**：M2 只有 Fake 网关（`TraceFlow:TopMode` 默认 `fixture`）。`RealTopGateway` 是抛 `TopNotCertifiedException` 的占位，不写任何签名/HTTP 代码；`env=LIVE` 的上游下载/企业同步一律 422 `TOP_NOT_CERTIFIED`。签名算法、接口入参、限流限值、包装层级枚举、重复号 sub_code 均属 G-TOP 取证（附录 B），取证前用**候选合同值**并显式标注，真实通道禁用。AT-020..034 的 M2 证据 = Fake 网关 + 候选契约文档 + 集成测试；真实平台联调属交付前门禁。
- **候选合同值（待 G-TOP 取证，集中声明于 `docs/contracts/top-contract.md`）**：bill_code 前缀默认 `"TF"`、MaxLength 默认 32（settings 可配，有界）；分页参数 `page_no/page_size`（page_size 默认 50）；包装层级最小包装 token `"MIN"`（其余值/null 一律视为非最小或不明，阻断自动与人工分配）；限流错误 sub_code 前缀候选 `"FAIL_BIZ_RATE"`；重复单据号 sub_code 候选 `"FAIL_BIZ_BILL_CODE_DUPLICATE"`；`FAIL_BIZ_AUTH_ERROR` 与 `WARN_BIZ_PARTSUCCESS` 为规格/PRD 已点名 token（非候选）。外部单号候选字段 `ywdjbh`（jh_rkhz，未取证；只从 raw_header JSON 读取，不改 ISourceAdapter 契约）。
- **M2 不发送**：`upload-dispatch`/`bill-query` 处理器属 M3（PRD §15.2）。M2 创建的 102 任务状态 Ready、outbox PENDING，无处理器认领即静置；UI 显示"已排队（发送通道 M3 交付）"。禁止为演示伪造平台成功。
- **Cancelled 行政终态（IN-09 裁定）**：PRD IN-09 明确"冻结待发任务若从未发起网络调用，可通过原子取消任务及释放预留重算"。0008 迁移为 `upload_task`/`upload_part` 的 CHECK 约束增加 `N'Cancelled'`；`UploadState` 增加 `Cancelled`，`UploadEvent` 增加 `AllocationCancelled`，状态机仅增两条边：`(Blocked, AllocationCancelled)→Cancelled`、`(Ready, AllocationCancelled)→Cancelled`。Cancelled 是纯本地行政终态，任何已发起 UPLOAD 尝试后不可达；不是平台状态，不进入平台统计成功/失败口径。
- **自动关联证据规则（IN-02/03/04/06 的 M2 可判定子集）**：自动确认仅当——快照 COMPLETE；供应商映射有效且上游单 `upstream_ref_ent_id` 等于映射企业版本 `ref_ent_id`；门店 `ent_id` 已配置且上游单 `to_ent_id` 与之相等；商品映射有效且上游行 `platform_drug_id` 等于映射平台药 key；批号双方非空且 Trim 后 Ordinal 相等；单位双方非空且 Trim 后 Ordinal 相等（比例 1/1）；上游行数量 == 本地行数量（decimal 精确）；上游行码全部 `S8P20` 且 `package_level='MIN'`；码数 == 数量（整数）；全部码无 ACTIVE `PURCHASE_IN` 占用；候选唯一。任一不满足→不自动（UNMATCHED/PARTIAL/AMBIGUOUS/CONFLICT 按原因）。**禁止**排序取前 N、按金额比例、只比合计（IN-03）。有 `ywdjbh` 时只按外部单号找候选（找不到→UNMATCHED，不回退窗口）；无外部单号时 ±7 天窗口仅为候选范围。批号冲突且外部单号明确→CONFLICT（AT-025）。
- **人工分配强约束（§4.2/IN-03/04）**：批号一致、商品映射一致、供应商一致、上游单 COMPLETE 为人工也须满足的强冲突约束（422）；码必须属于所选上游行、`MIN` 层级、`S8P20`、单内去重；`quantity>0` 且 ≤6 位小数；`unitRatio="分子/分母"` 正整数，`UnitRatio.Convert` 精确整除且结果 == 码数（AT-029 不舍入）；非 MIN/不明层级码一律阻断（AT-028 不造子码）；父子重叠（code_relation 已知边）阻断（DB-05）。保存即冻结：RESERVED 分配 + ACTIVE claim 同事务；两 session 争同一码→一个成功另一个 409 `CODE_CONTENDED`（AT-032）。
- **确认即复查（IN-07/AT-031）**：confirm-allocation 重新执行与自动路径同一套资格规则，禁止"人工已处理因此跳过校验"；全部 REQUIRED 行被 RESERVED 分配精确覆盖才通过；通过→COMMITTED + T2 建任务，响应提示"符合资格后将自动上传"。无常态手工上传按钮。
- **冻结与取消（IN-09/AT-034）**：快照已有 COMMITTED 分配或非 Cancelled 任务→编辑拒绝 409 `FROZEN`。取消仅当任务 Blocked/Ready 且无 UPLOAD 尝试且 outbox 非 RUNNING；Dispatching/AcceptanceUnknown/Processing/PartialSuccess/Succeeded/Failed/ManualAction/ResultUnresolved/RetryWait 一律 409 `CANCEL_REFUSED`。终态失败不自动释放（§2.6）。
- **T2 边界（IN-08/AT-033）**：资格结论+RESERVED→COMMITTED+claim 冻结+不可变载荷+bill_code+code_event+outbox 入队同一事务；任何一步失败整体回滚，不出现"占用但永不发"。event_key=`KSOA:{djbh}:PURCHASE_IN`（IDEM-01：sourceHash 不入键）；UQ_task_event 保证一个逻辑任务；已存在任务永远复用（IDEM-02），bill_code 首次合法创建即持久。
- **限流预算（JOB-03）**：`top_rate_budget` 持久计数器（重启不清零），键 `appkey:total`、`method:{method}`、`ent:{refEntId}:method:{method}`（门店 ref_ent_id 已知时）；平台明确等待秒数优先，否则指数退避（30s×2^min(attempt,5)，上限 600s）+ 0..5s 抖动；超限改期不硬撞；限值列 NULL=本地不设帽（G-TOP 未取证不虚构限值），平台限流错误仍触发改期。
- **授权暂停（§3.3/JOB-05）**：`FAIL_BIZ_AUTH_ERROR`→按 `method:{m}` + `credential:top`（有主体时加 `ent:{refEntId}`）范围暂停 + 告警审计；暂停只影响相关管道，其余任务继续；恢复须 system:manage 人工动作 + 审计。
- **M1 携带项处置**：T6(e) 暂停门禁 fail-open→本计划 Task 11 修复（fail-closed）；T12(c) 信用代码 CONFLICT 死胡同→Task 11 提供 resolve 改绑端点（商品/供应商对称）；§4.7 DPAPI 作用域/运行账号显示→Task 12；trace-policy org 过滤→Task 11；供应商解除守卫 dwbh 细化→Task 11（经 inbound_match.supplier_dwbh）；affectedNonTerminalTasks 接线→Task 11（分类变更取消未发送任务+释放+重排）；ImportStageJobHandler 审计 env=LIVE→**裁定符合控制面约定，不改码，gate report 声明**；Kestrel body 上限→Task 11 配置 100MB；ImportWizard 预览分页→**携带 M4**；手动同步终态意图重入队→**携带 G-ERP 后**（M2 rematch 意图用 nonce 避免同缺陷）。
- **fixture 纪律**：不得向 `docs/contracts/fixtures/ksoa/` 既有目录新增/修改文件（M1 测试按目录全量断言）；M2 测试一律自建临时 fixture 目录（沿用 SourceSyncTests 模式）。TOP fixture 新目录 `docs/contracts/fixtures/top/` 只放方法响应样例，测试用委托网关精确注入。
- **迁移纪律**：0001–0007 字节不可变（checksum 门禁）；新 DDL 全部进 `0008_m2_upstream_allocation.sql`；GO 分批沿用 0007 模式。

## File Structure（M2 新增/修改）

```
traceflow/
  db/migrations/0008_m2_upstream_allocation.sql   # T1：inbound_match/top_rate_budget/top_channel_pause/进度窗口列/Cancelled 约束
  src/TraceFlow.Domain/
    Uploads/UploadState.cs, UploadEvent.cs, UploadStateMachine.cs   # T1 修改（Cancelled 两边）
    Top/TopOutcome.cs, TopErrorTokens.cs, TopResponseParser.cs      # T2
    Sources/SourceHasher.cs                                          # T7 修改（CodeSetHash）
  src/TraceFlow.Application/Abstractions/
    ITopGateway.cs                                                   # T4（TopCall/TopRawResponse/端口）
    ServiceExceptions.cs                                             # T3/T4/T7/T9 修改（新异常）
  src/TraceFlow.Infrastructure/
    Top/RateBudgetService.cs, ChannelPauseService.cs                 # T3
    Top/FakeTopGateway.cs, RealTopGateway.cs, TopGatewayFactory.cs, TopFixtureHandler.cs  # T4
    Top/TopClientService.cs                                          # T4（预算+暂停+解析编排）
    Codes/TraceCodeStore.cs                                          # T5
    Upstream/UpstreamDownloadService.cs, EnterpriseSyncService.cs    # T5/T6
    Jobs/UpstreamDownloadJobHandler.cs, EnterpriseSyncJobHandler.cs  # T5/T6
    Inbound/CodeClaimWriter.cs, InboundCancelService.cs, InboundTaskFactory.cs  # T7
    Inbound/InboundMatchService.cs, AllocationService.cs             # T8/T9/T10
    Jobs/InboundMatchJobHandler.cs                                   # T8
    Sources/SyncScheduler.cs                                         # T5/T6/T8 修改（新桶）
    Settings/SettingsService.cs                                      # T5/T7/T11/T12 修改（新键/合同读取/暂停修复）
    Jobs/JobQueue.cs                                                 # 不动
  src/TraceFlow.Host/
    WorkerHostedService.cs                                           # T5 修改（平台等待优先）
    Security/ApiErrors.cs                                            # T3/T4/T7/T9 修改（新异常映射）
    Endpoints/UpstreamEndpoints.cs, InboundEndpoints.cs              # T10/T11
    Endpoints/OpsEndpoints.cs                                        # T6 修改（entity 扩展）
    Endpoints/MasterEndpoints.cs                                     # T11 修改（resolve/org 过滤/dwbh 守卫/分类接线）
    Endpoints/SettingsEndpoints.cs                                   # T12 修改（作用域/账号显示）
    Endpoints/TopBudgetEndpoints.cs                                  # T12
    Program.cs                                                       # T4/T5/T6/T8/T10/T11/T12 接线
  src/TraceFlow.Web/src/
    m2types.ts                                                       # T13
    pages/UpstreamBillsPage.tsx, pages/InboundsPage.tsx              # T13
    pages/SettingsPage.tsx, pages/SuppliersPage.tsx, pages/ProductsPage.tsx  # T14 修改
    components/AppShell.tsx, App.tsx                                 # T13/T14 修改
  tests/TraceFlow.UnitTests/
    Uploads/UploadStateMachineTests.cs                               # T1 修改（+2 理论行）
    Top/TopResponseParserTests.cs                                    # T2
    Sources/SourceValueObjectTests.cs                                # T7 修改（+CodeSetHash 测试）
  tests/TraceFlow.IntegrationTests/
    Migrations/Migration0008Tests.cs                                 # T1
    Top/RateBudgetTests.cs, ChannelPauseTests.cs                     # T3
    Top/TopGatewayIsolationTests.cs                                  # T4
    Upstream/UpstreamDownloadTests.cs                                # T5（AT-022/023、SYNC-03）
    Upstream/EnterpriseSyncTests.cs                                  # T6（AT-020/021）
    Inbound/ClaimAndCancelTests.cs, InboundTaskFactoryTests.cs       # T7（AT-033/034；后者含 BillContractTests 类）
    Inbound/InboundMatchTests.cs                                     # T8（AT-024/025/026/030）
    Sources/SyncSchedulerTests.cs                                    # T5/T6/T8 修改（桶计数 5→6→7→8）
    Inbound/AllocationServiceTests.cs                                # T9（AT-027/028/029/032）
    Api/M2ApiGateTests.cs, InboundApiTests.cs                        # T10（AT-031/067）
    Settings/PauseGateTests.cs                                       # T11（T6e）
    Master/MappingResolveTests.cs                                    # T11（T12c）
    Master/ClassificationWiringTests.cs                              # T11（affectedNonTerminalTasks）
    Settings/DpapiDisplayTests.cs                                    # T12
docs/contracts/top-contract.md                                       # T4 新建，T15 定稿（候选合同声明）
docs/contracts/ksoa-adapter-contract.md                              # T15 修改（ywdjbh 候选）
docs/contracts/openapi.yaml, data-dictionary.md                      # T15 回写
docs/traceability/matrix.md                                          # T15
docs/delivery/M2-rulings-and-carryforwards.md, M2-gate-report.md     # T15
```

## 任务总览（依赖顺序）

| # | 任务 | 关键验收 |
| --- | --- | --- |
| 1 | 0008 迁移 + Cancelled 状态机扩展 | IN-09 裁定；AT-070 增量 |
| 2 | TOP 响应解析器（TOP-01） | 系统/业务错误分离、候选 token 声明 |
| 3 | 持久限流预算 + 授权范围暂停 | JOB-03、§3.3、重启不清零 |
| 4 | ITopGateway + Fake/Real 网关 + TopClientService | G-TOP 诚实、Live/Mock 硬隔离 |
| 5 | 上游下载服务 + 作业 + 调度 | AT-022/023、SYNC-03、UP-01/02 |
| 6 | 企业/往来单位同步 + sync-jobs 扩展 | AT-020/021、ENT-02 |
| 7 | 占用/取消原语 + T2 任务工厂 | AT-033/034、IDEM-02、DB-05 |
| 8 | 入库自动匹配 + 作业 + 触发 | AT-024/025/026/030、IN-01..05 |
| 9 | 人工分配保存/取消服务 | AT-027/028/029/032、IN-03/04/09 |
| 10 | 确认补配 + 入库/上游端点 | AT-031/067、IN-06/07/08、UI-04/05 后端 |
| 11 | M1 携带修复批 | T6(e)/T12(c)/org 过滤/dwbh 守卫/分类接线/Kestrel |
| 12 | 设置 §4.7 + 预算用量端点 | DPAPI 作用域/账号显示、新键 |
| 13 | 前端上游单/入库关联页 | UI-04/05/11、409 刷新、后果提示 |
| 14 | 前端设置/映射 resolve + 导航 | §4.7 显示、CONFLICT 处置入口 |
| 15 | 契约回写 + 门禁报告 + 双排序回归 | AT-070 增量声明、全量绿 |

---

### Task 1: 0008 迁移 + Cancelled 行政终态

**Files:**
- Create: `traceflow/db/migrations/0008_m2_upstream_allocation.sql`
- Modify: `traceflow/src/TraceFlow.Domain/Uploads/UploadState.cs`
- Modify: `traceflow/src/TraceFlow.Domain/Uploads/UploadEvent.cs`
- Modify: `traceflow/src/TraceFlow.Domain/Uploads/UploadStateMachine.cs`（Table 末尾 +2 边）
- Modify: `traceflow/tests/TraceFlow.UnitTests/Uploads/UploadStateMachineTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/Migration0008Tests.cs`

**Interfaces:**
- Consumes: `Sql2008Fixture`（`_fx.OpenAsync()`、`ConnectionString`）；`TestSeed.EnsureAsync(c)→(OrgId,SysId)`；既有 0001–0007 表结构（0006 `upload_task`/`upload_part` 的 `CK_task_state`/`CK_part_state`；0004 `download_progress`）。
- Produces: 表 `dbo.inbound_match`、`dbo.top_rate_budget`、`dbo.top_channel_pause`；`download_progress.window_start/window_end`；`UploadState.Cancelled`、`UploadEvent.AllocationCancelled`；状态机边 `(Blocked,AllocationCancelled)→Cancelled`、`(Ready,AllocationCancelled)→Cancelled`。后续 Task 3/5/7/8/9/10/11 全部依赖这些名字。

- [ ] **Step 1: 写失败的单元测试（状态机 +2 合法边、+3 非法边、终态断言修正）**

编辑 `UploadStateMachineTests.cs`：`LegalEdges` 末尾（第 53 行 `ManualAction/QueryFailed` 行之后）追加：

```csharp
        { UploadState.Blocked, UploadEvent.AllocationCancelled, UploadState.Cancelled },
        { UploadState.Ready, UploadEvent.AllocationCancelled, UploadState.Cancelled },
```

`IllegalEdges` 末尾追加：

```csharp
        { UploadState.Dispatching, UploadEvent.AllocationCancelled },   // 已发起发送不可行政取消（IN-09）
        { UploadState.Accepted, UploadEvent.AllocationCancelled },
        { UploadState.Cancelled, UploadEvent.Claimed },                 // Cancelled 是终态
```

`Succeeded_is_terminal_and_every_other_state_has_exit` 改为（Cancelled 同为终态）：

```csharp
    [Fact]
    public void Terminal_states_have_no_exit_and_every_other_state_has_exit()
    {
        Assert.DoesNotContain(UploadStateMachine.AllTransitions, t => t.From == UploadState.Succeeded);
        Assert.DoesNotContain(UploadStateMachine.AllTransitions, t => t.From == UploadState.Cancelled);
        foreach (UploadState s in Enum.GetValues<UploadState>())
            if (s != UploadState.Succeeded && s != UploadState.Cancelled)
                Assert.Contains(UploadStateMachine.AllTransitions, t => t.From == s);
    }
```

- [ ] **Step 2: 运行单元测试确认失败**

Run: `cd "/e/Viding/Retail Trace Code" && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter UploadStateMachineTests`
Expected: 编译失败 CS0117（`UploadState` 不含 `Cancelled`、`UploadEvent` 不含 `AllocationCancelled`）——这就是本步的 RED。

- [ ] **Step 3: 扩展 Domain 枚举与状态机**

`UploadState.cs` 全文替换为：

```csharp
namespace TraceFlow.Domain.Uploads;

/// <summary>PRD §9.2 平台子任务状态（12 态）+ 本地行政终态 Cancelled（IN-09 裁定，非平台状态）。</summary>
public enum UploadState
{
    Blocked, Ready, Dispatching, Accepted, AcceptanceUnknown, Processing,
    RetryWait, PartialSuccess, ResultUnresolved, Succeeded, Failed, ManualAction,
    Cancelled
}
```

`UploadEvent.cs` 全文替换为：

```csharp
namespace TraceFlow.Domain.Uploads;

public enum UploadEvent
{
    EligibilitySatisfied, EligibilityLost, Claimed,
    ReceiptAccepted, PartialEvidence, Rejected, TransportUncertain, DuplicateBillCode, LeaseLost,
    QueryProcessing, QueryAllSuccess, QueryPartial, QueryFailed, QueryUnresolved,
    QueryConfirmedAccepted, QueryConfirmedNotAccepted,
    ObservationExpired, RetryDue, ManualEscalate, ControlledRetryApproved,
    EvidenceVerifiedAccepted, EvidenceVerifiedProcessing,
    AllocationCancelled
}
```

`UploadStateMachine.cs` 的 Table 初始化器末尾（`[(UploadState.ManualAction, UploadEvent.QueryFailed)] = UploadState.Failed,` 之后）追加：

```csharp
        // IN-09：从未发起网络调用的冻结任务可原子取消；发送后任何状态不可达 Cancelled
        [(UploadState.Blocked, UploadEvent.AllocationCancelled)] = UploadState.Cancelled,
        [(UploadState.Ready, UploadEvent.AllocationCancelled)] = UploadState.Cancelled,
```

- [ ] **Step 4: 运行单元测试确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（原 136 + 新增理论行，0 失败）。

- [ ] **Step 5: 写失败的迁移集成测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Migrations/Migration0008Tests.cs`：

```csharp
using Dapper;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class Migration0008Tests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Migration0008Tests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task M2_tables_exist_with_expected_columns()
    {
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME IN ('inbound_match','top_rate_budget','top_channel_pause')");
        Assert.Equal(3, n);
        var dwbh = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='inbound_match' AND COLUMN_NAME='supplier_dwbh' AND CHARACTER_MAXIMUM_LENGTH=64");
        Assert.Equal(1, dwbh);
        var win = await c.ExecuteScalarAsync<int>(@"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='download_progress' AND COLUMN_NAME IN ('window_start','window_end') AND DATA_TYPE='datetime2'");
        Assert.Equal(2, win);
    }

    [Fact]
    public async Task Inbound_match_unique_per_snapshot_and_state_check()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash,raw_header)
OUTPUT inserted.id
VALUES(@org,@sys,N'M2-MATCH-1',N'PURCHASE_IN',1,REPLICATE('b',64),N'{}')", new { org = orgId, sys = sysId });
        await c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state)
VALUES(@org,N'MOCK',@snap,N'DW0001',N'UNMATCHED')", new { org = orgId, snap = snapId });
        // 同快照重复→唯一约束拒绝
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,match_state)
VALUES(@org,N'MOCK',@snap,N'COMPLETE')", new { org = orgId, snap = snapId }));
        // 非法 match_state→CHECK 拒绝
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,match_state)
VALUES(@org,N'MOCK',@snap,N'MATCHED')", new { org = orgId, snap = snapId }));
    }

    [Fact]
    public async Task Rate_budget_unique_per_env_key_and_pause_filtered_unique()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(N'MOCK',N'appkey:total')");
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(N'MOCK',N'appkey:total')"));
        // LIVE/MOCK 同键互不冲突（硬隔离）
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(N'LIVE',N'appkey:total')");
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(N'MOCK',N'method:listupout',N'auth',N'test')");
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(N'MOCK',N'method:listupout',N'auth2',N'test')"));
        // 解除后允许再次暂停（过滤唯一索引只约束 ACTIVE）
        await c.ExecuteAsync("UPDATE dbo.top_channel_pause SET is_active=0 WHERE env=N'MOCK' AND scope_key=N'method:listupout'");
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(N'MOCK',N'method:listupout',N'auth3',N'test')");
    }

    [Fact]
    public async Task Task_and_part_state_checks_accept_cancelled()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
OUTPUT inserted.id
VALUES(@org,N'MOCK',N'KSOA:M2-CANCEL:PURCHASE_IN',N'PURCHASE_IN_102',REPLICATE('c',64),REPLICATE('d',64),N'Cancelled')",
            new { org = orgId });
        Assert.True(taskId > 0);
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
VALUES(@t,@org,N'MOCK',0,N'TFM2CANCEL',N'{}',REPLICATE('e',64),REPLICATE('f',64),N'Cancelled')",
            new { t = taskId, org = orgId });
        // 非法状态仍被拒绝
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => c.ExecuteAsync(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
VALUES(@org,N'MOCK',N'KSOA:M2-BAD:PURCHASE_IN',N'PURCHASE_IN_102',REPLICATE('c',64),REPLICATE('d',64),N'Deleted')",
            new { org = orgId }));
    }

    [Fact]
    public async Task Migration_0008_recorded_with_checksum()
    {
        await using var c = await _fx.OpenAsync();
        var ok = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.schema_version WHERE version=8 AND success=1 AND name LIKE '0008_%'");
        Assert.Equal(1, ok);
    }
}
```

- [ ] **Step 6: 运行迁移测试确认失败**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter Migration0008Tests`
Expected: FAIL（表不存在/列不存在/schema_version 无 version=8）。

- [ ] **Step 7: 写 0008 迁移**

Create `traceflow/db/migrations/0008_m2_upstream_allocation.sql`（以换行结尾；2008 R2 兼容；0001–0007 一字节不动）：

```sql
-- 0008 M2：入库匹配维度/持久限流预算/授权范围暂停/下载窗口列/Cancelled 行政终态（IN-09）
CREATE TABLE dbo.inbound_match (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_inbound_match PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_imatch_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  source_snapshot_id bigint NOT NULL CONSTRAINT FK_imatch_snapshot REFERENCES dbo.source_snapshot(id),
  supplier_dwbh nvarchar(64) COLLATE Latin1_General_BIN2 NULL,
  match_state nvarchar(16) NOT NULL CONSTRAINT DF_imatch_state DEFAULT N'UNMATCHED',
  reason nvarchar(500) NULL,
  evidence_json nvarchar(max) NULL,
  version int NOT NULL CONSTRAINT DF_imatch_version DEFAULT 1,
  matched_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_inbound_match UNIQUE (org_id, env, source_snapshot_id),
  CONSTRAINT CK_imatch_state CHECK (match_state IN (N'NOT_REQUIRED',N'UNMATCHED',N'PARTIAL',N'AMBIGUOUS',N'CONFLICT',N'COMPLETE')),
  CONSTRAINT CK_imatch_env CHECK (env IN (N'LIVE',N'MOCK'))
);
GO
CREATE TABLE dbo.top_rate_budget (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rate_budget PRIMARY KEY,
  env char(4) NOT NULL,
  budget_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  next_allowed_at datetime2(3) NULL,
  day_count int NOT NULL CONSTRAINT DF_budget_day DEFAULT 0,
  day_window_start datetime2(3) NULL,
  minute_count int NOT NULL CONSTRAINT DF_budget_min DEFAULT 0,
  minute_window_start datetime2(3) NULL,
  second_count int NOT NULL CONSTRAINT DF_budget_sec DEFAULT 0,
  second_window_start datetime2(3) NULL,
  day_limit int NULL,
  minute_limit int NULL,
  second_limit int NULL,
  updated_at datetime2(3) NOT NULL CONSTRAINT DF_budget_at DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_rate_budget UNIQUE (env, budget_key),
  CONSTRAINT CK_budget_env CHECK (env IN (N'LIVE',N'MOCK'))
);
GO
CREATE TABLE dbo.top_channel_pause (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_channel_pause PRIMARY KEY,
  env char(4) NOT NULL,
  scope_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  reason nvarchar(500) NOT NULL,
  is_active bit NOT NULL CONSTRAINT DF_pause_active DEFAULT 1,
  paused_at datetime2(3) NOT NULL CONSTRAINT DF_pause_at DEFAULT SYSUTCDATETIME(),
  paused_by nvarchar(64) NOT NULL,
  resumed_at datetime2(3) NULL,
  resumed_by nvarchar(64) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT CK_pause_env CHECK (env IN (N'LIVE',N'MOCK'))
);
CREATE UNIQUE INDEX UX_pause_active ON dbo.top_channel_pause(env, scope_key) WHERE is_active = 1;
GO
ALTER TABLE dbo.download_progress ADD window_start datetime2(3) NULL;
ALTER TABLE dbo.download_progress ADD window_end datetime2(3) NULL;
GO
ALTER TABLE dbo.upload_task DROP CONSTRAINT CK_task_state;
ALTER TABLE dbo.upload_task ADD CONSTRAINT CK_task_state CHECK (state IN (N'Blocked',N'Ready',N'Dispatching',N'Accepted',N'AcceptanceUnknown',N'Processing',N'RetryWait',N'PartialSuccess',N'ResultUnresolved',N'Succeeded',N'Failed',N'ManualAction',N'Cancelled'));
GO
ALTER TABLE dbo.upload_part DROP CONSTRAINT CK_part_state;
ALTER TABLE dbo.upload_part ADD CONSTRAINT CK_part_state CHECK (state IN (N'Blocked',N'Ready',N'Dispatching',N'Accepted',N'AcceptanceUnknown',N'Processing',N'RetryWait',N'PartialSuccess',N'ResultUnresolved',N'Succeeded',N'Failed',N'ManualAction',N'Cancelled'));
```

注意：`Sql2008CompatibilityTests` 会扫描迁移文本禁用词——本文件不得出现 OFFSET/FETCH/IIF/CONCAT/TRIM/THROW/DROP IF EXISTS 等（上述 DDL 已合规）。

- [ ] **Step 8: 运行迁移测试与全量集成确认通过**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（原 180 + Migration0008Tests 5 个）。M1 既有测试不得回归。

- [ ] **Step 9: Commit**

```bash
cd "/e/Viding/Retail Trace Code"
git add traceflow/db/migrations/0008_m2_upstream_allocation.sql traceflow/src/TraceFlow.Domain/Uploads/UploadState.cs traceflow/src/TraceFlow.Domain/Uploads/UploadEvent.cs traceflow/src/TraceFlow.Domain/Uploads/UploadStateMachine.cs traceflow/tests/TraceFlow.UnitTests/Uploads/UploadStateMachineTests.cs traceflow/tests/TraceFlow.IntegrationTests/Migrations/Migration0008Tests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): 0008 migration (inbound_match/rate budget/channel pause) + Cancelled administrative terminal state (IN-09)"
```

---

### Task 2: TOP 响应解析器（TOP-01）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Top/TopOutcome.cs`
- Create: `traceflow/src/TraceFlow.Domain/Top/TopErrorTokens.cs`
- Create: `traceflow/src/TraceFlow.Domain/Top/TopResponseParser.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Top/TopResponseParserTests.cs`

**Interfaces:**
- Consumes: 无（纯 Domain，System.Text.Json）。
- Produces（Task 3/4/5/6 逐字使用）:
  - `enum TopOutcomeKind { Success, PartialEvidence, AuthError, RateLimited, SystemError, BusinessRejected, TransportUncertain, DuplicateBillCode }`
  - `sealed record TopOutcome(TopOutcomeKind Kind, string? Code, string? SubCode, string? Message, string? ResultJson, string? RequestId, int? RetryAfterSeconds)`
  - `static class TopErrorTokens { const string AuthError="FAIL_BIZ_AUTH_ERROR"; const string PartialSuccess="WARN_BIZ_PARTSUCCESS"; const string RateLimitPrefix="FAIL_BIZ_RATE"; const string DuplicateBillCode="FAIL_BIZ_BILL_CODE_DUPLICATE"; }`（后两者为候选，待 G-TOP）
  - `static TopOutcome TopResponseParser.Parse(bool transportOk, string? errorJson, string? businessJson, int? retryAfterSeconds)`——errorJson 为平台 `error_response` 节点 JSON；businessJson 为业务 `*_response` 节点 JSON；两者皆 null 视为系统错误。

- [ ] **Step 1: 写失败的单元测试**

Create `traceflow/tests/TraceFlow.UnitTests/Top/TopResponseParserTests.cs`：

```csharp
using TraceFlow.Domain.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

public class TopResponseParserTests
{
    private const string OkBusiness = """
        {"response_success":true,"request_id":"req-1","result":{"total_pages":1}}
        """;

    [Fact]
    public void Transport_failure_is_uncertain_never_success()
    {
        var o = TopResponseParser.Parse(false, null, OkBusiness, null);
        Assert.Equal(TopOutcomeKind.TransportUncertain, o.Kind);
    }

    [Fact]
    public void Success_keeps_result_raw_and_request_id()
    {
        var o = TopResponseParser.Parse(true, null, OkBusiness, null);
        Assert.Equal(TopOutcomeKind.Success, o.Kind);
        Assert.Equal("req-1", o.RequestId);
        Assert.Contains("total_pages", o.ResultJson);
    }

    [Fact]
    public void Error_envelope_auth_token_maps_auth_error()
    {
        var err = """{"code":15,"msg":"Remote service error","sub_code":"FAIL_BIZ_AUTH_ERROR","sub_msg":"无权限","request_id":"req-2"}""";
        var o = TopResponseParser.Parse(true, err, null, null);
        Assert.Equal(TopOutcomeKind.AuthError, o.Kind);
        Assert.Equal("FAIL_BIZ_AUTH_ERROR", o.SubCode);
        Assert.Equal("req-2", o.RequestId);
    }

    [Fact]
    public void Error_envelope_rate_prefix_maps_rate_limited_with_retry_after()
    {
        var err = """{"code":7,"msg":"App Call Limited","sub_code":"FAIL_BIZ_RATE_LIMIT","request_id":"req-3"}""";
        var o = TopResponseParser.Parse(true, err, null, 12);
        Assert.Equal(TopOutcomeKind.RateLimited, o.Kind);
        Assert.Equal(12, o.RetryAfterSeconds);
    }

    [Fact]
    public void Error_envelope_other_maps_system_error()
    {
        var err = """{"code":2,"msg":"Service unavailable","sub_code":"isp.top-remote-connection-timeout"}""";
        var o = TopResponseParser.Parse(true, err, null, null);
        Assert.Equal(TopOutcomeKind.SystemError, o.Kind);
        Assert.Equal("isp.top-remote-connection-timeout", o.SubCode);
    }

    [Fact]
    public void Business_failure_is_never_success()
    {
        var biz = """{"response_success":false,"msg_code":"FAIL_BIZ_CHECK","sub_msg_code":"FAIL_BIZ_BATCH_CONFLICT","sub_msg":"批号不符","request_id":"req-4"}""";
        var o = TopResponseParser.Parse(true, null, biz, null);
        Assert.Equal(TopOutcomeKind.BusinessRejected, o.Kind);
        Assert.Equal("FAIL_BIZ_CHECK", o.Code);
        Assert.Equal("FAIL_BIZ_BATCH_CONFLICT", o.SubCode);
        Assert.Equal("批号不符", o.Message);
    }

    [Fact]
    public void Missing_response_success_flag_is_business_rejected()
    {
        var o = TopResponseParser.Parse(true, null, """{"result":{}}""", null);
        Assert.Equal(TopOutcomeKind.BusinessRejected, o.Kind);
    }

    [Fact]
    public void Partial_success_token_maps_partial_evidence_even_when_flag_false()
    {
        var biz = """{"response_success":false,"msg_code":"WARN_BIZ_PARTSUCCESS","result":{"success_count":3}}""";
        var o = TopResponseParser.Parse(true, null, biz, null);
        Assert.Equal(TopOutcomeKind.PartialEvidence, o.Kind);
        Assert.Contains("success_count", o.ResultJson);
    }

    [Fact]
    public void Duplicate_bill_code_sub_token_maps_duplicate()
    {
        var biz = """{"response_success":false,"sub_msg_code":"FAIL_BIZ_BILL_CODE_DUPLICATE","sub_msg":"单据号重复"}""";
        var o = TopResponseParser.Parse(true, null, biz, null);
        Assert.Equal(TopOutcomeKind.DuplicateBillCode, o.Kind);
    }

    [Fact]
    public void Business_level_auth_and_rate_tokens_also_mapped()
    {
        var auth = TopResponseParser.Parse(true, null, """{"response_success":false,"sub_msg_code":"FAIL_BIZ_AUTH_ERROR"}""", null);
        Assert.Equal(TopOutcomeKind.AuthError, auth.Kind);
        var rate = TopResponseParser.Parse(true, null, """{"response_success":false,"sub_msg_code":"FAIL_BIZ_RATE_APP"}""", 5);
        Assert.Equal(TopOutcomeKind.RateLimited, rate.Kind);
        Assert.Equal(5, rate.RetryAfterSeconds);
    }

    [Fact]
    public void Unparseable_json_is_system_error_not_success()
    {
        var o = TopResponseParser.Parse(true, "{not json", null, null);
        Assert.Equal(TopOutcomeKind.SystemError, o.Kind);
        var o2 = TopResponseParser.Parse(true, null, "<html>502</html>", null);
        Assert.Equal(TopOutcomeKind.SystemError, o2.Kind);
    }

    [Fact]
    public void Empty_envelope_is_system_error()
    {
        var o = TopResponseParser.Parse(true, null, null, null);
        Assert.Equal(TopOutcomeKind.SystemError, o.Kind);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter TopResponseParserTests`
Expected: 编译失败（命名空间 `TraceFlow.Domain.Top` 不存在）——RED。

- [ ] **Step 3: 实现 Domain/Top 三文件**

`TopOutcome.cs`：

```csharp
namespace TraceFlow.Domain.Top;

public enum TopOutcomeKind
{
    Success, PartialEvidence, AuthError, RateLimited, SystemError, BusinessRejected, TransportUncertain, DuplicateBillCode
}

public sealed record TopOutcome(
    TopOutcomeKind Kind,
    string? Code,
    string? SubCode,
    string? Message,
    string? ResultJson,
    string? RequestId,
    int? RetryAfterSeconds);
```

`TopErrorTokens.cs`：

```csharp
namespace TraceFlow.Domain.Top;

/// <summary>
/// AuthError/PartialSuccess 为规格 §3.3 与 PRD TOP-03 点名 token（已确认）。
/// RateLimitPrefix/DuplicateBillCode 为候选值，待 G-TOP 取证后修订（docs/contracts/top-contract.md）。
/// </summary>
public static class TopErrorTokens
{
    public const string AuthError = "FAIL_BIZ_AUTH_ERROR";
    public const string PartialSuccess = "WARN_BIZ_PARTSUCCESS";
    public const string RateLimitPrefix = "FAIL_BIZ_RATE";
    public const string DuplicateBillCode = "FAIL_BIZ_BILL_CODE_DUPLICATE";
}
```

`TopResponseParser.cs`：

```csharp
using System.Text.Json;

namespace TraceFlow.Domain.Top;

/// <summary>TOP-01：系统错误（error_response）与业务错误（response_success=false）分离；任何解析失败都不得当成功。</summary>
public static class TopResponseParser
{
    public static TopOutcome Parse(bool transportOk, string? errorJson, string? businessJson, int? retryAfterSeconds)
    {
        if (!transportOk)
            return new TopOutcome(TopOutcomeKind.TransportUncertain, null, null, "传输不确定，受理状态未知", null, null, retryAfterSeconds);

        if (!string.IsNullOrWhiteSpace(errorJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(errorJson);
                var r = doc.RootElement;
                var code = GetString(r, "code");
                var subCode = GetString(r, "sub_code");
                var msg = GetString(r, "sub_msg") ?? GetString(r, "msg");
                var reqId = GetString(r, "request_id");
                var token = subCode ?? code;
                var kind = ClassifyToken(token) ?? TopOutcomeKind.SystemError;
                return new TopOutcome(kind, code, subCode, msg, null, reqId, retryAfterSeconds);
            }
            catch (JsonException)
            {
                return new TopOutcome(TopOutcomeKind.SystemError, null, null, "error_response 不可解析", null, null, retryAfterSeconds);
            }
        }

        if (!string.IsNullOrWhiteSpace(businessJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(businessJson);
                var r = doc.RootElement;
                var msgCode = GetString(r, "msg_code");
                var subMsgCode = GetString(r, "sub_msg_code");
                var msg = GetString(r, "sub_msg") ?? GetString(r, "msg");
                var reqId = GetString(r, "request_id");
                string? resultJson = r.TryGetProperty("result", out var result) ? result.GetRawText() : null;

                if (msgCode == TopErrorTokens.PartialSuccess || subMsgCode == TopErrorTokens.PartialSuccess)
                    return new TopOutcome(TopOutcomeKind.PartialEvidence, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);
                if (subMsgCode == TopErrorTokens.DuplicateBillCode)
                    return new TopOutcome(TopOutcomeKind.DuplicateBillCode, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);

                var tokenKind = ClassifyToken(subMsgCode) ?? ClassifyToken(msgCode);
                if (tokenKind is TopOutcomeKind.AuthError or TopOutcomeKind.RateLimited)
                    return new TopOutcome(tokenKind.Value, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);

                var success = r.TryGetProperty("response_success", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (success)
                    return new TopOutcome(TopOutcomeKind.Success, msgCode, subMsgCode, msg, resultJson, reqId, retryAfterSeconds);
                var reason = flag.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "缺少 response_success" : msg;
                return new TopOutcome(TopOutcomeKind.BusinessRejected, msgCode, subMsgCode, reason, resultJson, reqId, retryAfterSeconds);
            }
            catch (JsonException)
            {
                return new TopOutcome(TopOutcomeKind.SystemError, null, null, "业务响应不可解析", null, null, retryAfterSeconds);
            }
        }

        return new TopOutcome(TopOutcomeKind.SystemError, null, null, "空响应", null, null, retryAfterSeconds);
    }

    private static TopOutcomeKind? ClassifyToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (token == TopErrorTokens.AuthError) return TopOutcomeKind.AuthError;
        if (token.StartsWith(TopErrorTokens.RateLimitPrefix, StringComparison.Ordinal)) return TopOutcomeKind.RateLimited;
        return null;
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
         : e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText()
         : null;
}
```

注意 `GetString` 对 `code`（TOP 系统错误 code 是数字）返回原始数字文本；`flag` 在 `TryGetProperty` 失败时为 default(JsonElement)（ValueKind=Undefined），`reason` 分支安全。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（12 个新 Fact）。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Domain/Top/ traceflow/tests/TraceFlow.UnitTests/Top/
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): TOP response parser separating system/business errors (TOP-01) with candidate tokens declared"
```

---

### Task 3: 持久限流预算 + 授权范围暂停（JOB-03 / §3.3）

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/RateBudgetService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/ChannelPauseService.cs`
- Modify: `traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs`（追加 2 异常）
- Modify: `traceflow/src/TraceFlow.Host/Security/ApiErrors.cs`（追加 2 映射）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Top/RateBudgetTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Top/ChannelPauseTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Top/RateBackoffTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `dbo.top_rate_budget`/`dbo.top_channel_pause`；Dapper `CommandDefinition` ct 模式。
- Produces（Task 4/5/6/12 逐字使用）:
  - `sealed class RateBudgetService`：`Task<TimeSpan?> TryAcquireAsync(SqlConnection conn, SqlTransaction tx, string env, IReadOnlyList<string> budgetKeys, DateTime nowUtc, CancellationToken ct)`（null=已获取且计数+1；非 null=需等待，不消费任何计数）；`Task RegisterWaitAsync(SqlConnection conn, SqlTransaction tx, string env, IReadOnlyList<string> budgetKeys, DateTime waitUntilUtc, CancellationToken ct)`（next_allowed_at 取 max）；`static TimeSpan Backoff(int attemptCount, int jitterSeconds)` 与 `static TimeSpan Backoff(int attemptCount)`（30s×2^min(attempt,5) 上限 600s + 0..5s 抖动）。
  - `sealed class ChannelPauseService`：`Task PauseAsync(SqlConnection, SqlTransaction, string env, IReadOnlyList<string> scopeKeys, string reason, string actor, CancellationToken)`（幂等）；`Task<bool> IsPausedAsync(SqlConnection, SqlTransaction?, string env, IReadOnlyList<string> scopeKeys, CancellationToken)`；`Task<int> ResumeAsync(SqlConnection, SqlTransaction, string env, string scopeKey, string actor, CancellationToken)`；`Task<IReadOnlyList<PauseView>> ListAsync(SqlConnection, string env, CancellationToken)`。
  - `sealed record PauseView(long Id, string ScopeKey, string Reason, bool IsActive, DateTime PausedAtUtc, string PausedBy, DateTime? ResumedAtUtc, string? ResumedBy)`。
  - `sealed class TopRateLimitedException(TimeSpan wait)`（属性 `TimeSpan Wait`）；`sealed class ChannelPausedException(string scopeKey, string message)`（属性 `string ScopeKey`）。
  - ApiErrors：`TopRateLimitedException→(429,"TOP_RATE_LIMITED",true)`、`ChannelPausedException→(503,"TOP_CHANNEL_PAUSED",false)`，message 用 ex.Message。

- [ ] **Step 1: 写失败的单元测试（退避公式）**

Create `traceflow/tests/TraceFlow.UnitTests/Top/RateBackoffTests.cs`：

```csharp
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

public class RateBackoffTests
{
    [Theory]
    [InlineData(0, 0, 30)]
    [InlineData(1, 0, 60)]
    [InlineData(4, 0, 480)]
    [InlineData(5, 0, 600)]   // 30×2^5=960 → 上限 600
    [InlineData(9, 5, 605)]
    public void Backoff_formula(int attempt, int jitter, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RateBudgetService.Backoff(attempt, jitter));

    [Fact]
    public void Backoff_jitter_within_0_to_5_seconds()
    {
        for (var i = 0; i < 50; i++)
        {
            var t = RateBudgetService.Backoff(0);
            Assert.InRange(t.TotalSeconds, 30, 35);
        }
    }
}
```

- [ ] **Step 2: 写失败的集成测试（预算 + 暂停）**

Create `traceflow/tests/TraceFlow.IntegrationTests/Top/RateBudgetTests.cs`：

```csharp
using Dapper;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class RateBudgetTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public RateBudgetTests(Sql2008Fixture fx) => _fx = fx;
    private static string[] NewKeys() => new[] { "appkey:total", "method:t3." + Guid.NewGuid().ToString("N")[..8] };

    [Fact]
    public async Task Acquire_increments_counters_and_persists_across_connections()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;
        await using (var c = await _fx.OpenAsync())
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
            Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
            tx.Commit();
        }
        // “重启”=新连接：计数仍在（JOB-03 持久化）
        await using var c2 = await _fx.OpenAsync();
        var counts = await c2.QuerySingleAsync<CountsRow>(
            "SELECT day_count AS Day, minute_count AS Minute, second_count AS Second FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k",
            new { k = keys[0] });
        Assert.Equal(2, counts.Day);
        Assert.Equal(2, counts.Minute);
        Assert.Equal(2, counts.Second);
    }

    private sealed record CountsRow(int Day, int Minute, int Second);

    [Fact]
    public async Task Second_limit_exhaustion_returns_wait_without_consuming()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now, default));
        await c.ExecuteAsync("UPDATE dbo.top_rate_budget SET second_limit=1 WHERE env=N'MOCK' AND budget_key=@k", new { k = keys[1] }, tx);
        var wait = await svc.TryAcquireAsync(c, tx, "MOCK", keys, now.AddMilliseconds(100), default);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalMilliseconds, 1, 1000);
        // 未消费：method 键 second_count 仍为 1
        var n = await c.ExecuteScalarAsync<int>("SELECT second_count FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = keys[1] }, tx);
        Assert.Equal(1, n);
        tx.Commit();
    }

    [Fact]
    public async Task Window_roll_resets_counter()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        var past = DateTime.UtcNow.AddSeconds(-5);
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key,second_count,second_window_start,second_limit)
VALUES(N'MOCK',@k,1,@past,1)", new { k = keys[1], past }, tx);
        // 窗口已过期→重置计数→获取成功
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, DateTime.UtcNow, default));
        var n = await c.ExecuteScalarAsync<int>("SELECT second_count FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = keys[1] }, tx);
        Assert.Equal(1, n); // 重置 0 后 +1
        tx.Commit();
    }

    [Fact]
    public async Task Next_allowed_wait_and_register_wait_max_semantics()
    {
        var svc = new RateBudgetService();
        var keys = NewKeys();
        var now = DateTime.UtcNow;
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        await svc.RegisterWaitAsync(c, tx, "MOCK", keys, now.AddSeconds(30), default);
        // RegisterWait 对不存在行是 UPDATE 0 行——先确保行存在再注册
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", keys, now.AddSeconds(31), default)); // 创建行（等待已过）
        await svc.RegisterWaitAsync(c, tx, "MOCK", keys, now.AddSeconds(60), default);
        await svc.RegisterWaitAsync(c, tx, "MOCK", keys, now.AddSeconds(45), default); // 更早值不得回退
        var wait = await svc.TryAcquireAsync(c, tx, "MOCK", keys, now.AddSeconds(50), default);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalSeconds, 9, 11);
        tx.Commit();
    }

    [Fact]
    public async Task Live_and_mock_budgets_isolated()
    {
        var svc = new RateBudgetService();
        var key = "method:t3iso." + Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;
        await using var c = await _fx.OpenAsync();
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction();
        await svc.RegisterWaitAsync(c, tx, "MOCK", new[] { key }, now, default);
        Assert.Null(await svc.TryAcquireAsync(c, tx, "MOCK", new[] { key }, now, default));
        await c.ExecuteAsync("UPDATE dbo.top_rate_budget SET next_allowed_at=@w WHERE env=N'MOCK' AND budget_key=@k", new { w = now.AddHours(1), k = key }, tx);
        // LIVE 同键不受 MOCK 暂停影响（硬隔离）
        Assert.Null(await svc.TryAcquireAsync(c, tx, "LIVE", new[] { key }, now, default));
        tx.Commit();
    }
}
```

Create `traceflow/tests/TraceFlow.IntegrationTests/Top/ChannelPauseTests.cs`：

```csharp
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class ChannelPauseTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public ChannelPauseTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Pause_idempotent_scope_isolated_resume_and_repause()
    {
        var svc = new ChannelPauseService();
        var scope = "method:t3pause." + Guid.NewGuid().ToString("N")[..8];
        var keys = new[] { scope, "credential:top" };
        await using var c = await _fx.OpenAsync();
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            await svc.PauseAsync(c, tx, "MOCK", keys, "FAIL_BIZ_AUTH_ERROR", "test", default);
            await svc.PauseAsync(c, tx, "MOCK", keys, "again", "test", default); // 幂等
            tx.Commit();
        }
        Assert.True(await svc.IsPausedAsync(c, null, "MOCK", new[] { scope }, default));
        Assert.False(await svc.IsPausedAsync(c, null, "LIVE", new[] { scope }, default)); // env 隔离
        Assert.False(await svc.IsPausedAsync(c, null, "MOCK", new[] { "method:other" }, default)); // 只影响相关管道
        var list = await svc.ListAsync(c, "MOCK", default);
        Assert.Contains(list, p => p.ScopeKey == scope && p.IsActive);
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            Assert.Equal(1, await svc.ResumeAsync(c, tx, "MOCK", scope, "admin", default));
            tx.Commit();
        }
        Assert.False(await svc.IsPausedAsync(c, null, "MOCK", new[] { scope }, default));
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)c.BeginTransaction())
        {
            await svc.PauseAsync(c, tx, "MOCK", new[] { scope }, "repause", "test", default); // 解除后可再暂停
            tx.Commit();
        }
        Assert.True(await svc.IsPausedAsync(c, null, "MOCK", new[] { scope }, default));
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter RateBackoffTests`
Expected: 编译失败（`RateBudgetService` 不存在）——RED。

- [ ] **Step 4: 实现异常与 ApiErrors 映射**

`ServiceExceptions.cs` 末尾追加：

```csharp
public sealed class TopRateLimitedException : Exception
{
    public TimeSpan Wait { get; }
    public TopRateLimitedException(TimeSpan wait) : base($"TOP 限流，需等待 {wait.TotalSeconds:F0} 秒") => Wait = wait;
}
public sealed class ChannelPausedException : Exception
{
    public string ScopeKey { get; }
    public ChannelPausedException(string scopeKey, string message) : base(message) => ScopeKey = scopeKey;
}
```

`ApiErrors.cs`：在 `ImportBoundsException => (422, "IMPORT_BOUNDS", false),` 行后插入：

```csharp
            TopRateLimitedException => (429, "TOP_RATE_LIMITED", true),
            ChannelPausedException => (503, "TOP_CHANNEL_PAUSED", false),
```

message switch 的第一臂改为（追加两个类型）：

```csharp
            IdempotencyConflictException or SecretUndecryptableException
                or AdapterNotCertifiedException or SettingValidationException
                or SettingVersionException or ImportBoundsException or FormatException
                or TopRateLimitedException or ChannelPausedException => ex.Message,
```

- [ ] **Step 5: 实现 RateBudgetService**

Create `traceflow/src/TraceFlow.Infrastructure/Top/RateBudgetService.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Top;

/// <summary>JOB-03：持久共享限流预算。计数落库重启不清零；限值列 NULL=本地不设帽（G-TOP 未取证不虚构限值）。</summary>
public sealed class RateBudgetService
{
    private sealed record BudgetRow(
        long Id, DateTime? NextAllowedAt,
        int DayCount, DateTime? DayWindowStart,
        int MinuteCount, DateTime? MinuteWindowStart,
        int SecondCount, DateTime? SecondWindowStart,
        int? DayLimit, int? MinuteLimit, int? SecondLimit);

    public async Task<TimeSpan?> TryAcquireAsync(SqlConnection conn, SqlTransaction tx, string env,
        IReadOnlyList<string> budgetKeys, DateTime nowUtc, CancellationToken ct)
    {
        var rows = new List<BudgetRow>();
        foreach (var key in budgetKeys.OrderBy(k => k, StringComparer.Ordinal)) // 固定加锁顺序防死锁
        {
            var row = await SelectAsync(conn, tx, env, key, ct);
            if (row is null)
            {
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO dbo.top_rate_budget(env,budget_key) VALUES(@env,@key)",
                        new { env, key }, tx, cancellationToken: ct));
                }
                catch (SqlException e) when (e.Number is 2601 or 2627) { }
                row = await SelectAsync(conn, tx, env, key, ct);
            }
            rows.Add(row!);
        }

        var wait = TimeSpan.Zero;
        var updates = new List<(BudgetRow Row, int Day, DateTime DayStart, int Minute, DateTime MinuteStart, int Second, DateTime SecondStart)>();
        foreach (var r in rows)
        {
            if (r.NextAllowedAt is DateTime na && na > nowUtc)
                wait = Max(wait, na - nowUtc);
            var (day, dayStart) = Roll(r.DayCount, r.DayWindowStart, nowUtc, TimeSpan.FromDays(1));
            var (minute, minuteStart) = Roll(r.MinuteCount, r.MinuteWindowStart, nowUtc, TimeSpan.FromMinutes(1));
            var (second, secondStart) = Roll(r.SecondCount, r.SecondWindowStart, nowUtc, TimeSpan.FromSeconds(1));
            if (r.DayLimit is int dl && day >= dl) wait = Max(wait, dayStart.AddDays(1) - nowUtc);
            if (r.MinuteLimit is int ml && minute >= ml) wait = Max(wait, minuteStart.AddMinutes(1) - nowUtc);
            if (r.SecondLimit is int sl && second >= sl) wait = Max(wait, secondStart.AddSeconds(1) - nowUtc);
            updates.Add((r, day, dayStart, minute, minuteStart, second, secondStart));
        }

        if (wait > TimeSpan.Zero) return wait; // 超限不消费任何计数（全有或全无）

        foreach (var (r, day, dayStart, minute, minuteStart, second, secondStart) in updates)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.top_rate_budget
SET day_count=@day, day_window_start=@dayStart,
    minute_count=@minute, minute_window_start=@minuteStart,
    second_count=@second, second_window_start=@secondStart,
    updated_at=@now
WHERE id=@id",
                new { day = day + 1, dayStart, minute = minute + 1, minuteStart, second = second + 1, secondStart, now = nowUtc, id = r.Id },
                tx, cancellationToken: ct));
        return null;
    }

    public async Task RegisterWaitAsync(SqlConnection conn, SqlTransaction tx, string env,
        IReadOnlyList<string> budgetKeys, DateTime waitUntilUtc, CancellationToken ct)
    {
        foreach (var key in budgetKeys)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.top_rate_budget
SET next_allowed_at = CASE WHEN next_allowed_at IS NULL OR next_allowed_at < @w THEN @w ELSE next_allowed_at END,
    updated_at = SYSUTCDATETIME()
WHERE env=@env AND budget_key=@key",
                new { env, key, w = waitUntilUtc }, tx, cancellationToken: ct));
    }

    public static TimeSpan Backoff(int attemptCount, int jitterSeconds)
        => TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(attemptCount, 5))) + jitterSeconds);

    public static TimeSpan Backoff(int attemptCount) => Backoff(attemptCount, Random.Shared.Next(0, 6));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static (int Count, DateTime Start) Roll(int count, DateTime? start, DateTime now, TimeSpan period)
        => start is DateTime s && now < s + period ? (count, s) : (0, now);

    private static async Task<BudgetRow?> SelectAsync(SqlConnection conn, SqlTransaction tx, string env, string key, CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<BudgetRow>(new CommandDefinition(@"
SELECT id AS Id, next_allowed_at AS NextAllowedAt,
       day_count AS DayCount, day_window_start AS DayWindowStart,
       minute_count AS MinuteCount, minute_window_start AS MinuteWindowStart,
       second_count AS SecondCount, second_window_start AS SecondWindowStart,
       day_limit AS DayLimit, minute_limit AS MinuteLimit, second_limit AS SecondLimit
FROM dbo.top_rate_budget WITH (UPDLOCK, HOLDLOCK)
WHERE env=@env AND budget_key=@key",
            new { env, key }, tx, cancellationToken: ct));
}
```

- [ ] **Step 6: 实现 ChannelPauseService**

Create `traceflow/src/TraceFlow.Infrastructure/Top/ChannelPauseService.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Top;

public sealed record PauseView(long Id, string ScopeKey, string Reason, bool IsActive, DateTime PausedAtUtc, string PausedBy, DateTime? ResumedAtUtc, string? ResumedBy);

/// <summary>§3.3/JOB-05：FAIL_BIZ_AUTH_ERROR 按接口/主体/凭证范围暂停；只影响相关管道，恢复须人工动作。</summary>
public sealed class ChannelPauseService
{
    public async Task PauseAsync(SqlConnection conn, SqlTransaction tx, string env,
        IReadOnlyList<string> scopeKeys, string reason, string actor, CancellationToken ct)
    {
        foreach (var key in scopeKeys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var active = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.top_channel_pause WITH (UPDLOCK, HOLDLOCK) WHERE env=@env AND scope_key=@key AND is_active=1",
                new { env, key }, tx, cancellationToken: ct));
            if (active > 0) continue;
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO dbo.top_channel_pause(env,scope_key,reason,paused_by) VALUES(@env,@key,@reason,@actor)",
                    new { env, key, reason, actor }, tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627) { } // 并发暂停幂等
        }
    }

    public async Task<bool> IsPausedAsync(SqlConnection conn, SqlTransaction? tx, string env,
        IReadOnlyList<string> scopeKeys, CancellationToken ct)
    {
        if (scopeKeys.Count == 0) return false;
        var n = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.top_channel_pause
WHERE env=@env AND is_active=1 AND scope_key IN @keys",
            new { env, keys = scopeKeys.ToArray() }, tx, cancellationToken: ct));
        return n > 0;
    }

    public async Task<int> ResumeAsync(SqlConnection conn, SqlTransaction tx, string env, string scopeKey, string actor, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.top_channel_pause SET is_active=0, resumed_at=SYSUTCDATETIME(), resumed_by=@actor
WHERE env=@env AND scope_key=@key AND is_active=1",
            new { env, key = scopeKey, actor }, tx, cancellationToken: ct));

    public async Task<IReadOnlyList<PauseView>> ListAsync(SqlConnection conn, string env, CancellationToken ct)
        => (await conn.QueryAsync<PauseView>(new CommandDefinition(@"
SELECT id AS Id, scope_key AS ScopeKey, reason AS Reason, is_active AS IsActive,
       paused_at AS PausedAtUtc, paused_by AS PausedBy, resumed_at AS ResumedAtUtc, resumed_by AS ResumedBy
FROM dbo.top_channel_pause WHERE env=@env ORDER BY paused_at DESC, id DESC",
            new { env }, cancellationToken: ct))).ToList();
}
```

- [ ] **Step 7: 运行全部测试确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（单元 +6；集成 +6）。

- [ ] **Step 8: Commit**

```bash
git add traceflow/src/TraceFlow.Infrastructure/Top/RateBudgetService.cs traceflow/src/TraceFlow.Infrastructure/Top/ChannelPauseService.cs traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs traceflow/src/TraceFlow.Host/Security/ApiErrors.cs traceflow/tests/TraceFlow.IntegrationTests/Top/ traceflow/tests/TraceFlow.UnitTests/Top/RateBackoffTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): persistent rate budget (JOB-03) and scoped auth-error channel pause (spec 3.3)"
```

---

### Task 4: ITopGateway 端口 + Fake/Real 网关 + TopClientService + DI

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Abstractions/ITopGateway.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/FakeTopGateway.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/RealTopGateway.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/TopGatewayFactory.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/TopFixtureHandler.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Top/TopClientService.cs`
- Modify: `traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs`（+TopNotCertifiedException）
- Modify: `traceflow/src/TraceFlow.Host/Security/ApiErrors.cs`（+422 TOP_NOT_CERTIFIED）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（sourceMode 块后接线）
- Modify: `traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj`、`traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj`（fixtures/top 复制 glob）
- Create: `docs/contracts/top-contract.md`（候选合同声明）
- Test: `traceflow/tests/TraceFlow.UnitTests/Top/TopGatewayTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Top/TopClientServiceTests.cs`

**Interfaces:**
- Consumes: Task 2 `TopResponseParser.Parse(bool, string?, string?, int?)→TopOutcome`、`TopOutcomeKind`；Task 3 `RateBudgetService`/`ChannelPauseService`/`TopRateLimitedException`/`ChannelPausedException`；`IAuditLog.AppendAsync(SqlConnection, SqlTransaction?, AuditEntry)`；`AuditEntry(string Actor, string ActorKind, string Action, string ObjectType, string? ObjectId, string? Reason, string? BeforeDigest, string? AfterDigest, string? RequestId, string? RuleVersion, string Env)`；`AuditLog` 无参构造。
- Produces（Task 5/6 逐字使用）:
  - `sealed record TopCall(string Method, IReadOnlyDictionary<string, string> Parameters, string Env)`
  - `sealed record TopRawResponse(bool TransportOk, int? HttpStatus, string? ErrorJson, string? BusinessJson, int? RetryAfterSeconds)`
  - `interface ITopGateway { Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct); }`
  - `sealed class FakeTopGateway(Func<TopCall, TopRawResponse> handler)`（Env≠MOCK 抛 TopNotCertifiedException）
  - `sealed class RealTopGateway`（恒抛 TopNotCertifiedException）
  - `static ITopGateway TopGatewayFactory.Create(string mode, string fixtureDir)`
  - `sealed class TopFixtureHandler(string dir)`，`TopRawResponse Handle(TopCall call)`
  - `sealed class TopClientService(string connectionString, ITopGateway gateway, RateBudgetService budget, ChannelPauseService pause, IAuditLog audit)`：`Task<TopOutcome> CallAsync(string env, string method, IReadOnlyDictionary<string,string> parameters, string? refEntId, string actor, CancellationToken ct)`；`static IReadOnlyList<string> ScopeKeys(string method, string? refEntId)`；`static IReadOnlyList<string> BudgetKeys(string method, string? refEntId)`。
  - `sealed class TopNotCertifiedException(string message)`；ApiErrors→(422,"TOP_NOT_CERTIFIED",false)。
  - 配置键 `TraceFlow:TopMode`（fixture|real，默认 fixture）。

- [ ] **Step 1: 写失败的单元测试（网关隔离 + fixture 处理器）**

Create `traceflow/tests/TraceFlow.UnitTests/Top/TopGatewayTests.cs`：

```csharp
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.UnitTests.Top;

public class TopGatewayTests
{
    private static TopCall Call(string env = "MOCK", string method = "listupout")
        => new(method, new Dictionary<string, string> { ["page_no"] = "1" }, env);

    [Fact]
    public async Task Fake_gateway_rejects_live_env()
    {
        var gw = new FakeTopGateway(_ => new TopRawResponse(true, 200, null, """{"response_success":true}""", null));
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => gw.CallAsync(Call("LIVE"), default));
    }

    [Fact]
    public async Task Real_gateway_is_not_certified_placeholder()
        => await Assert.ThrowsAsync<TopNotCertifiedException>(() => new RealTopGateway().CallAsync(Call(), default));

    [Fact]
    public void Factory_unknown_mode_throws()
        => Assert.Throws<ArgumentException>(() => TopGatewayFactory.Create("sandbox", "unused"));

    [Fact]
    public void Fixture_handler_matches_params_and_extracts_error_node()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t4top." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "listupout.json"), """
                [
                  {"match":{"page_no":"9"},"response":{"error_response":{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT"}},"retryAfterSeconds":8},
                  {"match":{"page_no":"1"},"response":{"alibaba_alihealth_drugtrace_top_listupout_response":{"response_success":true,"request_id":"r1","result":{"total_pages":2}}}}
                ]
                """);
            var h = new TopFixtureHandler(dir);
            var ok = h.Handle(Call());
            Assert.True(ok.TransportOk);
            Assert.Null(ok.ErrorJson);
            Assert.Contains("total_pages", ok.BusinessJson);
            var rate = h.Handle(new TopCall("listupout", new Dictionary<string, string> { ["page_no"] = "9" }, "MOCK"));
            Assert.Contains("FAIL_BIZ_RATE_LIMIT", rate.ErrorJson);
            Assert.Equal(8, rate.RetryAfterSeconds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Fixture_handler_missing_file_defaults_success_and_transport_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t4top." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var h = new TopFixtureHandler(dir);
            var def = h.Handle(Call(method: "no.such.method"));
            Assert.True(def.TransportOk);
            Assert.Contains("response_success", def.BusinessJson);
            File.WriteAllText(Path.Combine(dir, "down.json"), """[{"transportOk":false}]""");
            var down = h.Handle(Call(method: "down"));
            Assert.False(down.TransportOk);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: 写失败的集成测试（TopClientService 编排）**

Create `traceflow/tests/TraceFlow.IntegrationTests/Top/TopClientServiceTests.cs`：

```csharp
using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using Xunit;

namespace TraceFlow.IntegrationTests.Top;

public class TopClientServiceTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public TopClientServiceTests(Sql2008Fixture fx) => _fx = fx;

    private TopClientService Build(Func<TopCall, TopRawResponse> handler)
        => new(_fx.ConnectionString, new FakeTopGateway(handler), new RateBudgetService(), new ChannelPauseService(), new AuditLog());

    private static IReadOnlyDictionary<string, string> NoParams => new Dictionary<string, string>();

    [Fact]
    public async Task Success_increments_budget_and_returns_outcome()
    {
        var method = "t4ok." + Guid.NewGuid().ToString("N")[..8];
        var svc = Build(_ => new TopRawResponse(true, 200, null, """{"response_success":true,"request_id":"r-ok","result":{}}""", null));
        var o = await svc.CallAsync("MOCK", method, NoParams, null, "test", default);
        Assert.Equal(TopOutcomeKind.Success, o.Kind);
        await using var c = await _fx.OpenAsync();
        var n = await c.ExecuteScalarAsync<int>("SELECT day_count FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = "method:" + method });
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Auth_error_pauses_scopes_audits_and_blocks_next_call_without_http()
    {
        var method = "t4auth." + Guid.NewGuid().ToString("N")[..8];
        var calls = 0;
        var svc = Build(call =>
        {
            if (call.Method != method)
                return new TopRawResponse(true, 200, null, """{"response_success":true,"result":{}}""", null);
            calls++;
            return new TopRawResponse(true, 200, """{"code":15,"sub_code":"FAIL_BIZ_AUTH_ERROR","sub_msg":"无权限","request_id":"r-auth"}""", null, null);
        });
        var ex = await Assert.ThrowsAsync<ChannelPausedException>(() => svc.CallAsync("MOCK", method, NoParams, "REF-T4", "test", default));
        Assert.Equal("method:" + method, ex.ScopeKey);
        Assert.Equal(1, calls);
        // 第二次调用：暂停门禁在 HTTP 之前→不再触达网关
        await Assert.ThrowsAsync<ChannelPausedException>(() => svc.CallAsync("MOCK", method, NoParams, "REF-T4", "test", default));
        Assert.Equal(1, calls);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.top_channel_pause WHERE env=N'MOCK' AND scope_key=@k AND is_active=1", new { k = "ent:REF-T4" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action='top.channel.pause' AND object_id=@oid AND env=N'MOCK'", new { oid = "method:" + method }));
        // 其他方法不受影响（只暂停相关管道）
        var other = await svc.CallAsync("MOCK", method + ".other", NoParams, null, "test", default);
        Assert.Equal(TopOutcomeKind.Success, other.Kind);
    }

    [Fact]
    public async Task Rate_limit_registers_platform_wait_seconds_priority()
    {
        var method = "t4rate." + Guid.NewGuid().ToString("N")[..8];
        var svc = Build(_ => new TopRawResponse(true, 200, """{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT"}""", null, 42));
        var ex = await Assert.ThrowsAsync<TopRateLimitedException>(() => svc.CallAsync("MOCK", method, NoParams, null, "test", default));
        Assert.Equal(42, (int)ex.Wait.TotalSeconds); // 平台明确等待秒数优先于退避
        await using var c = await _fx.OpenAsync();
        var next = await c.ExecuteScalarAsync<DateTime>("SELECT next_allowed_at FROM dbo.top_rate_budget WHERE env=N'MOCK' AND budget_key=@k", new { k = "method:" + method });
        Assert.True(next > DateTime.UtcNow.AddSeconds(30));
    }

    [Fact]
    public async Task Live_env_through_fake_gateway_is_not_certified()
    {
        var svc = Build(_ => new TopRawResponse(true, 200, null, """{"response_success":true}""", null));
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => svc.CallAsync("LIVE", "t4live", NoParams, null, "test", default));
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --filter TopGatewayTests`
Expected: 编译失败（ITopGateway/FakeTopGateway 等不存在）——RED。

- [ ] **Step 4: 实现端口与异常**

Create `traceflow/src/TraceFlow.Application/Abstractions/ITopGateway.cs`：

```csharp
namespace TraceFlow.Application.Abstractions;

public sealed record TopCall(string Method, IReadOnlyDictionary<string, string> Parameters, string Env);

/// <summary>ErrorJson=平台 error_response 节点；BusinessJson=业务 *_response 节点（TOP-01 分离解析）。</summary>
public sealed record TopRawResponse(bool TransportOk, int? HttpStatus, string? ErrorJson, string? BusinessJson, int? RetryAfterSeconds);

public interface ITopGateway
{
    Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct);
}
```

`ServiceExceptions.cs` 末尾追加：

```csharp
public sealed class TopNotCertifiedException : Exception
{
    public TopNotCertifiedException(string message) : base(message) { }
}
```

`ApiErrors.cs`：在 `ChannelPausedException => (503, "TOP_CHANNEL_PAUSED", false),` 行后插入：

```csharp
            TopNotCertifiedException => (422, "TOP_NOT_CERTIFIED", false),
```

message switch 第一臂追加 `or TopNotCertifiedException`。

- [ ] **Step 5: 实现 Fake/Real/Factory/FixtureHandler**

`FakeTopGateway.cs`：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

/// <summary>Fixture 网关只服务 MOCK；LIVE 一律拒绝（硬隔离，G-TOP 前不演示真实通道）。</summary>
public sealed class FakeTopGateway : ITopGateway
{
    private readonly Func<TopCall, TopRawResponse> _handler;
    public FakeTopGateway(Func<TopCall, TopRawResponse> handler) => _handler = handler;

    public Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct)
    {
        if (call.Env != "MOCK")
            throw new TopNotCertifiedException("G-TOP 未取证：Fake 网关只服务 MOCK，真实通道禁用");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_handler(call));
    }
}
```

`RealTopGateway.cs`：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

/// <summary>G-TOP 取证前占位：不写签名/HTTP 代码，恒拒绝（附录 B 门禁）。</summary>
public sealed class RealTopGateway : ITopGateway
{
    public Task<TopRawResponse> CallAsync(TopCall call, CancellationToken ct)
        => throw new TopNotCertifiedException("G-TOP 未取证：TOP 签名与协议未实现，真实通道禁用");
}
```

`TopGatewayFactory.cs`：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

public static class TopGatewayFactory
{
    public static ITopGateway Create(string mode, string fixtureDir) => mode switch
    {
        "fixture" => new FakeTopGateway(new TopFixtureHandler(fixtureDir).Handle),
        "real" => new RealTopGateway(),
        _ => throw new ArgumentException($"未知 TOP 网关模式: {mode}（仅 fixture/real）", nameof(mode))
    };
}
```

`TopFixtureHandler.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Top;

/// <summary>文件 fixture：{method}.json = [{"match":{参数子集},"transportOk":bool?,"retryAfterSeconds":int?,"response":{平台信封}}]。候选信封形状，待 G-TOP。</summary>
public sealed class TopFixtureHandler
{
    private readonly string _dir;
    public TopFixtureHandler(string dir) => _dir = dir;

    public TopRawResponse Handle(TopCall call)
    {
        var path = Path.Combine(_dir, call.Method + ".json");
        if (!File.Exists(path))
            return new TopRawResponse(true, 200, null, """{"response_success":true,"result":{}}""", null);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("match", out var match))
            {
                var ok = true;
                foreach (var p in match.EnumerateObject())
                    if (!call.Parameters.TryGetValue(p.Name, out var v) || !string.Equals(v, p.Value.GetString(), StringComparison.Ordinal))
                    { ok = false; break; }
                if (!ok) continue;
            }
            var transportOk = !entry.TryGetProperty("transportOk", out var t) || t.GetBoolean();
            int? retry = entry.TryGetProperty("retryAfterSeconds", out var r) ? r.GetInt32() : null;
            string? errorJson = null, businessJson = null;
            if (entry.TryGetProperty("response", out var resp))
            {
                if (resp.TryGetProperty("error_response", out var err))
                    errorJson = err.GetRawText();
                else
                    foreach (var p in resp.EnumerateObject())
                        if (p.Name.EndsWith("_response", StringComparison.Ordinal))
                        { businessJson = p.Value.GetRawText(); break; }
            }
            return new TopRawResponse(transportOk, transportOk ? 200 : null, errorJson, businessJson, retry);
        }
        return new TopRawResponse(true, 200, null, """{"response_success":true,"result":{}}""", null);
    }
}
```

- [ ] **Step 6: 实现 TopClientService**

Create `traceflow/src/TraceFlow.Infrastructure/Top/TopClientService.cs`：

```csharp
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;

namespace TraceFlow.Infrastructure.Top;

/// <summary>TOP 调用编排：暂停门禁→预算→HTTP 事务外→解析→授权暂停/限流改期（§3.3、JOB-03、§2.9）。</summary>
public sealed class TopClientService
{
    private readonly string _connectionString;
    private readonly ITopGateway _gateway;
    private readonly RateBudgetService _budget;
    private readonly ChannelPauseService _pause;
    private readonly IAuditLog _audit;

    public TopClientService(string connectionString, ITopGateway gateway, RateBudgetService budget, ChannelPauseService pause, IAuditLog audit)
    {
        _connectionString = connectionString;
        _gateway = gateway;
        _budget = budget;
        _pause = pause;
        _audit = audit;
    }

    public async Task<TopOutcome> CallAsync(string env, string method, IReadOnlyDictionary<string, string> parameters,
        string? refEntId, string actor, CancellationToken ct)
    {
        var scopeKeys = ScopeKeys(method, refEntId);
        var budgetKeys = BudgetKeys(method, refEntId);

        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            if (await _pause.IsPausedAsync(conn, tx, env, scopeKeys, ct))
                throw new ChannelPausedException("method:" + method, $"TOP 通道已暂停（{method}）：授权错误须人工恢复");
            var wait = await _budget.TryAcquireAsync(conn, tx, env, budgetKeys, DateTime.UtcNow, ct);
            if (wait is TimeSpan w)
                throw new TopRateLimitedException(w);
            await tx.CommitAsync(ct);
        }

        var raw = await _gateway.CallAsync(new TopCall(method, parameters, env), ct); // HTTP 在事务外（§2.9）
        var outcome = TopResponseParser.Parse(raw.TransportOk, raw.ErrorJson, raw.BusinessJson, raw.RetryAfterSeconds);

        if (outcome.Kind is TopOutcomeKind.AuthError or TopOutcomeKind.RateLimited)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            if (outcome.Kind == TopOutcomeKind.AuthError)
            {
                await _pause.PauseAsync(conn, tx, env, scopeKeys, "FAIL_BIZ_AUTH_ERROR: " + (outcome.Message ?? ""), actor, ct);
                await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "System", "top.channel.pause", "top_channel",
                    "method:" + method, outcome.Message, null, null, outcome.RequestId, null, env));
                await tx.CommitAsync(ct);
                throw new ChannelPausedException("method:" + method, $"TOP 授权错误，已按范围暂停（{method}）");
            }
            var seconds = raw.RetryAfterSeconds ?? (int)RateBudgetService.Backoff(0).TotalSeconds;
            await _budget.RegisterWaitAsync(conn, tx, env, budgetKeys, DateTime.UtcNow.AddSeconds(seconds), ct);
            await tx.CommitAsync(ct);
            throw new TopRateLimitedException(TimeSpan.FromSeconds(seconds));
        }
        return outcome;
    }

    public static IReadOnlyList<string> ScopeKeys(string method, string? refEntId)
        => refEntId is null
            ? new[] { "method:" + method, "credential:top" }
            : new[] { "method:" + method, "credential:top", "ent:" + refEntId };

    public static IReadOnlyList<string> BudgetKeys(string method, string? refEntId)
        => refEntId is null
            ? new[] { "appkey:total", "method:" + method }
            : new[] { "appkey:total", "method:" + method, $"ent:{refEntId}:method:{method}" };
}
```

- [ ] **Step 7: DI 接线与 fixture 复制**

`Program.cs` 在 `builder.Services.AddSingleton<ISourceAdapter>(...)` 语句（第 42–43 行）之后插入：

```csharp
var topMode = builder.Configuration["TraceFlow:TopMode"] ?? "fixture";
builder.Services.AddSingleton<ITopGateway>(TraceFlow.Infrastructure.Top.TopGatewayFactory.Create(
    topMode, Path.Combine(AppContext.BaseDirectory, "fixtures", "top")));
builder.Services.AddSingleton<TraceFlow.Infrastructure.Top.RateBudgetService>();
builder.Services.AddSingleton<TraceFlow.Infrastructure.Top.ChannelPauseService>();
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Top.TopClientService(
    connectionString, sp.GetRequiredService<ITopGateway>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Top.RateBudgetService>(),
    sp.GetRequiredService<TraceFlow.Infrastructure.Top.ChannelPauseService>(),
    sp.GetRequiredService<IAuditLog>()));
```

两个 csproj 在既有 ksoa `<None Include=...>` 行后各加一行：

```xml
    <None Include="..\..\..\docs\contracts\fixtures\top\*.json" CopyToOutputDirectory="PreserveNewest" LinkBase="fixtures\top" />
```

（目录暂不存在时 glob 为空不报错；Task 5/6 会放入样例文件。）

- [ ] **Step 8: 写候选合同文档**

Create `docs/contracts/top-contract.md`：

```markdown
# TOP（码上放心）平台合同 — M2 候选声明

状态：**G-TOP 未取证**。本文件所有"候选"值在真实通道启用前必须经平台文档/联调取证修订；
M2 真实通道（env=LIVE）一律禁用（RealTopGateway 占位拒绝，422 TOP_NOT_CERTIFIED）。

## 已确认（规格/PRD 点名）

| 项 | 值 | 来源 |
| --- | --- | --- |
| 授权错误 token | `FAIL_BIZ_AUTH_ERROR` | 设计 §3.3 |
| 部分成功 token | `WARN_BIZ_PARTSUCCESS` | PRD TOP-03 |
| 系统/业务错误分离 | error_response vs response_success=false | PRD TOP-01 |
| 查询超时≠受理结论 | TransportUncertain 不得判成功/失败 | PRD TOP-02 |

## 候选（待 G-TOP 取证）

| 项 | 候选值 | 说明 |
| --- | --- | --- |
| bill_code 前缀 | `TF` | settings `top.billPrefix` 可配；须平台验证合法字符集 |
| bill_code 最大长度 | 32 | settings `top.billMaxLength` 可配；超限抛错禁截断 |
| 限流错误 sub_code 前缀 | `FAIL_BIZ_RATE` | 命中即 RateLimited |
| 重复单据号 sub_code | `FAIL_BIZ_BILL_CODE_DUPLICATE` | 命中即 DuplicateBillCode→AcceptanceUnknown |
| 最小包装层级 token | `MIN` | 其余值/null=非最小或不明，阻断分配 |
| 分页参数 | `page_no`/`page_size`（默认 50） | 列表/详情通用 |
| 方法名 | `listupout`、`listupout.detail`、`query.listparts`、`getentinfolist` | 短名；真实 API 名取证后映射 |
| 列表响应 result | `{total_pages,total_count,page_no,bills:[{platform_bill_id,bill_no,upstream_ref_ent_id,bill_date,from_ent_id,to_ent_id,owner_ent_id,deliver_ent_id,source_version}]}` | 候选形状 |
| 详情响应 result | `{total_pages,page_no,lines:[{platform_line_id,platform_drug_id,batch_no,quantity,unit,package_level,codes:[{code,package_level,parent_code}]}]}` | 候选形状；parent_code 仅平台显式提供时落库（UP-02） |
| 限流限值 | 未知（本地预算列 NULL=不设帽） | 平台限流错误仍触发改期（JOB-03） |

## 预算键与暂停范围

- 预算键：`appkey:total`、`method:{method}`、`ent:{refEntId}:method:{method}`（门店 ref_ent_id 已知时）。
- 暂停范围：`method:{method}`、`credential:top`、`ent:{refEntId}`（有主体时）。
- 平台明确等待秒数优先；否则 30s×2^min(attempt,5)（上限 600s）+0..5s 抖动。
```

- [ ] **Step 9: 运行全部测试确认通过**

Run: `dotnet build traceflow/TraceFlow.sln -c Release`（0 警告）
Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（单元 +5；集成 +4）。

- [ ] **Step 10: Commit**

```bash
git add traceflow/src/TraceFlow.Application/Abstractions/ITopGateway.cs traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs traceflow/src/TraceFlow.Infrastructure/Top/ traceflow/src/TraceFlow.Host/Security/ApiErrors.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/src/TraceFlow.Host/TraceFlow.Host.csproj traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj traceflow/tests/TraceFlow.UnitTests/Top/TopGatewayTests.cs traceflow/tests/TraceFlow.IntegrationTests/Top/TopClientServiceTests.cs docs/contracts/top-contract.md
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): TOP gateway port with fixture/real isolation, client orchestration, candidate contract declaration"
```

---

### Task 5: 上游出库单下载（SYNC-03 / UP-01 / UP-02 / AT-022 / AT-023）

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Codes/TraceCodeStore.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Upstream/UpstreamDownloadService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/UpstreamDownloadJobHandler.cs`
- Modify: `traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`（+upstream.pollSeconds 键与读取）
- Modify: `traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs`（+upstream-download 周期桶）
- Modify: `traceflow/src/TraceFlow.Host/WorkerHostedService.cs`（平台等待秒数优先）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（DI）
- Create: `docs/contracts/fixtures/top/listupout.json`、`docs/contracts/fixtures/top/listupout.detail.json`（候选形状样例）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Upstream/UpstreamDownloadTests.cs`

**Interfaces:**
- Consumes: Task 4 `TopClientService.CallAsync(env, method, parameters, refEntId, actor, ct)→TopOutcome`（限流/授权/暂停以异常抛出）；`TopOutcomeKind.Success`、`outcome.ResultJson`；Task 3 异常；`IJobQueue.EnqueueAsync(conn, tx, orgId, env, jobKind, intentKey, payloadJson, nextRunAtUtc, ct)→long`；`IAuditLog`；`TraceCodeNormalizer.Normalize(string?)→NormalizedCode(Raw, Normalized, CodeFormat)`；`CodeFormatStorage.ToToken`；0004 表 `upstream_bill`（UQ org,env,upstream_ref_ent_id,platform_bill_id）、`upstream_bill_line`（UQ bill_id,platform_line_id）、`upstream_bill_code`（UQ bill_id,code_normalized）、`download_progress`（UQ org,env,job_kind,bill_id；Task 1 加 window_start/end）。
- Produces（Task 6/8/10/11 逐字使用）:
  - `static class TraceCodeStore`：`Task<StoredCode> EnsureAsync(SqlConnection, SqlTransaction, string? raw, CancellationToken)`；`sealed record StoredCode(long Id, string Raw, string Normalized, CodeFormat Format)`（Empty 格式抛 ArgumentException）。
  - `sealed class UpstreamDownloadService(string connectionString, TopClientService top, IJobQueue queue, IAuditLog audit)`：`Task<DownloadSummary> RunAsync(long orgId, string env, DateTime windowStartUtc, DateTime windowEndUtc, long? onlyBillId, CancellationToken ct)`；`sealed record DownloadSummary(int BillsSeen, int BillsComplete, int BillsIncomplete, int ListPages, int DetailPages)`。
  - `sealed class UpstreamDownloadJobHandler(UpstreamDownloadService svc) : IJobHandler`，`Kind="upstream-download"`；payload `{orgId, env, start?, end?, billId?, trigger}`（缺省窗口 now-7d..now）。
  - `SettingsService.GetUpstreamPollSecondsAsync(ct)→int`；Catalog 键 `upstream.pollSeconds`（默认 300，界 30..3600）。
  - 详情全部页成功后同事务入队 `inbound-match` 作业，意图 `$"up:{billId}:{sourceVersion ?? "v0"}"`，payload `{orgId, env, snapshotId=null, trigger="upstream", billId}`。
  - 调度意图 `periodic:{unix/upstreamPollSeconds}`（upstream-download），payload 含 start/end。
- **裁定（SYNC-03 检查点语义）**：每次运行列表从第 1 页开始（upsert 幂等去重）；进度行逐页同事务持久化（崩溃后可见 pages_done/状态），跨运行断点续传=重新拉取而非跳过——查询幂等，不伪造完成。总数校验按本次运行实收 distinct 身份数。

- [ ] **Step 1: 写失败的集成测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Upstream/UpstreamDownloadTests.cs`：

```csharp
using System.Text.Json;
using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Upstream;
using Xunit;

namespace TraceFlow.IntegrationTests.Upstream;

public class UpstreamDownloadTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public UpstreamDownloadTests(Sql2008Fixture fx) => _fx = fx;

    private sealed class Script
    {
        public Dictionary<(string Method, string PageNo), TopRawResponse> Responses = new();
        public List<TopCall> Calls = new();
        public TopRawResponse Handle(TopCall call)
        {
            Calls.Add(call);
            var pageNo = call.Parameters.TryGetValue("page_no", out var p) ? p : "1";
            return Responses.TryGetValue((call.Method, pageNo), out var r)
                ? r
                : Ok("""{"total_pages":1,"page_no":1,"bills":[]}""");
        }
    }

    private static TopRawResponse Ok(string resultJson)
        => new(true, 200, null, $$"""{"response_success":true,"result":{{resultJson}}}""", null);

    private static string ListResult(string billsJson, int totalPages = 1, int? totalCount = null)
        => $$"""{"total_pages":{{totalPages}},"total_count":{{totalCount ?? 0}},"page_no":1,"bills":{{billsJson}}}""";

    private const string BillA = """
        {"platform_bill_id":"UB-A","bill_no":"XS-SAME","upstream_ref_ent_id":"REF-SUP1","bill_date":"2026-11-18T02:00:00Z","from_ent_id":"ENT-SUP1","to_ent_id":"ENT-STORE","owner_ent_id":"ENT-SUP1","deliver_ent_id":null,"source_version":"v1"}
        """;
    private const string BillB = """
        {"platform_bill_id":"UB-B","bill_no":"XS-SAME","upstream_ref_ent_id":"REF-SUP2","bill_date":"2026-11-18T03:00:00Z","from_ent_id":"ENT-SUP2","to_ent_id":"ENT-STORE","owner_ent_id":"ENT-SUP2","deliver_ent_id":null,"source_version":"v1"}
        """;
    private const string DetailA = """
        {"total_pages":1,"page_no":1,"lines":[{"platform_line_id":"L1","platform_drug_id":"PD1","batch_no":"P20260101","quantity":2,"unit":"盒","package_level":"MIN","codes":[{"code":"81234567890123456701","package_level":"MIN","parent_code":null},{"code":"81234567890123456702","package_level":"MIN","parent_code":null}]}]}
        """;
    private const string DetailB = """
        {"total_pages":1,"page_no":1,"lines":[{"platform_line_id":"L1","platform_drug_id":"PD2","batch_no":"P20260202","quantity":1,"unit":"盒","package_level":"MIN","codes":[{"code":"81234567890123456703","package_level":"MIN","parent_code":null}]}]}
        """;

    private async Task<(UpstreamDownloadService svc, Script script, long orgId)> BuildAsync(bool withIdentity = true)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        if (withIdentity)
            await c.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var script = new Script();
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(script.Handle),
            new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());
        return (svc, script, orgId);
    }

    private static (DateTime Start, DateTime End) Window => (DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

    [Fact]
    public async Task Full_download_completes_bills_and_enqueues_match_same_bill_no_different_subjects_not_merged()
    {
        await using var c0 = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c0);
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var detailByBill = new Dictionary<string, string> { ["UB-A"] = DetailA, ["UB-B"] = DetailB };
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(call =>
            call.Method == "listupout.detail"
                ? Ok(detailByBill[call.Parameters["bill_id"]])
                : Ok(ListResult($"[{BillA},{BillB}]", 1, 2))),
            new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());

        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(2, sum.BillsSeen);
        Assert.Equal(2, sum.BillsComplete);
        Assert.Equal(0, sum.BillsIncomplete);

        await using var c = await _fx.OpenAsync();
        // AT-023：同 bill_no 不同上游主体→两条独立单据
        var bills = (await c.QueryAsync<BillView>(
            "SELECT id AS Id, platform_bill_id AS PlatformBillId, completeness AS Completeness FROM dbo.upstream_bill WHERE org_id=@org AND env=N'MOCK' AND bill_no=N'XS-SAME'",
            new { org = orgId })).ToList();
        Assert.Equal(2, bills.Count);
        Assert.All(bills, b => Assert.Equal("COMPLETE", b.Completeness));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill_code WHERE bill_id=@b", new { b = bills.Single(x => x.PlatformBillId == "UB-A").Id }));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.trace_code WHERE code_normalized IN (N'81234567890123456701',N'81234567890123456702',N'81234567890123456703')"));
        // 列表/详情进度分离且 COMPLETE
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
        // 每单入队一个 inbound-match 事件作业（意图含版本）
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match' AND intent_key LIKE 'up:%:v1'", new { org = orgId }));
    }

    private sealed record BillView(long Id, string PlatformBillId, string Completeness);

    [Fact]
    public async Task Detail_page2_failure_marks_incomplete_never_complete()
    {
        await using var c0 = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c0);
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=N'REF-STORE', ent_id=N'ENT-STORE' WHERE id=@org", new { org = orgId });
        var detailCalls = 0;
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(call =>
        {
            if (call.Method == "listupout")
                return Ok(ListResult($"[{BillA}]", 1, 1));
            detailCalls++;
            if (call.Parameters["page_no"] == "1")
                return Ok("""{"total_pages":2,"page_no":1,"lines":[{"platform_line_id":"L1","platform_drug_id":"PD1","batch_no":"P1","quantity":1,"unit":"盒","package_level":"MIN","codes":[{"code":"81234567890123456711","package_level":"MIN","parent_code":null}]}]}""");
            return new TopRawResponse(true, 200, """{"code":2,"sub_code":"isp.top-remote-connection-timeout"}""", null, null);
        }), new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        var svc = new UpstreamDownloadService(_fx.ConnectionString, top, new JobQueue(_fx.ConnectionString, new AuditLog()), new AuditLog());
        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(1, sum.BillsIncomplete); // AT-022：第 2 页失败→INCOMPLETE，不供完整入库上传
        Assert.Equal(0, sum.BillsComplete);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("INCOMPLETE", await c.ExecuteScalarAsync<string>("SELECT completeness FROM dbo.upstream_bill WHERE org_id=@org AND platform_bill_id=N'UB-A'", new { org = orgId }));
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-detail' AND bill_id<>0", new { org = orgId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match'", new { org = orgId }));
        // 第 1 页码已落库（页级同事务），但单据不完整
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill_code bc JOIN dbo.upstream_bill b ON b.id=bc.bill_id WHERE b.org_id=@org", new { org = orgId }));
    }

    [Fact]
    public async Task Duplicate_list_page_never_fakes_complete()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult($"[{BillA}]", 2, 2));
        script.Responses[("listupout", "2")] = Ok(ListResult($"[{BillA}]", 2, 2)); // 重复页
        var (start, end) = Window;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
    }

    [Fact]
    public async Task Total_count_mismatch_never_fakes_complete()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult($"[{BillA}]", 1, 5)); // 期望 5 实收 1
        var (start, end) = Window;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("FAILED", await c.ExecuteScalarAsync<string>("SELECT status FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
    }

    [Fact]
    public async Task Live_env_rejected_as_not_certified()
    {
        var (svc, _, orgId) = await BuildAsync();
        var (start, end) = Window;
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => svc.RunAsync(orgId, "LIVE", start, end, null, default));
    }

    [Fact]
    public async Task Rate_limit_midrun_keeps_page1_progress_for_next_run()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult($"[{BillA}]", 2, 2));
        script.Responses[("listupout", "2")] = new TopRawResponse(true, 200, """{"code":7,"sub_code":"FAIL_BIZ_RATE_LIMIT"}""", null, 30);
        var (start, end) = Window;
        await Assert.ThrowsAsync<TopRateLimitedException>(() => svc.RunAsync(orgId, "MOCK", start, end, null, default));
        await using var c = await _fx.OpenAsync();
        // 第 1 页数据与进度同事务已提交；限流是瞬态→进度保持 RUNNING 供下轮重拉
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT pages_done FROM dbo.download_progress WHERE org_id=@org AND job_kind=N'upstream-list' AND bill_id=0", new { org = orgId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill WHERE org_id=@org", new { org = orgId }));
    }

    [Fact]
    public async Task Missing_store_identity_skips_with_audit()
    {
        var (svc, script, orgId) = await BuildAsync(withIdentity: false);
        await using var c0 = await _fx.OpenAsync();
        await c0.ExecuteAsync("UPDATE dbo.organization SET ref_ent_id=NULL WHERE id=@org", new { org = orgId });
        var (start, end) = Window;
        var sum = await svc.RunAsync(orgId, "MOCK", start, end, null, default);
        Assert.Equal(0, sum.BillsSeen);
        Assert.Empty(script.Calls);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.audit_log WHERE action='upstream.download.skip' AND env=N'MOCK' AND object_id=@oid",
            new { oid = orgId.ToString() }));
    }

    [Fact]
    public async Task Handler_reads_payload_and_runs()
    {
        var (svc, script, orgId) = await BuildAsync();
        script.Responses[("listupout", "1")] = Ok(ListResult("[]", 1, 0));
        var handler = new UpstreamDownloadJobHandler(svc);
        Assert.Equal("upstream-download", handler.Kind);
        var payload = JsonSerializer.Serialize(new { orgId, env = "MOCK", trigger = "manual" });
        await handler.HandleAsync(new ClaimedJob(1, "upstream-download", "manual:test", payload, 1, DateTime.UtcNow.AddMinutes(5), 0), default);
        Assert.Single(script.Calls);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter UpstreamDownloadTests`
Expected: 编译失败（UpstreamDownloadService 等不存在）——RED。

- [ ] **Step 3: 实现 TraceCodeStore**

Create `traceflow/src/TraceFlow.Infrastructure/Codes/TraceCodeStore.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Codes;

namespace TraceFlow.Infrastructure.Codes;

public sealed record StoredCode(long Id, string Raw, string Normalized, CodeFormat Format);

public static class TraceCodeStore
{
    public static async Task<StoredCode> EnsureAsync(SqlConnection conn, SqlTransaction tx, string? raw, CancellationToken ct)
    {
        var n = TraceCodeNormalizer.Normalize(raw);
        if (n.Format == CodeFormat.Empty)
            throw new ArgumentException("空码不得入台账", nameof(raw));
        var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = n.Normalized }, tx, cancellationToken: ct));
        if (id is null)
        {
            try
            {
                id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id VALUES(@n,@raw,@fmt)",
                    new { n = n.Normalized, raw = n.Raw, fmt = CodeFormatStorage.ToToken(n.Format) }, tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627)
            {
                id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = n.Normalized }, tx, cancellationToken: ct));
            }
        }
        return new StoredCode(id!.Value, n.Raw, n.Normalized, n.Format);
    }
}
```

- [ ] **Step 4: 实现 UpstreamDownloadService**

Create `traceflow/src/TraceFlow.Infrastructure/Upstream/UpstreamDownloadService.cs`：

```csharp
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Upstream;

public sealed record DownloadSummary(int BillsSeen, int BillsComplete, int BillsIncomplete, int ListPages, int DetailPages);

/// <summary>
/// SYNC-03/UP-01/UP-02：上游出库单分页下载。页数据与进度同事务；列表/详情进度分离；
/// 详情未全部成功→INCOMPLETE；重复页/总数不一致→FAILED，不伪造完成。
/// 每次运行从第 1 页重拉（upsert 幂等）；检查点逐页持久化供观测与下轮重拉。
/// </summary>
public sealed class UpstreamDownloadService
{
    private const int PageSize = 50; // 候选，待 G-TOP
    private const int MaxPages = 1000;
    private readonly string _cs;
    private readonly TopClientService _top;
    private readonly IJobQueue _queue;
    private readonly IAuditLog _audit;

    public UpstreamDownloadService(string connectionString, TopClientService top, IJobQueue queue, IAuditLog audit)
    {
        _cs = connectionString; _top = top; _queue = queue; _audit = audit;
    }

    public async Task<DownloadSummary> RunAsync(long orgId, string env, DateTime windowStartUtc, DateTime windowEndUtc, long? onlyBillId, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new TopNotCertifiedException("G-TOP 未取证：上游下载仅 MOCK 可用");
        string? refEntId;
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            refEntId = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT ref_ent_id FROM dbo.organization WHERE id=@org", new { org = orgId }, cancellationToken: ct));
        }
        if (string.IsNullOrEmpty(refEntId))
        {
            await AuditAsync(env, "upstream.download.skip", orgId, "门店 ref_ent_id 未配置，上游下载跳过", ct);
            return new DownloadSummary(0, 0, 0, 0, 0);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listPages = onlyBillId is null
            ? await DownloadListAsync(orgId, env, refEntId!, windowStartUtc, windowEndUtc, seen, ct)
            : 0;

        var detailPages = 0; var complete = 0; var incomplete = 0;
        List<BillRow> bills;
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            bills = (await c.QueryAsync<BillRow>(new CommandDefinition(@"
SELECT id AS Id, platform_bill_id AS PlatformBillId, upstream_ref_ent_id AS UpstreamRefEntId, source_version AS SourceVersion
FROM dbo.upstream_bill
WHERE org_id=@org AND env=@env AND ((@only IS NULL AND completeness<>N'COMPLETE') OR id=@only)",
                new { org = orgId, env, only = onlyBillId }, cancellationToken: ct))).ToList();
        }
        foreach (var b in bills)
        {
            try
            {
                detailPages += await DownloadDetailAsync(orgId, env, b, ct);
                complete++;
            }
            catch (Exception ex) when (ex is not (TopRateLimitedException or ChannelPausedException or TopNotCertifiedException or OperationCanceledException))
            {
                await MarkBillIncompleteAsync(orgId, env, b.Id, ex.Message, ct);
                incomplete++;
            }
        }
        await AuditAsync(env, "upstream.download", orgId,
            $"列表页 {listPages}，详情页 {detailPages}，完整 {complete}，不完整 {incomplete}", ct);
        return new DownloadSummary(seen.Count, complete, incomplete, listPages, detailPages);
    }

    private async Task<int> DownloadListAsync(long orgId, string env, string refEntId, DateTime start, DateTime end, HashSet<string> seen, CancellationToken ct)
    {
        var pageNo = 1; int? totalPages = null; int? totalCount = null;
        HashSet<string>? prevPageIds = null;
        while (pageNo <= MaxPages)
        {
            var outcome = await _top.CallAsync(env, "listupout", new Dictionary<string, string>
            {
                ["ref_ent_id"] = refEntId,
                ["page_no"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture),
                ["start_date"] = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["end_date"] = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            }, refEntId, "SYSTEM", ct);
            if (outcome.Kind != TopOutcomeKind.Success)
            {
                await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo - 1, totalPages, totalCount, "FAILED", ct);
                throw new InvalidOperationException($"上游列表第 {pageNo} 页非成功：{outcome.Kind} {outcome.SubCode}");
            }
            using var doc = JsonDocument.Parse(outcome.ResultJson ?? "{}");
            var r = doc.RootElement;
            totalPages = r.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : totalPages;
            totalCount = r.TryGetProperty("total_count", out var tc) ? tc.GetInt32() : totalCount;
            var pageIds = new HashSet<string>(StringComparer.Ordinal);
            var bills = new List<ListBill>();
            if (r.TryGetProperty("bills", out var arr))
                foreach (var b in arr.EnumerateArray())
                {
                    var lb = ParseListBill(b);
                    bills.Add(lb);
                    pageIds.Add(lb.PlatformBillId);
                    seen.Add(lb.UpstreamRefEntId + "|" + lb.PlatformBillId);
                }
            if (prevPageIds is not null && pageIds.Count > 0 && pageIds.SetEquals(prevPageIds))
            {
                await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo - 1, totalPages, totalCount, "FAILED", ct);
                throw new InvalidOperationException($"上游列表第 {pageNo} 页与上一页重复，禁止伪造完成");
            }
            prevPageIds = pageIds;
            await using (var c = new SqlConnection(_cs))
            {
                await c.OpenAsync(ct);
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
                foreach (var lb in bills)
                    await UpsertBillAsync(c, tx, orgId, env, lb, ct);
                await UpsertProgressAsync(c, tx, orgId, env, "upstream-list", 0, start, end, pageNo, totalPages, totalCount, "RUNNING", ct);
                await tx.CommitAsync(ct);
            }
            if (bills.Count == 0 || (totalPages is int t && pageNo >= t)) break;
            pageNo++;
        }
        if (totalCount is int expected && seen.Count != expected)
        {
            await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo, totalPages, totalCount, "FAILED", ct);
            throw new InvalidOperationException($"上游列表总数不一致：期望 {expected}，实收 {seen.Count}");
        }
        await SetProgressAsync(orgId, env, "upstream-list", 0, start, end, pageNo, totalPages, totalCount, "COMPLETE", ct);
        return pageNo;
    }

    private async Task<int> DownloadDetailAsync(long orgId, string env, BillRow bill, CancellationToken ct)
    {
        var pageNo = 1; int? totalPages = null; var pages = 0;
        while (pageNo <= MaxPages)
        {
            var outcome = await _top.CallAsync(env, "listupout.detail", new Dictionary<string, string>
            {
                ["ref_ent_id"] = bill.UpstreamRefEntId,
                ["bill_id"] = bill.PlatformBillId,
                ["page_no"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture),
            }, bill.UpstreamRefEntId, "SYSTEM", ct);
            if (outcome.Kind != TopOutcomeKind.Success)
                throw new InvalidOperationException($"上游详情第 {pageNo} 页非成功：{outcome.Kind} {outcome.SubCode}");
            using var doc = JsonDocument.Parse(outcome.ResultJson ?? "{}");
            var r = doc.RootElement;
            totalPages = r.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : totalPages;
            await using (var c = new SqlConnection(_cs))
            {
                await c.OpenAsync(ct);
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
                if (r.TryGetProperty("lines", out var lines))
                    foreach (var ln in lines.EnumerateArray())
                        await UpsertLineAsync(c, tx, bill.Id, ln, ct);
                await UpsertProgressAsync(c, tx, orgId, env, "upstream-detail", bill.Id, null, null, pageNo, totalPages, null, "RUNNING", ct);
                await tx.CommitAsync(ct);
            }
            pages++;
            if (totalPages is not int t || pageNo >= t) break;
            pageNo++;
        }
        // 全部详情页成功→COMPLETE + 匹配事件入队（同事务，SYNC-03）
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.upstream_bill SET completeness=N'COMPLETE', fetched_at=SYSUTCDATETIME() WHERE id=@id",
                new { id = bill.Id }, tx, cancellationToken: ct));
            await UpsertProgressAsync(c, tx, orgId, env, "upstream-detail", bill.Id, null, null, pageNo, totalPages, null, "COMPLETE", ct);
            var payload = JsonSerializer.Serialize(new { orgId, env, snapshotId = (long?)null, trigger = "upstream", billId = bill.Id });
            await _queue.EnqueueAsync(c, tx, orgId, env, "inbound-match", $"up:{bill.Id}:{bill.SourceVersion ?? "v0"}", payload, DateTime.UtcNow, ct);
            await tx.CommitAsync(ct);
        }
        return pages;
    }

    private sealed record BillRow(long Id, string PlatformBillId, string UpstreamRefEntId, string? SourceVersion);
    private sealed record ListBill(string PlatformBillId, string? BillNo, string UpstreamRefEntId, DateTime? BillDate,
        string? FromEntId, string? ToEntId, string? OwnerEntId, string? DeliverEntId, string? SourceVersion, string RawJson);
    private sealed record ExistingBill(long Id, string? SourceVersion);

    private static ListBill ParseListBill(JsonElement b)
    {
        var pid = GetString(b, "platform_bill_id") ?? throw new InvalidOperationException("上游单缺 platform_bill_id（候选合同不符）");
        var uref = GetString(b, "upstream_ref_ent_id") ?? throw new InvalidOperationException("上游单缺 upstream_ref_ent_id（候选合同不符）");
        DateTime? billDate = null;
        var bd = GetString(b, "bill_date");
        if (bd is not null && DateTime.TryParse(bd, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            billDate = d;
        return new ListBill(pid, GetString(b, "bill_no"), uref, billDate,
            GetString(b, "from_ent_id"), GetString(b, "to_ent_id"), GetString(b, "owner_ent_id"), GetString(b, "deliver_ent_id"),
            GetString(b, "source_version"), b.GetRawText());
    }

    private static async Task UpsertBillAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, ListBill lb, CancellationToken ct)
    {
        var existing = await conn.QuerySingleOrDefaultAsync<ExistingBill>(new CommandDefinition(@"
SELECT id AS Id, source_version AS SourceVersion FROM dbo.upstream_bill WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@org AND env=@env AND upstream_ref_ent_id=@ref AND platform_bill_id=@pid",
            new { org = orgId, env, @ref = lb.UpstreamRefEntId, pid = lb.PlatformBillId }, tx, cancellationToken: ct));
        if (existing is null)
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,from_ent_id,to_ent_id,owner_ent_id,deliver_ent_id,completeness,list_raw,source_version,fetched_at)
VALUES(@org,@env,@ref,@pid,@no,@date,@from,@to,@owner,@deliver,N'LIST_ONLY',@raw,@sv,SYSUTCDATETIME())",
                    new { org = orgId, env, @ref = lb.UpstreamRefEntId, pid = lb.PlatformBillId, no = lb.BillNo, date = lb.BillDate,
                          from = lb.FromEntId, to = lb.ToEntId, owner = lb.OwnerEntId, deliver = lb.DeliverEntId, raw = lb.RawJson, sv = lb.SourceVersion },
                    tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627)
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill SET list_raw=@raw, source_version=@sv, fetched_at=SYSUTCDATETIME(), completeness=N'LIST_ONLY'
WHERE org_id=@org AND env=@env AND upstream_ref_ent_id=@ref AND platform_bill_id=@pid",
                    new { org = orgId, env, @ref = lb.UpstreamRefEntId, pid = lb.PlatformBillId, raw = lb.RawJson, sv = lb.SourceVersion },
                    tx, cancellationToken: ct));
            }
        }
        else if (existing.SourceVersion != lb.SourceVersion)
        {
            // 版本变化→重拉详情（列表字段更新，完整性降级 LIST_ONLY）
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill SET bill_no=@no, bill_date=@date, from_ent_id=@from, to_ent_id=@to, owner_ent_id=@owner, deliver_ent_id=@deliver,
       list_raw=@raw, source_version=@sv, fetched_at=SYSUTCDATETIME(), completeness=N'LIST_ONLY'
WHERE id=@id",
                new { id = existing.Id, no = lb.BillNo, date = lb.BillDate, from = lb.FromEntId, to = lb.ToEntId,
                      owner = lb.OwnerEntId, deliver = lb.DeliverEntId, raw = lb.RawJson, sv = lb.SourceVersion },
                tx, cancellationToken: ct));
        }
        // 同版本已存在：不触碰（COMPLETE 保持，避免无谓重拉）
    }

    private static async Task UpsertLineAsync(SqlConnection conn, SqlTransaction tx, long billId, JsonElement ln, CancellationToken ct)
    {
        var lineKey = GetString(ln, "platform_line_id") ?? throw new InvalidOperationException("上游行缺 platform_line_id（候选合同不符）");
        var drugId = GetString(ln, "platform_drug_id");
        var batch = GetString(ln, "batch_no");
        decimal? qty = ln.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDecimal() : null;
        var unit = GetString(ln, "unit");
        var level = GetString(ln, "package_level");
        var raw = ln.GetRawText();
        var existingId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM dbo.upstream_bill_line WITH (UPDLOCK, HOLDLOCK) WHERE bill_id=@bill AND platform_line_id=@lid",
            new { bill = billId, lid = lineKey }, tx, cancellationToken: ct));
        long id;
        if (existingId is null)
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit,package_level,raw_json)
OUTPUT inserted.id VALUES(@bill,@lid,@drug,@batch,@qty,@unit,@level,@raw)",
                new { bill = billId, lid = lineKey, drug = drugId, batch, qty, unit, level, raw }, tx, cancellationToken: ct));
        else
        {
            id = existingId.Value;
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill_line SET platform_drug_id=@drug, batch_no=@batch, quantity=@qty, unit=@unit, package_level=@level, raw_json=@raw WHERE id=@id",
                new { drug = drugId, batch, qty, unit, level, raw, id }, tx, cancellationToken: ct));
        }
        if (!ln.TryGetProperty("codes", out var codes)) return;
        foreach (var cd in codes.EnumerateArray())
        {
            var rawCode = GetString(cd, "code");
            if (string.IsNullOrWhiteSpace(rawCode)) continue;
            var stored = await TraceCodeStore.EnsureAsync(conn, tx, rawCode, ct);
            var codeLevel = GetString(cd, "package_level");
            var parent = GetString(cd, "parent_code"); // UP-02：仅平台显式提供时落库，不由码拼接推导
            var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upstream_bill_code SET line_id=@line, code_raw=@raw, package_level=@level, parent_code=@parent
WHERE bill_id=@bill AND code_normalized=@norm",
                new { line = id, raw = stored.Raw, level = codeLevel, parent, bill = billId, norm = stored.Normalized }, tx, cancellationToken: ct));
            if (rows == 0)
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level,parent_code)
VALUES(@bill,@line,@norm,@raw,@level,@parent)",
                    new { bill = billId, line = id, norm = stored.Normalized, raw = stored.Raw, level = codeLevel, parent }, tx, cancellationToken: ct));
        }
    }

    private async Task MarkBillIncompleteAsync(long orgId, string env, long billId, string reason, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upstream_bill SET completeness=N'INCOMPLETE' WHERE id=@id", new { id = billId }, tx, cancellationToken: ct));
        await UpsertProgressAsync(c, tx, orgId, env, "upstream-detail", billId, null, null, 0, null, null, "FAILED", ct);
        await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "System", "upstream.detail.incomplete", "upstream_bill",
            billId.ToString(CultureInfo.InvariantCulture), reason, null, null, null, null, env));
        await tx.CommitAsync(ct);
    }

    private async Task SetProgressAsync(long orgId, string env, string kind, long billId, DateTime? ws, DateTime? we,
        int done, int? totalPages, int? totalCount, string status, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await UpsertProgressAsync(c, tx, orgId, env, kind, billId, ws, we, done, totalPages, totalCount, status, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task UpsertProgressAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, string kind, long billId,
        DateTime? ws, DateTime? we, int done, int? totalPages, int? totalCount, string status, CancellationToken ct)
    {
        var p = new { org = orgId, env, kind, bill = billId, cursor = done.ToString(CultureInfo.InvariantCulture),
                      done, tp = totalPages, tc = totalCount, status, ws, we };
        var rows = await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.download_progress
SET page_cursor=@cursor, pages_done=@done, total_pages=@tp, total_count_expected=@tc, status=@status,
    last_success_at=SYSUTCDATETIME(), window_start=@ws, window_end=@we
WHERE org_id=@org AND env=@env AND job_kind=@kind AND bill_id=@bill", p, tx, cancellationToken: ct));
        if (rows == 0)
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.download_progress(org_id,env,job_kind,bill_id,page_cursor,pages_done,total_pages,total_count_expected,status,last_success_at,window_start,window_end)
VALUES(@org,@env,@kind,@bill,@cursor,@done,@tp,@tc,@status,SYSUTCDATETIME(),@ws,@we)", p, tx, cancellationToken: ct));
            }
            catch (SqlException e) when (e.Number is 2601 or 2627)
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.download_progress
SET page_cursor=@cursor, pages_done=@done, total_pages=@tp, total_count_expected=@tc, status=@status,
    last_success_at=SYSUTCDATETIME(), window_start=@ws, window_end=@we
WHERE org_id=@org AND env=@env AND job_kind=@kind AND bill_id=@bill", p, tx, cancellationToken: ct));
            }
        }
    }

    private async Task AuditAsync(string env, string action, long orgId, string reason, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await _audit.AppendAsync(c, null, new AuditEntry("SYSTEM", "System", action, "upstream_download",
            orgId.ToString(CultureInfo.InvariantCulture), reason, null, null, null, null, env));
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 5: 实现作业处理器 + 设置键 + 调度桶 + Worker 等待优先 + DI**

Create `traceflow/src/TraceFlow.Infrastructure/Jobs/UpstreamDownloadJobHandler.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Upstream;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class UpstreamDownloadJobHandler : IJobHandler
{
    private readonly UpstreamDownloadService _svc;
    public UpstreamDownloadJobHandler(UpstreamDownloadService svc) => _svc = svc;
    public string Kind => "upstream-download";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var r = doc.RootElement;
        var orgId = r.GetProperty("orgId").GetInt64();
        var env = r.GetProperty("env").GetString()!;
        var end = r.TryGetProperty("end", out var e) ? e.GetDateTime() : DateTime.UtcNow;
        var start = r.TryGetProperty("start", out var s) ? s.GetDateTime() : end.AddDays(-7);
        long? billId = r.TryGetProperty("billId", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null;
        await _svc.RunAsync(orgId, env, start, end, billId, ct);
    }
}
```

`SettingsService.cs`：Catalog 末尾（`import.maxRows` 行后）追加：

```csharp
        ["upstream.pollSeconds"] = new("upstream.pollSeconds", false, "300", "int", 30, 3600),
```

`GetImportBoundsAsync` 方法后追加：

```csharp
    public async Task<int> GetUpstreamPollSecondsAsync(CancellationToken ct)
        => (int)await GetIntAsync("upstream.pollSeconds", ct);
```

`SyncScheduler.cs`：`var policy = await _settings.GetSyncPolicyAsync(ct);` 行后加：

```csharp
        var upstreamPoll = await _settings.GetUpstreamPollSecondsAsync(ct);
```

`!paused` 块内 source-sync foreach 之后追加：

```csharp
            var upPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, trigger = "periodic", start = nowUtc.AddDays(-7), end = nowUtc });
            await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "upstream-download",
                $"periodic:{unix / upstreamPoll}", upPayload, nowUtc, ct);
            n++;
```

类头注释更新为"…暂停通道跳过 source-sync/upstream-download（master-match 为本地计算，继续）"。

`WorkerHostedService.cs` 内层 catch 改为（平台明确等待秒数优先，JOB-03）：

```csharp
                        catch (Exception ex)
                        {
                            var delay = ex is TopRateLimitedException rle
                                ? rle.Wait
                                : TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(job.AttemptCount, 5))));
                            await _queue.RescheduleAsync(job.Id, _workerId, job.FencingToken, DateTime.UtcNow + delay, ex.Message, stoppingToken);
                        }
```

`Program.cs` Task 4 接线块后追加：

```csharp
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Upstream.UpstreamDownloadService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(),
    sp.GetRequiredService<IJobQueue>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.UpstreamDownloadJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Upstream.UpstreamDownloadService>()));
```

- [ ] **Step 6: 放 TOP fixture 样例（候选形状）**

Create `docs/contracts/fixtures/top/listupout.json`：

```json
[
  {
    "match": { "page_no": "1" },
    "response": {
      "alibaba_alihealth_drugtrace_top_listupout_response": {
        "response_success": true,
        "request_id": "fixture-list-1",
        "result": {
          "total_pages": 1,
          "total_count": 1,
          "page_no": 1,
          "bills": [
            {
              "platform_bill_id": "FIX-UB-1",
              "bill_no": "XS-FIX-1",
              "upstream_ref_ent_id": "REF-FIX-SUP",
              "bill_date": "2026-11-18T02:00:00Z",
              "from_ent_id": "ENT-FIX-SUP",
              "to_ent_id": "ENT-FIX-STORE",
              "owner_ent_id": "ENT-FIX-SUP",
              "deliver_ent_id": null,
              "source_version": "v1"
            }
          ]
        }
      }
    }
  }
]
```

Create `docs/contracts/fixtures/top/listupout.detail.json`：

```json
[
  {
    "match": { "page_no": "1" },
    "response": {
      "alibaba_alihealth_drugtrace_top_listupout_detail_response": {
        "response_success": true,
        "request_id": "fixture-detail-1",
        "result": {
          "total_pages": 1,
          "page_no": 1,
          "lines": [
            {
              "platform_line_id": "FIX-L1",
              "platform_drug_id": "FIX-PD1",
              "batch_no": "P20260101",
              "quantity": 1,
              "unit": "盒",
              "package_level": "MIN",
              "codes": [
                { "code": "81234567890123456799", "package_level": "MIN", "parent_code": null }
              ]
            }
          ]
        }
      }
    }
  }
]
```

- [ ] **Step 6b: 更新 M1 调度计数断言（SyncSchedulerTests 锁定总数，必须同步）**

`traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs`：
- `Tick_enqueues_periodic_daily_and_match_intents_idempotent`：`Assert.Equal(5, n); // 2 实体 × (periodic+daily) + 1 match` 改为 `Assert.Equal(6, n); // 2 实体 × (periodic+daily) + 1 match + 1 upstream`；两处 `Assert.Equal(5, await JobCountAsync());` 改为 `Assert.Equal(6, await JobCountAsync());`。
- `Next_poll_bucket_creates_new_periodic_intents`：`Assert.Equal(7, await JobCountAsync()); // +2 periodic（daily/match 桶未变）` 改为 `Assert.Equal(8, await JobCountAsync()); // +2 periodic（daily/match/upstream 桶未变）`。
- `Paused_channel_skips_source_sync_but_keeps_match`：末尾追加 `Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upstream-download'"));`。

- [ ] **Step 7: 运行全部测试确认通过**

Run: `dotnet build traceflow/TraceFlow.sln -c Release`（0 警告）
Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（+8 UpstreamDownloadTests；SyncSchedulerTests 按 Step 6b 更新后通过；SourceSync 测试不回归）。

- [ ] **Step 8: Commit**

```bash
git add traceflow/src/TraceFlow.Infrastructure/Codes/TraceCodeStore.cs traceflow/src/TraceFlow.Infrastructure/Upstream/UpstreamDownloadService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/UpstreamDownloadJobHandler.cs traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs traceflow/src/TraceFlow.Host/WorkerHostedService.cs traceflow/src/TraceFlow.Host/Program.cs docs/contracts/fixtures/top/ traceflow/tests/TraceFlow.IntegrationTests/Upstream/UpstreamDownloadTests.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): upstream bill download with page-level checkpoint integrity (SYNC-03, AT-022/023)"
```

---

### Task 6: 企业/往来单位同步（ENT-02 / AT-020 / AT-021）+ sync-jobs 扩展

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Upstream/EnterpriseSyncService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/EnterpriseSyncJobHandler.cs`
- Modify: `traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs`（+enterprise-sync 每日桶）
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/OpsEndpoints.cs`（sync-jobs entity 扩展）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（DI）
- Create: `docs/contracts/fixtures/top/query.listparts.json`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Upstream/EnterpriseSyncTests.cs`

**Interfaces:**
- Consumes: Task 4 `TopClientService`；0003 `platform_enterprise_version`（UQ ref_ent_id+ent_id，列 ref_ent_id/ent_id/name/credit_code/audit_status/role/observed_at）；Task 5 调度/处理器模式。
- Produces（Task 8/10 使用）:
  - `sealed class EnterpriseSyncService(string connectionString, TopClientService top, IAuditLog audit)`：`Task<EnterpriseSyncSummary> RunAsync(long orgId, string env, CancellationToken ct)`；`sealed record EnterpriseSyncSummary(int PartnersSeen, int VersionsAdded)`。
  - `sealed class EnterpriseSyncJobHandler(EnterpriseSyncService svc) : IJobHandler`，`Kind="enterprise-sync"`，payload `{orgId, env, trigger}`。
  - sync-jobs entity 扩展为 `sale|purchase-in|upstream|enterprise`；upstream→jobKind `upstream-download`、enterprise→`enterprise-sync`；这两类 LIVE 门禁返回 422 `TOP_NOT_CERTIFIED`；手动意图含 nonce（`manual:{entity}:{start}:{end}:{Guid:N}`，幂等由 Idempotency-Key 层保证；sale/purchase-in 意图保持 M1 原样）。
  - **裁定**：`getentinfolist` 按信用代码精确查询属 G-TOP 取证后能力，M2 只实现 `query.listparts` 分页下载（候选方法名）；ENT-02 只增不改由 UQ+IF NOT EXISTS 保证。
- 角色分离（AT-021）：`role` 原样落库（deliver/owner 等）；**不**自动创建 supplier_mapping；入库匹配只认 supplier_mapping（Task 8），上游单 deliver_ent_id/owner_ent_id 仅作证据字段。

- [ ] **Step 1: 写失败的集成测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Upstream/EnterpriseSyncTests.cs`：

```csharp
using System.Text.Json;
using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Top;
using TraceFlow.Infrastructure.Upstream;
using Xunit;

namespace TraceFlow.IntegrationTests.Upstream;

public class EnterpriseSyncTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public EnterpriseSyncTests(Sql2008Fixture fx) => _fx = fx;

    private static TopRawResponse Parts(string partsJson, int totalPages = 1)
        => new(true, 200, null, $$"""{"response_success":true,"result":{"total_pages":{{totalPages}},"parts":{{partsJson}}}}""", null);

    private async Task<(EnterpriseSyncService svc, long orgId)> BuildAsync(Func<TopCall, TopRawResponse> handler)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var top = new TopClientService(_fx.ConnectionString, new FakeTopGateway(handler),
            new RateBudgetService(), new ChannelPauseService(), new AuditLog());
        return (new EnterpriseSyncService(_fx.ConnectionString, top, new AuditLog()), orgId);
    }

    [Fact]
    public async Task Rename_creates_new_ent_version_and_old_row_untouched()
    {
        var refId = "REF-R." + Guid.NewGuid().ToString("N")[..8];
        var phase = 0;
        var (svc, orgId) = await BuildAsync(_ => Parts(phase == 0
            ? $$"""[{"ref_ent_id":"{{refId}}","ent_id":"E-OLD","name":"旧名称","credit_code":"CC1","audit_status":"PASS","role":"owner"}]"""
            : $$"""[{"ref_ent_id":"{{refId}}","ent_id":"E-NEW","name":"新名称","credit_code":"CC1","audit_status":"PASS","role":"owner"}]"""));
        var s1 = await svc.RunAsync(orgId, "MOCK", default);
        Assert.Equal(1, s1.VersionsAdded);
        await using var c0 = await _fx.OpenAsync();
        var oldBefore = await c0.QuerySingleAsync<VersionRow>(
            "SELECT name AS Name, audit_status AS AuditStatus, observed_at AS ObservedAt FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-OLD'", new { r = refId });
        phase = 1;
        var s2 = await svc.RunAsync(orgId, "MOCK", default);
        Assert.Equal(1, s2.VersionsAdded); // AT-020：改名→新 ent_id 版本行
        await using var c = await _fx.OpenAsync();
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId }));
        var oldAfter = await c.QuerySingleAsync<VersionRow>(
            "SELECT name AS Name, audit_status AS AuditStatus, observed_at AS ObservedAt FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-OLD'", new { r = refId });
        Assert.Equal(oldBefore.Name, oldAfter.Name); // ENT-02：历史版本字节不动
        Assert.Equal(oldBefore.ObservedAt, oldAfter.ObservedAt);
        Assert.Equal("新名称", await c.ExecuteScalarAsync<string>("SELECT name FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-NEW'", new { r = refId }));
    }

    private sealed record VersionRow(string Name, string? AuditStatus, DateTime ObservedAt);

    [Fact]
    public async Task Roles_stored_separately_and_no_supplier_mapping_auto_created()
    {
        var refId = "REF-ROLE." + Guid.NewGuid().ToString("N")[..8];
        var (svc, orgId) = await BuildAsync(_ => Parts($$"""
            [{"ref_ent_id":"{{refId}}","ent_id":"E-D","name":"配送商","credit_code":"CC-D","audit_status":"PASS","role":"deliver"},
             {"ref_ent_id":"{{refId}}","ent_id":"E-O","name":"所有者","credit_code":"CC-O","audit_status":"PASS","role":"owner"}]
            """));
        await svc.RunAsync(orgId, "MOCK", default);
        await using var c = await _fx.OpenAsync();
        // AT-021：deliver 与 owner 角色分别保留，不混同
        Assert.Equal("deliver", await c.ExecuteScalarAsync<string>("SELECT role FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-D'", new { r = refId }));
        Assert.Equal("owner", await c.ExecuteScalarAsync<string>("SELECT role FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r AND ent_id=N'E-O'", new { r = refId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.supplier_mapping")); // 不自动建映射
    }

    [Fact]
    public async Task Same_identity_twice_does_not_duplicate_or_touch_observed_at()
    {
        var refId = "REF-DUP." + Guid.NewGuid().ToString("N")[..8];
        var (svc, orgId) = await BuildAsync(_ => Parts($$"""[{"ref_ent_id":"{{refId}}","ent_id":"E-1","name":"单位","credit_code":"CC","audit_status":"PASS","role":"owner"}]"""));
        await svc.RunAsync(orgId, "MOCK", default);
        await using var c0 = await _fx.OpenAsync();
        var first = await c0.ExecuteScalarAsync<DateTime>("SELECT observed_at FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId });
        var s2 = await svc.RunAsync(orgId, "MOCK", default);
        Assert.Equal(0, s2.VersionsAdded);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId }));
        Assert.Equal(first, await c.ExecuteScalarAsync<DateTime>("SELECT observed_at FROM dbo.platform_enterprise_version WHERE ref_ent_id=@r", new { r = refId }));
    }

    [Fact]
    public async Task Live_env_rejected_and_handler_kind_correct()
    {
        var (svc, orgId) = await BuildAsync(_ => Parts("[]"));
        await Assert.ThrowsAsync<TopNotCertifiedException>(() => svc.RunAsync(orgId, "LIVE", default));
        var handler = new EnterpriseSyncJobHandler(svc);
        Assert.Equal("enterprise-sync", handler.Kind);
        var payload = JsonSerializer.Serialize(new { orgId, env = "MOCK", trigger = "daily" });
        await handler.HandleAsync(new ClaimedJob(1, "enterprise-sync", "daily:test", payload, 1, DateTime.UtcNow.AddMinutes(5), 0), default);
    }

    [Fact]
    public async Task Page_failure_throws_for_job_backoff_no_partial_fake()
    {
        var calls = 0;
        var (svc, orgId) = await BuildAsync(_ =>
        {
            calls++;
            return calls == 1
                ? Parts("""[{"ref_ent_id":"REF-PF","ent_id":"E-1","name":"n","credit_code":null,"audit_status":null,"role":null}]""", 2)
                : new TopRawResponse(true, 200, """{"code":2,"sub_code":"isp.top-remote-connection-timeout"}""", null, null);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunAsync(orgId, "MOCK", default));
        await using var c = await _fx.OpenAsync();
        // 第 1 页已同事务落库（查询幂等，下轮重拉不重复）
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.platform_enterprise_version WHERE ref_ent_id=N'REF-PF'"));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter EnterpriseSyncTests`
Expected: 编译失败（EnterpriseSyncService 不存在）——RED。

- [ ] **Step 3: 实现 EnterpriseSyncService**

Create `traceflow/src/TraceFlow.Infrastructure/Upstream/EnterpriseSyncService.cs`：

```csharp
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Top;
using TraceFlow.Infrastructure.Top;

namespace TraceFlow.Infrastructure.Upstream;

public sealed record EnterpriseSyncSummary(int PartnersSeen, int VersionsAdded);

/// <summary>ENT-02：往来单位/企业版本只增不改（UQ ref_ent_id+ent_id）；改名→新 ent_id 版本行；角色原样保留（AT-020/021）。</summary>
public sealed class EnterpriseSyncService
{
    private const int PageSize = 50; // 候选，待 G-TOP
    private const int MaxPages = 1000;
    private readonly string _cs;
    private readonly TopClientService _top;
    private readonly IAuditLog _audit;

    public EnterpriseSyncService(string connectionString, TopClientService top, IAuditLog audit)
    {
        _cs = connectionString; _top = top; _audit = audit;
    }

    public async Task<EnterpriseSyncSummary> RunAsync(long orgId, string env, CancellationToken ct)
    {
        if (env != "MOCK")
            throw new TopNotCertifiedException("G-TOP 未取证：企业同步仅 MOCK 可用");
        string? orgRef;
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            orgRef = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT ref_ent_id FROM dbo.organization WHERE id=@org", new { org = orgId }, cancellationToken: ct));
        }
        var pageNo = 1; int? totalPages = null; var seen = 0; var added = 0;
        while (pageNo <= MaxPages)
        {
            var outcome = await _top.CallAsync(env, "query.listparts", new Dictionary<string, string>
            {
                ["page_no"] = pageNo.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture),
            }, orgRef, "SYSTEM", ct);
            if (outcome.Kind != TopOutcomeKind.Success)
                throw new InvalidOperationException($"往来单位第 {pageNo} 页非成功：{outcome.Kind} {outcome.SubCode}");
            using var doc = JsonDocument.Parse(outcome.ResultJson ?? "{}");
            var r = doc.RootElement;
            totalPages = r.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : totalPages;
            await using (var c = new SqlConnection(_cs))
            {
                await c.OpenAsync(ct);
                await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
                if (r.TryGetProperty("parts", out var parts))
                    foreach (var p in parts.EnumerateArray())
                    {
                        seen++;
                        var refEntId = GetString(p, "ref_ent_id") ?? throw new InvalidOperationException("往来单位缺 ref_ent_id（候选合同不符）");
                        var entId = GetString(p, "ent_id") ?? throw new InvalidOperationException("往来单位缺 ent_id（候选合同不符）");
                        var rows = await c.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.platform_enterprise_version WHERE ref_ent_id=@ref AND ent_id=@ent)
    INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code,audit_status,role)
    VALUES(@ref,@ent,@name,@cc,@audit,@role)",
                            new { @ref = refEntId, ent = entId, name = GetString(p, "name") ?? "", cc = GetString(p, "credit_code"),
                                  audit = GetString(p, "audit_status"), role = GetString(p, "role") },
                            tx, cancellationToken: ct));
                        added += rows;
                    }
                await tx.CommitAsync(ct);
            }
            if (totalPages is not int t || pageNo >= t) break;
            pageNo++;
        }
        await using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            await _audit.AppendAsync(c, null, new AuditEntry("SYSTEM", "System", "enterprise.sync", "platform_enterprise",
                orgId.ToString(CultureInfo.InvariantCulture), $"往来单位 {seen}，新版本 {added}", null, null, null, null, env));
        }
        return new EnterpriseSyncSummary(seen, added);
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 4: 实现处理器 + 调度桶 + DI + fixture**

Create `traceflow/src/TraceFlow.Infrastructure/Jobs/EnterpriseSyncJobHandler.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Upstream;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class EnterpriseSyncJobHandler : IJobHandler
{
    private readonly EnterpriseSyncService _svc;
    public EnterpriseSyncJobHandler(EnterpriseSyncService svc) => _svc = svc;
    public string Kind => "enterprise-sync";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var r = doc.RootElement;
        await _svc.RunAsync(r.GetProperty("orgId").GetInt64(), r.GetProperty("env").GetString()!, ct);
    }
}
```

`SyncScheduler.cs` `!paused` 块内 upstream-download 桶后追加：

```csharp
            var entPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, trigger = "daily" });
            await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "enterprise-sync",
                $"daily:{nowUtc:yyyyMMdd}", entPayload, nowUtc, ct);
            n++;
```

`Program.cs` Task 5 接线后追加：

```csharp
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Upstream.EnterpriseSyncService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Top.TopClientService>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.EnterpriseSyncJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Upstream.EnterpriseSyncService>()));
```

Create `docs/contracts/fixtures/top/query.listparts.json`：

```json
[
  {
    "match": { "page_no": "1" },
    "response": {
      "alibaba_alihealth_drugtrace_top_query_listparts_response": {
        "response_success": true,
        "request_id": "fixture-parts-1",
        "result": {
          "total_pages": 1,
          "parts": [
            { "ref_ent_id": "REF-FIX-SUP", "ent_id": "ENT-FIX-SUP", "name": "fixture 供应商", "credit_code": "FIXCC001", "audit_status": "PASS", "role": "owner" }
          ]
        }
      }
    }
  }
]
```

- [ ] **Step 5: 扩展 sync-jobs 端点**

`OpsEndpoints.cs` 精确修改（保持 M1 sale/purchase-in 行为不变）：

第 22–23 行改为：

```csharp
                if (req is null || req.Entity is not ("sale" or "purchase-in" or "upstream" or "enterprise"))
                    return Err(ctx, 422, "VALIDATION", "entity 须为 sale/purchase-in/upstream/enterprise");
```

第 33–34 行改为：

```csharp
                if (env != "MOCK")
                    return Err(ctx, 422, req.Entity is "upstream" or "enterprise" ? "TOP_NOT_CERTIFIED" : "ERP_NOT_CERTIFIED",
                        req.Entity is "upstream" or "enterprise" ? "LIVE 通道须 G-TOP 取证门禁通过后开启" : "LIVE 通道须 G-ERP 取证门禁通过后开启");
```

第 39–41 行改为：

```csharp
                var jobKind = req.Entity switch { "upstream" => "upstream-download", "enterprise" => "enterprise-sync", _ => "source-sync" };
                // upstream/enterprise 手动意图含 nonce：同窗口补拉不被 DONE 终态挡死（幂等由 Idempotency-Key 层保证）
                var intent = req.Entity is "upstream" or "enterprise"
                    ? $"manual:{req.Entity}:{start:yyyyMMddHHmmss}:{end:yyyyMMddHHmmss}:{Guid.NewGuid():N}"
                    : $"manual:{req.Entity}:{start:yyyyMMddHHmmss}:{end:yyyyMMddHHmmss}";
                var payload = JsonSerializer.Serialize(new { orgId, env, entity = req.Entity, trigger = "manual", startUtc = start, endUtc = end, start, end });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, jobKind, intent, payload, now, ct);
```

（payload 同时携带 startUtc/endUtc（M1 处理器读）与 start/end（M2 处理器读）；审计行不变。）

- [ ] **Step 5b: 更新 M1 调度计数断言（enterprise 每日桶 +1）**

`SyncSchedulerTests.cs`：`Assert.Equal(6, n); // 2 实体 × (periodic+daily) + 1 match + 1 upstream` 改为 `Assert.Equal(7, n); // 2 实体 × (periodic+daily) + 1 match + 1 upstream + 1 enterprise`；两处 `Assert.Equal(6, await JobCountAsync());` 改为 `Assert.Equal(7, await JobCountAsync());`；`Assert.Equal(8, await JobCountAsync()); // +2 periodic（daily/match/upstream 桶未变）` 改为 `Assert.Equal(9, await JobCountAsync()); // +2 periodic（daily/match/upstream/enterprise 桶未变）`；`Paused_channel_skips_source_sync_but_keeps_match` 末尾追加 `Assert.Equal(0, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'enterprise-sync'"));`。

- [ ] **Step 6: 运行全部测试确认通过**

Run: `dotnet build traceflow/TraceFlow.sln -c Release`（0 警告）
Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（+5 EnterpriseSyncTests；SyncSchedulerTests 按 Step 5b 更新后通过；M1ApiGateTests 的 sync-jobs 断言不回归）。

- [ ] **Step 7: Commit**

```bash
git add traceflow/src/TraceFlow.Infrastructure/Upstream/EnterpriseSyncService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/EnterpriseSyncJobHandler.cs traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs traceflow/src/TraceFlow.Host/Endpoints/OpsEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs docs/contracts/fixtures/top/query.listparts.json traceflow/tests/TraceFlow.IntegrationTests/Upstream/EnterpriseSyncTests.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): append-only enterprise version sync (ENT-02, AT-020/021) and sync-jobs entity extension"
```

---

### Task 7: 占用/取消原语 + T2 任务工厂

**Files:**
- Modify: `traceflow/src/TraceFlow.Domain/Sources/SourceHasher.cs`（+CodeSetHash）
- Modify: `traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`（string 种类 + top.billPrefix/top.billMaxLength + GetBillContractAsync）
- Modify: `traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs`（+2 异常）
- Modify: `traceflow/src/TraceFlow.Host/Security/ApiErrors.cs`（动态错误码 + fieldErrors）
- Create: `traceflow/src/TraceFlow.Infrastructure/Inbound/CodeClaimWriter.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Inbound/InboundCancelService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Inbound/InboundTaskFactory.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Sources/SourceValueObjectTests.cs`（类内追加 3 方法）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Inbound/ClaimAndCancelTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Inbound/InboundTaskFactoryTests.cs`（含 BillContractTests 类）

**Interfaces:**
- Consumes: Task 1 `dbo.inbound_match`（UQ(org,env,source_snapshot_id)，version int）与 `UploadState.Cancelled`；Task 5 `TraceCodeStore.EnsureAsync(SqlConnection, SqlTransaction, string?, CancellationToken)→StoredCode(long Id, string Raw, string Normalized, CodeFormat Format)`；`SettingsService.UpdateAsync(conn,tx,key,valueJson,secretValue,expectedVersionHex,reason,actor,ct)`（"0"=新建）；`BillCodeGenerator.Generate(BillCodeContract(string Prefix,int MaxLength), long taskId, int partIndex)`（超长抛 InvalidOperationException，禁截断）；`IJobQueue.EnqueueAsync(conn,tx,orgId,env,jobKind,intentKey,payloadJson,nextRunAtUtc,ct)→long`（UQ 冲突返回既有 id）；`JobQueue(string connectionString, IAuditLog audit)`；`AuditEntry(Actor,ActorKind,Action,ObjectType,ObjectId?,Reason?,BeforeDigest?,AfterDigest?,RequestId?,RuleVersion?,Env)`；`AuditLog` 无参构造；`DpapiProtector`（TraceFlow.Infrastructure.Security）。
- Produces（T8/T9/T10 按此签名消费）:
  - `SourceHasher.CodeSetHash(IReadOnlyList<string> normalizedCodes)→string`（Ordinal 排序后 Esc 拼接 ";"，SHA256 大写 hex）。
  - `SettingsService.GetBillContractAsync(CancellationToken)→Task<BillCodeContract>`（默认 TF/32）。
  - `static CodeClaimWriter.InsertActiveAsync(SqlConnection, SqlTransaction, long orgId, string env, long codeId, string eventScope, long? allocationId, long? uploadTaskId, CancellationToken)→Task<long>`（ACTIVE 唯一索引冲突→`AllocationConflictException("CODE_CONTENDED",…)`）。
  - `static CodeClaimWriter.ReleaseByAllocationAsync(conn, tx, long allocationId, string releasedBy, string evidence, CancellationToken)→Task<int>`。
  - `static CodeClaimWriter.BindTaskAsync(conn, tx, long allocationId, long taskId, CancellationToken)→Task`。
  - `sealed class AllocationConflictException(string errorCode, string message)`（属性 `string ErrorCode`）→HTTP 409，code 取 ErrorCode。
  - `sealed class AllocationValidationException(string errorCode, string message, IReadOnlyList<object>? fieldErrors = null)`（属性 ErrorCode/FieldErrors）→HTTP 422，code 取 ErrorCode，fieldErrors 透传。
  - `static InboundCancelService.CancelPreSendAsync(conn, tx, long orgId, string env, long snapshotId, string actor, string reason, string auditEnv, CancellationToken)→Task<bool>`（false=无任务；守卫失败抛 `AllocationConflictException("CANCEL_REFUSED",…)`）。
  - `sealed class InboundTaskFactory(SettingsService settings, IJobQueue queue)`，`.CreateAsync(conn, tx, InboundTaskContext ctx, CancellationToken)→Task<TaskCreationResult>`。
  - `record StoredCodeRef(long CodeId, string CodeNormalized)`；`record AllocatedLine(long AllocationId, long SourceLineId, string SourceLineKey, string ProductSourceId, string PlatformDrugId, string BatchNo, decimal Quantity, string Unit, IReadOnlyList<StoredCodeRef> Codes)`；`record InboundTaskContext(long OrgId, string Env, long SnapshotId, string DocKey, string SourceHash, DateTime? BusinessDateTimeUtc, string SupplierDwbh, string SupplierRefEntId, string SupplierEntId, IReadOnlyList<AllocatedLine> Lines, IReadOnlyList<string> MappingVersions, string CreatedBy)`；`record TaskCreationResult(long TaskId, long PartId, string BillCode, bool Reused)`。

**裁定（T15 汇入 M2-rulings-and-carryforwards.md）：**
- **R7a Cancelled 复活**：`UQ_task_event(org,env,event_key)` 禁止同事件键建第二个任务。Cancelled 任务**从未有 UPLOAD 尝试**时，行政性复用同一行：直接 UPDATE 回 Ready（不走状态机——状态机治理发送生命周期，复活是行政重置），payload/摘要/映射快照更新、version+1；part 0 payload 更新但 **bill_code 不变**（IDEM-02）；outbox CANCELLED→PENDING 且 attempt_count=0；code_event 历史不可变（IF NOT EXISTS）。已有 UPLOAD 尝试→抛 `AllocationConflictException("REACTIVATE_REFUSED",…)`。
- **R7b mapping_version_snapshot**：列宽 nvarchar(200) 放不下完整映射 JSON，存"映射版本对 Ordinal 排序拼接"的 SHA256 大写 hex 摘要（64 字符）。版本对格式 `product:{spid}:{version}` / `supplier:{dwbh}:{version}`，由调用方（T8/T10）组装。
- **R7c payload_json**：内部规范化 DTO（事件键/供应商身份/行/码集），**不是平台请求参数**——G-TOP 未取证，M3 取证后由 DTO 构造平台参数。payload_hash = DTO JSON 的 SHA256 大写 hex。

- [ ] **Step 1: 写失败的单元测试（CodeSetHash）**

在 `traceflow/tests/TraceFlow.UnitTests/Sources/SourceValueObjectTests.cs` 的 `SourceValueObjectTests` 类末尾（最后一个方法之后、类闭括号之前）追加：

```csharp
    [Fact]
    public void Code_set_hash_order_insensitive()
    {
        var a = SourceHasher.CodeSetHash(new[] { "81234567890123456789", "81234567890123456788" });
        var b = SourceHasher.CodeSetHash(new[] { "81234567890123456788", "81234567890123456789" });
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Code_set_hash_delimiter_does_not_collide()
    {
        var a = SourceHasher.CodeSetHash(new[] { "A|B", "C" });
        var b = SourceHasher.CodeSetHash(new[] { "A", "B|C" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Code_set_hash_empty_set_stable()
        => Assert.Equal(SourceHasher.CodeSetHash(Array.Empty<string>()), SourceHasher.CodeSetHash(Array.Empty<string>()));
```

- [ ] **Step 2: 运行确认失败，实现 CodeSetHash，再跑绿**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 编译失败 CS1061（SourceHasher 无 CodeSetHash）。

`SourceHasher.cs` 在 `DocumentHash` 方法后追加：

```csharp
    /// <summary>码集摘要（upload_part.code_set_hash）：Ordinal 排序→转义→";" 拼接，顺序无关。</summary>
    public static string CodeSetHash(IReadOnlyList<string> normalizedCodes)
        => Sha(string.Join(";", normalizedCodes.OrderBy(c => c, StringComparer.Ordinal).Select(Esc)));
```

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Expected: 全绿（136+3=139）。

- [ ] **Step 3: 写失败的集成测试（占用/取消 + 任务工厂 + 合同设置）**

新建 `traceflow/tests/TraceFlow.IntegrationTests/Inbound/ClaimAndCancelTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

public class ClaimAndCancelTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public ClaimAndCancelTests(Sql2008Fixture fx) => _fx = fx;

    private InboundTaskFactory NewFactory() => new(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
        new JobQueue(_fx.ConnectionString, new AuditLog()));

    private sealed record Seed(long OrgId, long SnapshotId, long LineId, long UpLineId, long CodeId, long AllocId, string DocKey);

    private async Task<Seed> SeedInboundAsync(SqlConnection c, string docKey, string code)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@bill,N'COMPLETE')", new { orgId, bill = "BILL-" + docKey });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',N'DRUG-1',N'B2026',2,N'盒')", new { billId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, key = docKey, hash = new string('0', 64) });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',N'SP0001',2,N'盒',N'B2026',@hash,N'PURCHASE_IN')",
            new { snapId, hash = new string('0', 64) });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var stored = await TraceCodeStore.EnsureAsync(c, tx, code, default);
        var allocId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@lineId,@upLineId,2,1,1,N'RESERVED',N'test')",
            new { orgId, lineId, upLineId }, tx);
        await c.ExecuteAsync("INSERT INTO dbo.allocation_code(allocation_id,code_id) VALUES(@allocId,@codeId)",
            new { allocId, codeId = stored.Id }, tx);
        await CodeClaimWriter.InsertActiveAsync(c, tx, orgId, "MOCK", stored.Id, "PURCHASE_IN", allocId, null, default);
        await c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state)
VALUES(@orgId,N'MOCK',@snapId,N'SUP001',N'COMPLETE')", new { orgId, snapId }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, upLineId, stored.Id, allocId, docKey);
    }

    private static InboundTaskContext Ctx(Seed s, string code) => new(s.OrgId, "MOCK", s.SnapshotId, s.DocKey,
        new string('0', 64), new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), "SUP001", "REF-SUP", "ENT-SUP",
        new[] { new AllocatedLine(s.AllocId, s.LineId, "1", "SP0001", "DRUG-1", "B2026", 2m, "盒",
            new[] { new StoredCodeRef(s.CodeId, code) }) },
        new[] { "product:SP0001:1", "supplier:SUP001:1" }, "test");

    [Fact]
    public async Task Second_active_claim_same_code_and_scope_is_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CLAIM-1", "81234567890123456701");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
                CodeClaimWriter.InsertActiveAsync(c, tx, s.OrgId, "MOCK", s.CodeId, "PURCHASE_IN", null, null, default));
            Assert.Equal("CODE_CONTENDED", ex.ErrorCode);
            await tx.RollbackAsync();
        }
        // 释放后可再占用：过滤唯一索引只约束 ACTIVE（DB-05）
        await c.ExecuteAsync("UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME() WHERE code_id=@id AND claim_state=N'ACTIVE'", new { id = s.CodeId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var id = await CodeClaimWriter.InsertActiveAsync(c, tx2, s.OrgId, "MOCK", s.CodeId, "PURCHASE_IN", null, null, default);
        await tx2.CommitAsync();
        Assert.True(id > 0);
    }

    [Fact]
    public async Task Cancel_pre_send_rolls_back_everything_atomically()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-1", "81234567890123456702");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456702"), default);
            await tx.CommitAsync();
        }
        var verBefore = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapshotId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var ok = await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapshotId, "tester", "上游单据作废", "LIVE", default);
            Assert.True(ok);
            await tx.CommitAsync();
        }
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE source_snapshot_id=@id", new { id = s.SnapshotId });
        var partId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@id", new { id = partId }));
        Assert.Equal("CANCELLED", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{partId}" }));
        Assert.Equal("CANCELLED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE id=@id", new { id = s.AllocId }));
        Assert.Equal("RELEASED", await c.ExecuteScalarAsync<string>("SELECT claim_state FROM dbo.code_claim WHERE allocation_id=@id", new { id = s.AllocId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapshotId }));
        Assert.Equal(verBefore + 1, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapshotId }));
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.audit_log WHERE action=N'inbound.cancel' AND object_id=@oid AND env=N'LIVE'", new { oid = taskId.ToString() }) >= 1);
    }

    [Fact]
    public async Task Cancel_refused_after_upload_attempt()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-2", "81234567890123456703");
        long taskId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456703"), default);
            taskId = r.TaskId;
            await c.ExecuteAsync("INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token) VALUES(@id,1,N'UPLOAD',N'MOCK',1)", new { id = taskId }, tx);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            InboundCancelService.CancelPreSendAsync(c, tx2, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        Assert.Equal("CANCEL_REFUSED", ex.ErrorCode);
        await tx2.RollbackAsync();
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
    }

    [Fact]
    public async Task Cancel_refused_when_dispatch_job_running()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-3", "81234567890123456704");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456704"), default);
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET job_state=N'RUNNING' WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r.PartId}" }, tx);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            InboundCancelService.CancelPreSendAsync(c, tx2, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        Assert.Equal("CANCEL_REFUSED", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_refused_when_task_dispatching()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-4", "81234567890123456705");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456705"), default);
            await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Dispatching' WHERE id=@id", new { id = r.TaskId }, tx);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            InboundCancelService.CancelPreSendAsync(c, tx2, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        Assert.Equal("CANCEL_REFUSED", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_without_task_returns_false()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-CANCEL-5", "81234567890123456706");
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        Assert.False(await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapshotId, "tester", "取消", "LIVE", default));
        await tx.RollbackAsync();
    }
}
```

新建 `traceflow/tests/TraceFlow.IntegrationTests/Inbound/InboundTaskFactoryTests.cs`（两个测试类各自独立测试库；BillContractTests 会改全局合同设置，必须与工厂测试隔离）：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

public class InboundTaskFactoryTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public InboundTaskFactoryTests(Sql2008Fixture fx) => _fx = fx;

    private InboundTaskFactory NewFactory() => new(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
        new JobQueue(_fx.ConnectionString, new AuditLog()));

    private sealed record Seed(long OrgId, long SnapshotId, long LineId, long UpLineId, long CodeId, long AllocId, string DocKey);

    private async Task<Seed> SeedInboundAsync(SqlConnection c, string docKey, string code)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@bill,N'COMPLETE')", new { orgId, bill = "BILL-" + docKey });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',N'DRUG-1',N'B2026',2,N'盒')", new { billId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, key = docKey, hash = new string('0', 64) });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',N'SP0001',2,N'盒',N'B2026',@hash,N'PURCHASE_IN')",
            new { snapId, hash = new string('0', 64) });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var stored = await TraceCodeStore.EnsureAsync(c, tx, code, default);
        var allocId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@lineId,@upLineId,2,1,1,N'RESERVED',N'test')",
            new { orgId, lineId, upLineId }, tx);
        await c.ExecuteAsync("INSERT INTO dbo.allocation_code(allocation_id,code_id) VALUES(@allocId,@codeId)",
            new { allocId, codeId = stored.Id }, tx);
        await CodeClaimWriter.InsertActiveAsync(c, tx, orgId, "MOCK", stored.Id, "PURCHASE_IN", allocId, null, default);
        await c.ExecuteAsync(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state)
VALUES(@orgId,N'MOCK',@snapId,N'SUP001',N'COMPLETE')", new { orgId, snapId }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, upLineId, stored.Id, allocId, docKey);
    }

    private static InboundTaskContext Ctx(Seed s, string code) => new(s.OrgId, "MOCK", s.SnapshotId, s.DocKey,
        new string('0', 64), new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc), "SUP001", "REF-SUP", "ENT-SUP",
        new[] { new AllocatedLine(s.AllocId, s.LineId, "1", "SP0001", "DRUG-1", "B2026", 2m, "盒",
            new[] { new StoredCodeRef(s.CodeId, code) }) },
        new[] { "product:SP0001:1", "supplier:SUP001:1" }, "test");

    [Fact]
    public async Task Create_atomic_rollback_leaves_nothing()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-1", "81234567890123456711");
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456711"), default);
            Assert.True(r.TaskId > 0);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=@k", new { k = $"KSOA:{s.DocKey}:PURCHASE_IN" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id", new { id = s.SnapshotId }));
        Assert.Null(await c.ExecuteScalarAsync<long?>("SELECT upload_task_id FROM dbo.code_claim WHERE allocation_id=@id", new { id = s.AllocId }));
    }

    [Fact]
    public async Task Create_writes_task_part_outbox_event_and_binds_claim()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-2", "81234567890123456712");
        TaskCreationResult r;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456712"), default);
            await tx.CommitAsync();
        }
        Assert.False(r.Reused);
        Assert.StartsWith("TF", r.BillCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal(64, (await c.ExecuteScalarAsync<string>("SELECT mapping_version_snapshot FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }))!.Length);
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r.PartId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id AND event_type=N'PURCHASE_IN' AND direction=N'IN'", new { id = s.SnapshotId }));
        Assert.Equal(r.TaskId, await c.ExecuteScalarAsync<long>("SELECT upload_task_id FROM dbo.code_claim WHERE allocation_id=@id", new { id = s.AllocId }));
    }

    [Fact]
    public async Task Create_reuses_existing_task_unchanged()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-3", "81234567890123456713");
        TaskCreationResult r1, r2;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r1 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456713"), default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r2 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456713"), default);
            await tx.CommitAsync();
        }
        Assert.True(r2.Reused);
        Assert.Equal(r1.TaskId, r2.TaskId);
        Assert.Equal(r1.BillCode, r2.BillCode);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
    }

    [Fact]
    public async Task Cancelled_task_reactivates_same_row_with_stable_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedInboundAsync(c, "PI-FACT-4", "81234567890123456714");
        TaskCreationResult r1, r2;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r1 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456714"), default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            Assert.True(await InboundCancelService.CancelPreSendAsync(c, tx, s.OrgId, "MOCK", s.SnapshotId, "tester", "上游单据作废", "LIVE", default));
            await tx.CommitAsync();
        }
        // 重新占用（取消已释放旧占用；模拟 T9 重新保存分配后的新 ACTIVE 占用）
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync("UPDATE dbo.inbound_allocation SET state=N'RESERVED', version=version+1 WHERE id=@id", new { id = s.AllocId }, tx);
            await CodeClaimWriter.InsertActiveAsync(c, tx, s.OrgId, "MOCK", s.CodeId, "PURCHASE_IN", s.AllocId, null, default);
            r2 = await NewFactory().CreateAsync(c, tx, Ctx(s, "81234567890123456714"), default);
            await tx.CommitAsync();
        }
        Assert.False(r2.Reused);
        Assert.Equal(r1.TaskId, r2.TaskId);
        Assert.Equal(r1.BillCode, r2.BillCode); // IDEM-02：复活不换单号
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.upload_task WHERE id=@id", new { id = r1.TaskId }));
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r2.PartId}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT attempt_count FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k", new { k = $"part:{r2.PartId}" }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE source_snapshot_id=@id", new { id = s.SnapshotId })); // 历史不可变，不重复
        Assert.Equal(r1.TaskId, await c.ExecuteScalarAsync<long>("SELECT upload_task_id FROM dbo.code_claim WHERE allocation_id=@id AND claim_state=N'ACTIVE'", new { id = s.AllocId }));
    }
}

public class BillContractTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public BillContractTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Default_contract_is_TF_32()
    {
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        var contract = await settings.GetBillContractAsync(default);
        Assert.Equal("TF", contract.Prefix);
        Assert.Equal(32, contract.MaxLength);
    }

    [Fact]
    public async Task Prefix_setting_rejects_non_string()
    {
        await using var c = await _fx.OpenAsync();
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Assert.ThrowsAsync<SettingValidationException>(() =>
            settings.UpdateAsync(c, tx, "top.billPrefix", "123", null, "0", "测试非字符串拒绝", "test", default));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Over_length_bill_code_throws_without_truncation()
    {
        await using var c = await _fx.OpenAsync();
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await settings.UpdateAsync(c, tx, "top.billMaxLength", "8", null, "0", "测试超长拒绝", "test", default);
            await tx.CommitAsync();
        }
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-LEN-1',N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, hash = new string('0', 64) });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',N'SP0001',1,@hash,N'PURCHASE_IN')", new { snapId, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',N'BILL-LEN-1',N'COMPLETE')", new { orgId });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id) OUTPUT INSERTED.id VALUES(@billId,N'L1')", new { billId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var stored = await TraceCodeStore.EnsureAsync(c, tx2, "81234567890123456721", default);
        var allocId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@lineId,@upLineId,1,1,1,N'RESERVED',N'test')", new { orgId, lineId, upLineId }, tx2);
        await CodeClaimWriter.InsertActiveAsync(c, tx2, orgId, "MOCK", stored.Id, "PURCHASE_IN", allocId, null, default);
        var ctx = new InboundTaskContext(orgId, "MOCK", snapId, "PI-LEN-1", new string('0', 64), null,
            "SUP001", "REF-SUP", "ENT-SUP",
            new[] { new AllocatedLine(allocId, lineId, "1", "SP0001", "DRUG-1", "B2026", 1m, "盒",
                new[] { new StoredCodeRef(stored.Id, stored.Normalized) }) },
            new[] { "product:SP0001:1" }, "test");
        var factory = new InboundTaskFactory(settings, new JobQueue(_fx.ConnectionString, new AuditLog()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(c, tx2, ctx, default));
        await tx2.RollbackAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=N'KSOA:PI-LEN-1:PURCHASE_IN'"));
    }
}
```

Run: `dotnet build traceflow/TraceFlow.sln -c Release`
Expected: 编译失败（CodeClaimWriter/InboundCancelService/InboundTaskFactory/AllocationConflictException/GetBillContractAsync 不存在）——RED。

- [ ] **Step 4: 实现全部生产代码**

**4a. `ServiceExceptions.cs` 末尾追加：**

```csharp
public sealed class AllocationConflictException : Exception
{
    public string ErrorCode { get; }
    public AllocationConflictException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
}

public sealed class AllocationValidationException : Exception
{
    public string ErrorCode { get; }
    public IReadOnlyList<object>? FieldErrors { get; }
    public AllocationValidationException(string errorCode, string message, IReadOnlyList<object>? fieldErrors = null) : base(message)
    {
        ErrorCode = errorCode;
        FieldErrors = fieldErrors;
    }
}
```

**4b. `ApiErrors.cs`：** 在 `TopNotCertifiedException => (422, "TOP_NOT_CERTIFIED", false),` 行后插入：

```csharp
            AllocationConflictException => (409, "ALLOCATION_CONFLICT", false),
            AllocationValidationException => (422, "ALLOCATION_INVALID", false),
```

在 switch 解构语句 `};` 之后、`ctx.Response.StatusCode = status;` 之前插入（动态错误码：409/422 的 code 由异常自带，如 CODE_CONTENDED/CANCEL_REFUSED/ALLOCATION_EXISTS/BIG_CODE_BLOCKED）：

```csharp
        var fieldErrors = Array.Empty<object>();
        if (ex is AllocationConflictException ace) code = ace.ErrorCode;
        if (ex is AllocationValidationException ave) { code = ave.ErrorCode; fieldErrors = ave.FieldErrors ?? Array.Empty<object>(); }
```

message switch 第一臂追加 `or AllocationConflictException or AllocationValidationException`；`WriteAsJsonAsync` 匿名对象中 `fieldErrors = Array.Empty<object>()` 改为 `fieldErrors`。

**4c. `SettingsService.cs`：** 文件头 using 追加 `using TraceFlow.Domain.Billing;`。Catalog 在 `["import.maxRows"]` 行后追加：

```csharp
        ["top.billPrefix"] = new("top.billPrefix", false, "\"TF\"", "string", 0, 0),
        ["top.billMaxLength"] = new("top.billMaxLength", false, "32", "int", 8, 64),
```

`ValidateJson` switch 追加：

```csharp
            case "string":
                if (e.ValueKind != JsonValueKind.String || e.GetString()!.Length is < 1 or > 200)
                    throw new SettingValidationException($"配置 {def.Key} 须为 1..200 字符字符串");
                break;
```

`GetImportBoundsAsync` 方法后追加：

```csharp
    public async Task<BillCodeContract> GetBillContractAsync(CancellationToken ct)
    {
        var raw = await GetRawAsync("top.billPrefix", ct) ?? Catalog["top.billPrefix"].DefaultJson;
        var prefix = JsonSerializer.Deserialize<string>(raw) ?? "TF";
        return new BillCodeContract(prefix, (int)await GetIntAsync("top.billMaxLength", ct));
    }
```

**4d. 新建 `traceflow/src/TraceFlow.Infrastructure/Inbound/CodeClaimWriter.cs`：**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Inbound;

/// <summary>DB-05 占用原语：UX_claim_active 过滤唯一索引保证一码不被两个有效入库消费；争用→409 CODE_CONTENDED。</summary>
public static class CodeClaimWriter
{
    public static async Task<long> InsertActiveAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long codeId, string eventScope, long? allocationId, long? uploadTaskId, CancellationToken ct)
    {
        try
        {
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,upload_task_id,allocation_id,claim_state)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@codeId,@scope,@taskId,@allocId,N'ACTIVE')",
                new { orgId, env, codeId, scope = eventScope, taskId = uploadTaskId, allocId = allocationId }, tx, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new AllocationConflictException("CODE_CONTENDED", $"码 {codeId} 在 {eventScope} 范围已被其他有效入库占用");
        }
    }

    public static Task<int> ReleaseByAllocationAsync(SqlConnection conn, SqlTransaction tx, long allocationId,
        string releasedBy, string evidence, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME(), released_by=@by, release_evidence=@ev
WHERE allocation_id=@allocId AND claim_state=N'ACTIVE'",
            new { allocId = allocationId, by = releasedBy, ev = evidence }, tx, cancellationToken: ct));

    public static Task BindTaskAsync(SqlConnection conn, SqlTransaction tx, long allocationId, long taskId, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.code_claim SET upload_task_id=@taskId WHERE allocation_id=@allocId AND claim_state=N'ACTIVE'",
            new { taskId, allocId = allocationId }, tx, cancellationToken: ct));
}
```

**4e. 新建 `traceflow/src/TraceFlow.Infrastructure/Inbound/InboundCancelService.cs`：**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Infrastructure.Inbound;

/// <summary>
/// 发送前取消（IN-09/AT-034）：仅 Blocked/Ready 且无 UPLOAD 尝试、outbox 非 RUNNING 可取消；
/// 任务/分片/队列/分配/占用/匹配同事务原子回退。无任务返回 false。
/// auditEnv：人工取消="LIVE"（控制面），作业自动取消=作业 env。
/// </summary>
public static class InboundCancelService
{
    public static async Task<bool> CancelPreSendAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, string actor, string reason, string auditEnv, CancellationToken ct)
    {
        var task = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id,state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snap AND task_type=N'PURCHASE_IN_102' AND state<>N'Cancelled'",
            new { orgId, env, snap = snapshotId }, tx, cancellationToken: ct));
        if (task is null) return false;
        if (task.state is not ("Blocked" or "Ready"))
            throw new AllocationConflictException("CANCEL_REFUSED", $"任务状态 {task.state} 已进入发送生命周期，禁止发送前取消");
        var attempts = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
            new { id = task.id }, tx, cancellationToken: ct));
        if (attempts > 0)
            throw new AllocationConflictException("CANCEL_REFUSED", "任务已有 UPLOAD 尝试，禁止取消（走 M3 结果处置流程）");
        var partIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM dbo.upload_part WHERE task_id=@id", new { id = task.id }, tx, cancellationToken: ct))).ToList();
        foreach (var partId in partIds)
        {
            var running = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@orgId AND env=@env AND job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state=N'RUNNING'",
                new { orgId, env, intent = $"part:{partId}" }, tx, cancellationToken: ct));
            if (running > 0)
                throw new AllocationConflictException("CANCEL_REFUSED", "发送作业正在 RUNNING，禁止取消");
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upload_task SET state=N'Cancelled', state_reason=@reason, version=version+1 WHERE id=@id",
            new { id = task.id, reason }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.upload_part SET state=N'Cancelled' WHERE task_id=@id", new { id = task.id }, tx, cancellationToken: ct));
        foreach (var partId in partIds)
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'CANCELLED', finished_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND job_kind=N'upload-dispatch' AND intent_key=@intent AND job_state IN (N'PENDING',N'FAILED')",
                new { orgId, env, intent = $"part:{partId}" }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1
WHERE org_id=@orgId AND env=@env AND state IN (N'RESERVED',N'COMMITTED')
  AND source_line_id IN (SELECT id FROM dbo.source_line WHERE snapshot_id=@snap)",
            new { orgId, env, snap = snapshotId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.code_claim SET claim_state=N'RELEASED', released_at=SYSUTCDATETIME(), released_by=@by, release_evidence=@reason
WHERE claim_state=N'ACTIVE' AND (upload_task_id=@taskId OR allocation_id IN (
    SELECT a.id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id WHERE l.snapshot_id=@snap))",
            new { by = actor, reason, taskId = task.id, snap = snapshotId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=N'UNMATCHED', reason=@reason, matched_at=NULL, version=version+1
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snap",
            new { orgId, env, snap = snapshotId, reason }, tx, cancellationToken: ct));
        await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.cancel", "upload_task",
            task.id.ToString(), reason, null, null, null, null, auditEnv));
        return true;
    }

    private sealed class TaskRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
    }
}
```

**4f. 新建 `traceflow/src/TraceFlow.Infrastructure/Inbound/InboundTaskFactory.cs`：**

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Billing;
using TraceFlow.Domain.Sources;
using TraceFlow.Infrastructure.Settings;

namespace TraceFlow.Infrastructure.Inbound;

public sealed record StoredCodeRef(long CodeId, string CodeNormalized);

public sealed record AllocatedLine(long AllocationId, long SourceLineId, string SourceLineKey, string ProductSourceId,
    string PlatformDrugId, string BatchNo, decimal Quantity, string Unit, IReadOnlyList<StoredCodeRef> Codes);

public sealed record InboundTaskContext(long OrgId, string Env, long SnapshotId, string DocKey, string SourceHash,
    DateTime? BusinessDateTimeUtc, string SupplierDwbh, string SupplierRefEntId, string SupplierEntId,
    IReadOnlyList<AllocatedLine> Lines, IReadOnlyList<string> MappingVersions, string CreatedBy);

public sealed record TaskCreationResult(long TaskId, long PartId, string BillCode, bool Reused);

/// <summary>
/// T2 任务工厂（AT-033/IDEM-02）：同事务创建 upload_task+part0+outbox+code_event+占用绑定。
/// R7a：Cancelled 且从未发送的任务行政性复活同一行（bill_code 不变）；
/// R7b：mapping_version_snapshot 存映射版本对的 SHA256 摘要；
/// R7c：payload_json 为内部规范化 DTO，平台参数 M3 取证后构造。
/// </summary>
public sealed class InboundTaskFactory
{
    private readonly SettingsService _settings;
    private readonly IJobQueue _queue;
    public InboundTaskFactory(SettingsService settings, IJobQueue queue)
    {
        _settings = settings;
        _queue = queue;
    }

    public async Task<TaskCreationResult> CreateAsync(SqlConnection conn, SqlTransaction tx, InboundTaskContext ctx, CancellationToken ct)
    {
        if (ctx.Lines.Count == 0)
            throw new AllocationValidationException("EMPTY_LINES", "任务至少须包含一条已分配行");
        var eventKey = $"KSOA:{ctx.DocKey}:PURCHASE_IN";
        var payloadJson = JsonSerializer.Serialize(new
        {
            eventKey,
            taskType = "PURCHASE_IN_102",
            businessDateTimeUtc = ctx.BusinessDateTimeUtc,
            supplier = new { dwbh = ctx.SupplierDwbh, refEntId = ctx.SupplierRefEntId, entId = ctx.SupplierEntId },
            lines = ctx.Lines.Select(l => new
            {
                sourceLineKey = l.SourceLineKey, productSourceId = l.ProductSourceId, platformDrugId = l.PlatformDrugId,
                batchNo = l.BatchNo, quantity = l.Quantity.ToString("0.000000", CultureInfo.InvariantCulture), unit = l.Unit,
                codes = l.Codes.Select(c => c.CodeNormalized).ToArray()
            }).ToArray()
        });
        var payloadHash = Sha(payloadJson);
        var mappingDigest = Sha(string.Join(";", ctx.MappingVersions.OrderBy(v => v, StringComparer.Ordinal)));
        var codeSetHash = SourceHasher.CodeSetHash(ctx.Lines.SelectMany(l => l.Codes.Select(c => c.CodeNormalized)).ToList());

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingRow>(new CommandDefinition(@"
SELECT id,state FROM dbo.upload_task WITH (UPDLOCK, HOLDLOCK) WHERE org_id=@orgId AND env=@env AND event_key=@key",
            new { orgId = ctx.OrgId, env = ctx.Env, key = eventKey }, tx, cancellationToken: ct));
        long taskId, partId;
        string billCode;
        if (existing is not null && existing.state != "Cancelled")
        {
            partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.upload_part WHERE task_id=@id AND part_index=0", new { id = existing.id }, tx, cancellationToken: ct));
            billCode = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = partId }, tx, cancellationToken: ct));
            return new TaskCreationResult(existing.id, partId, billCode, true);
        }
        if (existing is not null)
        {
            var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'",
                new { id = existing.id }, tx, cancellationToken: ct));
            if (sent > 0)
                throw new AllocationConflictException("REACTIVATE_REFUSED", "Cancelled 任务已有 UPLOAD 尝试，禁止同行复活");
            taskId = existing.id;
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_task SET state=N'Ready', state_reason=N'Cancelled 任务复活（从未发送）',
       source_snapshot_id=@snap, source_hash=@sh, payload_json=@pj, payload_hash=@ph, mapping_version_snapshot=@mv,
       source_integrity=N'COMPLETE', match_state=N'COMPLETE', verification_state=N'NOT_STARTED', dependency_state=N'NONE',
       version=version+1
WHERE id=@id",
                new { id = taskId, snap = ctx.SnapshotId, sh = ctx.SourceHash, pj = payloadJson, ph = payloadHash, mv = mappingDigest },
                tx, cancellationToken: ct));
            partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM dbo.upload_part WHERE task_id=@id AND part_index=0", new { id = taskId }, tx, cancellationToken: ct));
            billCode = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT bill_code FROM dbo.upload_part WHERE id=@id", new { id = partId }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.upload_part SET payload_json=@pj, payload_hash=@ph, code_set_hash=@csh, state=N'Ready' WHERE id=@id",
                new { id = partId, pj = payloadJson, ph = payloadHash, csh = codeSetHash }, tx, cancellationToken: ct));
            var jobId = await EnqueueDispatchAsync(conn, tx, ctx, taskId, partId, ct);
            await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.outbox_job SET job_state=N'PENDING', attempt_count=0, next_run_at=SYSUTCDATETIME(),
       finished_at=NULL, last_error=NULL, lease_owner=NULL, lease_until=NULL
WHERE id=@id AND job_state=N'CANCELLED'", new { id = jobId }, tx, cancellationToken: ct));
        }
        else
        {
            taskId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,mapping_version_snapshot,
    payload_json,payload_hash,state,source_integrity,match_state,verification_state,dependency_state,created_by)
OUTPUT INSERTED.id
VALUES(@orgId,@env,@key,N'PURCHASE_IN_102',@snap,@sh,@mv,@pj,@ph,N'Ready',N'COMPLETE',N'COMPLETE',N'NOT_STARTED',N'NONE',@by)",
                new { orgId = ctx.OrgId, env = ctx.Env, key = eventKey, snap = ctx.SnapshotId, sh = ctx.SourceHash,
                      mv = mappingDigest, pj = payloadJson, ph = payloadHash, by = ctx.CreatedBy }, tx, cancellationToken: ct));
            var contract = await _settings.GetBillContractAsync(ct);
            billCode = BillCodeGenerator.Generate(contract, taskId, 0); // 超长抛 InvalidOperationException，禁截断
            partId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT INSERTED.id
VALUES(@taskId,@orgId,@env,0,@bill,@pj,@ph,@csh,N'Ready')",
                new { taskId, orgId = ctx.OrgId, env = ctx.Env, bill = billCode, pj = payloadJson, ph = payloadHash, csh = codeSetHash },
                tx, cancellationToken: ct));
            await EnqueueDispatchAsync(conn, tx, ctx, taskId, partId, ct);
        }
        foreach (var line in ctx.Lines)
            foreach (var code in line.Codes)
                await conn.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.code_event WHERE org_id=@orgId AND env=@env AND code_id=@codeId AND event_type=N'PURCHASE_IN' AND source_line_id=@lineId)
INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_snapshot_id,source_line_id,upload_task_id,business_datetime)
VALUES(@orgId,@env,@codeId,N'PURCHASE_IN',N'IN',@snap,@lineId,@taskId,@biz)",
                    new { orgId = ctx.OrgId, env = ctx.Env, codeId = code.CodeId, lineId = line.SourceLineId,
                          snap = ctx.SnapshotId, taskId, biz = ctx.BusinessDateTimeUtc }, tx, cancellationToken: ct));
        foreach (var line in ctx.Lines)
            await CodeClaimWriter.BindTaskAsync(conn, tx, line.AllocationId, taskId, ct);
        return new TaskCreationResult(taskId, partId, billCode, false);
    }

    private Task<long> EnqueueDispatchAsync(SqlConnection conn, SqlTransaction tx, InboundTaskContext ctx, long taskId, long partId, CancellationToken ct)
        => _queue.EnqueueAsync(conn, tx, ctx.OrgId, ctx.Env, "upload-dispatch", $"part:{partId}",
            JsonSerializer.Serialize(new { orgId = ctx.OrgId, env = ctx.Env, taskId, partId, trigger = "allocation" }), DateTime.UtcNow, ct);

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private sealed class ExistingRow
    {
        public long id { get; set; }
        public string state { get; set; } = "";
    }
}
```

- [ ] **Step 5: 构建 + 全部测试确认通过**

Run: `dotnet build traceflow/TraceFlow.sln -c Release`（0 警告）
Run: `dotnet test traceflow/tests/TraceFlow.UnitTests -c Release`
Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（+6 ClaimAndCancelTests、+4 InboundTaskFactoryTests、+3 BillContractTests；M1 不回归）。

- [ ] **Step 6: Commit**

```bash
git add traceflow/src/TraceFlow.Domain/Sources/SourceHasher.cs traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs traceflow/src/TraceFlow.Application/Abstractions/ServiceExceptions.cs traceflow/src/TraceFlow.Host/Security/ApiErrors.cs traceflow/src/TraceFlow.Infrastructure/Inbound/CodeClaimWriter.cs traceflow/src/TraceFlow.Infrastructure/Inbound/InboundCancelService.cs traceflow/src/TraceFlow.Infrastructure/Inbound/InboundTaskFactory.cs traceflow/tests/TraceFlow.UnitTests/Sources/SourceValueObjectTests.cs traceflow/tests/TraceFlow.IntegrationTests/Inbound/ClaimAndCancelTests.cs traceflow/tests/TraceFlow.IntegrationTests/Inbound/InboundTaskFactoryTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): code claim/cancel primitives and inbound task factory (AT-033/034, IDEM-02, DB-05)"
```

---

### Task 8: 入库自动匹配 + 作业 + 触发

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Inbound/InboundMatchService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/InboundMatchJobHandler.cs`
- Modify: `traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs`（inbound-match 5 分钟桶，暂停时仍跑）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（DI）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Inbound/InboundMatchTests.cs`

**Interfaces:**
- Consumes: Task 1 `dbo.inbound_match`；Task 5 `TraceCodeStore`（上游码已入 trace_code）、upstream_bill/line/code 表、`up:{billId}:{ver}` 意图（job_kind=inbound-match，payload `{orgId,env,snapshotId=null,trigger="upstream",billId}`）；Task 7 `InboundTaskFactory.CreateAsync(conn,tx,InboundTaskContext,ct)`、`InboundCancelService.CancelPreSendAsync(conn,tx,orgId,env,snapshotId,actor,reason,auditEnv,ct)`、`CodeClaimWriter.InsertActiveAsync`、`AllocatedLine/StoredCodeRef/InboundTaskContext`；`IJobHandler{string Kind; Task HandleAsync(ClaimedJob,CancellationToken)}`；`ClaimedJob(long Id,string JobKind,string IntentKey,string? PayloadJson,long FencingToken,DateTime LeaseUntilUtc,int AttemptCount)`；`TraceCodeNormalizer.Normalize(string?)→NormalizedCode(Raw,Normalized,CodeFormat)`、`CodeFormat.Supported8Prefix20`（TraceFlow.Domain.Codes）；0003 product/supplier/product_mapping/supplier_mapping/platform_drug/platform_enterprise_version（active 态=AUTO_CONFIRMED/MANUAL_CONFIRMED）；0001 organization.ref_ent_id/ent_id；0002 ingest_exception。
- Produces（T9/T10/T11 按此消费）:
  - `sealed class InboundMatchService(string connectionString, InboundTaskFactory factory, IAuditLog audit)`。
  - `.RunAsync(long orgId, string env, long? snapshotId, CancellationToken)→Task<MatchSummary>`：snapshotId=null 扫描全部 COMPLETE+PURCHASE_IN 且（无 inbound_match 或 match_state<>COMPLETE）的快照；非 null 只处理该快照（无论当前 match_state）。
  - `sealed record MatchSummary(int SnapshotsSeen, int Complete, int NotRequired, int Partial, int Ambiguous, int Conflict, int Unmatched, int TasksCreated)`。
  - `sealed class InboundMatchJobHandler(InboundMatchService svc) : IJobHandler`，`Kind="inbound-match"`，payload `{orgId,env,snapshotId?,trigger,billId?}`。
  - 调度意图 `inbound:periodic:{unix/300}`（job_kind=inbound-match，暂停通道时仍入队——本地计算）。
  - T10 手动重匹配意图 `rematch:{snapshotId}:{Guid:N}`；T11 映射变更意图 `map:{Guid:N}`（payload snapshotId=null 全量扫描）。

**裁定（T15 汇入）：**
- **R8a 既有任务处置**：event_key=`KSOA:{docKey}:PURCHASE_IN` 的既有任务——已发送（有 UPLOAD 尝试）或状态不在 {Blocked,Ready,Cancelled} → **冻结**（match=COMPLETE，reason 注明，不动任务/不建分配）；pre-send 且 source_hash==快照 content_hash → 无操作（防周期扫描空转）；pre-send 且 hash 不同 → `CancelPreSendAsync`（按**旧任务自己的 source_snapshot_id**）后重建（走 R7a 复活，bill_code 不变）。
- **R8b 触发语义**：`up:`/`map:`/periodic 触发一律跑待匹配扫描；只有携带 snapshotId 的触发（rematch）定向单快照。billId 仅作审计线索，不做反查。
- **R8c 不接线 src: 触发**：M1 SourceSyncService 无 IJobQueue 依赖，不改其构造；新 COMPLETE 快照由 ≤5 分钟周期扫描兜底。`src:` 意图命名空间保留未用。
- **R8d 自动匹配证据门槛**（IN-03/AT-024）：仅单位相同（1/1）、批号 Trim 后 Ordinal 相等且非空、数量精确相等、上游行码集全部 MIN 包装级（候选值）+S8P20 格式、码数==数量（整数）、任一码无 ACTIVE PURCHASE_IN 占用、码集内无父子边（code_relation 双向）。每 REQUIRED 行恰一候选且上游行互不重复才算唯一全解→COMPLETE+自动建任务；任一行多候选或争用同一上游行→AMBIGUOUS（不自动挑选）；部分行无候选→PARTIAL；全部无候选→UNMATCHED。
- **R8e 单据候选**：supplier_mapping active→ref_ent_id 过滤 upstream_bill（COMPLETE）；`to_ent_id` 非空且≠org.ent_id 排除；raw_header 含 `ywdjbh`（候选字段）→按 bill_no 精确命中，未命中→UNMATCHED；命中但存在"药+数量相符而批号不同"的行→CONFLICT（AT-025）；无 ywdjbh→bill_date 在业务时间 ±7 天窗口（业务时间缺失→UNMATCHED）。
- **R8f 分类门槛**（IN-02/AT-030）：任一来源行档案缺失或 UNKNOWN→UNMATCHED；全部 EXEMPT→NOT_REQUIRED 且 0 任务；REQUIRED 行数量≤0→写 ingest_exception（QTY_NONPOSITIVE，OPEN 去重）+UNMATCHED。

- [ ] **Step 1: 写失败的集成测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Inbound/InboundMatchTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

public class InboundMatchTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public InboundMatchTests(Sql2008Fixture fx) => _fx = fx;

    private InboundMatchService NewService() => new(_fx.ConnectionString,
        new InboundTaskFactory(new SettingsService(_fx.ConnectionString, new DpapiProtector()),
            new JobQueue(_fx.ConnectionString, new AuditLog())), new AuditLog());

    private sealed record Seed(long OrgId, long SysId, long SnapId, long LineId, long BillId, long UpLineId,
        string Dwbh, string Spid, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    private async Task<Seed> SeedBaseAsync(SqlConnection c, string tag, int codeBase)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.supplier WHERE org_id=@orgId AND source_system_id=@sysId AND dwbh=@dwbh)
INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND source_system_id=@sysId AND dwbh=@dwbh", new { orgId, sysId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync(@"
INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=@spid", new { orgId, sysId, spid });
        var drugId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var (billId, upLineId) = await AddBillAsync(c, orgId, "PB-" + tag, "UP-" + tag, drug, "B2026", codeBase);
        return new Seed(orgId, sysId, snapId, lineId, billId, upLineId, dwbh, spid, Code(codeBase), Code(codeBase + 1));
    }

    private async Task<(long BillId, long UpLineId)> AddBillAsync(SqlConnection c, long orgId, string platformBillId, string billNo, string drug, string batch, int codeBase)
    {
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness,source_version)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE',N'v1')",
            new { orgId, pb = platformBillId, no = billNo, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,@batch,2,N'盒')", new { billId, drug, batch });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync(@"
INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level)
VALUES(@billId,@lineId,@n,@raw,N'MIN')", new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await tx.CommitAsync();
        return (billId, upLineId);
    }

    [Fact]
    public async Task Unique_full_solution_auto_creates_task()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M1", 10);
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Complete);
        Assert.Equal(1, sum.TasksCreated);
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M1:PURCHASE_IN" });
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'RESERVED'", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_event WHERE upload_task_id=@id", new { id = taskId }));
    }

    [Fact]
    public async Task All_exempt_lines_not_required_zero_tasks()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M2", 20);
        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'EXEMPT' WHERE org_id=@orgId AND spid=@spid", new { orgId = s.OrgId, spid = s.Spid });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.NotRequired);
        Assert.Equal(0, sum.TasksCreated);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M2:PURCHASE_IN" }));
    }

    [Fact]
    public async Task Unknown_classification_unmatched()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M3", 30);
        await c.ExecuteAsync("UPDATE dbo.product SET trace_classification=N'UNKNOWN' WHERE org_id=@orgId AND spid=@spid", new { orgId = s.OrgId, spid = s.Spid });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Unmatched);
        Assert.Equal(0, sum.TasksCreated);
    }

    [Fact]
    public async Task Nonpositive_required_qty_writes_ingest_exception()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M4", 40);
        await c.ExecuteAsync("UPDATE dbo.source_line SET quantity_original=0 WHERE id=@id", new { id = s.LineId });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Unmatched);
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ingest_exception WHERE source_document_key=N'PI-M4' AND exception_type=N'QTY_NONPOSITIVE' AND status=N'OPEN'") >= 1);
    }

    [Fact]
    public async Task Multiple_candidates_ambiguous_no_auto_pick()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M5", 50);
        await AddBillAsync(c, s.OrgId, "PB-M5B", "UP-M5B", "DRUG-M5", "B2026", 52);
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Ambiguous);
        Assert.Equal(0, sum.TasksCreated);
    }

    [Fact]
    public async Task External_no_hit_but_batch_conflict()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M6", 60);
        await c.ExecuteAsync("UPDATE dbo.source_snapshot SET raw_header=@h WHERE id=@id", new { id = s.SnapId, h = "{\"dwbh\":\"" + s.Dwbh + "\",\"ywdjbh\":\"UP-M6\"}" });
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_line SET batch_no=N'B9999' WHERE id=@id", new { id = s.UpLineId });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Conflict);
        Assert.Equal(0, sum.TasksCreated);
    }

    [Fact]
    public async Task One_line_unmatched_partial()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M7", 70);
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'2',@spid,2,N'盒',N'B0000',@hash,N'PURCHASE_IN')", new { snapId = s.SnapId, spid = s.Spid, hash = new string('0', 64) });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Partial);
        Assert.Equal(0, sum.TasksCreated);
    }

    [Fact]
    public async Task Sent_task_frozen_on_rematch()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M8", 80);
        await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M8:PURCHASE_IN" });
        await c.ExecuteAsync("INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,fencing_token) VALUES(@id,1,N'UPLOAD',N'MOCK',1)", new { id = taskId });
        await c.ExecuteAsync("UPDATE dbo.upload_task SET state=N'Succeeded' WHERE id=@id", new { id = taskId });
        await c.ExecuteAsync("UPDATE dbo.source_snapshot SET content_hash=@h WHERE id=@id", new { id = s.SnapId, h = new string('1', 64) });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        Assert.Equal(1, sum.Complete);
        Assert.Equal(0, sum.TasksCreated);
        Assert.Equal("Succeeded", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'RESERVED'", new { id = s.LineId }));
    }

    [Fact]
    public async Task New_snapshot_version_rebuilds_pre_send_task_same_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedBaseAsync(c, "M9", 90);
        await NewService().RunAsync(s.OrgId, "MOCK", s.SnapId, default);
        var taskId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M9:PURCHASE_IN" });
        var billCode = await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = taskId });
        var snap2 = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-M9',N'PURCHASE_IN',2,@biz,N'COMPLETE',@hash,@header)",
            new { orgId = s.OrgId, sysId = s.SysId, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('1', 64), header = "{\"dwbh\":\"" + s.Dwbh + "\"}" });
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId = snap2, spid = s.Spid, hash = new string('1', 64) });
        var sum = await NewService().RunAsync(s.OrgId, "MOCK", snap2, default);
        Assert.Equal(1, sum.Complete);
        Assert.Equal(1, sum.TasksCreated);
        Assert.Equal(taskId, await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_task WHERE event_key=@k", new { k = "KSOA:PI-M9:PURCHASE_IN" }));
        Assert.Equal(billCode, await c.ExecuteScalarAsync<string>("SELECT bill_code FROM dbo.upload_part WHERE task_id=@id", new { id = taskId }));
        Assert.Equal(snap2, await c.ExecuteScalarAsync<long>("SELECT source_snapshot_id FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = snap2 }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = taskId }));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build traceflow/TraceFlow.sln -c Release`
Expected: 编译失败（InboundMatchService/MatchSummary 不存在）——RED。

- [ ] **Step 3: 实现 InboundMatchService**

Create `traceflow/src/TraceFlow.Infrastructure/Inbound/InboundMatchService.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;

namespace TraceFlow.Infrastructure.Inbound;

public sealed record MatchSummary(int SnapshotsSeen, int Complete, int NotRequired, int Partial, int Ambiguous, int Conflict, int Unmatched, int TasksCreated);

/// <summary>
/// 入库自动匹配（IN-01..05/AT-024..026/030）：分类门槛→供应商身份→单据候选→行级证据唯一全解→自动建任务。
/// 多解 AMBIGUOUS 留人工补配；既有任务处置/触发语义/证据门槛见计划裁定 R8a..R8f。
/// </summary>
public sealed class InboundMatchService
{
    private readonly string _cs;
    private readonly InboundTaskFactory _factory;
    private readonly IAuditLog _audit;
    public InboundMatchService(string connectionString, InboundTaskFactory factory, IAuditLog audit)
    {
        _cs = connectionString;
        _factory = factory;
        _audit = audit;
    }

    public async Task<MatchSummary> RunAsync(long orgId, string env, long? snapshotId, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        List<long> ids;
        if (snapshotId is not null) ids = new List<long> { snapshotId.Value };
        else ids = (await c.QueryAsync<long>(new CommandDefinition(@"
SELECT s.id FROM dbo.source_snapshot s
LEFT JOIN dbo.inbound_match m ON m.org_id=s.org_id AND m.env=@env AND m.source_snapshot_id=s.id
WHERE s.org_id=@orgId AND s.document_type=N'PURCHASE_IN' AND s.completeness_status=N'COMPLETE'
  AND (m.id IS NULL OR m.match_state<>N'COMPLETE')", new { orgId, env }, cancellationToken: ct))).ToList();
        int seen = 0, complete = 0, notRequired = 0, partial = 0, ambiguous = 0, conflict = 0, unmatched = 0, tasks = 0;
        foreach (var id in ids)
        {
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            var (state, created) = await MatchOneAsync(c, tx, orgId, env, id, ct);
            await tx.CommitAsync(ct);
            seen++;
            switch (state)
            {
                case "COMPLETE": complete++; break;
                case "NOT_REQUIRED": notRequired++; break;
                case "PARTIAL": partial++; break;
                case "AMBIGUOUS": ambiguous++; break;
                case "CONFLICT": conflict++; break;
                case "UNMATCHED": unmatched++; break;
            }
            if (created) tasks++;
        }
        return new MatchSummary(seen, complete, notRequired, partial, ambiguous, conflict, unmatched, tasks);
    }

    private async Task<(string State, bool TaskCreated)> MatchOneAsync(SqlConnection c, SqlTransaction tx, long orgId, string env, long snapshotId, CancellationToken ct)
    {
        var snap = await c.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id,source_system_id,source_document_key,document_type,completeness_status,business_datetime,content_hash,raw_header
FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId", new { id = snapshotId, orgId }, tx, cancellationToken: ct));
        if (snap is null || snap.document_type != "PURCHASE_IN" || snap.completeness_status != "COMPLETE")
            return ("SKIPPED", false);
        var lines = (await c.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw
FROM dbo.source_line WHERE snapshot_id=@id AND event_kind=N'PURCHASE_IN' ORDER BY id", new { id = snapshotId }, tx, cancellationToken: ct))).ToList();

        async Task<(string, bool)> Save(string state, string? reason, string? dwbh, object evidence, bool created = false)
        {
            await SaveMatchAsync(c, tx, orgId, env, snapshotId, dwbh, state, reason, JsonSerializer.Serialize(evidence), ct);
            await _audit.AppendAsync(c, tx, new AuditEntry("SYSTEM", "System", "inbound.match", "source_snapshot",
                snapshotId.ToString(), reason ?? state, null, null, null, null, env));
            return (state, created);
        }

        if (lines.Count == 0)
            return await Save("UNMATCHED", "采购入库快照无明细行", null, new { });
        var spids = lines.Select(l => l.product_source_id).Distinct().ToList();
        var classes = (await c.QueryAsync<ClassRow>(new CommandDefinition(
            "SELECT spid,trace_classification FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sys AND spid IN @spids",
            new { orgId, sys = snap.source_system_id, spids }, tx, cancellationToken: ct)))
            .ToDictionary(r => r.spid, r => r.trace_classification, StringComparer.Ordinal);
        if (lines.Any(l => !classes.TryGetValue(l.product_source_id, out var cl) || cl == "UNKNOWN"))
            return await Save("UNMATCHED", "存在 UNKNOWN 分类或档案缺失的来源行", null, new { });
        var required = lines.Where(l => classes[l.product_source_id] == "REQUIRED").ToList();
        if (required.Count == 0)
            return await Save("NOT_REQUIRED", "全部行 EXEMPT，无需上传", null, new { });
        foreach (var l in required.Where(l => l.quantity_original <= 0))
            await c.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.ingest_exception WHERE org_id=@orgId AND source_document_key=@key AND source_line_key=@lk AND exception_type=N'QTY_NONPOSITIVE' AND status=N'OPEN')
INSERT INTO dbo.ingest_exception(org_id,source_system_id,source_document_key,source_line_key,exception_type,detail)
VALUES(@orgId,@sys,@key,@lk,N'QTY_NONPOSITIVE',N'REQUIRED 行数量≤0')",
                new { orgId, sys = snap.source_system_id, key = snap.source_document_key, lk = l.source_line_key }, tx, cancellationToken: ct));
        if (required.Any(l => l.quantity_original <= 0))
            return await Save("UNMATCHED", "REQUIRED 行数量≤0，已记录接入异常", null, new { });

        string? dwbh = null, ywdjbh = null;
        if (snap.raw_header is not null)
        {
            using var hd = JsonDocument.Parse(snap.raw_header);
            dwbh = Str(hd.RootElement, "dwbh");
            ywdjbh = Str(hd.RootElement, "ywdjbh");
        }
        if (string.IsNullOrEmpty(dwbh))
            return await Save("UNMATCHED", "来源单头缺少供应商 dwbh", null, new { });
        var org = await c.QuerySingleAsync<OrgRow>(new CommandDefinition(
            "SELECT ent_id FROM dbo.organization WHERE id=@orgId", new { orgId }, tx, cancellationToken: ct));
        if (string.IsNullOrEmpty(org.ent_id))
            return await Save("UNMATCHED", "门店平台身份 ent_id 未配置", dwbh, new { });
        var sup = await c.QuerySingleOrDefaultAsync<SupRow>(new CommandDefinition(@"
SELECT sm.version AS map_version, ev.ref_ent_id, ev.ent_id
FROM dbo.supplier s
JOIN dbo.supplier_mapping sm ON sm.supplier_id=s.id AND sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE s.org_id=@orgId AND s.source_system_id=@sys AND s.dwbh=@dwbh",
            new { orgId, sys = snap.source_system_id, dwbh }, tx, cancellationToken: ct));
        if (sup is null)
            return await Save("UNMATCHED", "供应商映射未确认，无法定位上游身份", dwbh, new { });

        var bills = (await c.QueryAsync<BillRow>(new CommandDefinition(@"
SELECT id,bill_no,bill_date,to_ent_id FROM dbo.upstream_bill
WHERE org_id=@orgId AND env=@env AND upstream_ref_ent_id=@ref AND completeness=N'COMPLETE'",
            new { orgId, env, @ref = sup.ref_ent_id }, tx, cancellationToken: ct)))
            .Where(b => b.to_ent_id is null || string.Equals(b.to_ent_id, org.ent_id, StringComparison.Ordinal)).ToList();
        if (ywdjbh is not null)
        {
            bills = bills.Where(b => string.Equals(b.bill_no, ywdjbh, StringComparison.Ordinal)).ToList();
            if (bills.Count == 0)
                return await Save("UNMATCHED", "外部单号未命中上游出库单", dwbh, new { ywdjbh });
        }
        else
        {
            if (snap.business_datetime is null)
                return await Save("UNMATCHED", "来源单缺少业务时间，无法窗口匹配", dwbh, new { });
            var lo = snap.business_datetime.Value.AddDays(-7);
            var hi = snap.business_datetime.Value.AddDays(7);
            bills = bills.Where(b => b.bill_date is not null && b.bill_date >= lo && b.bill_date <= hi).ToList();
        }

        var maps = (await c.QueryAsync<MapRow>(new CommandDefinition(@"
SELECT p.spid, pd.platform_drug_id, pm.version
FROM dbo.product p
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE p.org_id=@orgId AND p.source_system_id=@sys AND p.spid IN @spids",
            new { orgId, sys = snap.source_system_id, spids = required.Select(l => l.product_source_id).Distinct().ToList() }, tx, cancellationToken: ct)))
            .ToDictionary(r => r.spid, StringComparer.Ordinal);
        if (required.Any(l => !maps.ContainsKey(l.product_source_id)))
            return await Save("UNMATCHED", "商品映射未确认，无法定位平台药品身份", dwbh, new { });

        var billIds = bills.Select(b => b.id).ToList();
        var upLines = billIds.Count == 0 ? new List<UpLineRow>() : (await c.QueryAsync<UpLineRow>(new CommandDefinition(
            "SELECT id,bill_id,platform_drug_id,batch_no,quantity,unit FROM dbo.upstream_bill_line WHERE bill_id IN @billIds",
            new { billIds }, tx, cancellationToken: ct))).ToList();
        var upCodes = billIds.Count == 0 ? new List<UpCodeRow>() : (await c.QueryAsync<UpCodeRow>(new CommandDefinition(
            "SELECT line_id,code_normalized,package_level FROM dbo.upstream_bill_code WHERE bill_id IN @billIds",
            new { billIds }, tx, cancellationToken: ct))).ToList();
        var codesByLine = upCodes.GroupBy(k => k.line_id).ToDictionary(g => g.Key, g => g.ToList());
        var allCodes = upCodes.Select(k => k.code_normalized).Distinct().ToList();
        var claimed = allCodes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            new HashSet<string>(await c.QueryAsync<string>(new CommandDefinition(@"
SELECT tc.code_normalized FROM dbo.code_claim cc JOIN dbo.trace_code tc ON tc.id=cc.code_id
WHERE cc.org_id=@orgId AND cc.env=@env AND cc.event_scope=N'PURCHASE_IN' AND cc.claim_state=N'ACTIVE' AND tc.code_normalized IN @allCodes",
                new { orgId, env, allCodes }, tx, cancellationToken: ct)), StringComparer.Ordinal);
        var relations = allCodes.Count == 0 ? new List<RelRow>() : (await c.QueryAsync<RelRow>(new CommandDefinition(@"
SELECT p.code_normalized AS parent_norm, ch.code_normalized AS child_norm
FROM dbo.code_relation r JOIN dbo.trace_code p ON p.id=r.parent_code_id JOIN dbo.trace_code ch ON ch.id=r.child_code_id
WHERE p.code_normalized IN @allCodes AND ch.code_normalized IN @allCodes",
            new { allCodes }, tx, cancellationToken: ct))).ToList();

        bool CodesEligible(UpLineRow u)
        {
            if (u.quantity is null || u.quantity != decimal.Truncate(u.quantity.Value)) return false;
            if (!codesByLine.TryGetValue(u.id, out var ks) || ks.Count == 0 || ks.Count != (int)u.quantity.Value) return false;
            if (ks.Any(k => k.package_level != "MIN")) return false;
            if (ks.Any(k => TraceCodeNormalizer.Normalize(k.code_normalized).Format != CodeFormat.Supported8Prefix20)) return false;
            if (ks.Any(k => claimed.Contains(k.code_normalized))) return false;
            var set = new HashSet<string>(ks.Select(k => k.code_normalized), StringComparer.Ordinal);
            if (relations.Any(r => set.Contains(r.parent_norm) && set.Contains(r.child_norm))) return false;
            return true;
        }

        var candidates = new Dictionary<long, List<UpLineRow>>();
        foreach (var l in required)
        {
            var drugId = maps[l.product_source_id].platform_drug_id;
            candidates[l.id] = upLines.Where(u =>
                string.Equals(u.platform_drug_id, drugId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(l.batch_raw) && !string.IsNullOrWhiteSpace(u.batch_no)
                && string.Equals(l.batch_raw.Trim(), u.batch_no.Trim(), StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(l.unit_original) && !string.IsNullOrWhiteSpace(u.unit)
                && string.Equals(l.unit_original.Trim(), u.unit.Trim(), StringComparison.Ordinal)
                && u.quantity == l.quantity_original
                && CodesEligible(u)).ToList();
        }
        var evidence = new { lines = required.Select(l => new { sourceLineKey = l.source_line_key, candidates = candidates[l.id].Select(u => u.id).ToArray() }).ToArray() };
        if (required.All(l => candidates[l.id].Count == 0))
            return await Save("UNMATCHED", "无符合证据门槛的上游明细", dwbh, evidence);
        if (required.Any(l => candidates[l.id].Count == 0))
        {
            if (ywdjbh is not null && required.Where(l => candidates[l.id].Count == 0).Any(l =>
                upLines.Any(u => string.Equals(u.platform_drug_id, maps[l.product_source_id].platform_drug_id, StringComparison.Ordinal) && u.quantity == l.quantity_original)))
                return await Save("CONFLICT", "外部单号命中但批号冲突，须人工核对", dwbh, evidence);
            return await Save("PARTIAL", "部分来源行无匹配上游明细", dwbh, evidence);
        }
        if (required.Any(l => candidates[l.id].Count > 1)
            || required.Select(l => candidates[l.id][0].id).Distinct().Count() != required.Count)
            return await Save("AMBIGUOUS", "存在多解或上游明细争用，须人工补配", dwbh, evidence);

        var existing = await c.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(
            "SELECT id,state,source_hash,source_snapshot_id FROM dbo.upload_task WHERE org_id=@orgId AND env=@env AND event_key=@key",
            new { orgId, env, key = $"KSOA:{snap.source_document_key}:PURCHASE_IN" }, tx, cancellationToken: ct));
        if (existing is not null && existing.state != "Cancelled")
        {
            var sent = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@id AND attempt_kind=N'UPLOAD'", new { id = existing.id }, tx, cancellationToken: ct));
            if (sent > 0 || existing.state is not ("Blocked" or "Ready"))
                return await Save("COMPLETE", "既有已发送任务冻结，来源变化由 M3 处置", dwbh, evidence);
            if (existing.source_hash == snap.content_hash)
                return await Save("COMPLETE", "既有发送前任务与来源一致，无需重建", dwbh, evidence);
            await InboundCancelService.CancelPreSendAsync(c, tx, orgId, env, existing.source_snapshot_id, "SYSTEM", "来源版本变化，重建补配", env, ct);
        }

        var allocLines = new List<AllocatedLine>();
        var mappingVersions = new List<string> { $"supplier:{dwbh}:{sup.map_version}" };
        foreach (var l in required)
        {
            var u = candidates[l.id][0];
            var ks = codesByLine[u.id];
            var stored = (await c.QueryAsync<CodeRow>(new CommandDefinition(
                "SELECT id,code_normalized FROM dbo.trace_code WHERE code_normalized IN @codes",
                new { codes = ks.Select(k => k.code_normalized).ToList() }, tx, cancellationToken: ct))).ToList();
            var allocId = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.inbound_allocation(org_id,env,source_line_id,upstream_line_id,quantity,unit_ratio_numerator,unit_ratio_denominator,package_level,state,created_by)
OUTPUT INSERTED.id VALUES(@orgId,@env,@lineId,@upLineId,@qty,1,1,N'MIN',N'RESERVED',N'SYSTEM')",
                new { orgId, env, lineId = l.id, upLineId = u.id, qty = l.quantity_original }, tx, cancellationToken: ct));
            foreach (var sc in stored)
            {
                await c.ExecuteAsync(new CommandDefinition("INSERT INTO dbo.allocation_code(allocation_id,code_id) VALUES(@allocId,@codeId)",
                    new { allocId, codeId = sc.id }, tx, cancellationToken: ct));
                await CodeClaimWriter.InsertActiveAsync(c, tx, orgId, env, sc.id, "PURCHASE_IN", allocId, null, ct);
            }
            allocLines.Add(new AllocatedLine(allocId, l.id, l.source_line_key, l.product_source_id,
                maps[l.product_source_id].platform_drug_id, u.batch_no!, l.quantity_original, l.unit_original!,
                stored.Select(sc => new StoredCodeRef(sc.id, sc.code_normalized)).ToList()));
            mappingVersions.Add($"product:{l.product_source_id}:{maps[l.product_source_id].version}");
        }
        var ctx = new InboundTaskContext(orgId, env, snapshotId, snap.source_document_key, snap.content_hash,
            snap.business_datetime, dwbh, sup.ref_ent_id, sup.ent_id, allocLines, mappingVersions.Distinct().ToList(), "SYSTEM");
        await _factory.CreateAsync(c, tx, ctx, ct);
        return await Save("COMPLETE", null, dwbh, evidence, created: true);
    }

    private static async Task SaveMatchAsync(SqlConnection c, SqlTransaction tx, long orgId, string env, long snapshotId,
        string? dwbh, string state, string? reason, string evidenceJson, CancellationToken ct)
    {
        var n = await c.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET supplier_dwbh=@dwbh, match_state=@state, reason=@reason, evidence_json=@ev, version=version+1, matched_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snap",
            new { orgId, env, snap = snapshotId, dwbh, state, reason, ev = evidenceJson }, tx, cancellationToken: ct));
        if (n == 0)
            await c.ExecuteAsync(new CommandDefinition(@"
INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state,reason,evidence_json,matched_at)
VALUES(@orgId,@env,@snap,@dwbh,@state,@reason,@ev,SYSUTCDATETIME())",
                new { orgId, env, snap = snapshotId, dwbh, state, reason, ev = evidenceJson }, tx, cancellationToken: ct));
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class SnapRow
    {
        public long id { get; set; }
        public long source_system_id { get; set; }
        public string source_document_key { get; set; } = "";
        public string document_type { get; set; } = "";
        public string completeness_status { get; set; } = "";
        public DateTime? business_datetime { get; set; }
        public string content_hash { get; set; } = "";
        public string? raw_header { get; set; }
    }
    private sealed class LineRow
    {
        public long id { get; set; }
        public string source_line_key { get; set; } = "";
        public string product_source_id { get; set; } = "";
        public decimal quantity_original { get; set; }
        public string? unit_original { get; set; }
        public string? batch_raw { get; set; }
    }
    private sealed class ClassRow { public string spid { get; set; } = ""; public string trace_classification { get; set; } = ""; }
    private sealed class OrgRow { public string? ent_id { get; set; } }
    private sealed class SupRow { public int map_version { get; set; } public string ref_ent_id { get; set; } = ""; public string ent_id { get; set; } = ""; }
    private sealed class BillRow { public long id { get; set; } public string? bill_no { get; set; } public DateTime? bill_date { get; set; } public string? to_ent_id { get; set; } }
    private sealed class MapRow { public string spid { get; set; } = ""; public string platform_drug_id { get; set; } = ""; public int version { get; set; } }
    private sealed class UpLineRow
    {
        public long id { get; set; }
        public long bill_id { get; set; }
        public string? platform_drug_id { get; set; }
        public string? batch_no { get; set; }
        public decimal? quantity { get; set; }
        public string? unit { get; set; }
    }
    private sealed class UpCodeRow { public long line_id { get; set; } public string code_normalized { get; set; } = ""; public string? package_level { get; set; } }
    private sealed class RelRow { public string parent_norm { get; set; } = ""; public string child_norm { get; set; } = ""; }
    private sealed class CodeRow { public long id { get; set; } public string code_normalized { get; set; } = ""; }
    private sealed class TaskRow { public long id { get; set; } public string state { get; set; } = ""; public string source_hash { get; set; } = ""; public long source_snapshot_id { get; set; } }
}
```

- [ ] **Step 4: 作业处理器 + 调度桶 + DI**

Create `traceflow/src/TraceFlow.Infrastructure/Jobs/InboundMatchJobHandler.cs`：

```csharp
using System.Text.Json;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Inbound;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class InboundMatchJobHandler : IJobHandler
{
    private readonly InboundMatchService _svc;
    public InboundMatchJobHandler(InboundMatchService svc) => _svc = svc;
    public string Kind => "inbound-match";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(job.PayloadJson ?? "{}");
        var r = doc.RootElement;
        var orgId = r.GetProperty("orgId").GetInt64();
        var env = r.GetProperty("env").GetString()!;
        long? snapshotId = r.TryGetProperty("snapshotId", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
        await _svc.RunAsync(orgId, env, snapshotId, ct);
    }
}
```

`SyncScheduler.cs`：master-match 入队块（`$"match:periodic:{unix / 300}"` 与其后的 `n++;`）之后追加（**在 !paused 块外**——本地计算，暂停通道仍跑）：

```csharp
        var inboundPayload = JsonSerializer.Serialize(new { orgId = orgId.Value, env = _env, snapshotId = (long?)null, trigger = "periodic" });
        await _queue.EnqueueAsync(c, tx, orgId.Value, _env, "inbound-match",
            $"inbound:periodic:{unix / 300}", inboundPayload, nowUtc, ct);
        n++;
```

`Program.cs` Task 6 接线后追加：

```csharp
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Inbound.InboundTaskFactory(
    sp.GetRequiredService<TraceFlow.Infrastructure.Settings.SettingsService>(), sp.GetRequiredService<IJobQueue>()));
builder.Services.AddSingleton(sp => new TraceFlow.Infrastructure.Inbound.InboundMatchService(
    connectionString, sp.GetRequiredService<TraceFlow.Infrastructure.Inbound.InboundTaskFactory>(), sp.GetRequiredService<IAuditLog>()));
builder.Services.AddSingleton<IJobHandler>(sp => new TraceFlow.Infrastructure.Jobs.InboundMatchJobHandler(
    sp.GetRequiredService<TraceFlow.Infrastructure.Inbound.InboundMatchService>()));
```

在 `InboundMatchTests.cs` 的 `InboundMatchTests` 类末尾追加调度桶测试（暂停时 inbound-match 仍入队、source-sync 不入队）：

```csharp
    [Fact]
    public async Task Scheduler_enqueues_inbound_match_even_when_paused()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, _) = await TestSeed.EnsureAsync(c);
        var settings = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await settings.UpdateAsync(c, tx, "sync.paused", "true", null, "0", "测试暂停", "test", default);
            await tx.CommitAsync();
        }
        var scheduler = new TraceFlow.Infrastructure.Sources.SyncScheduler(_fx.ConnectionString,
            new JobQueue(_fx.ConnectionString, new AuditLog()), settings, "MOCK");
        await scheduler.TickAsync(new DateTime(2026, 11, 23, 3, 0, 0, DateTimeKind.Utc), default);
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'inbound-match' AND intent_key LIKE 'inbound:periodic:%'", new { org = orgId }) >= 1);
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE org_id=@org AND job_kind=N'source-sync'", new { org = orgId }));
    }
```

- [ ] **Step 5: 更新 M1 调度计数断言 + 构建 + 全部测试确认通过**

`SyncSchedulerTests.cs`（inbound-match 桶 +1，暂停时仍入队）：`Assert.Equal(7, n); // 2 实体 × (periodic+daily) + 1 match + 1 upstream + 1 enterprise` 改为 `Assert.Equal(8, n); // 2 实体 × (periodic+daily) + 1 match + 1 upstream + 1 enterprise + 1 inbound`；两处 `Assert.Equal(7, await JobCountAsync());` 改为 `Assert.Equal(8, await JobCountAsync());`；`Assert.Equal(9, await JobCountAsync()); // +2 periodic（daily/match/upstream/enterprise 桶未变）` 改为 `Assert.Equal(10, await JobCountAsync()); // +2 periodic（其余桶未变）`；`Paused_channel_skips_source_sync_but_keeps_match` 末尾追加 `Assert.Equal(1, await c2.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'inbound-match'"));`。

Run: `dotnet build traceflow/TraceFlow.sln -c Release`（0 警告）
Run: `TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release`
Expected: 全绿（+10 InboundMatchTests；SyncSchedulerTests 按上述更新后通过）。

- [ ] **Step 6: Commit**

```bash
git add traceflow/src/TraceFlow.Infrastructure/Inbound/InboundMatchService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/InboundMatchJobHandler.cs traceflow/src/TraceFlow.Infrastructure/Sources/SyncScheduler.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Inbound/InboundMatchTests.cs traceflow/tests/TraceFlow.IntegrationTests/Sources/SyncSchedulerTests.cs
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): inbound auto-match with evidence gates and task triggers (AT-024/025/026/030, IN-01..05)"
```

---

### Task 9: 人工分配保存/取消服务

**Files:**
- Create: `traceflow/src/TraceFlow.Infrastructure/Inbound/AllocationService.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Inbound/AllocationServiceTests.cs`

**Interfaces:**
- Consumes: Task 7 `CodeClaimWriter.InsertActiveAsync/ReleaseByAllocationAsync`、`InboundCancelService.CancelPreSendAsync`、`AllocationConflictException/AllocationValidationException`；Task 8 供应商/商品 active 映射查询模式；`Qty(decimal)`（精度>6 位或 |v|≥10^12 抛 ArgumentOutOfRangeException）、`UnitRatio(long,long).Convert(Qty)→Qty`（不整除/超域抛 UnitConversionException）、`TraceCodeNormalizer.Normalize`、`CodeFormat.Supported8Prefix20`；0005 inbound_allocation（UX_alloc_pair_active）/allocation_code/code_claim。
- Produces（T10 逐字使用）:
  - `sealed class AllocationService(IAuditLog audit)`。
  - `.SaveAsync(SqlConnection, SqlTransaction, long orgId, string env, long snapshotId, AllocationRequest req, string actor, CancellationToken)→Task<AllocationSaveResult>`。
  - `.CancelAsync(conn, tx, long orgId, string env, long snapshotId, long? allocationId, string actor, string reason, CancellationToken)→Task<int>`（返回取消的分配数；allocationId=null 整单取消；COMMITTED 单行取消=整单发送前取消）。
  - `sealed record AllocationLineInput(long SourceLineId, long UpstreamLineId, decimal Quantity, string UnitRatio, string? ConversionEvidence, IReadOnlyList<string> Codes)`。
  - `sealed record AllocationRequest(string ExpectedMatchVersion, IReadOnlyList<AllocationLineInput> Lines)`。
  - `sealed record AllocationSaveResult(int MatchVersion, int AllocationsSaved, bool AllRequiredCovered)`。

**裁定（T15 汇入）：**
- **R9a 保存=全量替换**：SaveAsync 先撤销该快照全部 RESERVED 分配（释放占用）；若存在发送前任务先 `CancelPreSendAsync`（任务/分配/占用/匹配一并回退），再插入新分配。已发送任务→409 `FROZEN`。
- **R9b 保存后匹配态**：全部 REQUIRED 行被覆盖→COMPLETE，否则 PARTIAL（不建任务——任务只在 T10 确认时创建）；version+1。
- **R9c 校验失败语义**：行级错误收集为 fieldErrors（`{sourceLineId,code,message}`），一次性 422 `ALLOCATION_INVALID` 抛出；强冲突（药品/批号/供应商身份）拒绝而非警告；非 MIN 码→`BIG_CODE_BLOCKED`（AT-028，v1 无拆零）；换算不整除或换算结果≠码数→`CONVERSION_INEXACT`（AT-029）；非 1/1 比例缺依据→`CONVERSION_EVIDENCE_REQUIRED`。
- **R9d 版本闸**：`ExpectedMatchVersion`（int 字符串）≠inbound_match.version→409 `VERSION_CONFLICT`（AT-067）；占用争用→409 `CODE_CONTENDED`（DB-05）；分配对重复→409 `ALLOCATION_EXISTS`。

- [ ] **Step 1: 写失败的集成测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Inbound/AllocationServiceTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Security;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

public class AllocationServiceTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public AllocationServiceTests(Sql2008Fixture fx) => _fx = fx;

    private sealed record Seed(long OrgId, long SnapId, long LineId, long UpLineId, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    private async Task<Seed> SeedAsync(SqlConnection c, string tag, int codeBase)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=@dwbh", new { orgId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-A-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE')",
            new { orgId, pb = "PB-A-" + tag, no = "UP-A-" + tag, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,N'B2026',2,N'盒')", new { billId, drug });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync("INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level) VALUES(@billId,@lineId,@n,@raw,N'MIN')",
                new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,@dwbh,N'UNMATCHED')",
            new { orgId, snapId, dwbh }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, upLineId, Code(codeBase), Code(codeBase + 1));
    }

    private static AllocationRequest Req(long lineId, long upLineId, string version, decimal qty, string ratio, string? evidence, params string[] codes)
        => new(version, new[] { new AllocationLineInput(lineId, upLineId, qty, ratio, evidence, codes) });

    private static AllocationService NewService() => new(new AuditLog());

    // fieldErrors 元素是 Infrastructure 程序集的匿名类型，跨程序集 dynamic 不可用，用反射读 code
    private static bool HasError(IReadOnlyList<object> errors, string code)
        => errors.Any(e => (string?)e.GetType().GetProperty("code")?.GetValue(e) == code);

    [Fact]
    public async Task Save_valid_reserves_claims_and_completes_match()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A1", 10);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var r = await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
        await tx.CommitAsync();
        Assert.Equal(1, r.AllocationsSaved);
        Assert.True(r.AllRequiredCovered);
        Assert.Equal(2, r.MatchVersion);
        Assert.Equal("RESERVED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim cc JOIN dbo.inbound_allocation a ON a.id=cc.allocation_id WHERE a.source_line_id=@id AND cc.claim_state=N'ACTIVE'", new { id = s.LineId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Save_stale_version_conflict()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A2", 20);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "99", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.Equal("VERSION_CONFLICT", ex.ErrorCode);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_big_code_blocked()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A3", 30);
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_code SET package_level=N'BIG' WHERE code_normalized=@n", new { n = s.Code1 });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.Equal("ALLOCATION_INVALID", ex.ErrorCode);
        Assert.True(HasError(ex.FieldErrors!, "BIG_CODE_BLOCKED"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_conversion_inexact_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A4", 40);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/3", null, s.Code1, s.Code2), "tester", default));
        Assert.True(HasError(ex.FieldErrors!, "CONVERSION_INEXACT"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_conversion_count_mismatch_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A5", 50);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "3/2", null, s.Code1, s.Code2), "tester", default));
        Assert.True(HasError(ex.FieldErrors!, "CONVERSION_INEXACT"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Save_nonunit_ratio_requires_evidence()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A6", 60);
        await c.ExecuteAsync("UPDATE dbo.source_line SET quantity_original=1 WHERE id=@id", new { id = s.LineId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
                NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 1m, "2/1", null, s.Code1, s.Code2), "tester", default));
            Assert.True(HasError(ex.FieldErrors!, "CONVERSION_EVIDENCE_REQUIRED"));
            await tx.RollbackAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var r = await NewService().SaveAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 1m, "2/1", "1 箱=2 盒，随货同行单编号 X", s.Code1, s.Code2), "tester", default);
        await tx2.CommitAsync();
        Assert.True(r.AllRequiredCovered);
    }

    [Fact]
    public async Task Save_code_contended_409()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A7", 70);
        var codeId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = s.Code1 });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await CodeClaimWriter.InsertActiveAsync(c, tx, s.OrgId, "MOCK", codeId, "PURCHASE_IN", null, null, default);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            NewService().SaveAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.Equal("CODE_CONTENDED", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Save_drug_conflict_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A8", 80);
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_line SET platform_drug_id=N'OTHER-DRUG' WHERE id=@id", new { id = s.UpLineId });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default));
        Assert.True(HasError(ex.FieldErrors!, "DRUG_CONFLICT"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Cancel_reserved_releases_claims_and_unmatches()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A9", 90);
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        var allocId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId });
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var n = await NewService().CancelAsync(c, tx, s.OrgId, "MOCK", s.SnapId, allocId, "tester", "取消补配", default);
            Assert.Equal(1, n);
            await tx.CommitAsync();
        }
        Assert.Equal("CANCELLED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE id=@id", new { id = allocId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE allocation_id=@id AND claim_state=N'ACTIVE'", new { id = allocId }));
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Save_replaces_previous_reserved()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "A10", 100);
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var r = await NewService().SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "2", 2m, "1/1", null, s.Code1, s.Code2), "tester", default);
            Assert.Equal(3, r.MatchVersion);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'CANCELLED'", new { id = s.LineId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.inbound_allocation WHERE source_line_id=@id AND state=N'RESERVED'", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim cc JOIN dbo.inbound_allocation a ON a.id=cc.allocation_id WHERE a.source_line_id=@id AND cc.claim_state=N'ACTIVE'", new { id = s.LineId }));
    }
}
```

- [ ] **Step 2: 运行测试确认失败（RED）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~AllocationServiceTests"
```

Expected: 编译失败（AllocationService/AllocationLineInput/AllocationRequest/AllocationSaveResult 不存在）。

- [ ] **Step 3: 实现 AllocationService**

Create `traceflow/src/TraceFlow.Infrastructure/Inbound/AllocationService.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Codes;
using TraceFlow.Domain.Quantities;

namespace TraceFlow.Infrastructure.Inbound;

public sealed record AllocationLineInput(long SourceLineId, long UpstreamLineId, decimal Quantity, string UnitRatio, string? ConversionEvidence, IReadOnlyList<string> Codes);
public sealed record AllocationRequest(string ExpectedMatchVersion, IReadOnlyList<AllocationLineInput> Lines);
public sealed record AllocationSaveResult(int MatchVersion, int AllocationsSaved, bool AllRequiredCovered);

public sealed class AllocationService
{
    private readonly IAuditLog _audit;
    public AllocationService(IAuditLog audit) => _audit = audit;

    private sealed record SnapRow(long id, long source_system_id, string source_document_key, string document_type, string completeness_status, string? raw_header);
    private sealed record MatchRow(long id, int version, string match_state);
    private sealed record TaskRow(long id, string state, long source_snapshot_id);
    private sealed record LineRow(long id, string source_line_key, string product_source_id, decimal quantity_original, string? unit_original, string? batch_raw);
    private sealed record UpRow(long id, long bill_id, string? platform_drug_id, string? batch_no, string upstream_ref_ent_id, string completeness);
    private sealed record AllocRow(long id, string state);
    private sealed record ParsedLine(AllocationLineInput Input, LineRow Line, Qty Qty, long Num, long Den);

    public async Task<AllocationSaveResult> SaveAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, AllocationRequest req, string actor, CancellationToken ct)
    {
        var snap = await conn.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_system_id, source_document_key, document_type, completeness_status, raw_header
FROM dbo.source_snapshot WHERE id=@snapshotId AND org_id=@orgId", new { orgId, snapshotId }, tx, cancellationToken: ct));
        if (snap is null || snap.document_type != "PURCHASE_IN" || snap.completeness_status != "COMPLETE")
            throw new AllocationValidationException("VALIDATION", "快照不存在或非 COMPLETE 采购入库快照");
        var match = await conn.QuerySingleOrDefaultAsync<MatchRow>(new CommandDefinition(@"
SELECT id, version, match_state FROM dbo.inbound_match WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId", new { orgId, env, snapshotId }, tx, cancellationToken: ct));
        if (match is null)
            throw new AllocationValidationException("VALIDATION", "尚未生成匹配记录，请先触发匹配");
        if (match.match_state == "NOT_REQUIRED")
            throw new AllocationValidationException("VALIDATION", "全部行豁免，无需补配");
        if (!int.TryParse(req.ExpectedMatchVersion, out var expected) || expected != match.version)
            throw new AllocationConflictException("VERSION_CONFLICT", "匹配版本已变化，请刷新后重试");

        var eventKey = $"KSOA:{snap.source_document_key}:PURCHASE_IN";
        var task = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id, state, source_snapshot_id FROM dbo.upload_task
WHERE org_id=@orgId AND env=@env AND event_key=@eventKey AND source_snapshot_id IS NOT NULL",
            new { orgId, env, eventKey }, tx, cancellationToken: ct));
        if (task is not null && task.state != "Cancelled")
        {
            var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
                new { taskId = task.id }, tx, cancellationToken: ct));
            if (sent > 0 || (task.state != "Blocked" && task.state != "Ready"))
                throw new AllocationConflictException("FROZEN", "任务已发送或已进入发送流程，禁止修改补配");
            await InboundCancelService.CancelPreSendAsync(conn, tx, orgId, env, task.source_snapshot_id, actor, "人工补配保存前撤销旧任务", "LIVE", ct);
        }
        else
        {
            await CancelReservedForSnapshotAsync(conn, tx, orgId, env, snapshotId, actor, "人工补配保存替换", ct);
        }

        var lines = (await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id, source_line_key, product_source_id, quantity_original, unit_original, batch_raw
FROM dbo.source_line WHERE snapshot_id=@snapshotId AND event_kind=N'PURCHASE_IN'",
            new { snapshotId }, tx, cancellationToken: ct))).ToDictionary(l => l.id);
        var required = await RequiredLineIdsAsync(conn, tx, orgId, snap.source_system_id, lines.Values.ToList(), ct);
        var dwbh = Str(snap.raw_header, "dwbh");
        var supRef = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT ev.ref_ent_id FROM dbo.supplier s
JOIN dbo.supplier_mapping sm ON sm.supplier_id=s.id AND sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE s.org_id=@orgId AND s.source_system_id=@sys AND s.dwbh=@dwbh",
            new { orgId, sys = snap.source_system_id, dwbh }, tx, cancellationToken: ct));
        if (supRef is null)
            throw new AllocationValidationException("SUPPLIER_UNRESOLVED", "供应商映射未确认，无法补配");

        var fieldErrors = new List<object>();
        var parsed = new List<ParsedLine>();
        var seenSource = new HashSet<long>();
        var seenUp = new HashSet<long>();
        foreach (var input in req.Lines)
        {
            void Err(string code, string msg) => fieldErrors.Add(new { sourceLineId = input.SourceLineId, code, message = msg });
            if (!lines.TryGetValue(input.SourceLineId, out var sl)) { Err("LINE_NOT_FOUND", "来源行不属于该快照"); continue; }
            if (!required.Contains(input.SourceLineId)) { Err("NOT_REQUIRED_LINE", "仅 REQUIRED 行可补配（EXEMPT/UNKNOWN 拒绝）"); continue; }
            if (!seenSource.Add(input.SourceLineId)) { Err("DUPLICATE_LINE", "同一来源行重复提交"); continue; }
            if (!seenUp.Add(input.UpstreamLineId)) { Err("UPSTREAM_LINE_REUSED", "同一上游明细被多行争用"); continue; }
            var up = await conn.QuerySingleOrDefaultAsync<UpRow>(new CommandDefinition(@"
SELECT l.id, l.bill_id, l.platform_drug_id, l.batch_no, b.upstream_ref_ent_id, b.completeness
FROM dbo.upstream_bill_line l JOIN dbo.upstream_bill b ON b.id=l.bill_id
WHERE l.id=@id AND b.org_id=@orgId AND b.env=@env",
                new { id = input.UpstreamLineId, orgId, env }, tx, cancellationToken: ct));
            if (up is null || up.completeness != "COMPLETE") { Err("UPSTREAM_NOT_FOUND", "上游明细不存在或单据未完成"); continue; }
            if (!string.Equals(up.upstream_ref_ent_id, supRef, StringComparison.Ordinal)) { Err("SUPPLIER_MISMATCH", "上游单据不属于已确认供应商"); continue; }
            var drugId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT pd.platform_drug_id FROM dbo.product p
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE p.org_id=@orgId AND p.source_system_id=@sys AND p.spid=@spid",
                new { orgId, sys = snap.source_system_id, spid = sl.product_source_id }, tx, cancellationToken: ct));
            if (drugId is null) { Err("PRODUCT_UNRESOLVED", "商品映射未确认"); continue; }
            if (!string.Equals(up.platform_drug_id, drugId, StringComparison.Ordinal)) { Err("DRUG_CONFLICT", "药品身份强冲突，禁止分配"); continue; }
            if (string.IsNullOrWhiteSpace(sl.batch_raw) || string.IsNullOrWhiteSpace(up.batch_no)
                || !string.Equals(sl.batch_raw.Trim(), up.batch_no.Trim(), StringComparison.Ordinal))
            { Err("BATCH_CONFLICT", "批号强冲突，禁止分配"); continue; }
            if (input.Codes.Count == 0) { Err("CODES_EMPTY", "至少选择一个码"); continue; }
            if (input.Codes.Distinct(StringComparer.Ordinal).Count() != input.Codes.Count) { Err("CODES_DUPLICATE", "码重复"); continue; }
            var upCodes = (await conn.QueryAsync<UpCodeRow>(new CommandDefinition(
                "SELECT code_normalized, package_level FROM dbo.upstream_bill_code WHERE line_id=@id",
                new { id = input.UpstreamLineId }, tx, cancellationToken: ct))).ToDictionary(k => k.code_normalized, StringComparer.Ordinal);
            if (input.Codes.Any(cn => !upCodes.ContainsKey(cn))) { Err("CODE_NOT_IN_LINE", "码不属于所选上游明细"); continue; }
            if (input.Codes.Any(cn => upCodes[cn].package_level != "MIN")) { Err("BIG_CODE_BLOCKED", "非最小包装码禁止入库分配（v1 无拆零，AT-028）"); continue; }
            if (input.Codes.Any(cn => TraceCodeNormalizer.Normalize(cn).Format != CodeFormat.Supported8Prefix20)) { Err("CODE_FORMAT", "码格式不支持"); continue; }
            var codeIds = new List<long>();
            foreach (var cn in input.Codes)
                codeIds.Add(await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = cn }, tx, cancellationToken: ct)));
            var rel = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.code_relation WHERE parent_code_id IN @ids AND child_code_id IN @ids",
                new { ids = codeIds }, tx, cancellationToken: ct));
            if (rel > 0) { Err("CODE_RELATION", "码集存在父子关系，禁止重复计数"); continue; }
            Qty qty;
            try { qty = new Qty(input.Quantity); }
            catch (ArgumentOutOfRangeException) { Err("QTY_DOMAIN", "数量超出 decimal(18,6) 域"); continue; }
            if (qty.Value <= 0) { Err("QTY_NONPOSITIVE", "数量必须为正"); continue; }
            var parts = input.UnitRatio.Split('/');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var num) || !long.TryParse(parts[1], out var den) || num <= 0 || den <= 0)
            { Err("RATIO_FORMAT", "单位比例须为 n/d 正整数"); continue; }
            var ratio = new UnitRatio(num, den);
            Qty converted;
            try { converted = ratio.Convert(qty); }
            catch (UnitConversionException) { Err("CONVERSION_INEXACT", "换算不整除或超域（AT-029）"); continue; }
            if (converted.Value != input.Codes.Count)
            { Err("CONVERSION_INEXACT", $"换算结果 {converted.Value} 与码数 {input.Codes.Count} 不一致（AT-029）"); continue; }
            if ((num != 1 || den != 1) && string.IsNullOrWhiteSpace(input.ConversionEvidence))
            { Err("CONVERSION_EVIDENCE_REQUIRED", "非 1/1 换算必须提供换算依据"); continue; }
            parsed.Add(new ParsedLine(input, sl, qty, num, den));
        }
        if (fieldErrors.Count > 0)
            throw new AllocationValidationException("ALLOCATION_INVALID", "补配校验失败", fieldErrors);
        if (parsed.Count == 0)
            throw new AllocationValidationException("EMPTY_LINES", "至少提交一条补配行");

        foreach (var p in parsed)
        {
            long allocId;
            try
            {
                allocId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO dbo.inbound_allocation(org_id, env, source_line_id, upstream_line_id, quantity, unit_ratio_numerator, unit_ratio_denominator, conversion_evidence, package_level, state, created_by)
OUTPUT INSERTED.id
VALUES(@orgId, @env, @sourceLineId, @upstreamLineId, @qty, @num, @den, @evidence, N'MIN', N'RESERVED', @actor)",
                    new { orgId, env, sourceLineId = p.Input.SourceLineId, upstreamLineId = p.Input.UpstreamLineId,
                          qty = p.Qty.Value, num = p.Num, den = p.Den, evidence = p.Input.ConversionEvidence, actor },
                    tx, cancellationToken: ct));
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                throw new AllocationConflictException("ALLOCATION_EXISTS", "该来源行/上游明细对已存在有效分配");
            }
            foreach (var cn in p.Input.Codes)
            {
                var codeId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.trace_code WHERE code_normalized=@n", new { n = cn }, tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO dbo.allocation_code(allocation_id, code_id) VALUES(@allocId, @codeId)",
                    new { allocId, codeId }, tx, cancellationToken: ct));
                await CodeClaimWriter.InsertActiveAsync(conn, tx, orgId, env, codeId, "PURCHASE_IN", allocId, null, ct);
            }
        }
        var covered = required.SetEquals(parsed.Select(p => p.Input.SourceLineId));
        var newState = covered ? "COMPLETE" : "PARTIAL";
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=@state, reason=N'人工补配保存', version=version+1, matched_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId",
            new { state = newState, orgId, env, snapshotId }, tx, cancellationToken: ct));
        var newVersion = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT version FROM dbo.inbound_match WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct));
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.save", "source_snapshot", snapshotId.ToString(),
            null, null, null, null, null, "LIVE"));
        return new AllocationSaveResult(newVersion, parsed.Count, covered);
    }

    public async Task<int> CancelAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, long snapshotId,
        long? allocationId, string actor, string reason, CancellationToken ct)
    {
        if (allocationId is not null)
        {
            var alloc = await conn.QuerySingleOrDefaultAsync<AllocRow>(new CommandDefinition(@"
SELECT a.id, a.state FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.id=@id AND a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId",
                new { id = allocationId, orgId, env, snapshotId }, tx, cancellationToken: ct));
            if (alloc is null)
                throw new AllocationValidationException("VALIDATION", "分配不存在或不属于该快照");
            if (alloc.state == "CANCELLED") return 0;
            if (alloc.state == "COMMITTED")
            {
                // COMMITTED 已挂任务：单行取消=整单发送前取消（任务/全部分配一并回退）
                var ok = await InboundCancelService.CancelPreSendAsync(conn, tx, orgId, env, snapshotId, actor, reason, "LIVE", ct);
                if (ok)
                    await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.cancel", "source_snapshot", snapshotId.ToString(), reason, null, null, null, null, "LIVE"));
                return ok ? 1 : 0;
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1 WHERE id=@id",
                new { id = alloc.id }, tx, cancellationToken: ct));
            await CodeClaimWriter.ReleaseByAllocationAsync(conn, tx, alloc.id, actor, reason, ct);
            await RecomputeMatchAsync(conn, tx, orgId, env, snapshotId, ct);
            await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.cancel", "inbound_allocation", alloc.id.ToString(), reason, null, null, null, null, "LIVE"));
            return 1;
        }
        var snap = await conn.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_system_id, source_document_key, document_type, completeness_status, raw_header
FROM dbo.source_snapshot WHERE id=@snapshotId AND org_id=@orgId", new { orgId, snapshotId }, tx, cancellationToken: ct));
        if (snap is null)
            throw new AllocationValidationException("VALIDATION", "快照不存在");
        var eventKey = $"KSOA:{snap.source_document_key}:PURCHASE_IN";
        var task = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id, state, source_snapshot_id FROM dbo.upload_task
WHERE org_id=@orgId AND env=@env AND event_key=@eventKey AND source_snapshot_id IS NOT NULL",
            new { orgId, env, eventKey }, tx, cancellationToken: ct));
        if (task is not null && task.state != "Cancelled")
        {
            var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
                new { taskId = task.id }, tx, cancellationToken: ct));
            if (sent > 0 || (task.state != "Blocked" && task.state != "Ready"))
                throw new AllocationConflictException("FROZEN", "任务已发送或已进入发送流程，禁止取消");
            var ok = await InboundCancelService.CancelPreSendAsync(conn, tx, orgId, env, task.source_snapshot_id, actor, reason, "LIVE", ct);
            return ok ? 1 : 0;
        }
        var n = await CancelReservedForSnapshotAsync(conn, tx, orgId, env, snapshotId, actor, reason, ct);
        if (n > 0)
        {
            await RecomputeMatchAsync(conn, tx, orgId, env, snapshotId, ct);
            await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.cancel", "source_snapshot", snapshotId.ToString(), reason, null, null, null, null, "LIVE"));
        }
        return n;
    }

    private static async Task<int> CancelReservedForSnapshotAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, long snapshotId, string actor, string reason, CancellationToken ct)
    {
        var ids = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT a.id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId AND a.state=N'RESERVED'",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct))).ToList();
        foreach (var id in ids)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.inbound_allocation SET state=N'CANCELLED', version=version+1 WHERE id=@id",
                new { id }, tx, cancellationToken: ct));
            await CodeClaimWriter.ReleaseByAllocationAsync(conn, tx, id, actor, reason, ct);
        }
        return ids.Count;
    }

    private static async Task RecomputeMatchAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env, long snapshotId, CancellationToken ct)
    {
        var snap = await conn.QuerySingleAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_system_id, source_document_key, document_type, completeness_status, raw_header
FROM dbo.source_snapshot WHERE id=@snapshotId", new { snapshotId }, tx, cancellationToken: ct));
        var lines = (await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id, source_line_key, product_source_id, quantity_original, unit_original, batch_raw
FROM dbo.source_line WHERE snapshot_id=@snapshotId AND event_kind=N'PURCHASE_IN'",
            new { snapshotId }, tx, cancellationToken: ct))).ToList();
        var required = await RequiredLineIdsAsync(conn, tx, orgId, snap.source_system_id, lines, ct);
        var coveredIds = (await conn.QueryAsync<long>(new CommandDefinition(@"
SELECT a.source_line_id FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId AND a.state IN (N'RESERVED',N'COMMITTED')",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct))).ToHashSet();
        var state = required.Count == 0 ? "NOT_REQUIRED"
            : required.SetEquals(coveredIds) ? "COMPLETE"
            : coveredIds.Count > 0 ? "PARTIAL" : "UNMATCHED";
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=@state, reason=N'人工取消补配', version=version+1, matched_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId",
            new { state, orgId, env, snapshotId }, tx, cancellationToken: ct));
    }

    private static async Task<HashSet<long>> RequiredLineIdsAsync(SqlConnection conn, SqlTransaction tx, long orgId, long sysId, IReadOnlyList<LineRow> lines, CancellationToken ct)
    {
        var result = new HashSet<long>();
        foreach (var l in lines)
        {
            var cls = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT trace_classification FROM dbo.product WHERE org_id=@orgId AND source_system_id=@sysId AND spid=@spid",
                new { orgId, sysId, spid = l.product_source_id }, tx, cancellationToken: ct));
            if (cls == "REQUIRED") result.Add(l.id);
        }
        return result;
    }

    private sealed record UpCodeRow(string code_normalized, string? package_level);

    private static string? Str(string? json, string name)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }
}
```

注意：`AuditEntry` 构造参数顺序为 `(Actor, ActorKind, Action, ObjectType, ObjectId?, Reason?, BeforeDigest?, AfterDigest?, RequestId?, RuleVersion?, Env)`；审计接口为 `IAuditLog.AppendAsync(SqlConnection, SqlTransaction?, AuditEntry)`（与 Task 7/8 一致）。`UpCodeRow` 与 Task 8 的同名私有记录互不影响（各自文件内私有）。

- [ ] **Step 4: 运行测试确认通过（GREEN）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~AllocationServiceTests"
```

Expected: PASS（10 个测试）。再跑全量集成+单元确认无回归：

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
```

- [ ] **Step 5: 提交**

```bash
cd "/e/Viding/Retail Trace Code" && git status --porcelain && git add traceflow/src/TraceFlow.Infrastructure/Inbound/AllocationService.cs traceflow/tests/TraceFlow.IntegrationTests/Inbound/AllocationServiceTests.cs && git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): manual allocation save/cancel with eligibility gates (AT-027/028/029/032/067, IN-03/04/09)"
```

---

### Task 10: 确认资格复核 + 入库/上游 API + 门禁测试

**Files:**
- Modify: `traceflow/src/TraceFlow.Infrastructure/Inbound/AllocationService.cs`（追加 ConfirmAsync/ConfirmResult）
- Create: `traceflow/src/TraceFlow.Host/Endpoints/InboundEndpoints.cs`
- Create: `traceflow/src/TraceFlow.Host/Endpoints/UpstreamEndpoints.cs`
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（DI AllocationService + 两个 Map 注册）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Inbound/AllocationConfirmTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/M2ApiGateTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/InboundApiTests.cs`

**Interfaces:**
- Consumes: Task 9 `AllocationService.SaveAsync/CancelAsync`、`AllocationRequest/AllocationLineInput`；Task 7 `InboundTaskFactory(SettingsService, IJobQueue).CreateAsync(conn,tx,InboundTaskContext,ct)→TaskCreationResult(TaskId,PartId,BillCode,Reused)`、`AllocatedLine/StoredCodeRef/InboundTaskContext`、`InboundCancelService`、`CodeClaimWriter.BindTaskAsync`（factory 内部已调）；Task 8 `InboundMatchJobHandler` payload `{orgId,env,snapshotId?,trigger,billId?}`；Task 5 `UpstreamDownloadJobHandler` payload `{orgId,env,start?,end?,billId?,trigger}`（缺省窗口 now-7d..now）；`IdempotencyExecutor.RunAsync(HttpContext, Func<SqlConnection,SqlTransaction,CancellationToken,Task<(int,string)>>)→Task<IResult>`；`RequirePermissionAttribute(PermissionCatalog.X)`；`IJobQueue.EnqueueAsync(conn,tx,orgId,env,jobKind,intentKey,payloadJson,nextRunAtUtc,ct)→Task<long>`；CSRF 全局中间件（Program.cs 已有，POST/PUT 自动校验 X-CSRF-TOKEN）。
- Produces（T13/T14 前端逐字消费）:
  - `AllocationService.ConfirmAsync(conn, tx, long orgId, string env, long snapshotId, string expectedMatchVersion, string actor, InboundTaskFactory factory, CancellationToken)→Task<ConfirmResult>`；`sealed record ConfirmResult(long TaskId, string BillCode, int MatchVersion, bool Reused)`。
  - `GET /api/v1/inbounds?page&pageSize&matchState&docKey`（trace:read）→ `{data:{records:[{snapshotId,docKey,version,businessDatetimeUtc,completeness,matchState,matchVersion,reason,supplierDwbh,taskId,taskState}],total,page,pageSize}}`；每 docKey 仅最新版本（ROW_NUMBER）。
  - `GET /api/v1/inbounds/{id}`（trace:read）→ `{data:{snapshot:{...},lines:[{lineId,sourceLineKey,productSourceId,quantity,unit,batchRaw,classification}],match:{matchState,version,reason},allocations:[{allocationId,sourceLineId,upstreamLineId,quantity,unitRatio,state,codes:[{codeId,codeNormalized}]}],task:{taskId,state,billCode}|null}}`。
  - `POST /api/v1/inbounds/{id}/rematch`（job:rematch + Idempotency-Key）→ 202 `{data:{jobId}}`；意图 `rematch:{snapshotId}:{Guid:N}`。
  - `POST /api/v1/inbounds/{id}/allocations`（inbound:allocate + Idempotency-Key，body=AllocationRequest JSON）→ 200 `{data:{matchVersion,allocationsSaved,allRequiredCovered}}`。
  - `POST /api/v1/inbounds/{id}/confirm-allocation`（inbound:allocate + Idempotency-Key，body `{expectedMatchVersion}`）→ 200 `{data:{taskId,billCode,matchVersion,reused,message:"符合资格后将自动上传"}}`。
  - `POST /api/v1/inbounds/{id}/cancel-allocation`（inbound:allocate + Idempotency-Key，body `{allocationId?,reason}`）→ 200 `{data:{cancelled}}`。
  - `GET /api/v1/upstream-bills?page&pageSize&billNo&completeness`（trace:read）→ `{data:{records:[{billId,platformBillId,billNo,billDate,upstreamRefEntId,toEntId,completeness,sourceVersion,fetchedAtUtc}],total,page,pageSize}}`。
  - `GET /api/v1/upstream-bills/{id}`（trace:read）→ `{data:{bill:{...},lines:[{lineId,platformLineId,platformDrugId,batchNo,quantity,unit,codes:[{codeNormalized,packageLevel}]}]}}`。
  - `POST /api/v1/upstream-bills/{id}/redownload`（job:rematch + Idempotency-Key）→ 202 `{data:{jobId}}`；LIVE 门禁 `TOP_NOT_CERTIFIED`；意图 `redownload:{billId}:{Guid:N}`，payload `{orgId,env,start=now-90d,end=now,billId,trigger="redownload"}`。

**裁定（T15 汇入）：**
- **R10a 确认时任务态**：Save 已保证无存活发送前任务；Confirm 时若存在 Blocked/Ready 任务（并发自动匹配产物）→409 `VERSION_CONFLICT`（提示刷新后重新保存）；已发送/其他非 Cancelled 态→409 `FROZEN`；Cancelled→factory R7a 复活（bill_code 不变）。
- **R10b factory 注入方式**：ConfirmAsync 以参数接收 InboundTaskFactory（端点从 DI 取），AllocationService 构造保持 `(IAuditLog)` 不变，Task 9 测试不受影响。
- **R10c 确认复核（AT-031）**：Confirm 对每条 RESERVED/COMMITTED 分配重跑与 Save 相同的资格闸（供应商身份/药品/批号/MIN/S8P20/码归属/换算一致/占用 ACTIVE 归属本分配），并校验全部 REQUIRED 行被覆盖且 Σ分配数量==来源行数量（十进制精确相等）；任一失败→422 `ALLOCATION_INVALID`+fieldErrors，不落任何效果。
- **R10d redownload 窗口**：显式 start=now-90d、end=now（bill_date 可能早于缺省 7d 窗口），onlyBillId 过滤；LIVE 通道须 G-TOP 门禁（TOP_NOT_CERTIFIED）。

- [ ] **Step 1: 写失败的确认服务测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Inbound/AllocationConfirmTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.Infrastructure.Inbound;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using TraceFlow.IntegrationTests.Sources;
using Xunit;

namespace TraceFlow.IntegrationTests.Inbound;

public class AllocationConfirmTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public AllocationConfirmTests(Sql2008Fixture fx) => _fx = fx;

    private sealed record Seed(long OrgId, long SnapId, long LineId, long UpLineId, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    private async Task<Seed> SeedAsync(SqlConnection c, string tag, int codeBase, bool secondLine = false)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=@dwbh", new { orgId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-C-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        if (secondLine)
            await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
VALUES(@snapId,N'2',@spid,1,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE')",
            new { orgId, pb = "PB-C-" + tag, no = "UP-C-" + tag, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,N'B2026',2,N'盒')", new { billId, drug });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync("INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level) VALUES(@billId,@lineId,@n,@raw,N'MIN')",
                new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,@dwbh,N'UNMATCHED')",
            new { orgId, snapId, dwbh }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, upLineId, Code(codeBase), Code(codeBase + 1));
    }

    private InboundTaskFactory NewFactory()
        => new(new SettingsService(_fx.ConnectionString, new DpapiProtector()), new JobQueue(_fx.ConnectionString, new AuditLog()));

    private static AllocationRequest Req(long lineId, long upLineId, string version, params string[] codes)
        => new(version, new[] { new AllocationLineInput(lineId, upLineId, 2m, "1/1", null, codes) });

    [Fact]
    public async Task Confirm_commits_allocations_and_creates_ready_task()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C1", 10);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        ConfirmResult r;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            r = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default);
            await tx.CommitAsync();
        }
        Assert.Equal(3, r.MatchVersion);
        Assert.StartsWith("TF", r.BillCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("PURCHASE_IN_102", await c.ExecuteScalarAsync<string>("SELECT task_type FROM dbo.upload_task WHERE id=@id", new { id = r.TaskId }));
        Assert.Equal("COMMITTED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId }));
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE upload_task_id=@id AND claim_state=N'ACTIVE'", new { id = r.TaskId }));
        Assert.Equal("COMPLETE", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND job_state=N'PENDING' AND intent_key=@k",
            new { k = $"part:{await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.upload_part WHERE task_id=@id AND part_index=0", new { id = r.TaskId })}" }));
    }

    [Fact]
    public async Task Confirm_stale_version_conflict()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C2", 20);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "1", "tester", NewFactory(), default));
        Assert.Equal("VERSION_CONFLICT", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Confirm_rechecks_eligibility_after_tamper()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C3", 30);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            await tx.CommitAsync();
        }
        await c.ExecuteAsync("UPDATE dbo.upstream_bill_line SET batch_no=N'B9999' WHERE id=@id", new { id = s.UpLineId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default));
        Assert.Equal("ALLOCATION_INVALID", ex.ErrorCode);
        Assert.Contains(ex.FieldErrors!, e => (string?)e.GetType().GetProperty("code")?.GetValue(e) == "BATCH_CONFLICT");
        await tx2.RollbackAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upload_task WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Confirm_partial_coverage_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C4", 40, secondLine: true);
        var svc = new AllocationService(new AuditLog());
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            var save = await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            Assert.False(save.AllRequiredCovered);
            await tx.CommitAsync();
        }
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationValidationException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default));
        Assert.Contains(ex.FieldErrors!, e => (string?)e.GetType().GetProperty("code")?.GetValue(e) == "LINE_UNCOVERED");
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Confirm_frozen_when_sent()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C5", 50);
        var svc = new AllocationService(new AuditLog());
        long taskId;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            var r = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default);
            taskId = r.TaskId;
            await tx.CommitAsync();
        }
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,started_at)
VALUES(@taskId,1,N'UPLOAD',N'MOCK',SYSUTCDATETIME())", new { taskId });
        await using var tx2 = (SqlTransaction)await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AllocationConflictException>(() =>
            svc.ConfirmAsync(c, tx2, s.OrgId, "MOCK", s.SnapId, "3", "tester", NewFactory(), default));
        Assert.Equal("FROZEN", ex.ErrorCode);
        await tx2.RollbackAsync();
    }

    [Fact]
    public async Task Confirm_after_cancel_reactivates_same_bill_code()
    {
        await using var c = await _fx.OpenAsync();
        var s = await SeedAsync(c, "C6", 60);
        var svc = new AllocationService(new AuditLog());
        ConfirmResult first;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, "1", s.Code1, s.Code2), "tester", default);
            first = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, "2", "tester", NewFactory(), default);
            await tx.CommitAsync();
        }
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.CancelAsync(c, tx, s.OrgId, "MOCK", s.SnapId, null, "tester", "整单取消", default);
            await tx.CommitAsync();
        }
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = first.TaskId }));
        var ver = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId });
        ConfirmResult second;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.SaveAsync(c, tx, s.OrgId, "MOCK", s.SnapId, Req(s.LineId, s.UpLineId, ver.ToString(), s.Code1, s.Code2), "tester", default);
            var ver2 = await c.ExecuteScalarAsync<int>("SELECT version FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId });
            second = await svc.ConfirmAsync(c, tx, s.OrgId, "MOCK", s.SnapId, ver2.ToString(), "tester", NewFactory(), default);
            await tx.CommitAsync();
        }
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal(first.BillCode, second.BillCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = second.TaskId }));
    }
}
```

- [ ] **Step 2: 运行测试确认失败（RED）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~AllocationConfirmTests"
```

Expected: 编译失败（ConfirmAsync/ConfirmResult 不存在）。

- [ ] **Step 3: 实现 ConfirmAsync**

`AllocationService.cs`：`AllocationSaveResult` 记录行后追加：

```csharp
public sealed record ConfirmResult(long TaskId, string BillCode, int MatchVersion, bool Reused);
```

类内私有记录区（`ParsedLine` 行后）追加：

```csharp
    private sealed record AllocFullRow(long id, long source_line_id, long upstream_line_id, decimal quantity, long unit_ratio_numerator, long unit_ratio_denominator, string state, string source_line_key, string product_source_id, string? batch_raw, string? unit_original, decimal quantity_original, string? platform_drug_id, string? up_batch_no, string upstream_ref_ent_id, string completeness);
    private sealed record SupFullRow(string dwbh, int map_version, string? ref_ent_id, string? ent_id);
    private sealed record ProdMapRow(string spid, string platform_drug_id, int version);
    private sealed record AllocCodeRow(long code_id, string code_normalized, string? package_level);
    private sealed record SnapExtraRow(string content_hash, DateTime? business_datetime);
```

`CancelAsync` 方法后追加：

```csharp
    public async Task<ConfirmResult> ConfirmAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        long snapshotId, string expectedMatchVersion, string actor, InboundTaskFactory factory, CancellationToken ct)
    {
        var snap = await conn.QuerySingleOrDefaultAsync<SnapRow>(new CommandDefinition(@"
SELECT id, source_system_id, source_document_key, document_type, completeness_status, raw_header
FROM dbo.source_snapshot WHERE id=@snapshotId AND org_id=@orgId", new { orgId, snapshotId }, tx, cancellationToken: ct));
        if (snap is null || snap.document_type != "PURCHASE_IN" || snap.completeness_status != "COMPLETE")
            throw new AllocationValidationException("VALIDATION", "快照不存在或非 COMPLETE 采购入库快照");
        var match = await conn.QuerySingleOrDefaultAsync<MatchRow>(new CommandDefinition(@"
SELECT id, version, match_state FROM dbo.inbound_match WITH (UPDLOCK, HOLDLOCK)
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId", new { orgId, env, snapshotId }, tx, cancellationToken: ct));
        if (match is null)
            throw new AllocationValidationException("VALIDATION", "尚未生成匹配记录，请先触发匹配");
        if (!int.TryParse(expectedMatchVersion, out var expected) || expected != match.version)
            throw new AllocationConflictException("VERSION_CONFLICT", "匹配版本已变化，请刷新后重试");
        var eventKey = $"KSOA:{snap.source_document_key}:PURCHASE_IN";
        var task = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(@"
SELECT id, state, source_snapshot_id FROM dbo.upload_task
WHERE org_id=@orgId AND env=@env AND event_key=@eventKey AND source_snapshot_id IS NOT NULL",
            new { orgId, env, eventKey }, tx, cancellationToken: ct));
        if (task is not null && task.state != "Cancelled")
        {
            var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
                new { taskId = task.id }, tx, cancellationToken: ct));
            if (sent > 0 || (task.state != "Blocked" && task.state != "Ready"))
                throw new AllocationConflictException("FROZEN", "任务已发送或已进入发送流程，禁止确认");
            throw new AllocationConflictException("VERSION_CONFLICT", "存在自动匹配生成的发送前任务，请刷新后重新保存补配");
        }
        var allocs = (await conn.QueryAsync<AllocFullRow>(new CommandDefinition(@"
SELECT a.id, a.source_line_id, a.upstream_line_id, a.quantity, a.unit_ratio_numerator, a.unit_ratio_denominator, a.state,
       l.source_line_key, l.product_source_id, l.batch_raw, l.unit_original, l.quantity_original,
       u.platform_drug_id, u.batch_no AS up_batch_no, b.upstream_ref_ent_id, b.completeness
FROM dbo.inbound_allocation a
JOIN dbo.source_line l ON l.id=a.source_line_id
JOIN dbo.upstream_bill_line u ON u.id=a.upstream_line_id
JOIN dbo.upstream_bill b ON b.id=u.bill_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@snapshotId AND a.state IN (N'RESERVED',N'COMMITTED')",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct))).ToList();
        if (allocs.Count == 0)
            throw new AllocationValidationException("EMPTY_LINES", "无有效补配分配，请先保存");
        var sup = await conn.QuerySingleOrDefaultAsync<SupFullRow>(new CommandDefinition(@"
SELECT s.dwbh, sm.version AS map_version, ev.ref_ent_id, ev.ent_id
FROM dbo.supplier s
JOIN dbo.supplier_mapping sm ON sm.supplier_id=s.id AND sm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_enterprise_version ev ON ev.id=sm.enterprise_version_id
WHERE s.org_id=@orgId AND s.source_system_id=@sys AND s.dwbh=@dwbh",
            new { orgId, sys = snap.source_system_id, dwbh = Str(snap.raw_header, "dwbh") }, tx, cancellationToken: ct));
        if (sup is null || sup.ref_ent_id is null)
            throw new AllocationValidationException("SUPPLIER_UNRESOLVED", "供应商映射未确认，无法确认");
        var maps = (await conn.QueryAsync<ProdMapRow>(new CommandDefinition(@"
SELECT p.spid, pd.platform_drug_id, pm.version
FROM dbo.product p
JOIN dbo.product_mapping pm ON pm.product_id=p.id AND pm.state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED')
JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id
WHERE p.org_id=@orgId AND p.source_system_id=@sys AND p.spid IN @spids",
            new { orgId, sys = snap.source_system_id, spids = allocs.Select(a => a.product_source_id).Distinct().ToList() }, tx, cancellationToken: ct)))
            .ToDictionary(r => r.spid, StringComparer.Ordinal);
        var fieldErrors = new List<object>();
        var lines = new List<AllocatedLine>();
        foreach (var a in allocs)
        {
            void Err(string code, string msg) => fieldErrors.Add(new { allocationId = a.id, sourceLineId = a.source_line_id, code, message = msg });
            if (!string.Equals(a.upstream_ref_ent_id, sup.ref_ent_id, StringComparison.Ordinal)) { Err("SUPPLIER_MISMATCH", "上游单据不属于已确认供应商"); continue; }
            if (a.completeness != "COMPLETE") { Err("UPSTREAM_NOT_FOUND", "上游单据未完成"); continue; }
            if (!maps.TryGetValue(a.product_source_id, out var pm)) { Err("PRODUCT_UNRESOLVED", "商品映射未确认"); continue; }
            if (!string.Equals(a.platform_drug_id, pm.platform_drug_id, StringComparison.Ordinal)) { Err("DRUG_CONFLICT", "药品身份强冲突"); continue; }
            if (string.IsNullOrWhiteSpace(a.batch_raw) || string.IsNullOrWhiteSpace(a.up_batch_no)
                || !string.Equals(a.batch_raw.Trim(), a.up_batch_no.Trim(), StringComparison.Ordinal))
            { Err("BATCH_CONFLICT", "批号强冲突"); continue; }
            var codes = (await conn.QueryAsync<AllocCodeRow>(new CommandDefinition(@"
SELECT ac.code_id, tc.code_normalized, uc.package_level
FROM dbo.allocation_code ac
JOIN dbo.trace_code tc ON tc.id=ac.code_id
LEFT JOIN dbo.upstream_bill_code uc ON uc.line_id=@upLineId AND uc.code_normalized=tc.code_normalized
WHERE ac.allocation_id=@allocId",
                new { allocId = a.id, upLineId = a.upstream_line_id }, tx, cancellationToken: ct))).ToList();
            if (codes.Count == 0) { Err("CODES_EMPTY", "分配无码"); continue; }
            if (codes.Any(k => k.package_level is null)) { Err("CODE_NOT_IN_LINE", "码不属于所选上游明细"); continue; }
            if (codes.Any(k => k.package_level != "MIN")) { Err("BIG_CODE_BLOCKED", "非最小包装码禁止入库分配（AT-028）"); continue; }
            if (codes.Any(k => TraceCodeNormalizer.Normalize(k.code_normalized).Format != CodeFormat.Supported8Prefix20)) { Err("CODE_FORMAT", "码格式不支持"); continue; }
            Qty qty;
            try { qty = new Qty(a.quantity); }
            catch (ArgumentOutOfRangeException) { Err("QTY_DOMAIN", "数量超出域"); continue; }
            Qty converted;
            try { converted = new UnitRatio(a.unit_ratio_numerator, a.unit_ratio_denominator).Convert(qty); }
            catch (UnitConversionException) { Err("CONVERSION_INEXACT", "换算不整除或超域（AT-029）"); continue; }
            if (converted.Value != codes.Count) { Err("CONVERSION_INEXACT", "换算结果与码数不一致（AT-029）"); continue; }
            var owned = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.code_claim cc JOIN dbo.allocation_code ac ON ac.code_id=cc.code_id AND ac.allocation_id=@allocId
WHERE cc.org_id=@orgId AND cc.env=@env AND cc.event_scope=N'PURCHASE_IN' AND cc.claim_state=N'ACTIVE' AND cc.allocation_id=@allocId",
                new { allocId = a.id, orgId, env }, tx, cancellationToken: ct));
            if (owned != codes.Count) { Err("CLAIM_LOST", "码占用已失效或被争用"); continue; }
            lines.Add(new AllocatedLine(a.id, a.source_line_id, a.source_line_key, a.product_source_id, pm.platform_drug_id,
                a.batch_raw!.Trim(), a.quantity, a.unit_original ?? "",
                codes.Select(k => new StoredCodeRef(k.code_id, k.code_normalized)).ToList()));
        }
        var allLines = (await conn.QueryAsync<LineRow>(new CommandDefinition(@"
SELECT id, source_line_key, product_source_id, quantity_original, unit_original, batch_raw
FROM dbo.source_line WHERE snapshot_id=@snapshotId AND event_kind=N'PURCHASE_IN'",
            new { snapshotId }, tx, cancellationToken: ct))).ToList();
        var required = await RequiredLineIdsAsync(conn, tx, orgId, snap.source_system_id, allLines, ct);
        var bySource = allocs.GroupBy(a => a.source_line_id).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var rid in required)
        {
            if (!bySource.TryGetValue(rid, out var group))
            { fieldErrors.Add(new { sourceLineId = rid, code = "LINE_UNCOVERED", message = "REQUIRED 行未被补配覆盖" }); continue; }
            var src = allLines.First(l => l.id == rid);
            if (group.Sum(g => g.quantity) != src.quantity_original)
                fieldErrors.Add(new { sourceLineId = rid, code = "QTY_COVERAGE", message = "分配数量合计与来源行数量不一致" });
        }
        foreach (var a in allocs)
            if (!required.Contains(a.source_line_id))
                fieldErrors.Add(new { allocationId = a.id, sourceLineId = a.source_line_id, code = "NOT_REQUIRED_LINE", message = "非 REQUIRED 行不得分配" });
        if (fieldErrors.Count > 0)
            throw new AllocationValidationException("ALLOCATION_INVALID", "确认资格复核失败", fieldErrors);

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.inbound_allocation SET state=N'COMMITTED', version=version+1 WHERE id IN @ids AND state=N'RESERVED'",
            new { ids = allocs.Select(a => a.id).ToList() }, tx, cancellationToken: ct));
        var extra = await conn.QuerySingleAsync<SnapExtraRow>(new CommandDefinition(
            "SELECT content_hash, business_datetime FROM dbo.source_snapshot WHERE id=@snapshotId", new { snapshotId }, tx, cancellationToken: ct));
        var mappingVersions = new List<string> { $"supplier:{sup.dwbh}:{sup.map_version}" };
        mappingVersions.AddRange(maps.Values.Select(m => $"product:{m.spid}:{m.version}"));
        var ctx = new InboundTaskContext(orgId, env, snapshotId, snap.source_document_key, extra.content_hash, extra.business_datetime,
            sup.dwbh, sup.ref_ent_id, sup.ent_id ?? "", lines, mappingVersions.Distinct(StringComparer.Ordinal).ToList(), actor);
        var created = await factory.CreateAsync(conn, tx, ctx, ct);
        await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.inbound_match SET match_state=N'COMPLETE', reason=N'人工补配确认', version=version+1, matched_at=SYSUTCDATETIME()
WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId", new { orgId, env, snapshotId }, tx, cancellationToken: ct));
        var newVersion = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT version FROM dbo.inbound_match WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@snapshotId",
            new { orgId, env, snapshotId }, tx, cancellationToken: ct));
        await _audit.AppendAsync(conn, tx, new AuditEntry(actor, "User", "inbound.allocate.confirm", "upload_task", created.TaskId.ToString(),
            null, null, null, null, null, "LIVE"));
        return new ConfirmResult(created.TaskId, created.BillCode, newVersion, created.Reused);
    }
```

- [ ] **Step 4: 实现端点与 DI**

Create `traceflow/src/TraceFlow.Host/Endpoints/InboundEndpoints.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Inbound;

namespace TraceFlow.Host.Endpoints;

public static class InboundEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapInboundEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/inbounds", async (HttpContext ctx, int? page, int? pageSize, string? matchState, string? docKey) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            var orgId = await conn.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1");
            var where = "WHERE s.rn=1";
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", env);
            if (!string.IsNullOrEmpty(matchState)) { where += " AND m.match_state=@matchState"; args.Add("matchState", matchState); }
            if (!string.IsNullOrEmpty(docKey)) { where += " AND s.source_document_key=@docKey"; args.Add("docKey", docKey); }
            var baseSql = $@"
FROM (
    SELECT ROW_NUMBER() OVER (PARTITION BY source_document_key ORDER BY version DESC, id DESC) AS rn,
           id, source_document_key, version, business_datetime, completeness_status
    FROM dbo.source_snapshot WHERE org_id=@orgId AND document_type=N'PURCHASE_IN'
) s
LEFT JOIN dbo.inbound_match m ON m.source_snapshot_id=s.id AND m.org_id=@orgId AND m.env=@env
LEFT JOIN dbo.upload_task t ON t.source_snapshot_id=s.id AND t.org_id=@orgId AND t.env=@env AND t.task_type=N'PURCHASE_IN_102'
{where}";
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) {baseSql}", args);
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<InboundListRow>($@"
SELECT snapshotId,docKey,version,businessDatetimeUtc,completeness,matchState,matchVersion,reason,supplierDwbh,taskId,taskState FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY s.id DESC) AS rn2,
           s.id AS snapshotId, s.source_document_key AS docKey, s.version, s.business_datetime AS businessDatetimeUtc,
           s.completeness_status AS completeness, m.match_state AS matchState, m.version AS matchVersion, m.reason,
           m.supplier_dwbh AS supplierDwbh, t.id AS taskId, t.state AS taskState
    {baseSql}
) x WHERE rn2 BETWEEN @lo AND @hi ORDER BY rn2", args);
            return Results.Ok(new { data = new { records = rows, total, page = p, pageSize = size }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/inbounds/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var orgId = await conn.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1");
            var snap = await conn.QuerySingleOrDefaultAsync<SnapDetailRow>(
                "SELECT id, source_document_key, version, business_datetime, completeness_status, content_hash FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId AND document_type=N'PURCHASE_IN'",
                new { id, orgId });
            if (snap is null)
                return Results.Json(new { code = "NOT_FOUND", message = "入库快照不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            var lines = await conn.QueryAsync<LineDetailRow>(@"
SELECT l.id AS lineId, l.source_line_key AS sourceLineKey, l.product_source_id AS productSourceId, l.quantity_original AS quantity,
       l.unit_original AS unit, l.batch_raw AS batchRaw, p.trace_classification AS classification
FROM dbo.source_line l
LEFT JOIN dbo.product p ON p.org_id=@orgId AND p.source_system_id=(SELECT source_system_id FROM dbo.source_snapshot WHERE id=@id) AND p.spid=l.product_source_id
WHERE l.snapshot_id=@id AND l.event_kind=N'PURCHASE_IN' ORDER BY l.id", new { id, orgId });
            var match = await conn.QuerySingleOrDefaultAsync<MatchDetailRow>(
                "SELECT match_state AS matchState, version, reason FROM dbo.inbound_match WHERE org_id=@orgId AND env=@env AND source_snapshot_id=@id",
                new { orgId, env, id });
            var allocs = (await conn.QueryAsync<AllocDetailRow>(@"
SELECT a.id AS allocationId, a.source_line_id AS sourceLineId, a.upstream_line_id AS upstreamLineId, a.quantity,
       a.unit_ratio_numerator AS num, a.unit_ratio_denominator AS den, a.state
FROM dbo.inbound_allocation a JOIN dbo.source_line l ON l.id=a.source_line_id
WHERE a.org_id=@orgId AND a.env=@env AND l.snapshot_id=@id AND a.state IN (N'RESERVED',N'COMMITTED') ORDER BY a.id",
                new { orgId, env, id })).ToList();
            var allocCodes = allocs.Count == 0 ? new List<AllocCodeDetailRow>() : (await conn.QueryAsync<AllocCodeDetailRow>(@"
SELECT ac.allocation_id AS allocationId, ac.code_id AS codeId, tc.code_normalized AS codeNormalized
FROM dbo.allocation_code ac JOIN dbo.trace_code tc ON tc.id=ac.code_id WHERE ac.allocation_id IN @ids",
                new { ids = allocs.Select(a => a.allocationId).ToList() })).ToList();
            var task = await conn.QuerySingleOrDefaultAsync<TaskDetailRow>(@"
SELECT t.id AS taskId, t.state, p.bill_code AS billCode
FROM dbo.upload_task t LEFT JOIN dbo.upload_part p ON p.task_id=t.id AND p.part_index=0
WHERE t.org_id=@orgId AND t.env=@env AND t.source_snapshot_id=@id AND t.task_type=N'PURCHASE_IN_102'",
                new { orgId, env, id });
            return Results.Ok(new { data = new {
                snapshot = new { snapshotId = snap.id, docKey = snap.source_document_key, snap.version,
                    businessDatetimeUtc = snap.business_datetime, completeness = snap.completeness_status, contentHash = snap.content_hash },
                lines,
                match = match is null ? null : new { match.matchState, match.version, match.reason },
                allocations = allocs.Select(a => new { a.allocationId, a.sourceLineId, a.upstreamLineId, a.quantity,
                    unitRatio = $"{a.num}/{a.den}", a.state,
                    codes = allocCodes.Where(k => k.allocationId == a.allocationId).Select(k => new { k.codeId, k.codeNormalized }) }),
                task = task is null ? null : new { task.taskId, task.state, task.billCode } }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapPost("/api/v1/inbounds/{id:long}/rematch", (HttpContext ctx, long id, IdempotencyExecutor idem, IJobQueue queue, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.source_snapshot WHERE id=@id AND org_id=@orgId AND document_type=N'PURCHASE_IN'",
                    new { id, orgId }, tx, cancellationToken: ct));
                if (exists == 0) return Err(ctx, 404, "NOT_FOUND", "入库快照不存在");
                var payload = JsonSerializer.Serialize(new { orgId, env, snapshotId = id, trigger = "rematch" });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, "inbound-match", $"rematch:{id}:{Guid.NewGuid():N}", payload, DateTime.UtcNow, ct);
                await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "inbound.rematch",
                    "source_snapshot", id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.JobRematch));

        app.MapPost("/api/v1/inbounds/{id:long}/allocations", (HttpContext ctx, long id, IdempotencyExecutor idem, AllocationService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<AllocationRequest>(ctx.Request.Body, Web, ct);
                if (req is null || req.Lines is null || req.Lines.Count == 0)
                    return Err(ctx, 422, "VALIDATION", "lines 不能为空");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var r = await svc.SaveAsync(conn, tx, orgId, env, id, req, ctx.User.Identity!.Name!, ct);
                return (200, JsonSerializer.Serialize(new { data = new { matchVersion = r.MatchVersion, allocationsSaved = r.AllocationsSaved,
                    allRequiredCovered = r.AllRequiredCovered }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.InboundAllocate));

        app.MapPost("/api/v1/inbounds/{id:long}/confirm-allocation", (HttpContext ctx, long id, IdempotencyExecutor idem, AllocationService svc, InboundTaskFactory factory) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.ExpectedMatchVersion))
                    return Err(ctx, 422, "VALIDATION", "expectedMatchVersion 必填");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var r = await svc.ConfirmAsync(conn, tx, orgId, env, id, req.ExpectedMatchVersion, ctx.User.Identity!.Name!, factory, ct);
                return (200, JsonSerializer.Serialize(new { data = new { taskId = r.TaskId.ToString(), billCode = r.BillCode,
                    matchVersion = r.MatchVersion, reused = r.Reused, message = "符合资格后将自动上传" }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.InboundAllocate));

        app.MapPost("/api/v1/inbounds/{id:long}/cancel-allocation", (HttpContext ctx, long id, IdempotencyExecutor idem, AllocationService svc) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<CancelRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrWhiteSpace(req.Reason))
                    return Err(ctx, 422, "VALIDATION", "reason 必填");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var n = await svc.CancelAsync(conn, tx, orgId, env, id, req.AllocationId, ctx.User.Identity!.Name!, req.Reason, ct);
                return (200, JsonSerializer.Serialize(new { data = new { cancelled = n }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.InboundAllocate));
    }

    private static (int, string) Err(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    public sealed record ConfirmRequest(string ExpectedMatchVersion);
    public sealed record CancelRequest(long? AllocationId, string Reason);

    private sealed class InboundListRow
    {
        public long snapshotId { get; set; }
        public string docKey { get; set; } = "";
        public int version { get; set; }
        public DateTime? businessDatetimeUtc { get; set; }
        public string completeness { get; set; } = "";
        public string? matchState { get; set; }
        public int? matchVersion { get; set; }
        public string? reason { get; set; }
        public string? supplierDwbh { get; set; }
        public long? taskId { get; set; }
        public string? taskState { get; set; }
    }
    private sealed class SnapDetailRow
    {
        public long id { get; set; }
        public string source_document_key { get; set; } = "";
        public int version { get; set; }
        public DateTime? business_datetime { get; set; }
        public string completeness_status { get; set; } = "";
        public string content_hash { get; set; } = "";
    }
    private sealed class LineDetailRow
    {
        public long lineId { get; set; }
        public string sourceLineKey { get; set; } = "";
        public string productSourceId { get; set; } = "";
        public decimal quantity { get; set; }
        public string? unit { get; set; }
        public string? batchRaw { get; set; }
        public string? classification { get; set; }
    }
    private sealed class MatchDetailRow
    {
        public string matchState { get; set; } = "";
        public int version { get; set; }
        public string? reason { get; set; }
    }
    private sealed class AllocDetailRow
    {
        public long allocationId { get; set; }
        public long sourceLineId { get; set; }
        public long upstreamLineId { get; set; }
        public decimal quantity { get; set; }
        public long num { get; set; }
        public long den { get; set; }
        public string state { get; set; } = "";
    }
    private sealed class AllocCodeDetailRow
    {
        public long allocationId { get; set; }
        public long codeId { get; set; }
        public string codeNormalized { get; set; } = "";
    }
    private sealed class TaskDetailRow
    {
        public long taskId { get; set; }
        public string state { get; set; } = "";
        public string? billCode { get; set; }
    }
}
```

Create `traceflow/src/TraceFlow.Host/Endpoints/UpstreamEndpoints.cs`：

```csharp
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

public static class UpstreamEndpoints
{
    public static void MapUpstreamEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/upstream-bills", async (HttpContext ctx, int? page, int? pageSize, string? billNo, string? completeness) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            await using var conn = new SqlConnection(connectionString);
            var orgId = await conn.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1");
            var where = "WHERE org_id=@orgId AND env=@env";
            var args = new DynamicParameters();
            args.Add("orgId", orgId);
            args.Add("env", env);
            if (!string.IsNullOrEmpty(billNo)) { where += " AND bill_no=@billNo"; args.Add("billNo", billNo); }
            if (!string.IsNullOrEmpty(completeness)) { where += " AND completeness=@completeness"; args.Add("completeness", completeness); }
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.upstream_bill {where}", args);
            args.Add("lo", (p - 1) * size + 1);
            args.Add("hi", p * size);
            var rows = await conn.QueryAsync<BillListRow>($@"
SELECT billId,platformBillId,billNo,billDate,upstreamRefEntId,toEntId,completeness,sourceVersion,fetchedAtUtc FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY fetched_at DESC, id DESC) AS rn,
           id AS billId, platform_bill_id AS platformBillId, bill_no AS billNo, bill_date AS billDate,
           upstream_ref_ent_id AS upstreamRefEntId, to_ent_id AS toEntId, completeness, source_version AS sourceVersion, fetched_at AS fetchedAtUtc
    FROM dbo.upstream_bill {where}
) t WHERE rn BETWEEN @lo AND @hi ORDER BY rn", args);
            return Results.Ok(new { data = new { records = rows, total, page = p, pageSize = size }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapGet("/api/v1/upstream-bills/{id:long}", async (HttpContext ctx, long id) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var orgId = await conn.ExecuteScalarAsync<long>("SELECT id FROM dbo.organization WHERE is_active=1");
            var bill = await conn.QuerySingleOrDefaultAsync<BillListRow>(@"
SELECT id AS billId, platform_bill_id AS platformBillId, bill_no AS billNo, bill_date AS billDate,
       upstream_ref_ent_id AS upstreamRefEntId, to_ent_id AS toEntId, completeness, source_version AS sourceVersion, fetched_at AS fetchedAtUtc
FROM dbo.upstream_bill WHERE id=@id AND org_id=@orgId AND env=@env", new { id, orgId, env });
            if (bill is null)
                return Results.Json(new { code = "NOT_FOUND", message = "上游单据不存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 404);
            var lines = (await conn.QueryAsync<LineRow>(@"
SELECT id AS lineId, platform_line_id AS platformLineId, platform_drug_id AS platformDrugId, batch_no AS batchNo, quantity, unit
FROM dbo.upstream_bill_line WHERE bill_id=@id ORDER BY id", new { id })).ToList();
            var codes = lines.Count == 0 ? new List<CodeRow>() : (await conn.QueryAsync<CodeRow>(@"
SELECT line_id AS lineId, code_normalized AS codeNormalized, package_level AS packageLevel
FROM dbo.upstream_bill_code WHERE line_id IN @ids", new { ids = lines.Select(l => l.lineId).ToList() })).ToList();
            return Results.Ok(new { data = new { bill,
                lines = lines.Select(l => new { l.lineId, l.platformLineId, l.platformDrugId, l.batchNo, l.quantity, l.unit,
                    codes = codes.Where(k => k.lineId == l.lineId).Select(k => new { k.codeNormalized, k.packageLevel }) }) },
                requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));

        app.MapPost("/api/v1/upstream-bills/{id:long}/redownload", (HttpContext ctx, long id, IdempotencyExecutor idem, IJobQueue queue, IAuditLog audit) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                if (env != "MOCK")
                    return Err(ctx, 422, "TOP_NOT_CERTIFIED", "LIVE 通道须 G-TOP 取证门禁通过后开启");
                var orgId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM dbo.organization WHERE is_active=1", transaction: tx, cancellationToken: ct));
                var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.upstream_bill WHERE id=@id AND org_id=@orgId AND env=@env",
                    new { id, orgId, env }, tx, cancellationToken: ct));
                if (exists == 0) return Err(ctx, 404, "NOT_FOUND", "上游单据不存在");
                var now = DateTime.UtcNow;
                var payload = JsonSerializer.Serialize(new { orgId, env, start = now.AddDays(-90), end = now, billId = id, trigger = "redownload" });
                var jobId = await queue.EnqueueAsync(conn, tx, orgId, env, "upstream-download", $"redownload:{id}:{Guid.NewGuid():N}", payload, now, ct);
                await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "upstream.redownload",
                    "upstream_bill", id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
                return (202, JsonSerializer.Serialize(new { data = new { jobId = jobId.ToString() }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.JobRematch));
    }

    private static (int, string) Err(HttpContext ctx, int status, string code, string message)
        => (status, JsonSerializer.Serialize(new { code, message, retryable = false, requestId = ctx.TraceIdentifier }));

    private sealed class BillListRow
    {
        public long billId { get; set; }
        public string platformBillId { get; set; } = "";
        public string? billNo { get; set; }
        public DateTime? billDate { get; set; }
        public string upstreamRefEntId { get; set; } = "";
        public string? toEntId { get; set; }
        public string completeness { get; set; } = "";
        public string? sourceVersion { get; set; }
        public DateTime fetchedAtUtc { get; set; }
    }
    private sealed class LineRow
    {
        public long lineId { get; set; }
        public string platformLineId { get; set; } = "";
        public string? platformDrugId { get; set; }
        public string? batchNo { get; set; }
        public decimal? quantity { get; set; }
        public string? unit { get; set; }
    }
    private sealed class CodeRow
    {
        public long lineId { get; set; }
        public string codeNormalized { get; set; } = "";
        public string? packageLevel { get; set; }
    }
}
```

`Program.cs`：Task 8 的 DI 块（InboundMatchJobHandler 注册行）后追加：

```csharp
builder.Services.AddSingleton<TraceFlow.Infrastructure.Inbound.AllocationService>(sp =>
    new TraceFlow.Infrastructure.Inbound.AllocationService(sp.GetRequiredService<IAuditLog>()));
```

`app.MapOpsEndpoints(connectionString, env);` 行后追加：

```csharp
    app.MapInboundEndpoints(connectionString, env);
    app.MapUpstreamEndpoints(connectionString, env);
```

- [ ] **Step 5: 写 API 门禁与功能测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Api/M2ApiGateTests.cs`：

```csharp
using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class M2ApiGateTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public M2ApiGateTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await SeedUserAsync("m2viewer", "只读", "Test-Password-123", new[] { "trace:read" });
        await SeedUserAsync("m2admin", "管理员", "Admin-Password-123",
            new[] { "system:manage", "job:rematch", "inbound:allocate", "trace:read" });
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

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Anonymous_gets_401_on_all_m2_endpoints()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/inbounds")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/inbounds/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/upstream-bills")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/upstream-bills/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/inbounds/1/allocations",
            new { expectedMatchVersion = "1", lines = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/inbounds/1/confirm-allocation",
            new { expectedMatchVersion = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/inbounds/1/cancel-allocation",
            new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/inbounds/1/rematch", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/upstream-bills/1/redownload", new { })).StatusCode);
    }

    [Fact]
    public async Task Viewer_reads_but_cannot_mutate()
    {
        var client = await LoginAsync("m2viewer", "Test-Password-123");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/inbounds")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/upstream-bills")).StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/inbounds/1/allocations",
            new { expectedMatchVersion = "1", lines = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/inbounds/1/rematch", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/upstream-bills/1/redownload", new { })).StatusCode);
    }

    [Fact]
    public async Task Admin_mutations_require_csrf_and_idempotency_key()
    {
        var client = await LoginAsync("m2admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var noCsrf = await client.PostAsJsonAsync("/api/v1/inbounds/1/confirm-allocation", new { expectedMatchVersion = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Contains("CSRF_INVALID", await noCsrf.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session"))!.Data.CsrfToken);
        var noKey = await client.PostAsJsonAsync("/api/v1/inbounds/1/confirm-allocation", new { expectedMatchVersion = "1" });
        Assert.Equal((HttpStatusCode)422, noKey.StatusCode);
    }

    [Fact]
    public async Task Malformed_json_body_returns_400()
    {
        var client = await LoginAsync("m2admin", "Admin-Password-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var resp = await client.PostAsync("/api/v1/inbounds/1/allocations",
            new StringContent("{bad-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("BAD_REQUEST", await resp.Content.ReadAsStringAsync());
    }
}
```

Create `traceflow/tests/TraceFlow.IntegrationTests/Api/InboundApiTests.cs`：

```csharp
using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Codes;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class InboundApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public InboundApiTests(Sql2008Fixture fx) => _fx = fx;

    private sealed record Seed(long OrgId, long SnapId, long LineId, long BillId, long UpLineId, string Code1, string Code2);

    private static string Code(int i) => "812345678901234567" + i.ToString("D2");

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2ops')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2ops',N'入库操作员',@h);
SELECT id FROM dbo.app_user WHERE username=N'm2ops'", new { h = hasher.HashPassword("m2ops", "Ops-Password-123") });
        foreach (var p in new[] { "inbound:allocate", "job:rematch", "trace:read" })
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<Seed> SeedAsync(string tag, int codeBase)
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        var dwbh = "DW" + tag; var spid = "SP" + tag; var drug = "DRUG-" + tag;
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,@dwbh,N'供应商')", new { orgId, sysId, dwbh });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=@dwbh", new { orgId, dwbh });
        var entVerId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-SUP',N'ENT-SUP',N'上游企业')");
        await c.ExecuteAsync("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) VALUES(@orgId,@supId,@ev,N'MANUAL_CONFIRMED')", new { orgId, supId, ev = entVerId });
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药品',N'REQUIRED')", new { orgId, sysId, spid });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) OUTPUT INSERTED.id VALUES(@drug,N'fixture')", new { drug });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,business_datetime,completeness_status,content_hash,raw_header)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,@biz,N'COMPLETE',@hash,@header)",
            new { orgId, sysId, key = "PI-API-" + tag, biz = new DateTime(2026, 11, 20, 2, 0, 0, DateTimeKind.Utc),
                  hash = new string('0', 64), header = "{\"dwbh\":\"" + dwbh + "\"}" });
        var lineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,unit_original,batch_raw,line_hash,event_kind)
OUTPUT INSERTED.id VALUES(@snapId,N'1',@spid,2,N'盒',N'B2026',@hash,N'PURCHASE_IN')", new { snapId, spid, hash = new string('0', 64) });
        var billId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no,bill_date,to_ent_id,completeness)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'REF-SUP',@pb,@no,@date,N'ENT-STORE',N'COMPLETE')",
            new { orgId, pb = "PB-API-" + tag, no = "UP-API-" + tag, date = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc) });
        var upLineId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upstream_bill_line(bill_id,platform_line_id,platform_drug_id,batch_no,quantity,unit)
OUTPUT INSERTED.id VALUES(@billId,N'L1',@drug,N'B2026',2,N'盒')", new { billId, drug });
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var i in new[] { codeBase, codeBase + 1 })
        {
            var stored = await TraceCodeStore.EnsureAsync(c, tx, Code(i), default);
            await c.ExecuteAsync("INSERT INTO dbo.upstream_bill_code(bill_id,line_id,code_normalized,code_raw,package_level) VALUES(@billId,@lineId,@n,@raw,N'MIN')",
                new { billId, lineId = upLineId, n = stored.Normalized, raw = stored.Raw }, tx);
        }
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,@dwbh,N'UNMATCHED')",
            new { orgId, snapId, dwbh }, tx);
        await tx.CommitAsync();
        return new Seed(orgId, snapId, lineId, billId, upLineId, Code(codeBase), Code(codeBase + 1));
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "m2ops", password = "Ops-Password-123" })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private static void NewKey(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
    }

    private static object SaveBody(Seed s, string version) => new
    {
        expectedMatchVersion = version,
        lines = new[] { new { sourceLineId = s.LineId, upstreamLineId = s.UpLineId, quantity = 2m, unitRatio = "1/1", conversionEvidence = (string?)null, codes = new[] { s.Code1, s.Code2 } } },
    };

    [Fact]
    public async Task Save_confirm_and_list_flow()
    {
        var s = await SeedAsync("F1", 10);
        var client = await LoginAsync();
        NewKey(client);
        var save = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saveBody = await save.Content.ReadFromJsonAsync<SaveResp>();
        Assert.Equal(2, saveBody!.Data.MatchVersion);
        Assert.True(saveBody.Data.AllRequiredCovered);
        NewKey(client);
        var confirm = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/confirm-allocation", new { expectedMatchVersion = "2" });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmBody = await confirm.Content.ReadFromJsonAsync<ConfirmResp>();
        Assert.StartsWith("TF", confirmBody!.Data.BillCode);
        Assert.Equal("符合资格后将自动上传", confirmBody.Data.Message);
        var list = await client.GetFromJsonAsync<InboundList>($"/api/v1/inbounds?docKey=PI-API-F1");
        var rec = Assert.Single(list!.Data.Records);
        Assert.Equal("COMPLETE", rec.MatchState);
        Assert.Equal("Ready", rec.TaskState);
        var detail = await client.GetAsync($"/api/v1/inbounds/{s.SnapId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailText = await detail.Content.ReadAsStringAsync();
        Assert.Contains("COMMITTED", detailText);
        Assert.Contains(s.Code1, detailText);
    }

    [Fact]
    public async Task Rematch_enqueues_job()
    {
        var s = await SeedAsync("F2", 20);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/rematch", new { });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JobResp>();
        Assert.False(string.IsNullOrEmpty(body!.Data.JobId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/jobs/{body.Data.JobId}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_allocation_flow()
    {
        var s = await SeedAsync("F3", 30);
        var client = await LoginAsync();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"))).EnsureSuccessStatusCode();
        await using var c = await _fx.OpenAsync();
        var allocId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.inbound_allocation WHERE source_line_id=@id", new { id = s.LineId });
        NewKey(client);
        var cancel = await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/cancel-allocation", new { allocationId = allocId, reason = "录错上游单" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var body = await cancel.Content.ReadFromJsonAsync<CancelResp>();
        Assert.Equal(1, body!.Data.Cancelled);
        Assert.Equal("UNMATCHED", await c.ExecuteScalarAsync<string>("SELECT match_state FROM dbo.inbound_match WHERE source_snapshot_id=@id", new { id = s.SnapId }));
    }

    [Fact]
    public async Task Upstream_bills_list_detail_and_redownload()
    {
        var s = await SeedAsync("F4", 40);
        var client = await LoginAsync();
        var list = await client.GetFromJsonAsync<UpstreamList>($"/api/v1/upstream-bills?billNo=UP-API-F4");
        var rec = Assert.Single(list!.Data.Records);
        Assert.Equal("COMPLETE", rec.Completeness);
        var detail = await client.GetAsync($"/api/v1/upstream-bills/{rec.BillId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains(s.Code1, await detail.Content.ReadAsStringAsync());
        NewKey(client);
        var re = await client.PostAsJsonAsync($"/api/v1/upstream-bills/{rec.BillId}/redownload", new { });
        Assert.Equal(HttpStatusCode.Accepted, re.StatusCode);
    }

    private sealed record SaveResp(SaveData Data);
    private sealed record SaveData(int MatchVersion, int AllocationsSaved, bool AllRequiredCovered);
    private sealed record ConfirmResp(ConfirmData Data);
    private sealed record ConfirmData(string TaskId, string BillCode, int MatchVersion, bool Reused, string Message);
    private sealed record CancelResp(CancelData Data);
    private sealed record CancelData(int Cancelled);
    private sealed record JobResp(JobData Data);
    private sealed record JobData(string JobId);
    private sealed record InboundList(InboundListData Data);
    private sealed record InboundListData(InboundRecord[] Records, int Total);
    private sealed record InboundRecord(long SnapshotId, string DocKey, string? MatchState, string? TaskState);
    private sealed record UpstreamList(UpstreamListData Data);
    private sealed record UpstreamListData(UpstreamRecord[] Records, int Total);
    private sealed record UpstreamRecord(long BillId, string? BillNo, string Completeness);
}
```

注意：后台 Worker 与调度器随 WebApplicationFactory 启动；测试只断言自身操作产生的即时状态，不锁定后台作业状态（与 M1ApiGateTests 同约定）。

- [ ] **Step 6: 运行全部 M2 测试确认通过（GREEN）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~AllocationConfirmTests|FullyQualifiedName~M2ApiGateTests|FullyQualifiedName~InboundApiTests"
```

Expected: PASS（6+4+4=14 个测试）。再跑全量集成+单元+构建确认无回归：

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release && dotnet build traceflow/TraceFlow.sln -c Release
```

- [ ] **Step 7: 提交**

```bash
cd "/e/Viding/Retail Trace Code" && git status --porcelain && git add traceflow/src/TraceFlow.Infrastructure/Inbound/AllocationService.cs traceflow/src/TraceFlow.Host/Endpoints/InboundEndpoints.cs traceflow/src/TraceFlow.Host/Endpoints/UpstreamEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Inbound/AllocationConfirmTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/M2ApiGateTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/InboundApiTests.cs && git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): confirm eligibility recheck + inbound/upstream APIs with gates (AT-030/031/033/034, IN-06/07/08)"
```

---

### Task 11: M1 携带项修复（fail-closed/改绑/分类接线/守卫细化/请求体上限）

**Files:**
- Modify: `traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`（IsChannelPausedAsync）
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs`（trace-policy 接线 + resolve 端点 + disable 守卫）
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（Kestrel 请求体上限）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Settings/SettingsFailClosedTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/M2CarryforwardApiTests.cs`

**Interfaces:**
- Consumes: Task 7 `InboundCancelService.CancelPreSendAsync(conn,tx,orgId,env,snapshotId,actor,reason,auditEnv,ct)`；Task 8 调度意图 `map:{Guid:N}`（job_kind=inbound-match，payload `{orgId,env,snapshotId=null,trigger="map"}`）；`MappingStates`（TraceFlow.Domain.Matching）；`RowVer.ToHex/FromHex`（TraceFlow.Domain.Versioning）；`app_setting(setting_key,value_json,updated_by)`；`inbound_match.supplier_dwbh`（Task 1）。
- Produces（T14 前端消费）:
  - `POST /api/v1/product-mappings/{id}/resolve`（master:match + Idempotency-Key，body `{targetId,expectedVersion,reason}`）→ 200 `{data:{mappingId,state:"MANUAL_CONFIRMED",version,mappingVersion}}`；targetId=platform_drug_id；仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 可改绑。
  - `POST /api/v1/supplier-mappings/{id}/resolve`（同门禁，body 同形）→ 200 同形；targetId=ref_ent_id（取最新 observed_at 企业版本）；supplier.credit_code 与企业版本 credit_code 必须非空且 Ordinal 相等，否则 422 `CREDIT_MISMATCH`。
  - trace-policy 响应 `affectedNonTerminalTasks` 语义升级：非终态（排除 Succeeded/Failed/Cancelled）计数；发送前任务被取消并入队 map: 重匹配；已发送任务不动（AT-057）。

**裁定（T15 汇入）：**
- **R11a fail-closed**：`sync.paused` 值 JSON 解析非 False（含畸形/字符串/缺失值以外的任何形态）一律视为暂停；无设置行→默认 false（Catalog 默认）。
- **R11b 改绑范围**：resolve 仅受理 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED；AUTO_CONFIRMED/MANUAL_CONFIRMED 走 confirm（不允许改绑）、DISABLED 须先重新匹配→422 `STATE_NOT_RESOLVABLE`；有效唯一竞态→409 `MAPPING_ACTIVE_CONFLICT`。
- **R11c 分类接线**：REQUIRED→EXEMPT 等分类变更后，受影响发送前任务（Blocked/Ready 且无 UPLOAD 尝试）逐个 CancelPreSendAsync（auditEnv=LIVE），并按 env 去重入队 `map:{Guid:N}` 重匹配；已发送/其他非终态任务仅计数不动（AT-057）。
- **R11d 供应商解除守卫细化**：由 org 级采购任务计数改为 `inbound_match.supplier_dwbh` 关联计数（排除 Succeeded/Failed/Cancelled）；商品解除守卫同步排除 Cancelled。
- **R11e 请求体上限**：Kestrel MaxRequestBodySize=104_857_600（100 MiB，与 import.maxFileBytes 上界一致）；ImportStage 审计 env=LIVE 经复核为正确（控制面操作），不改。ImportWizard 分页→M4；手动同步 DONE 重入队→G-ERP 取证后处理（T15 记录）。

- [ ] **Step 1: 写失败的测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Settings/SettingsFailClosedTests.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;
using TraceFlow.Infrastructure.Settings;
using Xunit;

namespace TraceFlow.IntegrationTests.Settings;

public class SettingsFailClosedTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public SettingsFailClosedTests(Sql2008Fixture fx) => _fx = fx;

    private async Task SetPausedRawAsync(string valueJson)
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_setting WHERE setting_key=N'sync.paused')
    INSERT INTO dbo.app_setting(setting_key,value_json,updated_by) VALUES(N'sync.paused',@v,N'test');
ELSE UPDATE dbo.app_setting SET value_json=@v WHERE setting_key=N'sync.paused'", new { v = valueJson });
    }

    [Fact]
    public async Task Garbage_pause_value_fails_closed()
    {
        await SetPausedRawAsync("not-json");
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        Assert.True(await svc.IsChannelPausedAsync(default));
        await SetPausedRawAsync("\"true\""); // JSON 字符串而非布尔→保守视为暂停
        Assert.True(await svc.IsChannelPausedAsync(default));
    }

    [Fact]
    public async Task Boolean_false_stays_open()
    {
        await SetPausedRawAsync("false");
        var svc = new SettingsService(_fx.ConnectionString, new DpapiProtector());
        Assert.False(await svc.IsChannelPausedAsync(default));
        await SetPausedRawAsync("true");
        Assert.True(await svc.IsChannelPausedAsync(default));
    }
}
```

Create `traceflow/tests/TraceFlow.IntegrationTests/Api/M2CarryforwardApiTests.cs`：

```csharp
using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using TraceFlow.Domain.Versioning;
using TraceFlow.IntegrationTests.Sources;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class M2CarryforwardApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public M2CarryforwardApiTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2master')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2master',N'主数据员',@h);
SELECT id FROM dbo.app_user WHERE username=N'm2master'", new { h = hasher.HashPassword("m2master", "Master-Password-123") });
        foreach (var p in new[] { "master:match", "master:classify", "trace:read" })
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "m2master", password = "Master-Password-123" })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    private static void NewKey(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
    }

    private async Task<(long OrgId, long SysId)> OrgAsync(SqlConnection c)
    {
        var (orgId, sysId) = await TestSeed.EnsureAsync(c);
        await c.ExecuteAsync("UPDATE dbo.organization SET ent_id=N'ENT-STORE', ref_ent_id=N'REF-STORE' WHERE id=@orgId", new { orgId });
        return (orgId, sysId);
    }

    [Fact]
    public async Task Resolve_product_rebinds_conflict_mapping()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,N'SPR1',N'药',N'REQUIRED')", new { orgId, sysId });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=N'SPR1'", new { orgId });
        await c.ExecuteAsync("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(N'DRUG-R1-A',N'fixture'),(N'DRUG-R1-B',N'fixture')");
        var drugA = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-R1-A'");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@prodId,@drugA,N'CONFLICT')", new { orgId, prodId, drugA });
        var ver = RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/product-mappings/{mapId}/resolve",
            new { targetId = "DRUG-R1-B", expectedVersion = ver, reason = "人工核对批准文号后改绑" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("MANUAL_CONFIRMED", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        Assert.Equal("DRUG-R1-B", await c.ExecuteScalarAsync<string>(@"
SELECT pd.platform_drug_id FROM dbo.product_mapping pm JOIN dbo.platform_drug pd ON pd.id=pm.platform_drug_id WHERE pm.id=@id", new { id = mapId }));
        Assert.True(await c.ExecuteScalarAsync<bool>("SELECT is_locked FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
    }

    [Fact]
    public async Task Resolve_supplier_credit_mismatch_422()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name,credit_code) VALUES(@orgId,@sysId,N'DWR2',N'供应商',N'CREDIT-1')", new { orgId, sysId });
        var supId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=N'DWR2'", new { orgId });
        var evId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name,credit_code) OUTPUT INSERTED.id
VALUES(N'REF-R2',N'ENT-R2',N'企业',N'CREDIT-2')");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@supId,@evId,N'REVIEW_REQUIRED')", new { orgId, supId, evId });
        var ver = RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = mapId }));
        var client = await LoginAsync();
        NewKey(client);
        var bad = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{mapId}/resolve",
            new { targetId = "REF-R2", expectedVersion = ver, reason = "改绑" });
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Contains("CREDIT_MISMATCH", await bad.Content.ReadAsStringAsync());
        await c.ExecuteAsync("UPDATE dbo.platform_enterprise_version SET credit_code=N'CREDIT-1' WHERE id=@id", new { id = evId });
        NewKey(client);
        var ok = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{mapId}/resolve",
            new { targetId = "REF-R2", expectedVersion = ver, reason = "信用代码一致，改绑" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Resolve_active_mapping_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,N'SPR3',N'药',N'REQUIRED')", new { orgId, sysId });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=N'SPR3'", new { orgId });
        await c.ExecuteAsync("INSERT INTO dbo.platform_drug(platform_drug_id,source_kind) VALUES(N'DRUG-R3',N'fixture')");
        var drugId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.platform_drug WHERE platform_drug_id=N'DRUG-R3'");
        var mapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) OUTPUT INSERTED.id
VALUES(@orgId,@prodId,@drugId,N'MANUAL_CONFIRMED')", new { orgId, prodId, drugId });
        var ver = RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product_mapping WHERE id=@id", new { id = mapId }));
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/product-mappings/{mapId}/resolve",
            new { targetId = "DRUG-R3", expectedVersion = ver, reason = "x" });
        Assert.Equal((HttpStatusCode)422, resp.StatusCode);
        Assert.Contains("STATE_NOT_RESOLVABLE", await resp.Content.ReadAsStringAsync());
    }

    private async Task<(long ProdId, long SnapId, long TaskId, string ProdVer)> SeedTaskAsync(SqlConnection c, string tag, bool sent)
    {
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product(org_id,source_system_id,spid,name,trace_classification) VALUES(@orgId,@sysId,@spid,N'药',N'REQUIRED')", new { orgId, sysId, spid = "SPT-" + tag });
        var prodId = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.product WHERE org_id=@orgId AND spid=@spid", new { orgId, spid = "SPT-" + tag });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,@key,N'PURCHASE_IN',1,N'COMPLETE',@hash)",
            new { orgId, sysId, key = "PI-T-" + tag, hash = new string('0', 64) });
        await c.ExecuteAsync(@"
INSERT INTO dbo.source_line(snapshot_id,source_line_key,product_source_id,quantity_original,line_hash,event_kind)
VALUES(@snapId,N'1',@spid,1,@hash,N'PURCHASE_IN')", new { snapId, spid = "SPT-" + tag, hash = new string('0', 64) });
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',@key,N'PURCHASE_IN_102',@snapId,@hash,@hash,N'Ready')",
            new { orgId, key = $"KSOA:PI-T-{tag}:PURCHASE_IN", snapId, hash = new string('0', 64) });
        await c.ExecuteAsync(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
VALUES(@taskId,@orgId,N'MOCK',0,@bill,N'{}',@hash,@hash,N'Ready')",
            new { taskId, orgId, bill = "TF" + tag.PadRight(10, '0')[..10], hash = new string('0', 64) });
        if (sent)
            await c.ExecuteAsync("INSERT INTO dbo.upload_attempt(task_id,attempt_no,attempt_kind,env,started_at) VALUES(@taskId,1,N'UPLOAD',N'MOCK',SYSUTCDATETIME())", new { taskId });
        var prodVer = RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.product WHERE id=@id", new { id = prodId }));
        return (prodId, snapId, taskId, prodVer);
    }

    [Fact]
    public async Task Trace_policy_cancels_presend_task_and_enqueues_rematch()
    {
        await using var c = await _fx.OpenAsync();
        var (prodId, snapId, taskId, prodVer) = await SeedTaskAsync(c, "W1", sent: false);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/products/{prodId}/trace-policy",
            new { classification = "EXEMPT", reason = "确认豁免", expectedVersion = prodVer });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"affectedNonTerminalTasks\":1", await resp.Content.ReadAsStringAsync());
        Assert.Equal("Cancelled", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'inbound-match' AND intent_key LIKE N'map:%'"));
    }

    [Fact]
    public async Task Trace_policy_sent_task_untouched()
    {
        await using var c = await _fx.OpenAsync();
        var (prodId, snapId, taskId, prodVer) = await SeedTaskAsync(c, "W2", sent: true);
        var client = await LoginAsync();
        NewKey(client);
        var resp = await client.PostAsJsonAsync($"/api/v1/products/{prodId}/trace-policy",
            new { classification = "EXEMPT", reason = "确认豁免", expectedVersion = prodVer });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Ready", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_task WHERE id=@id", new { id = taskId }));
    }

    [Fact]
    public async Task Supplier_disable_guard_is_dwbh_scoped()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId) = await OrgAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.supplier(org_id,source_system_id,dwbh,name) VALUES(@orgId,@sysId,N'DWG1',N'甲'),(@orgId,@sysId,N'DWG2',N'乙')", new { orgId, sysId });
        var sup1 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=N'DWG1'", new { orgId });
        var sup2 = await c.ExecuteScalarAsync<long>("SELECT id FROM dbo.supplier WHERE org_id=@orgId AND dwbh=N'DWG2'", new { orgId });
        var evId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-G',N'ENT-G',N'企业')");
        var map1 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT INSERTED.id VALUES(@orgId,@sup1,@evId,N'MANUAL_CONFIRMED')", new { orgId, sup1, evId });
        var evId2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_enterprise_version(ref_ent_id,ent_id,name) OUTPUT INSERTED.id VALUES(N'REF-G2',N'ENT-G2',N'企业2')");
        var map2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.supplier_mapping(org_id,supplier_id,enterprise_version_id,state) OUTPUT INSERTED.id VALUES(@orgId,@sup2,@evId2,N'MANUAL_CONFIRMED')", new { orgId, sup2, evId = evId2 });
        var snapId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,completeness_status,content_hash)
OUTPUT INSERTED.id VALUES(@orgId,@sysId,N'PI-G1',N'PURCHASE_IN',1,N'COMPLETE',@hash)", new { orgId, sysId, hash = new string('0', 64) });
        await c.ExecuteAsync("INSERT INTO dbo.inbound_match(org_id,env,source_snapshot_id,supplier_dwbh,match_state) VALUES(@orgId,N'MOCK',@snapId,N'DWG1',N'COMPLETE')", new { orgId, snapId });
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_snapshot_id,source_hash,payload_hash,state)
OUTPUT INSERTED.id VALUES(@orgId,N'MOCK',N'KSOA:PI-G1:PURCHASE_IN',N'PURCHASE_IN_102',@snapId,@hash,@hash,N'Ready')",
            new { orgId, snapId, hash = new string('0', 64) });
        var ver1 = RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = map1 }));
        var ver2 = RowVer.ToHex(await c.ExecuteScalarAsync<byte[]>("SELECT ver FROM dbo.supplier_mapping WHERE id=@id", new { id = map2 }));
        var client = await LoginAsync();
        NewKey(client);
        var blocked = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{map1}/disable", new { expectedVersion = ver1, reason = "解除" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("TASKS_IN_FLIGHT", await blocked.Content.ReadAsStringAsync());
        NewKey(client);
        var ok = await client.PostAsJsonAsync($"/api/v1/supplier-mappings/{map2}/disable", new { expectedVersion = ver2, reason = "无关供应商解除" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
```

- [ ] **Step 2: 运行测试确认失败（RED）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SettingsFailClosedTests|FullyQualifiedName~M2CarryforwardApiTests"
```

Expected: FAIL（fail-closed 断言失败；resolve 端点 404；分类接线/守卫断言失败）。

- [ ] **Step 3: 实现修复**

`SettingsService.cs`：替换 `IsChannelPausedAsync` 方法体：

```csharp
    public async Task<bool> IsChannelPausedAsync(CancellationToken ct)
    {
        var raw = await GetRawAsync("sync.paused", ct);
        if (raw is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind != JsonValueKind.False; // R11a：非布尔 false 一律视为暂停（fail-closed）
        }
        catch (JsonException)
        {
            return true;
        }
    }
```

`Program.cs`：`builder.Host.UseWindowsService();` 行后追加：

```csharp
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 104_857_600); // 100 MiB，与 import.maxFileBytes 上界一致（R11e）
```

`MasterEndpoints.cs`：文件头 using 区追加 `using TraceFlow.Infrastructure.Inbound;`。

trace-policy 端点：lambda 参数 `(HttpContext ctx, long id, IdempotencyExecutor idem)` 改为 `(HttpContext ctx, long id, IdempotencyExecutor idem, IJobQueue queue)`；端点上方注释替换为：

```csharp
        // MD-01 分类专用端点：reason 必填；UPDATE 只动分类两列。
        // M2 接线（R11c/AT-057）：受影响发送前任务取消并入队 map: 重匹配；已发送任务仅计数不动。
```

product 查询 `WHERE id=@id` 改为 `WHERE id=@id AND org_id=(SELECT id FROM dbo.organization WHERE is_active=1)`。

将 `// 经快照→来源行 spid 关联的非终态任务数…` 起至 `var newVer = …` 行之前的 affected 计数块整体替换为：

```csharp
                var affectedTasks = (await conn.QueryAsync<AffectedTaskRow>(new CommandDefinition(@"
SELECT DISTINCT t.id, t.env, t.state, t.source_snapshot_id
FROM dbo.upload_task t
JOIN dbo.source_snapshot ss ON ss.id=t.source_snapshot_id
JOIN dbo.source_line sl ON sl.snapshot_id=ss.id
WHERE ss.org_id=@org AND ss.source_system_id=@sys AND sl.product_source_id=@spid
  AND t.state NOT IN (N'Succeeded',N'Failed',N'Cancelled')",
                    new { org = prod.org_id, sys = prod.source_system_id, spid = prod.spid }, tx, cancellationToken: ct))).ToList();
                var affected = affectedTasks.Count;
                var rematchedEnvs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in affectedTasks)
                {
                    var sent = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND attempt_kind=N'UPLOAD'",
                        new { taskId = t.id }, tx, cancellationToken: ct));
                    if (sent > 0 || (t.state != "Blocked" && t.state != "Ready")) continue; // AT-057：已发送任务冻结不动
                    await InboundCancelService.CancelPreSendAsync(conn, tx, prod.org_id, t.env, t.source_snapshot_id,
                        ctx.User.Identity!.Name!, "商品分类变更失效发送前任务", "LIVE", ct);
                    rematchedEnvs.Add(t.env);
                }
                foreach (var e in rematchedEnvs)
                {
                    var payload = JsonSerializer.Serialize(new { orgId = prod.org_id, env = e, snapshotId = (long?)null, trigger = "map" });
                    await queue.EnqueueAsync(conn, tx, prod.org_id, e, "inbound-match", $"map:{Guid.NewGuid():N}", payload, DateTime.UtcNow, ct);
                }
```

在两个 confirm 端点之后、disable 端点之前插入 resolve 端点：

```csharp
        // M2 改绑（R11b）：仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 可 resolve；
        // 与 confirm 不同——允许更换平台目标；有效唯一竞态→409 MAPPING_ACTIVE_CONFLICT。
        app.MapPost("/api/v1/product-mappings/{id:long}/resolve", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
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
                if (map.state is not (MappingStates.Conflict or MappingStates.ReviewRequired or MappingStates.Candidate or MappingStates.Unresolved))
                    return (422, Err(ctx, "STATE_NOT_RESOLVABLE", "仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 可改绑"));
                var target = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM dbo.platform_drug WHERE platform_drug_id=@key", new { key = req.TargetId }, tx, cancellationToken: ct));
                if (target is null) return (422, Err(ctx, "VALIDATION", "目标平台药不存在"));
                var actor = ctx.User.Identity!.Name!;
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.product_mapping SET platform_drug_id=@target, state=N'MANUAL_CONFIRMED', is_locked=1, confirmed_by=@by, confirmed_at=SYSUTCDATETIME(), version=version+1
WHERE id=@id", new { id, target, by = actor }, tx, cancellationToken: ct));
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
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.resolve", "product_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state, targetId = map.drug_key }),
                    JsonSerializer.Serialize(new { state = "MANUAL_CONFIRMED", targetId = req.TargetId }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "MANUAL_CONFIRMED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));

        app.MapPost("/api/v1/supplier-mappings/{id:long}/resolve", (HttpContext ctx, long id, IdempotencyExecutor idem) =>
            idem.RunAsync(ctx, async (conn, tx, ct) =>
            {
                ctx.Request.Body.Position = 0;
                var req = await JsonSerializer.DeserializeAsync<ConfirmMappingRequest>(ctx.Request.Body, Web, ct);
                if (req is null || string.IsNullOrEmpty(req.TargetId) || string.IsNullOrEmpty(req.ExpectedVersion)
                    || string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
                    return (422, Err(ctx, "VALIDATION", "targetId/expectedVersion 必填，reason 必填且不超过 500 字"));
                var map = await conn.QuerySingleOrDefaultAsync<SMapResolve>(new CommandDefinition(@"
SELECT sm.id, sm.state, sm.version, sm.ver, s.credit_code AS sup_credit
FROM dbo.supplier_mapping sm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.supplier s ON s.id=sm.supplier_id
WHERE sm.id=@id", new { id }, tx, cancellationToken: ct));
                if (map is null) return (404, Err(ctx, "NOT_FOUND", "映射不存在"));
                if (!map.ver.SequenceEqual(RowVer.FromHex(req.ExpectedVersion)))
                    throw new SettingVersionException("映射版本冲突，请刷新后重试");
                if (map.state is not (MappingStates.Conflict or MappingStates.ReviewRequired or MappingStates.Candidate or MappingStates.Unresolved))
                    return (422, Err(ctx, "STATE_NOT_RESOLVABLE", "仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 可改绑"));
                var ev = await conn.QuerySingleOrDefaultAsync<EvResolve>(new CommandDefinition(
                    "SELECT TOP 1 id, credit_code FROM dbo.platform_enterprise_version WHERE ref_ent_id=@ref ORDER BY observed_at DESC, id DESC",
                    new { @ref = req.TargetId }, tx, cancellationToken: ct));
                if (ev is null) return (422, Err(ctx, "VALIDATION", "目标企业不存在"));
                if (string.IsNullOrEmpty(map.sup_credit) || string.IsNullOrEmpty(ev.credit_code)
                    || !string.Equals(map.sup_credit, ev.credit_code, StringComparison.Ordinal))
                    return (422, Err(ctx, "CREDIT_MISMATCH", "统一社会信用代码不一致，禁止改绑"));
                var actor = ctx.User.Identity!.Name!;
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(@"
UPDATE dbo.supplier_mapping SET enterprise_version_id=@ev, state=N'MANUAL_CONFIRMED', is_locked=1, version=version+1
WHERE id=@id", new { id, ev = ev.id }, tx, cancellationToken: ct));
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
                await new AuditLog().AppendAsync(conn, tx, new AuditEntry(actor, "User", "master.mapping.resolve", "supplier_mapping",
                    id.ToString(), req.Reason, JsonSerializer.Serialize(new { state = map.state }),
                    JsonSerializer.Serialize(new { state = "MANUAL_CONFIRMED", targetId = req.TargetId }), null, null, "LIVE"));
                return (200, JsonSerializer.Serialize(new { data = new { mappingId = id.ToString(), state = "MANUAL_CONFIRMED", version = newVer, mappingVersion = newVersion }, requestId = ctx.TraceIdentifier }));
            })).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.MasterMatch));
```

商品 disable 守卫查询 `t.state NOT IN (N'Succeeded',N'Failed')` 改为 `t.state NOT IN (N'Succeeded',N'Failed',N'Cancelled')`。

供应商 disable：`SMapDisable` 查询替换为：

```csharp
                var map = await conn.QuerySingleOrDefaultAsync<SMapDisable>(new CommandDefinition(@"
SELECT sm.id, sm.org_id, sm.state, sm.ver, s.dwbh
FROM dbo.supplier_mapping sm WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.supplier s ON s.id=sm.supplier_id
WHERE sm.id=@id", new { id }, tx, cancellationToken: ct));
```

其注释与 inflight 块替换为：

```csharp
                // R11d：守卫细化到 dwbh 级——仅该供应商关联的非终态采购任务阻断解除
                var inflight = await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT COUNT(*) FROM dbo.upload_task t
JOIN dbo.inbound_match m ON m.source_snapshot_id=t.source_snapshot_id AND m.org_id=t.org_id AND m.env=t.env
WHERE t.org_id=@org AND t.task_type IN (N'PURCHASE_IN_102',N'PURCHASE_RETURN_202')
  AND m.supplier_dwbh=@dwbh AND t.state NOT IN (N'Succeeded',N'Failed',N'Cancelled')",
                    new { org = map.org_id, dwbh = map.dwbh }, tx, cancellationToken: ct));
```

记录区：`SMapDisable` 替换为 `public sealed record SMapDisable(long id, long org_id, string state, byte[] ver, string dwbh);`，并追加：

```csharp
    public sealed record SMapResolve(long id, string state, int version, byte[] ver, string? sup_credit);
    public sealed record EvResolve(long id, string? credit_code);
    private sealed record AffectedTaskRow(long id, string env, string state, long source_snapshot_id);
```

- [ ] **Step 4: 运行测试确认通过（GREEN）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SettingsFailClosedTests|FullyQualifiedName~M2CarryforwardApiTests"
```

Expected: PASS（2+6=8 个测试）。全量回归：

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
```

注意：M1 的 SettingsApiTests/ApiGateTests 若断言旧 fail-open 行为或供应商 org 级守卫，按新裁定同步更新断言（仅改断言，不改被测语义）。

- [ ] **Step 5: 提交**

```bash
cd "/e/Viding/Retail Trace Code" && git status --porcelain && git add traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs traceflow/src/TraceFlow.Host/Endpoints/MasterEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Settings/SettingsFailClosedTests.cs traceflow/tests/TraceFlow.IntegrationTests/Api/M2CarryforwardApiTests.cs && git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "fix(m2): fail-closed pause, mapping resolve, classification task wiring, dwbh-scoped guard, body limit (AT-015/057)"
```

---

### Task 12: §4.7 设置视图（保护作用域/运行账号）+ 限流预算用量端点

**Files:**
- Modify: `traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs`（SettingView/Row/ListAsync/读回）
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/SettingsEndpoints.cs`（两个新 GET）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/SettingsScopeApiTests.cs`

**Interfaces:**
- Consumes: `app_setting.protection_scope`（0001，secret 写入时="CurrentUser"）；Task 1 `dbo.top_rate_budget`；Task 3 `ChannelPauseService.ListAsync(conn,env,ct)→IReadOnlyList<PauseView>`（DI 已注册，T4）；`PermissionCatalog.SystemManage`。
- Produces（T14 前端消费）:
  - `SettingView` 追加末位属性 `string? ProtectionScope`（JSON `protectionScope`；非 secret=null）。
  - `GET /api/v1/settings/service-account`（system:manage）→ `{data:{serviceAccount,machineName}}`；serviceAccount=`Environment.UserDomainName + "\\" + Environment.UserName`（进程身份，Windows 服务即运行账号；不引入新包）。
  - `GET /api/v1/top-budgets`（system:manage）→ `{data:{budgets:[{budgetKey,dayCount,dayLimit,minuteCount,minuteLimit,secondCount,secondLimit,nextAllowedAtUtc}],pauses:[{id,scopeKey,reason,isActive,pausedAtUtc,pausedBy,resumedAtUtc,resumedBy}]}}`（env 取宿主 EnvOptions）。

**裁定：** 限值列 NULL=本地不设帽（G-TOP 未取证不虚构限值），前端显示"—"；用量只读，改期/暂停恢复走既有 ops 端点（M3）。

- [ ] **Step 1: 写失败的测试**

Create `traceflow/tests/TraceFlow.IntegrationTests/Api/SettingsScopeApiTests.cs`：

```csharp
using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace TraceFlow.IntegrationTests.Api;

[SupportedOSPlatform("windows")]
public class SettingsScopeApiTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public SettingsScopeApiTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TraceFlow", _fx.ConnectionString);
            b.UseSetting("TraceFlow:Env", "Mock");
        });
        await using var c = await _fx.OpenAsync();
        var hasher = new PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2scope')
    INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2scope',N'管理员',@h);
SELECT id FROM dbo.app_user WHERE username=N'm2scope'", new { h = hasher.HashPassword("m2scope", "Scope-Password-123") });
        foreach (var p in new[] { "system:manage", "trace:read" })
            await c.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@id AND permission=@p)
    INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'test')", new { id, p });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "m2scope", password = "Scope-Password-123" })).EnsureSuccessStatusCode();
        var session2 = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session2!.Data.CsrfToken);
        return client;
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Secret_setting_exposes_protection_scope()
    {
        var client = await LoginAsync();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var put = await client.PutAsJsonAsync("/api/v1/settings/top.appsecret",
            new { valueJson = (string?)null, secretValue = "test-secret-value", expectedVersion = "0", reason = "录入测试凭证" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Contains("\"protectionScope\":\"CurrentUser\"", await put.Content.ReadAsStringAsync());
        var list = await client.GetAsync("/api/v1/settings");
        var text = await list.Content.ReadAsStringAsync();
        Assert.Contains("\"protectionScope\":\"CurrentUser\"", text);
        Assert.DoesNotContain("test-secret-value", text); // 密文不回显
    }

    [Fact]
    public async Task Service_account_endpoint_returns_process_identity()
    {
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/v1/settings/service-account");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<AccountResp>();
        Assert.False(string.IsNullOrEmpty(body!.Data.ServiceAccount));
        Assert.Equal(Environment.MachineName, body.Data.MachineName);
    }

    [Fact]
    public async Task Top_budgets_endpoint_lists_usage_and_pauses()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"
INSERT INTO dbo.top_rate_budget(env,budget_key,day_count,minute_count,second_count)
VALUES(N'MOCK',N'appkey:total',3,1,0)");
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/v1/top-budgets");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BudgetResp>();
        Assert.Contains(body!.Data.Budgets, b => b.BudgetKey == "appkey:total" && b.DayCount == 3);
        Assert.Null(body.Data.Budgets.Single(b => b.BudgetKey == "appkey:total").DayLimit); // NULL=不设帽
        Assert.NotNull(body.Data.Pauses);
    }

    private sealed record AccountResp(AccountData Data);
    private sealed record AccountData(string ServiceAccount, string MachineName);
    private sealed record BudgetResp(BudgetData Data);
    private sealed record BudgetData(BudgetRow[] Budgets, PauseRow[] Pauses);
    private sealed record BudgetRow(string BudgetKey, int DayCount, int? DayLimit, int MinuteCount, int? MinuteLimit, int SecondCount, int? SecondLimit, DateTime? NextAllowedAtUtc);
    private sealed record PauseRow(long Id, string ScopeKey, bool IsActive);
}
```

- [ ] **Step 2: 运行测试确认失败（RED）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SettingsScopeApiTests"
```

Expected: FAIL（protectionScope 字段缺失；两个新端点 404）。

- [ ] **Step 3: 实现**

`SettingsService.cs`：

SettingView 记录替换为：

```csharp
public sealed record SettingView(long Id, string Key, string? ValueJson, bool IsSecret, bool HasSecretValue,
    string Version, string? UpdatedBy, DateTime? UpdatedAtUtc, string? Reason, string? ProtectionScope);
```

Row 类追加属性 `public string? protection_scope { get; set; }`。

ListAsync 与 UpdateAsync 读回的 SELECT 列均追加 `,protection_scope`（两处 SQL 字符串）。

三处 SettingView 构造调用末位追加参数：ListAsync 有行分支 `r.protection_scope`、缺行分支 `null`、UpdateAsync 读回 `row.protection_scope`。

`SettingsEndpoints.cs`：using 区追加 `using Dapper;`、`using Microsoft.Data.SqlClient;`、`using TraceFlow.Infrastructure.Top;`。MapSettingsEndpoints 签名追加参数 `string env`（Program.cs 调用处同步改为 `app.MapSettingsEndpoints(connectionString, env);`）。connections/test 端点后追加：

```csharp
        // §4.7：显示服务运行账号（进程身份）与机器名；换机/恢复备份须重新录入凭证（protectionScope 绑定 CurrentUser）
        app.MapGet("/api/v1/settings/service-account", (HttpContext ctx) =>
            Results.Json(new { data = new { serviceAccount = Environment.UserDomainName + "\\" + Environment.UserName,
                machineName = Environment.MachineName }, requestId = ctx.TraceIdentifier }, Web))
            .WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        // §4.7：限流预算用量与授权暂停只读视图（限值 NULL=本地不设帽，G-TOP 未取证不虚构）
        app.MapGet("/api/v1/top-budgets", async (HttpContext ctx, ChannelPauseService pauses) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var budgets = await conn.QueryAsync<BudgetRow>(
                "SELECT budget_key AS budgetKey, day_count AS dayCount, day_limit AS dayLimit, minute_count AS minuteCount, minute_limit AS minuteLimit, second_count AS secondCount, second_limit AS secondLimit, next_allowed_at AS nextAllowedAtUtc FROM dbo.top_rate_budget WHERE env=@env ORDER BY budget_key",
                new { env });
            var pauseList = await pauses.ListAsync(conn, env, ctx.RequestAborted);
            return Results.Json(new { data = new { budgets, pauses = pauseList }, requestId = ctx.TraceIdentifier }, Web);
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
```

文件末尾（类内）追加：

```csharp
    private sealed class BudgetRow
    {
        public string budgetKey { get; set; } = "";
        public int dayCount { get; set; }
        public int? dayLimit { get; set; }
        public int minuteCount { get; set; }
        public int? minuteLimit { get; set; }
        public int secondCount { get; set; }
        public int? secondLimit { get; set; }
        public DateTime? nextAllowedAtUtc { get; set; }
    }
```

注意：`Results.Json(..., Web)` 对匿名对象 camelCase 化；BudgetRow 属性已小写开头，经 Web 序列化保持原名。PauseView 记录属性经 Web camelCase 输出（id/scopeKey/isActive…）。

- [ ] **Step 4: 运行测试确认通过（GREEN）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~SettingsScopeApiTests" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release && dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
```

Expected: PASS（3 个新测试）+ 全量无回归（SettingView 追加属性为加性变更，M1 断言不受影响）。

- [ ] **Step 5: 提交**

```bash
cd "/e/Viding/Retail Trace Code" && git status --porcelain && git add traceflow/src/TraceFlow.Infrastructure/Settings/SettingsService.cs traceflow/src/TraceFlow.Host/Endpoints/SettingsEndpoints.cs traceflow/src/TraceFlow.Host/Program.cs traceflow/tests/TraceFlow.IntegrationTests/Api/SettingsScopeApiTests.cs && git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): settings protection scope, service account and rate budget usage views (spec 4.7)"
```

---

### Task 13: 前端上游单/入库关联页（UI-04/05/11）+ 已配/剩余量视图

**Files:**
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/UpstreamEndpoints.cs`（详情行加 allocatedQuantity/remainingQuantity）
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/InboundEndpoints.cs`（详情行加 allocatedQuantity）
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/Api/InboundApiTests.cs`（+2 测试）
- Modify: `traceflow/src/TraceFlow.Web/src/api.ts`（ApiError 携带 fieldErrors）
- Create: `traceflow/src/TraceFlow.Web/src/m2types.ts`
- Create: `traceflow/src/TraceFlow.Web/src/pages/UpstreamBillsPage.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/pages/InboundsPage.tsx`
- Modify: `traceflow/src/TraceFlow.Web/src/App.tsx`（两条路由）
- Modify: `traceflow/src/TraceFlow.Web/src/components/AppShell.tsx`（导航 ready）

**Interfaces:**
- Consumes: Task 10 全部入库/上游端点（形状见 Task 10 代码）；Task 9 `AllocationRequest`（expectedMatchVersion 为 int 字符串）；`GET /api/v1/jobs/{id}`（M1）；`Page<T>`（m1types）。
- Produces（T14 复用）: `m2types.ts` 全部类型（含 `ServiceAccount`/`TopBudgetView`/`ChannelPause`，T14 设置页消费）；`ApiError.fieldErrors?: { sourceLineId?: number; code?: string; message?: string }[]`（加性第 5 构造参数，M1 调用不受影响）。

**裁定：**
- R13a：UI-04"已配/剩余量"与 UI-05"应追溯量/已配量/差异"由**后端 SQL 聚合**（RESERVED+COMMITTED 分配求和），前端只做展示与 round6 差异高亮，不做 decimal 运算判定资格——资格判定永远在后端（AT-031）。
- R13b：上游列表**不提供"视为已入库"按钮**（UI-04 明文禁止）；入库页**无常态手工上传按钮**（UI-05），确认补配后果提示固定文案"符合资格后将自动上传"。
- R13c：冻结判定前端仅用于禁用编辑——`task` 存在且 `state ∉ {Blocked, Ready, Cancelled}` → 只读横幅；后端 Save/Confirm/Cancel 仍各自强制校验（前端判定不是安全边界）。
- R13d：409（VERSION_CONFLICT/FROZEN 等）统一提示"版本冲突或已冻结：数据可能已被他人修改，请刷新后重试"并自动重载详情（AT-067 不静默覆盖）；422 ALLOCATION_INVALID 的 fieldErrors 逐行展示（UI-11）。
- R13e：rematch/redownload 202 后仅显示 jobId 与"后台处理，稍后刷新"提示，不做页面轮询触发（AT-073 语义：Worker 独立处理）。

#### Part A：后端已配/剩余量视图

- [ ] **Step 1: 写失败的测试**

编辑 `traceflow/tests/TraceFlow.IntegrationTests/Api/InboundApiTests.cs`：在 `private sealed record SaveResp(SaveData Data);` 行之前插入：

```csharp
    [Fact]
    public async Task Upstream_detail_shows_allocated_and_remaining()
    {
        var s = await SeedAsync("F5", 50);
        var client = await LoginAsync();
        var detail = await client.GetAsync($"/api/v1/upstream-bills/{s.BillId}");
        using (var doc = System.Text.Json.JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            var line = doc.RootElement.GetProperty("data").GetProperty("lines")[0];
            Assert.Equal(0m, line.GetProperty("allocatedQuantity").GetDecimal());
            Assert.Equal(2m, line.GetProperty("remainingQuantity").GetDecimal());
        }
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", new
        {
            expectedMatchVersion = "1",
            lines = new[] { new { sourceLineId = s.LineId, upstreamLineId = s.UpLineId, quantity = 1m, unitRatio = "1/1", conversionEvidence = (string?)null, codes = new[] { s.Code1 } } },
        })).EnsureSuccessStatusCode();
        detail = await client.GetAsync($"/api/v1/upstream-bills/{s.BillId}");
        using (var doc = System.Text.Json.JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            var line = doc.RootElement.GetProperty("data").GetProperty("lines")[0];
            Assert.Equal(1m, line.GetProperty("allocatedQuantity").GetDecimal());
            Assert.Equal(1m, line.GetProperty("remainingQuantity").GetDecimal());
        }
    }

    [Fact]
    public async Task Inbound_detail_line_shows_allocated_quantity()
    {
        var s = await SeedAsync("F6", 60);
        var client = await LoginAsync();
        NewKey(client);
        (await client.PostAsJsonAsync($"/api/v1/inbounds/{s.SnapId}/allocations", SaveBody(s, "1"))).EnsureSuccessStatusCode();
        var detail = await client.GetAsync($"/api/v1/inbounds/{s.SnapId}");
        using var doc = System.Text.Json.JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var line = doc.RootElement.GetProperty("data").GetProperty("lines")[0];
        Assert.Equal(2m, line.GetProperty("allocatedQuantity").GetDecimal());
    }
```

- [ ] **Step 2: 运行测试确认失败（RED）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~InboundApiTests"
```

Expected: 新增 2 个 FAIL（allocatedQuantity 属性不存在，GetProperty 抛 KeyNotFoundException）；原 4 个 PASS。

- [ ] **Step 3: 实现**

`UpstreamEndpoints.cs` 详情行查询替换为（加 org/env 参数与两个聚合列）：

```csharp
            var lines = (await conn.QueryAsync<LineRow>(@"
SELECT l.id AS lineId, l.platform_line_id AS platformLineId, l.platform_drug_id AS platformDrugId, l.batch_no AS batchNo, l.quantity, l.unit,
       ISNULL((SELECT SUM(a.quantity) FROM dbo.inbound_allocation a
               WHERE a.org_id=@orgId AND a.env=@env AND a.upstream_line_id=l.id AND a.state IN (N'RESERVED',N'COMMITTED')),0) AS allocatedQuantity,
       CASE WHEN l.quantity IS NULL THEN NULL ELSE l.quantity - ISNULL((SELECT SUM(a.quantity) FROM dbo.inbound_allocation a
               WHERE a.org_id=@orgId AND a.env=@env AND a.upstream_line_id=l.id AND a.state IN (N'RESERVED',N'COMMITTED')),0) END AS remainingQuantity
FROM dbo.upstream_bill_line l WHERE l.bill_id=@id ORDER BY l.id", new { id, orgId, env })).ToList();
```

LineRow 类追加：

```csharp
        public decimal allocatedQuantity { get; set; }
        public decimal? remainingQuantity { get; set; }
```

详情匿名投影 `lines = lines.Select(l => new { l.lineId, l.platformLineId, l.platformDrugId, l.batchNo, l.quantity, l.unit,` 行追加 `l.allocatedQuantity, l.remainingQuantity,`（codes 子投影不变）。

`InboundEndpoints.cs` 详情行查询的 SELECT 列 `p.trace_classification AS classification` 后追加：

```sql
,
       ISNULL((SELECT SUM(a.quantity) FROM dbo.inbound_allocation a
               WHERE a.org_id=@orgId AND a.env=@env AND a.source_line_id=l.id AND a.state IN (N'RESERVED',N'COMMITTED')),0) AS allocatedQuantity
```

LineDetailRow 类追加 `public decimal allocatedQuantity { get; set; }`（lines 直接返回行对象，自动带出）。

- [ ] **Step 4: 运行测试确认通过（GREEN）**

```bash
cd "/e/Viding/Retail Trace Code" && TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~InboundApiTests"
```

Expected: PASS（6 个测试）。

#### Part B：前端类型 + 上游出库单页

- [ ] **Step 5: 前端类型与 ApiError 扩展**

Create `traceflow/src/TraceFlow.Web/src/m2types.ts`：

```typescript
export interface UpstreamBill {
  billId: number; platformBillId: string; billNo: string | null; billDate: string | null;
  upstreamRefEntId: string; toEntId: string | null; completeness: string;
  sourceVersion: string | null; fetchedAtUtc: string;
}
export interface UpstreamCode { codeNormalized: string; packageLevel: string | null; }
export interface UpstreamLine {
  lineId: number; platformLineId: string; platformDrugId: string | null; batchNo: string | null;
  quantity: number | null; unit: string | null; allocatedQuantity: number; remainingQuantity: number | null;
  codes: UpstreamCode[];
}
export interface UpstreamDetail { bill: UpstreamBill; lines: UpstreamLine[]; }
export interface InboundListItem {
  snapshotId: number; docKey: string; version: number; businessDatetimeUtc: string | null;
  completeness: string; matchState: string | null; matchVersion: number | null; reason: string | null;
  supplierDwbh: string | null; taskId: number | null; taskState: string | null;
}
export interface InboundLine {
  lineId: number; sourceLineKey: string; productSourceId: string; quantity: number; unit: string | null;
  batchRaw: string | null; classification: string | null; allocatedQuantity: number;
}
export interface InboundAllocCode { codeId: number; codeNormalized: string; }
export interface InboundAllocation {
  allocationId: number; sourceLineId: number; upstreamLineId: number; quantity: number;
  unitRatio: string; state: string; codes: InboundAllocCode[];
}
export interface InboundDetail {
  snapshot: { snapshotId: number; docKey: string; version: number; businessDatetimeUtc: string | null; completeness: string; contentHash: string };
  lines: InboundLine[];
  match: { matchState: string; version: number; reason: string | null } | null;
  allocations: InboundAllocation[];
  task: { taskId: number; state: string; billCode: string | null } | null;
}
export interface AllocationLineInput {
  sourceLineId: number; upstreamLineId: number; quantity: number; unitRatio: string;
  conversionEvidence: string | null; codes: string[];
}
export interface AllocationSaveResult { matchVersion: number; allocationsSaved: number; allRequiredCovered: boolean; }
export interface ConfirmResult { taskId: string; billCode: string; matchVersion: number; reused: boolean; message: string; }
export interface ServiceAccount { serviceAccount: string; machineName: string; }
export interface TopBudget {
  budgetKey: string; dayCount: number; dayLimit: number | null; minuteCount: number; minuteLimit: number | null;
  secondCount: number; secondLimit: number | null; nextAllowedAtUtc: string | null;
}
export interface ChannelPause {
  id: number; scopeKey: string; reason: string; isActive: boolean; pausedAtUtc: string;
  pausedBy: string; resumedAtUtc: string | null; resumedBy: string | null;
}
export interface TopBudgetView { budgets: TopBudget[]; pauses: ChannelPause[]; }
```

编辑 `traceflow/src/TraceFlow.Web/src/api.ts`：ApiError 类替换为（加性第 5 参数）：

```typescript
export interface FieldError { sourceLineId?: number; code?: string; message?: string; }

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryable: boolean,
    public fieldErrors?: FieldError[]) {
    super(message);
  }
}
```

api() 内错误分支替换为：

```typescript
  if (!resp.ok) {
    const e = json as { code?: string; message?: string; retryable?: boolean; fieldErrors?: FieldError[] } | null;
    throw new ApiError(resp.status, e?.code ?? 'HTTP_' + resp.status, e?.message ?? '请求失败', e?.retryable ?? false, e?.fieldErrors);
  }
```

- [ ] **Step 6: 上游出库单页**

Create `traceflow/src/TraceFlow.Web/src/pages/UpstreamBillsPage.tsx`：

```tsx
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { Page } from '../m1types';
import type { UpstreamBill, UpstreamDetail } from '../m2types';

const COMPLETENESS = ['ALL', 'COMPLETE', 'INCOMPLETE'];
const PAGE_SIZE = 50;
function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
function fmt(d: string | null): string { return d ? d.replace('T', ' ').slice(0, 19) : '—'; }

export default function UpstreamBillsPage() {
  const [billNo, setBillNo] = useState('');
  const [completeness, setCompleteness] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<UpstreamBill> | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [detailId, setDetailId] = useState<number | null>(null);
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (billNo.trim()) qs.set('billNo', billNo.trim());
    if (completeness !== 'ALL') qs.set('completeness', completeness);
    try {
      const res = await api<Page<UpstreamBill>>(`/api/v1/upstream-bills?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [billNo, completeness, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  async function redownload(billId: number) {
    setStatus(''); setError('');
    try {
      const r = await api<{ jobId: string }>(`/api/v1/upstream-bills/${billId}/redownload`, { method: 'POST', headers: idem() });
      setStatus(`补拉已排队（作业 ${r.jobId}），后台处理，稍后刷新查看结果。`);
    } catch (e) { setError(errMsg(e)); }
  }

  return (
    <section>
      <h1>上游出库单</h1>
      <p>完整性 INCOMPLETE 的单据不可供完整入库上传；列表不提供"视为已入库"。</p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={billNo} onChange={(e) => { setBillNo(e.target.value); setPage(1); }} placeholder="单号（精确）" />
        <select value={completeness} onChange={(e) => { setCompleteness(e.target.value); setPage(1); }}>
          {COMPLETENESS.map((c) => <option key={c} value={c}>{c === 'ALL' ? '全部完整性' : c}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {status && <p role="status">{status}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>单号</th><th>平台单 ID</th><th>上游主体</th><th>单据日期</th><th>完整性</th><th>抓取时间</th><th>操作</th></tr></thead>
            <tbody>
              {data.records.map((b) => (
                <tr key={b.billId}>
                  <td>{b.billNo ?? '（缺）'}</td><td>{b.platformBillId}</td><td>{b.upstreamRefEntId}</td>
                  <td>{fmt(b.billDate)}</td>
                  <td style={{ background: b.completeness === 'COMPLETE' ? '#d4edda' : '#f8d7da' }}>{b.completeness}</td>
                  <td>{fmt(b.fetchedAtUtc)}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    <button onClick={() => setDetailId(b.billId)}>详情</button>{' '}
                    <button onClick={() => void redownload(b.billId)}>补拉</button>
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
      {detailId !== null && <BillDetailDialog billId={detailId} onClose={() => setDetailId(null)} />}
    </section>
  );
}

function BillDetailDialog({ billId, onClose }: { billId: number; onClose: () => void }) {
  const [detail, setDetail] = useState<UpstreamDetail | null>(null);
  const [error, setError] = useState('');
  const [openLine, setOpenLine] = useState<number | null>(null);
  useEffect(() => {
    let active = true;
    api<UpstreamDetail>(`/api/v1/upstream-bills/${billId}`)
      .then((d) => { if (active) setDetail(d); })
      .catch((e) => { if (active) setError(errMsg(e)); });
    return () => { active = false; };
  }, [billId]);
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>上游单详情 · {detail?.bill.billNo ?? billId}</h3>
        {error && <p role="alert">{error}</p>}
        {detail === null ? <p>加载中…</p> : (
          <>
            <p>平台单 ID：{detail.bill.platformBillId} · 上游主体：{detail.bill.upstreamRefEntId} · 收货主体：{detail.bill.toEntId ?? '—'} · 完整性：{detail.bill.completeness}</p>
            <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
              <thead><tr><th>平台药</th><th>批号</th><th>数量</th><th>单位</th><th>已配</th><th>剩余</th><th>码</th></tr></thead>
              <tbody>
                {detail.lines.map((l) => (
                  <tr key={l.lineId}>
                    <td>{l.platformDrugId ?? '（缺）'}</td><td>{l.batchNo ?? '（缺）'}</td>
                    <td>{l.quantity ?? '—'}</td><td>{l.unit ?? '—'}</td>
                    <td>{l.allocatedQuantity}</td><td>{l.remainingQuantity ?? '—'}</td>
                    <td><button onClick={() => setOpenLine(openLine === l.lineId ? null : l.lineId)}>{l.codes.length} 个码{l.codes.length > 0 ? '（展开）' : ''}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            {openLine !== null && (
              <div style={{ marginTop: 8, maxHeight: 200, overflow: 'auto', border: '1px solid #ddd', padding: 8 }}>
                {detail.lines.find((l) => l.lineId === openLine)?.codes.map((c) => (
                  <p key={c.codeNormalized} style={{ margin: 2, fontFamily: 'monospace' }}>{c.codeNormalized}（{c.packageLevel ?? '?'}）</p>
                ))}
              </div>
            )}
          </>
        )}
        <div style={{ marginTop: 8 }}><button onClick={onClose}>关闭</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 860, width: '95%', maxHeight: '85vh', overflow: 'auto' };
```

#### Part C：入库关联页（补配/确认/取消/重排）

- [ ] **Step 7: 入库关联页**

Create `traceflow/src/TraceFlow.Web/src/pages/InboundsPage.tsx`：

```tsx
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError, type FieldError } from '../api';
import type { Page } from '../m1types';
import type {
  AllocationLineInput, AllocationSaveResult, ConfirmResult, InboundDetail, InboundListItem,
  UpstreamBill, UpstreamDetail,
} from '../m2types';

const MATCH_STATES = ['ALL', 'NOT_REQUIRED', 'UNMATCHED', 'PARTIAL', 'AMBIGUOUS', 'CONFLICT', 'COMPLETE'];
const PAGE_SIZE = 50;
const PRE_SEND = ['Blocked', 'Ready', 'Cancelled'];
function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
function fmt(d: string | null): string { return d ? d.replace('T', ' ').slice(0, 19) : '—'; }
const round6 = (x: number): number => Math.round(x * 1e6) / 1e6;
const CONFLICT_HINT = '版本冲突或已冻结：数据可能已被他人修改，请刷新后重试';

export default function InboundsPage() {
  const [docKey, setDocKey] = useState('');
  const [matchState, setMatchState] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<InboundListItem> | null>(null);
  const [error, setError] = useState('');
  const [detailId, setDetailId] = useState<number | null>(null);
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (docKey.trim()) qs.set('docKey', docKey.trim());
    if (matchState !== 'ALL') qs.set('matchState', matchState);
    try {
      const res = await api<Page<InboundListItem>>(`/api/v1/inbounds?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [docKey, matchState, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>入库关联</h1>
      <p>自动匹配优先；差异走人工补配，确认后由系统复核资格并自动上传（无手工上传按钮）。</p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={docKey} onChange={(e) => { setDocKey(e.target.value); setPage(1); }} placeholder="单据编号 djbh（精确）" />
        <select value={matchState} onChange={(e) => { setMatchState(e.target.value); setPage(1); }}>
          {MATCH_STATES.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部匹配态' : s}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>单据编号</th><th>供应商 dwbh</th><th>业务日期</th><th>版本</th><th>完整性</th><th>匹配态</th><th>任务态</th><th>操作</th></tr></thead>
            <tbody>
              {data.records.map((r) => (
                <tr key={r.snapshotId}>
                  <td>{r.docKey}</td><td>{r.supplierDwbh ?? '—'}</td><td>{fmt(r.businessDatetimeUtc)}</td>
                  <td>{r.version}</td><td>{r.completeness}</td>
                  <td>{r.matchState ?? '（未匹配）'}</td><td>{r.taskState ?? '—'}</td>
                  <td><button onClick={() => setDetailId(r.snapshotId)}>详情/补配</button></td>
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
      {detailId !== null && <InboundDetailDialog snapshotId={detailId} onClose={() => { setDetailId(null); void load(); }} />}
    </section>
  );
}

function InboundDetailDialog({ snapshotId, onClose }: { snapshotId: number; onClose: () => void }) {
  const [detail, setDetail] = useState<InboundDetail | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [cancelTarget, setCancelTarget] = useState<{ allocationId: number | null } | null>(null);

  const reload = useCallback(async () => {
    try { setDetail(await api<InboundDetail>(`/api/v1/inbounds/${snapshotId}`)); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, [snapshotId]);
  useEffect(() => { let active = true; (async () => { if (active) await reload(); })(); return () => { active = false; }; }, [reload]);

  const frozen = !!detail?.task && !PRE_SEND.includes(detail.task.state);
  const requiredLines = detail?.lines.filter((l) => l.classification === 'REQUIRED') ?? [];
  const allCovered = requiredLines.length > 0 && requiredLines.every((l) => round6(l.quantity - l.allocatedQuantity) === 0);
  const notRequired = detail?.match?.matchState === 'NOT_REQUIRED';

  async function rematch() {
    setStatus(''); setError('');
    try {
      const r = await api<{ jobId: string }>(`/api/v1/inbounds/${snapshotId}/rematch`, { method: 'POST', headers: idem() });
      setStatus(`重排已排队（作业 ${r.jobId}），后台处理，稍后刷新查看结果。`);
    } catch (e) { setError(errMsg(e)); }
  }

  function onConflict(e: unknown) {
    if (e instanceof ApiError && e.status === 409) {
      setError(CONFLICT_HINT); setEditing(false); setConfirming(false); setCancelTarget(null); void reload();
    } else setError(errMsg(e));
  }

  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>入库单 · {detail?.snapshot.docKey ?? snapshotId}</h3>
        {error && <p role="alert">{error}</p>}
        {status && <p role="status">{status}</p>}
        {detail === null ? <p>加载中…</p> : (
          <>
            <p>
              匹配态：{detail.match?.matchState ?? '（未匹配）'}（版本 {detail.match?.version ?? '—'}）
              {detail.match?.reason ? ` · 原因：${detail.match.reason}` : ''}
            </p>
            {detail.task
              ? <p>上传任务 #{detail.task.taskId} · 状态 {detail.task.state} · 单据号 {detail.task.billCode ?? '（待生成）'}</p>
              : <p>尚无上传任务（确认补配或自动匹配合格后创建）。</p>}
            {frozen && <p role="alert" style={{ background: '#f8d7da', padding: 8 }}>已发起上传，分配冻结（只读）。发送后变更须走异常处理流程。</p>}
            {notRequired && <p>全部行豁免（EXEMPT），无需追溯上传。</p>}
            <h4>本地入库行</h4>
            <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
              <thead><tr><th>商品 spid</th><th>批号</th><th>数量</th><th>单位</th><th>分类</th><th>已配</th><th>差异</th></tr></thead>
              <tbody>
                {detail.lines.map((l) => {
                  const diff = round6(l.quantity - l.allocatedQuantity);
                  const required = l.classification === 'REQUIRED';
                  return (
                    <tr key={l.lineId} style={{ background: required ? (diff === 0 ? '#d4edda' : '#f8d7da') : undefined }}>
                      <td>{l.productSourceId}</td><td>{l.batchRaw ?? '（缺）'}</td><td>{l.quantity}</td>
                      <td>{l.unit ?? '—'}</td><td>{l.classification ?? 'UNKNOWN'}</td>
                      <td>{l.allocatedQuantity}</td><td>{required ? diff : '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <h4>分配（RESERVED 暂存 / COMMITTED 已挂任务）</h4>
            {detail.allocations.length === 0 ? <p>暂无分配。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>本地行</th><th>上游行</th><th>数量</th><th>换算</th><th>状态</th><th>码</th><th>操作</th></tr></thead>
                <tbody>
                  {detail.allocations.map((a) => (
                    <tr key={a.allocationId}>
                      <td>{a.sourceLineId}</td><td>{a.upstreamLineId}</td><td>{a.quantity}</td>
                      <td>{a.unitRatio}</td><td>{a.state}</td>
                      <td title={a.codes.map((c) => c.codeNormalized).join('\n')}>{a.codes.length} 个</td>
                      <td>{!frozen && a.state === 'RESERVED' && <button onClick={() => setCancelTarget({ allocationId: a.allocationId })}>取消</button>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {!frozen && !notRequired && (
              <div style={{ marginTop: 12, display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                <button onClick={() => void rematch()}>一键重排</button>
                <button onClick={() => { setStatus(''); setEditing(true); }}>人工补配</button>
                <button disabled={!allCovered || !detail.match} onClick={() => { setStatus(''); setConfirming(true); }}>确认补配</button>
                <button disabled={detail.allocations.length === 0 && !detail.task} onClick={() => setCancelTarget({ allocationId: null })}>全部取消/回退</button>
                <button onClick={() => void reload()}>刷新</button>
              </div>
            )}
            {frozen && <div style={{ marginTop: 12 }}><button onClick={() => void reload()}>刷新</button></div>}
          </>
        )}
        <div style={{ marginTop: 8 }}><button onClick={onClose}>关闭</button></div>
      </div>
      {editing && detail?.match && (
        <AllocationEditor detail={detail} onClose={() => setEditing(false)}
          onSaved={(r) => { setEditing(false); setStatus(`已保存 ${r.allocationsSaved} 行分配（匹配版本 ${r.matchVersion}）。${r.allRequiredCovered ? '全部应追溯行已覆盖，可确认补配。' : '仍有差异，请继续补配。'}`); void reload(); }}
          onConflict={onConflict} />
      )}
      {confirming && detail?.match && (
        <ConfirmDialog detail={detail} onClose={() => setConfirming(false)}
          onDone={(r) => { setConfirming(false); setStatus(`已确认：任务 #${r.taskId} · 单据号 ${r.billCode}。${r.message}${r.reused ? '（复用既有单据号）' : ''}`); void reload(); }}
          onConflict={onConflict} />
      )}
      {cancelTarget && (
        <CancelDialog snapshotId={snapshotId} allocationId={cancelTarget.allocationId} onClose={() => setCancelTarget(null)}
          onDone={(n) => { setCancelTarget(null); setStatus(`已取消 ${n} 项。`); void reload(); }}
          onConflict={onConflict} />
      )}
    </div>
  );
}

interface EditRow {
  key: number; sourceLineId: number; upstreamLineId: string; quantity: string;
  unitRatio: string; evidence: string; codesText: string;
}

function AllocationEditor({ detail, onClose, onSaved, onConflict }: {
  detail: InboundDetail; onClose: () => void;
  onSaved: (r: AllocationSaveResult) => void; onConflict: (e: unknown) => void;
}) {
  const requiredLines = detail.lines.filter((l) => l.classification === 'REQUIRED');
  const [rows, setRows] = useState<EditRow[]>([]);
  const [nextKey, setNextKey] = useState(1);
  const [billNo, setBillNo] = useState('');
  const [bills, setBills] = useState<UpstreamBill[]>([]);
  const [upDetail, setUpDetail] = useState<UpstreamDetail | null>(null);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<FieldError[]>([]);
  const [busy, setBusy] = useState(false);

  function addRow(sourceLineId: number) {
    setRows([...rows, { key: nextKey, sourceLineId, upstreamLineId: '', quantity: '', unitRatio: '1/1', evidence: '', codesText: '' }]);
    setNextKey(nextKey + 1);
  }
  function patch(key: number, p: Partial<EditRow>) { setRows(rows.map((r) => (r.key === key ? { ...r, ...p } : r))); }

  async function searchBills() {
    setError('');
    try {
      const qs = new URLSearchParams({ page: '1', pageSize: '20' });
      if (billNo.trim()) qs.set('billNo', billNo.trim());
      setBills((await api<Page<UpstreamBill>>(`/api/v1/upstream-bills?${qs.toString()}`)).records);
    } catch (e) { setError(errMsg(e)); }
  }
  async function pickBill(billId: string) {
    setError('');
    if (!billId) { setUpDetail(null); return; }
    try { setUpDetail(await api<UpstreamDetail>(`/api/v1/upstream-bills/${billId}`)); }
    catch (e) { setError(errMsg(e)); }
  }

  async function submit() {
    setError(''); setFieldErrors([]);
    if (rows.length === 0) { setError('请至少添加一行分配'); return; }
    const lines: AllocationLineInput[] = [];
    for (const r of rows) {
      const qty = Number(r.quantity);
      const codes = r.codesText.split(/[\s,;，；]+/).filter(Boolean);
      if (!r.upstreamLineId) { setError('每行必须选择上游明细行'); return; }
      if (!Number.isFinite(qty) || qty <= 0) { setError('数量必须为正数'); return; }
      if (codes.length === 0) { setError('每行必须登记至少一个追溯码'); return; }
      lines.push({ sourceLineId: r.sourceLineId, upstreamLineId: Number(r.upstreamLineId), quantity: qty,
        unitRatio: r.unitRatio.trim(), conversionEvidence: r.evidence.trim() ? r.evidence.trim() : null, codes });
    }
    setBusy(true);
    try {
      const res = await api<AllocationSaveResult>(`/api/v1/inbounds/${detail.snapshot.snapshotId}/allocations`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ expectedMatchVersion: String(detail.match!.version), lines }),
      });
      onSaved(res);
    } catch (e) {
      setBusy(false);
      if (e instanceof ApiError && e.status === 409) { onConflict(e); return; }
      if (e instanceof ApiError && e.fieldErrors && e.fieldErrors.length > 0) setFieldErrors(e.fieldErrors);
      setError(errMsg(e));
    }
  }

  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>人工补配 · {detail.snapshot.docKey}（匹配版本 {detail.match!.version}）</h3>
        <p>强约束：批号一致、换算后数量=码数、码须属于所选上游行且为最小包装、码不得重复占用。保存为全量替换（原暂存分配先取消）。</p>
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 260 }}>
            <h4>本地应追溯行</h4>
            {requiredLines.map((l) => (
              <p key={l.lineId}>
                #{l.lineId} {l.productSourceId} · 批号 {l.batchRaw ?? '（缺）'} · {l.quantity}{l.unit ?? ''} · 已配 {l.allocatedQuantity}{' '}
                <button onClick={() => addRow(l.lineId)}>添加分配</button>
              </p>
            ))}
          </div>
          <div style={{ flex: 1, minWidth: 260 }}>
            <h4>上游明细来源</h4>
            <div style={{ display: 'flex', gap: 8 }}>
              <input value={billNo} onChange={(e) => setBillNo(e.target.value)} placeholder="上游单号" />
              <button onClick={() => void searchBills()}>搜索</button>
            </div>
            <select onChange={(e) => void pickBill(e.target.value)} defaultValue="" style={{ marginTop: 8, width: '100%' }}>
              <option value="">选择上游单…</option>
              {bills.map((b) => <option key={b.billId} value={b.billId}>{b.billNo ?? b.platformBillId}（{b.completeness}）</option>)}
            </select>
            {upDetail && upDetail.lines.map((l) => (
              <p key={l.lineId} style={{ margin: 4, fontSize: 13 }}>
                行 #{l.lineId} {l.platformDrugId ?? '?'} · 批号 {l.batchNo ?? '（缺）'} · 数量 {l.quantity ?? '?'} · 剩余 {l.remainingQuantity ?? '?'} · {l.codes.length} 码
              </p>
            ))}
          </div>
        </div>
        <h4>分配行</h4>
        {rows.map((r) => (
          <div key={r.key} style={{ border: '1px solid #ddd', padding: 8, marginBottom: 8 }}>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
              <select value={r.sourceLineId} onChange={(e) => patch(r.key, { sourceLineId: Number(e.target.value) })}>
                {requiredLines.map((l) => <option key={l.lineId} value={l.lineId}>本地行 #{l.lineId} {l.productSourceId}</option>)}
              </select>
              <select value={r.upstreamLineId} onChange={(e) => patch(r.key, { upstreamLineId: e.target.value })}>
                <option value="">上游行…</option>
                {(upDetail?.lines ?? []).map((l) => <option key={l.lineId} value={l.lineId}>#{l.lineId} {l.platformDrugId ?? '?'} / {l.batchNo ?? '?'} / 剩 {l.remainingQuantity ?? '?'}</option>)}
              </select>
              <input value={r.quantity} onChange={(e) => patch(r.key, { quantity: e.target.value })} placeholder="数量" style={{ width: 80 }} />
              <input value={r.unitRatio} onChange={(e) => patch(r.key, { unitRatio: e.target.value })} placeholder="换算 n/d" style={{ width: 80 }} />
              <input value={r.evidence} onChange={(e) => patch(r.key, { evidence: e.target.value })} placeholder="换算证据（比例≠1/1 必填）" style={{ flex: 1, minWidth: 160 }} />
              <button onClick={() => setRows(rows.filter((x) => x.key !== r.key))}>删除</button>
            </div>
            <textarea value={r.codesText} onChange={(e) => patch(r.key, { codesText: e.target.value })} rows={2}
              style={{ width: '100%', marginTop: 4, fontFamily: 'monospace' }} placeholder="追溯码（每行一个或空白分隔，须为该上游行的最小包装码）" />
          </div>
        ))}
        {fieldErrors.length > 0 && (
          <ul role="alert" style={{ color: '#a00' }}>
            {fieldErrors.map((f, i) => <li key={i}>行 {f.sourceLineId ?? '?'}{f.code ? `（${f.code}）` : ''}：{f.message ?? '校验失败'}</li>)}
          </ul>
        )}
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={() => void submit()}>保存分配（全量替换）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function ConfirmDialog({ detail, onClose, onDone, onConflict }: {
  detail: InboundDetail; onClose: () => void; onDone: (r: ConfirmResult) => void; onConflict: (e: unknown) => void;
}) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    setBusy(true); setError('');
    try {
      const r = await api<ConfirmResult>(`/api/v1/inbounds/${detail.snapshot.snapshotId}/confirm-allocation`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ expectedMatchVersion: String(detail.match!.version) }),
      });
      onDone(r);
    } catch (e) {
      setBusy(false);
      if (e instanceof ApiError && e.status === 409) { onConflict(e); return; }
      setError(errMsg(e));
    }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>确认补配 · {detail.snapshot.docKey}</h3>
        <p role="alert" style={{ background: '#fff3cd', padding: 8 }}>
          确认后将重新校验全部资格（批号/数量/码集合/占用/映射/供应商）。<strong>符合资格后将自动上传</strong>，无需手工上传；单据号由系统生成。
        </p>
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={() => void submit()}>确认（版本 {detail.match!.version}）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function CancelDialog({ snapshotId, allocationId, onClose, onDone, onConflict }: {
  snapshotId: number; allocationId: number | null; onClose: () => void;
  onDone: (cancelled: number) => void; onConflict: (e: unknown) => void;
}) {
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写取消理由'); return; }
    setBusy(true); setError('');
    try {
      const r = await api<{ cancelled: number }>(`/api/v1/inbounds/${snapshotId}/cancel-allocation`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ allocationId, reason }),
      });
      onDone(r.cancelled);
    } catch (e) {
      setBusy(false);
      if (e instanceof ApiError && e.status === 409) { onConflict(e); return; }
      setError(errMsg(e));
    }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>{allocationId === null ? '全部取消/回退' : `取消分配 #${allocationId}`}</h3>
        <p>{allocationId === null ? '取消全部暂存分配；若已挂发送前任务，任务与分配一并回退、码占用释放。已发送的单据将被拒绝（FROZEN）。' : '取消该暂存分配并释放码占用。'}</p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%' }} placeholder="取消理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={() => void submit()}>确认取消</button> <button onClick={onClose}>返回</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 960, width: '95%', maxHeight: '85vh', overflow: 'auto' };
```

编辑 `traceflow/src/TraceFlow.Web/src/App.tsx`：import 区追加：

```tsx
import UpstreamBillsPage from './pages/UpstreamBillsPage';
import InboundsPage from './pages/InboundsPage';
```

`<Route path="/suppliers" ... />` 行后追加：

```tsx
        <Route path="/upstream" element={<UpstreamBillsPage />} />
        <Route path="/inbounds" element={<InboundsPage />} />
```

编辑 `traceflow/src/TraceFlow.Web/src/components/AppShell.tsx`：NAV 中两行改为：

```tsx
  { to: '/upstream', label: '上游出库单', ready: true },
  { to: '/inbounds', label: '入库关联', ready: true },
```

- [ ] **Step 8: 前端构建（类型检查）**

```bash
cd "/e/Viding/Retail Trace Code/traceflow/src/TraceFlow.Web" && npm run build
```

Expected: tsc + vite 构建成功，产物输出到 `../TraceFlow.Host/wwwroot`。

- [ ] **Step 9: 浏览器验证（Mock 环境，真实点击）**

准备一次性验证库与账号（sqlcmd 可用时；哈希为 PasswordHasher 8.0.11 预生成，密码 `Manual-Verify-123`）：

```bash
cd "/e/Viding/Retail Trace Code" && sqlcmd -S localhost -E -Q "IF DB_ID('TraceFlow_M2Manual') IS NULL CREATE DATABASE TraceFlow_M2Manual" && dotnet run --project traceflow/db/tools/TraceFlow.DbMigrator -c Release -- "Server=localhost;Database=TraceFlow_M2Manual;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" "traceflow/db/migrations" && sqlcmd -S localhost -E -d TraceFlow_M2Manual -Q "IF NOT EXISTS (SELECT 1 FROM dbo.app_user WHERE username=N'm2manual') INSERT INTO dbo.app_user(username,display_name,password_hash) VALUES(N'm2manual',N'手动验证',N'AQAAAAIAAYagAAAAEEmxiThdpz9gbUqNTBCTLaa9+2WxS0skfvUy41NwHfNbKRyhkvd0krUiWJ+eGBOcrQ=='); DECLARE @uid bigint=(SELECT id FROM dbo.app_user WHERE username=N'm2manual'); INSERT INTO dbo.user_permission(user_id,permission,granted_by) SELECT @uid,p,N'manual' FROM (VALUES (N'system:manage'),(N'trace:read'),(N'audit:read'),(N'job:rematch'),(N'inbound:allocate'),(N'master:match'),(N'master:classify')) v(p) WHERE NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@uid AND permission=v.p)"
```

启动后端（后台运行）与前端 dev server（后台运行）：

```bash
cd "/e/Viding/Retail Trace Code" && ConnectionStrings__TraceFlow="Server=localhost;Database=TraceFlow_M2Manual;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TraceFlow__Env=Mock ASPNETCORE_URLS=http://localhost:5152 dotnet run --project traceflow/src/TraceFlow.Host -c Release --no-launch-profile
```

```bash
cd "/e/Viding/Retail Trace Code/traceflow/src/TraceFlow.Web" && npm run dev
```

用浏览器（chrome-devtools/browser-use 工具）打开 `http://localhost:5173`，登录 m2manual / Manual-Verify-123，逐项验证并截图留证：

1. 导航"上游出库单/入库关联"可点击（不再显示"未接入"）；环境徽标显示 MOCK。
2. 上游页：空列表正常渲染；查询/分页控件可用；无"视为已入库"按钮。
3. 入库页：空列表正常渲染；匹配态过滤切换不报错。
4. 造数（sqlcmd 向 TraceFlow_M2Manual 插入一条 PURCHASE_IN 快照+行+上游单+行+码，或运行一次 fixture 同步作业）后：入库详情显示本地行/差异高亮；人工补配弹窗可选上游行、填码保存；保存后差异变绿；确认补配出现"符合资格后将自动上传"后果提示，成功后显示单据号（TF 前缀）与任务态。
5. 409 路径：两个浏览器会话同开详情，A 保存分配后 B 用旧版本保存 → B 显示"版本冲突…请刷新后重试"且详情自动重载（AT-067）。
6. 冻结路径：对已确认任务用 sqlcmd 将 upload_task.state 改为 Dispatching 并插入一条 UPLOAD attempt → 刷新详情出现冻结横幅、编辑按钮消失。
7. 取消路径：RESERVED 分配单行取消、全部取消/回退均成功并释放（详情分配清空）。
8. 控制台无未捕获错误（list_console_messages）。

验证完成后停止两个后台进程；TraceFlow_M2Manual 为一次性验证库，保留不删（与 TraceFlow_Dev 同等待遇，不触碰）。若浏览器工具不可用，如实记录"未完成浏览器验证"并在 gate report 声明，不得声称已验证。

- [ ] **Step 10: 提交**

```bash
cd "/e/Viding/Retail Trace Code" && git status --porcelain && git add traceflow/src/TraceFlow.Host/Endpoints/UpstreamEndpoints.cs traceflow/src/TraceFlow.Host/Endpoints/InboundEndpoints.cs traceflow/tests/TraceFlow.IntegrationTests/Api/InboundApiTests.cs traceflow/src/TraceFlow.Web/src/api.ts traceflow/src/TraceFlow.Web/src/m2types.ts traceflow/src/TraceFlow.Web/src/pages/UpstreamBillsPage.tsx traceflow/src/TraceFlow.Web/src/pages/InboundsPage.tsx traceflow/src/TraceFlow.Web/src/App.tsx traceflow/src/TraceFlow.Web/src/components/AppShell.tsx && git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): upstream bills and inbound allocation pages with quantity views (UI-04/05/11, AT-067)"
```

注意：`traceflow/src/TraceFlow.Host/wwwroot/` 已被 .gitignore 忽略（M0 约定），构建产物不提交；`*.tsbuildinfo` 同样忽略。

---

### Task 14: 前端设置 §4.7 显示 + 映射改绑入口

**Files:**
- Modify: `traceflow/src/TraceFlow.Web/src/m1types.ts`（SettingView +protectionScope）
- Modify: `traceflow/src/TraceFlow.Web/src/pages/SettingsPage.tsx`（作用域列/运行账号面板/限流预算面板）
- Modify: `traceflow/src/TraceFlow.Web/src/pages/ProductsPage.tsx`（改绑按钮+ResolveDialog）
- Modify: `traceflow/src/TraceFlow.Web/src/pages/SuppliersPage.tsx`（改绑按钮+ResolveDialog）

**Interfaces:**
- Consumes: Task 12 `GET /api/v1/settings/service-account`、`GET /api/v1/top-budgets`、SettingView.protectionScope；Task 11 `POST /api/v1/product-mappings/{id}/resolve`、`POST /api/v1/supplier-mappings/{id}/resolve`（body `{targetId,expectedVersion,reason}`，expectedVersion=映射 rowversion hex 即 `m.version`；商品 targetId=platform_drug_id，供应商 targetId=ref_ent_id）；Task 13 `m2types.ts` 的 `ServiceAccount`/`TopBudgetView`。
- Produces: 无后端接口变更（纯前端）。

**裁定：**
- R14a：改绑按钮仅在 `mappingState ∈ {CONFLICT, REVIEW_REQUIRED, CANDIDATE, UNRESOLVED}` 且有 mapping 时显示（与后端 STATE_NOT_RESOLVABLE 门禁一致）；AUTO_CONFIRMED/MANUAL_CONFIRMED 想改绑须先"解除"再改绑（既有流程，不新增捷径）。
- R14b：新配置键（upstream.pollSeconds/top.billPrefix/top.billMaxLength）由后端 Catalog 驱动自动出现在设置表格，前端零改动；本任务只加 §4.7 三块显示。
- R14c：限值 NULL 显示"—（不设帽）"，与 T12 裁定一致；暂停列表只读展示（恢复动作 M3 交付）。

- [ ] **Step 1: 类型与设置页**

编辑 `traceflow/src/TraceFlow.Web/src/m1types.ts`：SettingView 接口末行 `version: string; updatedBy: string | null; updatedAtUtc: string | null; reason: string | null;` 替换为：

```typescript
  version: string; updatedBy: string | null; updatedAtUtc: string | null; reason: string | null;
  protectionScope: string | null;
```

`SettingsPage.tsx` 整体替换为（保留既有表格/编辑/连接测试/暂停切换，新增三块 §4.7 显示）：

```tsx
import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { ConnectionTestResult, SettingView } from '../m1types';
import type { ServiceAccount, TopBudgetView } from '../m2types';

function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
function fmt(d: string | null): string { return d ? d.replace('T', ' ').slice(0, 19) : '—'; }

export default function SettingsPage() {
  const [settings, setSettings] = useState<SettingView[] | null>(null);
  const [account, setAccount] = useState<ServiceAccount | null>(null);
  const [budgets, setBudgets] = useState<TopBudgetView | null>(null);
  const [error, setError] = useState('');
  const [editTarget, setEditTarget] = useState<SettingView | null>(null);
  const [testMsg, setTestMsg] = useState('');
  const [busyKey, setBusyKey] = useState('');

  const load = useCallback(async () => {
    try { setSettings(await api<SettingView[]>('/api/v1/settings')); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, []);
  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  useEffect(() => {
    let active = true;
    api<ServiceAccount>('/api/v1/settings/service-account').then((a) => { if (active) setAccount(a); }).catch(() => { /* 面板降级为不显示 */ });
    api<TopBudgetView>('/api/v1/top-budgets').then((b) => { if (active) setBudgets(b); }).catch(() => { /* 同上 */ });
    return () => { active = false; };
  }, []);

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

  const limit = (n: number | null): string => (n === null ? '—（不设帽）' : String(n));

  return (
    <section>
      <h1>系统设置</h1>
      {error && <p role="alert">{error}</p>}
      {testMsg && <p role="status">{testMsg}</p>}
      <h3>凭证保护与运行账号（§4.7）</h3>
      {account && (
        <p>
          服务运行账号：<code>{account.serviceAccount}</code> · 机器：<code>{account.machineName}</code>。
          密钥以 Windows DPAPI 保护，作用域 CurrentUser 绑定该账号：<strong>换机或恢复备份后密钥不可解密，须重新录入</strong>；
          密文不可解密时系统停止对应调用并告警，无默认凭据兜底。
        </p>
      )}
      <h3>限流预算用量（只读）</h3>
      {budgets === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse' }}>
            <thead><tr><th>预算键</th><th>日用量/限值</th><th>分用量/限值</th><th>秒用量/限值</th><th>下次允许时间</th></tr></thead>
            <tbody>
              {budgets.budgets.length === 0 ? <tr><td colSpan={5}>暂无用量记录</td></tr> : budgets.budgets.map((b) => (
                <tr key={b.budgetKey}>
                  <td>{b.budgetKey}</td>
                  <td>{b.dayCount} / {limit(b.dayLimit)}</td>
                  <td>{b.minuteCount} / {limit(b.minuteLimit)}</td>
                  <td>{b.secondCount} / {limit(b.secondLimit)}</td>
                  <td>{fmt(b.nextAllowedAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {budgets.pauses.filter((p) => p.isActive).length > 0 && (
            <>
              <h4>生效中的授权暂停</h4>
              <ul>
                {budgets.pauses.filter((p) => p.isActive).map((p) => (
                  <li key={p.id}>{p.scopeKey} · {p.reason} · {p.pausedBy} 于 {fmt(p.pausedAtUtc)}（恢复须管理员人工动作，M3 交付入口）</li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
      <h3>配置项</h3>
      {settings === null ? <p>加载中…</p> : (
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>键</th><th>值</th><th>保护作用域</th><th>版本</th><th>更新者</th><th>理由</th><th>操作</th></tr></thead>
          <tbody>
            {settings.map((s) => (
              <tr key={s.key}>
                <td>{s.key}</td>
                <td style={{ maxWidth: 280, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.isSecret ? (s.hasSecretValue ? '••••（已保存，只写）' : '（未设置）') : (s.valueJson ?? '（默认）')}
                </td>
                <td>{s.protectionScope ?? '—'}</td>
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
            <p>密钥只写：输入新值保存，不会回显已存明文。{setting.hasSecretValue ? '当前已保存密钥。' : '当前未设置。'}保护作用域：{setting.protectionScope ?? 'CurrentUser（保存时绑定）'}</p>
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

- [ ] **Step 2: 商品映射改绑入口**

编辑 `traceflow/src/TraceFlow.Web/src/pages/ProductsPage.tsx`：

(a) `const [disableTarget, setDisableTarget] = useState<Product | null>(null);` 行后追加：

```tsx
  const [resolveTarget, setResolveTarget] = useState<Product | null>(null);
```

(b) 操作列 `{p.mapping && p.mappingState !== 'DISABLED' && <button onClick={() => setConfirmTarget(p)}>确认映射</button>}{' '}` 行后追加：

```tsx
                    {p.mapping && ['CONFLICT', 'REVIEW_REQUIRED', 'CANDIDATE', 'UNRESOLVED'].includes(p.mappingState) &&
                      <button onClick={() => setResolveTarget(p)}>改绑</button>}{' '}
```

(c) `{disableTarget && disableTarget.mapping && <DisableDialog ... />}` 行后追加：

```tsx
      {resolveTarget && resolveTarget.mapping && <ResolveDialog product={resolveTarget} onClose={() => setResolveTarget(null)} onDone={() => { setResolveTarget(null); void load(); }} />}
```

(d) `function DisableDialog` 定义之前插入：

```tsx
function ResolveDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const m = product.mapping!;
  const [targetId, setTargetId] = useState(m.platformDrugId);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!targetId.trim()) { setError('请填写目标平台药 ID'); return; }
    if (!reason.trim()) { setError('请填写改绑理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/product-mappings/${m.mappingId}/resolve`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: targetId.trim(), expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>改绑映射 · {product.name}</h3>
        <p>当前状态 {m.state} · 当前平台药 {m.platformDrugId}。改绑成功后锁定为 MANUAL_CONFIRMED；目标已被其他有效映射占用时返回 409（双向唯一）。</p>
        <input value={targetId} onChange={(e) => setTargetId(e.target.value)} style={{ width: '100%' }} placeholder="目标平台药 platform_drug_id" />
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="改绑理由（必填，留审计）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>改绑（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}
```

- [ ] **Step 3: 供应商映射改绑入口**

编辑 `traceflow/src/TraceFlow.Web/src/pages/SuppliersPage.tsx`：

(a) `const [disableTarget, setDisableTarget] = useState<Supplier | null>(null);` 行后追加：

```tsx
  const [resolveTarget, setResolveTarget] = useState<Supplier | null>(null);
```

(b) 操作列 `{s.mapping && s.mappingState !== 'DISABLED' && <button onClick={() => setConfirmTarget(s)}>确认映射</button>}{' '}` 行后追加：

```tsx
                    {s.mapping && ['CONFLICT', 'REVIEW_REQUIRED', 'CANDIDATE', 'UNRESOLVED'].includes(s.mappingState) &&
                      <button onClick={() => setResolveTarget(s)}>改绑</button>}{' '}
```

(c) `{disableTarget && disableTarget.mapping && <DisableDialog ... />}` 行后追加：

```tsx
      {resolveTarget && resolveTarget.mapping && <ResolveDialog supplier={resolveTarget} onClose={() => setResolveTarget(null)} onDone={() => { setResolveTarget(null); void load(); }} />}
```

(d) `function DisableDialog` 定义之前插入：

```tsx
function ResolveDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: () => void }) {
  const m = supplier.mapping!;
  const [targetId, setTargetId] = useState(m.refEntId);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!targetId.trim()) { setError('请填写目标 ref_ent_id'); return; }
    if (!reason.trim()) { setError('请填写改绑理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/supplier-mappings/${m.mappingId}/resolve`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: targetId.trim(), expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>改绑供应商映射 · {supplier.name}</h3>
        <p>当前状态 {m.state} · 当前 ref_ent_id {m.refEntId}。改绑目标为同一 ref_ent_id 的最新企业版本；<strong>统一社会信用代码必须精确一致</strong>（否则 422 CREDIT_MISMATCH）；成功后锁定 MANUAL_CONFIRMED。</p>
        <input value={targetId} onChange={(e) => setTargetId(e.target.value)} style={{ width: '100%' }} placeholder="目标 ref_ent_id（稳定主体键）" />
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="改绑理由（必填，留审计）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>改绑（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}
```

- [ ] **Step 4: 前端构建**

```bash
cd "/e/Viding/Retail Trace Code/traceflow/src/TraceFlow.Web" && npm run build
```

Expected: tsc + vite 成功。

- [ ] **Step 5: 浏览器验证（沿用 Task 13 的 TraceFlow_M2Manual 环境）**

启动后端（后台运行）与前端 dev server（后台运行）；库与账号已在 Task 13 Step 9 建好（m2manual / Manual-Verify-123，TraceFlow_M2Manual 保留未删）：

```bash
cd "/e/Viding/Retail Trace Code" && ConnectionStrings__TraceFlow="Server=localhost;Database=TraceFlow_M2Manual;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TraceFlow__Env=Mock ASPNETCORE_URLS=http://localhost:5152 dotnet run --project traceflow/src/TraceFlow.Host -c Release --no-launch-profile
```

```bash
cd "/e/Viding/Retail Trace Code/traceflow/src/TraceFlow.Web" && npm run dev
```

打开 `http://localhost:5173` 登录 m2manual / Manual-Verify-123，逐项验证：

1. 设置页显示"服务运行账号/机器"面板（非空、与 Windows 当前账号一致）；密钥行保护作用域列显示 CurrentUser（已保存密钥时）或 —（未设置）。
2. 限流预算面板渲染（空态"暂无用量记录"或有用量行）；限值 NULL 显示"—（不设帽）"。
3. 配置表格出现新键 upstream.pollSeconds / top.billPrefix / top.billMaxLength（Catalog 驱动）。
4. 编辑 top.appsecret 保存新值 → 表格保护作用域 CurrentUser、值仍为 •••• 不回显。
5. 商品/供应商页：AUTO_CONFIRMED/MANUAL_CONFIRMED 行无"改绑"按钮；用 sqlcmd 将一条 product_mapping.state 改为 CONFLICT 后刷新 → 出现"改绑"按钮；点击弹窗，填不存在的目标 ID 提交 → 显示 422 错误文案不崩溃；填回原 platform_drug_id 提交 → 成功锁定 MANUAL_CONFIRMED（若原映射即占用该药则 409 提示，亦为预期路径，记录实际结果）。
6. 控制台无未捕获错误。

验证后停止后台进程。浏览器工具不可用时如实声明未验证。

- [ ] **Step 6: 提交**

```bash
cd "/e/Viding/Retail Trace Code" && git status --porcelain && git add traceflow/src/TraceFlow.Web/src/m1types.ts traceflow/src/TraceFlow.Web/src/pages/SettingsPage.tsx traceflow/src/TraceFlow.Web/src/pages/ProductsPage.tsx traceflow/src/TraceFlow.Web/src/pages/SuppliersPage.tsx && git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "feat(m2): settings scope/account/budget views and mapping resolve dialogs (spec 4.7, T12c carryforward)"
```

---

### Task 15: 契约回写 + 追踪矩阵 + 门禁报告 + 双 collation 全量回归

**Files:**
- Modify: `docs/contracts/openapi.yaml`（M2 端点真实形状回写 + 新端点）
- Modify: `docs/contracts/data-dictionary.md`（0008 表/列/状态 token）
- Modify: `docs/contracts/ksoa-adapter-contract.md`（ywdjbh 候选字段声明）
- Modify: `docs/contracts/top-contract.md`（M2 定稿状态：候选值仍待 G-TOP）
- Modify: `docs/traceability/matrix.md`（M0–M2，AT-020..034/067 等行）
- Create: `docs/delivery/M2-rulings-and-carryforwards.md`
- Create: `docs/delivery/M2-gate-report.md`

**Interfaces:**
- Consumes: Task 1–14 全部实现与测试（测试名清单见 Step 5 矩阵行）；M1 门禁报告格式（docs/delivery/M1-gate-report.md）。
- Produces: M3/M4 计划与执行所依赖的契约基线与携带项清单。

**裁定：**
- R15a：门禁报告**只记录真实运行输出**（@@VERSION、测试计数、构建尾部），禁止预填/伪造；2008 R2 未验证与 G-ERP/G-TOP 未取证必须沿用 M1 报告的降级声明口径。
- R15b：openapi 以**实现为准**回写（旧规划桩与实现不一致处一律改契约，不改实现迁就旧桩）；M3/M4 未实现端点桩保留不动。
- R15c：top-contract.md 候选值（billPrefix/billMaxLength/分页参数/MIN token/限流与重复单号 sub_code 前缀/ywdjbh）**全部仍标"待 G-TOP/G-ERP 取证"**，M2 定稿仅指"代码与文档一致"，不声称平台合同已验证。

- [ ] **Step 1: openapi.yaml 模式（schemas）回写**

(a) `AllocationRequest` 模式（`required: [lines, expectedVersion, reason]` 块）整体替换为：

```yaml
    AllocationRequest:
      type: object
      required: [expectedMatchVersion, lines]
      properties:
        expectedMatchVersion: { type: string, description: 'inbound_match.version 整数字符串（AT-067）；不匹配→409 VERSION_CONFLICT' }
        lines:
          type: array
          minItems: 1
          items:
            type: object
            required: [sourceLineId, upstreamLineId, quantity, unitRatio, codes]
            properties:
              sourceLineId: { type: integer, format: int64, description: 本地 source_line.id（内部 int64 代理，JSON number） }
              upstreamLineId: { type: integer, format: int64 }
              quantity: { type: number, description: 'decimal(18,6) JSON number；>6 位小数或 |v|≥10^12→422 QTY_DOMAIN；非正→QTY_NONPOSITIVE' }
              unitRatio: { type: string, description: '"n/d" 正整数；换算不精确→CONVERSION_INEXACT；换算后数量≠码数→QTY 校验失败' }
              conversionEvidence: { type: string, nullable: true, description: 比例≠1/1 时必填（CONVERSION_EVIDENCE_REQUIRED） }
              codes: { type: array, items: { type: string }, description: 须属该上游行、全 MIN、S8P20、集合内无重复/父子关系；大码→BIG_CODE_BLOCKED }
```

(b) `ConfirmAllocationRequest` 模式（`required: [allocationVersion, expectedVersion, reason]` 块）整体替换为：

```yaml
    ConfirmAllocationRequest:
      type: object
      required: [expectedMatchVersion]
      properties:
        expectedMatchVersion: { type: string, description: inbound_match.version 整数字符串 }
```

(c) `SettingView` 模式 `reason: { type: string, nullable: true }` 行后追加：

```yaml
        protectionScope: { type: string, nullable: true, description: DPAPI 保护作用域；密钥保存后为 CurrentUser，非密钥 null（§4.7） }
```

(d) `SyncJobRequest` 的 entity 行替换为：

```yaml
        entity: { type: string, enum: [sale, purchase-in, upstream, enterprise], description: 同步实体（有界枚举；upstream/enterprise 为 M2 新增） }
```

(e) `    SettingView:` 行之前插入 M2 响应模式块：

```yaml
    # ---- M2 已实现端点真实响应形状（T15 回写） ----
    AllocationSaveResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [matchVersion, allocationsSaved, allRequiredCovered]
          properties:
            matchVersion: { type: integer, description: 保存后 inbound_match.version }
            allocationsSaved: { type: integer }
            allRequiredCovered: { type: boolean, description: 全部 REQUIRED 行覆盖→COMPLETE，否则 PARTIAL }
        requestId: { type: string }
    ConfirmAllocationResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [taskId, billCode, matchVersion, reused, message]
          properties:
            taskId: { type: string, description: upload_task.id 字符串 }
            billCode: { type: string, description: 平台单据号（top.billPrefix 前缀，默认 TF；复用/重激活时不变） }
            matchVersion: { type: integer }
            reused: { type: boolean, description: true=复用既有任务或 Cancelled 重激活（单据号不变） }
            message: { type: string, description: 固定文案"符合资格后将自动上传" }
        requestId: { type: string }
    CancelAllocationRequest:
      type: object
      required: [reason]
      properties:
        allocationId: { type: integer, format: int64, nullable: true, description: null=取消全部暂存分配并回退发送前任务；单行 COMMITTED=整单发送前取消 }
        reason: { type: string }
    CancelAllocationResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [cancelled]
          properties:
            cancelled: { type: integer }
        requestId: { type: string }
    MappingResolveResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [mappingId, state, version, mappingVersion]
          properties:
            mappingId: { type: string }
            state: { type: string, description: 改绑成功恒 MANUAL_CONFIRMED（is_locked=1） }
            version: { type: string, description: 新 rowversion hex }
            mappingVersion: { type: integer }
        requestId: { type: string }
    ServiceAccountResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [serviceAccount, machineName]
          properties:
            serviceAccount: { type: string, description: '进程身份 DOMAIN\user（Windows 服务即运行账号，§4.7）' }
            machineName: { type: string }
        requestId: { type: string }
    TopBudgetsResponse:
      type: object
      required: [data, requestId]
      properties:
        data:
          type: object
          required: [budgets, pauses]
          properties:
            budgets:
              type: array
              items:
                type: object
                properties:
                  budgetKey: { type: string, description: 'appkey:total / method:{m} / ent:{ref}:method:{m}' }
                  dayCount: { type: integer }
                  dayLimit: { type: integer, nullable: true, description: null=本地不设帽（G-TOP 未取证不虚构限值） }
                  minuteCount: { type: integer }
                  minuteLimit: { type: integer, nullable: true }
                  secondCount: { type: integer }
                  secondLimit: { type: integer, nullable: true }
                  nextAllowedAtUtc: { type: string, format: date-time, nullable: true }
            pauses:
              type: array
              items:
                type: object
                properties:
                  id: { type: integer, format: int64 }
                  scopeKey: { type: string }
                  reason: { type: string }
                  isActive: { type: boolean }
                  pausedAtUtc: { type: string, format: date-time }
                  pausedBy: { type: string }
                  resumedAtUtc: { type: string, format: date-time, nullable: true }
                  resumedBy: { type: string, nullable: true }
        requestId: { type: string }
```

- [ ] **Step 2: openapi.yaml 路径（paths）回写**

(a) `/upstream-bills` GET 的 description 与 parameters 替换为（实现仅 billNo/completeness 两个过滤；旧桩的 upstreamRefEntId/dateStart/dateEnd 未实现，删除）：

```yaml
      description: 上游出库单列表（权限 trace:read，UP-01/02）。实现过滤：billNo 精确、completeness；按 fetched_at DESC,id DESC 排序。列表不提供"视为已入库"。
      parameters: [{ $ref: '#/components/parameters/Page' }, { $ref: '#/components/parameters/PageSize' },
        { name: billNo, in: query, schema: { type: string }, description: 单号精确等值 },
        { name: completeness, in: query, schema: { type: string, enum: [COMPLETE, INCOMPLETE] } }]
```

(b) `/upstream-bills/{id}` GET description 替换为：

```yaml
      description: 上游单详情（权限 trace:read）。行含 allocatedQuantity/remainingQuantity（RESERVED+COMMITTED 分配聚合，SQL 计算）；码含 codeNormalized/packageLevel（MIN=最小包装）。
```

(c) `  /sync-jobs:` 行之前插入补拉端点：

```yaml
  /upstream-bills/{id}/redownload:
    post:
      x-phase: M2
      description: 按条件补拉（权限 job:rematch，UI-04）。固定窗口 now-90d..now + billId（R10d）；202 入队 upstream-download；LIVE 环境 422 TOP_NOT_CERTIFIED（G-TOP 未取证）；意图含 nonce 避免终态重入队缺陷。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/IdempotencyKey' }
        - { $ref: '#/components/parameters/CsrfToken' }
      responses:
        '202': { description: 补拉作业已入队, content: { application/json: { schema: { $ref: '#/components/schemas/JobAccepted' } } } }
        '400': { $ref: '#/components/responses/BadRequest' }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
        '422': { description: LIVE 未取证（TOP_NOT_CERTIFIED）/缺幂等键（VALIDATION）, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
```

(d) `/sync-jobs` POST description 替换为：

```yaml
      description: 触发有界同步作业（权限 job:rematch）。entity 限 sale/purchase-in/upstream/enterprise；窗口 end>start、end≤now+5min、跨度≤90 天；202 入队（data.jobId 字符串）；LIVE 环境 sale/purchase-in→422 ERP_NOT_CERTIFIED、upstream/enterprise→422 TOP_NOT_CERTIFIED；通道暂停 422 CHANNEL_PAUSED；控制面审计 env=LIVE（T9 裁定）。
```

(e) `/inbounds` GET description 与 parameters 替换为（实现仅 matchState/docKey；旧桩 supplierId/billNo/dateStart/dateEnd 未实现，删除）：

```yaml
      description: 入库单列表（权限 trace:read）。每 docKey 取最新快照版本（ROW_NUMBER PARTITION）；含匹配态/匹配版本/原因/供应商 dwbh/任务态。
      parameters: [{ $ref: '#/components/parameters/Page' }, { $ref: '#/components/parameters/PageSize' },
        { name: matchState, in: query, schema: { type: string, enum: [NOT_REQUIRED, UNMATCHED, PARTIAL, AMBIGUOUS, CONFLICT, COMPLETE] } },
        { name: docKey, in: query, schema: { type: string }, description: 单据编号 djbh 精确等值 }]
```

(f) `/inbounds/{id}` GET description 替换为：

```yaml
      description: 入库单详情（权限 trace:read）。snapshot/lines（含 classification 与 allocatedQuantity）/match（matchState/version/reason）/allocations（RESERVED+COMMITTED，含码）/task（taskId/state/billCode）；冻结后前端只读（UI-05/11）。
```

(g) `/inbounds/{id}/rematch` 的 requestBody 块（`required: true` + TaskActionRequest 两行）替换为：

```yaml
      requestBody:
        required: false
        content: { application/json: { schema: { type: object, description: 实现不读取请求体；空对象即可 } } }
```

(h) `/inbounds/{id}/allocations` 的 '200' 行替换为：

```yaml
        '200': { description: 分配已保存（全量替换；RESERVED 暂存，不建任务）, content: { application/json: { schema: { $ref: '#/components/schemas/AllocationSaveResponse' } } } }
```

其 '422' 行替换为：

```yaml
        '422': { description: 'ALLOCATION_INVALID（fieldErrors 逐行 {sourceLineId,code,message}）：大码/换算不精确/缺证据/药冲突/批号冲突/供应商不符/码不属于行/格式/父子关系/数量域/重复行/上游行复用等；VALIDATION：空 lines', content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
```

其 '409' 行替换为：

```yaml
        '409': { description: 'VERSION_CONFLICT（expectedMatchVersion 过期，AT-067）/FROZEN（已发送）/CODE_CONTENDED（码占用争用，AT-032）', content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
```

(i) `/inbounds/{id}/confirm-allocation` 的 '200' 行替换为：

```yaml
        '200': { description: 资格复核通过→COMMITTED+任务创建/复用/重激活；提示"符合资格后将自动上传", content: { application/json: { schema: { $ref: '#/components/schemas/ConfirmAllocationResponse' } } } }
```

其 '422' 行替换为：

```yaml
        '422': { description: 'ALLOCATION_INVALID：复核失败逐项（LINE_UNCOVERED/QTY_COVERAGE/CLAIM_LOST 等，AT-031）；VALIDATION：缺 expectedMatchVersion', content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
```

(j) `  /sales:` 行之前插入取消端点：

```yaml
  /inbounds/{id}/cancel-allocation:
    post:
      x-phase: M2
      description: 取消分配（权限 inbound:allocate，IN-09）。allocationId=null→取消全部 RESERVED 并回退发送前任务；单行 RESERVED→取消该行释放占用；单行 COMMITTED→整单发送前取消。已发送/发送流程中→409 FROZEN（AT-034）；任务/分配/占用/outbox 同事务回退（AT-033）。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/IdempotencyKey' }
        - { $ref: '#/components/parameters/CsrfToken' }
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/CancelAllocationRequest' } } }
      responses:
        '200': { description: 取消完成, content: { application/json: { schema: { $ref: '#/components/schemas/CancelAllocationResponse' } } } }
        '400': { $ref: '#/components/responses/BadRequest' }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '409': { description: FROZEN（已发送或进入发送流程）, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
        '422': { description: VALIDATION（reason 必填/分配不属于该快照）, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
```

(k) `  /settings:` 行之前插入两个改绑端点：

```yaml
  /product-mappings/{id}/resolve:
    post:
      x-phase: M2
      description: 商品映射改绑（权限 master:match，M1 T12c 携带项）。仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 可改绑；targetId=platform_drug_id；成功→MANUAL_CONFIRMED+is_locked=1+历史+审计 master.mapping.resolve；双向唯一冲突→409 MAPPING_ACTIVE_CONFLICT。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/IdempotencyKey' }
        - { $ref: '#/components/parameters/CsrfToken' }
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/ConfirmMappingRequest' } } }
      responses:
        '200': { description: 改绑成功（锁定）, content: { application/json: { schema: { $ref: '#/components/schemas/MappingResolveResponse' } } } }
        '400': { $ref: '#/components/responses/BadRequest' }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
        '409': { description: 版本冲突/目标已被有效映射占用（MAPPING_ACTIVE_CONFLICT）, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
        '422': { description: STATE_NOT_RESOLVABLE/目标平台药不存在/参数缺失, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
  /supplier-mappings/{id}/resolve:
    post:
      x-phase: M2
      description: 供应商映射改绑（权限 master:match）。targetId=ref_ent_id（稳定主体键），取最新企业版本；统一社会信用代码须精确一致（否则 422 CREDIT_MISMATCH）；成功→MANUAL_CONFIRMED+锁定+历史+审计。
      parameters:
        - { $ref: '#/components/parameters/ResourceId' }
        - { $ref: '#/components/parameters/IdempotencyKey' }
        - { $ref: '#/components/parameters/CsrfToken' }
      requestBody:
        required: true
        content: { application/json: { schema: { $ref: '#/components/schemas/ConfirmMappingRequest' } } }
      responses:
        '200': { description: 改绑成功（锁定）, content: { application/json: { schema: { $ref: '#/components/schemas/MappingResolveResponse' } } } }
        '400': { $ref: '#/components/responses/BadRequest' }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
        '404': { $ref: '#/components/responses/NotFound' }
        '409': { description: 版本冲突/企业版本已被有效映射占用（MAPPING_ACTIVE_CONFLICT）, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
        '422': { description: STATE_NOT_RESOLVABLE/CREDIT_MISMATCH/目标企业不存在/参数缺失, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
```

(l) 文件末尾（`/settings/{key}` PUT 块之后）追加：

```yaml
  /settings/service-account:
    get:
      x-phase: M2
      description: 服务运行账号与机器名（权限 system:manage，§4.7）。进程身份 Environment.UserDomainName\UserName；换机/恢复备份后 DPAPI 密钥不可解密须重新录入。
      responses:
        '200': { description: 运行账号, content: { application/json: { schema: { $ref: '#/components/schemas/ServiceAccountResponse' } } } }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
  /top-budgets:
    get:
      x-phase: M2
      description: 限流预算用量与授权暂停只读视图（权限 system:manage，§4.7/JOB-03/05）。限值 null=本地不设帽；暂停恢复须人工动作（M3 交付入口）。
      responses:
        '200': { description: 预算与暂停, content: { application/json: { schema: { $ref: '#/components/schemas/TopBudgetsResponse' } } } }
        '401': { $ref: '#/components/responses/Unauthorized' }
        '403': { $ref: '#/components/responses/Forbidden' }
```

(m) `/settings` GET description 末尾追加一句：`M2 起含 protectionScope（密钥=CurrentUser）。`

校验 YAML 合法性（node 一行式，仓库根执行）：

```bash
cd "/e/Viding/Retail Trace Code" && node -e "const fs=require('fs');const s=fs.readFileSync('docs/contracts/openapi.yaml','utf8');if(!s.includes('AllocationSaveResponse')||!s.includes('/top-budgets'))throw new Error('回写缺失');console.log('openapi 回写标记齐全，行数',s.split('\n').length)"
```

（若本机有 js-yaml 可用则做完整 parse；无则以上标记检查 + 人工缩进复核为准，不新增依赖。）

- [ ] **Step 3: data-dictionary / ksoa-adapter-contract / top-contract 增补**

(a) `docs/contracts/data-dictionary.md` 在 `## 环境隔离（Live/Mock）` 小节**之前**插入（锚点即该标题行；0007 小节之后）：

```markdown
## 0008 迁移增量（M2，`0008_m2_upstream_allocation.sql`）

不修改 0001–0007 任何字节；0008 以 CREATE/ALTER 增量演进：

| 对象 | 变更 | 用途/约束 |
| --- | --- | --- |
| inbound_match | 新表 | 入库单匹配维度（IN-01..09）：`UQ_inbound_match`(org_id,env,source_snapshot_id)；match_state CHECK NOT_REQUIRED/UNMATCHED/PARTIAL/AMBIGUOUS/CONFLICT/COMPLETE；version int 乐观锁（AT-067）；supplier_dwbh BIN2 NULL；evidence_json 留证 |
| top_rate_budget | 新表 | 持久限流预算（JOB-03/05）：`UQ_rate_budget`(env,budget_key BIN2)；日/分/秒计数+窗口起点+next_allowed_at；limit NULL=本地不设帽（G-TOP 取证前不猜平台限值）；重启不清零 |
| top_channel_pause | 新表 | 授权范围暂停（FAIL_BIZ_AUTH_ERROR→按接口/主体/凭证暂停）：`UX_pause_active`(env,scope_key) WHERE is_active=1；恢复须人工动作留 resumed_by |
| download_progress.window_start/window_end | datetime2(3) NULL | 上游下载按窗口记录列表/详情分页进度（SYNC-03/04；详情未全部成功不得 COMPLETE） |
| upload_task.CK_task_state / upload_part.CK_part_state | CHECK 增加 N'Cancelled' | 行政终态（IN-09）：仅 (Blocked|Ready, AllocationCancelled)→Cancelled；任何 UPLOAD 尝试后不可达；不计入平台成功/失败口径 |

Cancelled 为纯本地行政终态，非平台状态；`UploadStateMachine` 仅增两条边（UploadStateMachineTests 全边覆盖含新边）。
```

(b) `docs/contracts/ksoa-adapter-contract.md` 文件末尾追加：

```markdown

## 5. M2 增补：采购单头供应商编号候选列（ywdjbh）

- 用途：入库匹配按供应商过滤上游单（R8b）；`inbound_match.supplier_dwbh` 来源。
- 候选：`jh_rkhz.ywdjbh`（业务单位编号）。**未取证（G-ERP）**：列名、语义（供应商 vs 部门）、与 supplier_map.dwbh 的对应关系均待真实库验证。
- M2 实现口径：仅从 `source_snapshot.raw_header` JSON 读取 `ywdjbh` 字段（fixture 提供）；`ISourceAdapter` 接口不变、不新增查询列；取不到→supplier_dwbh=NULL，匹配走 CONFLICT/人工路径，不猜供应商。
- 取证动作（并入 §3 清单执行）：真实 jh_rkhz 采样确认列存在与语义→更新 adapter-facts.md 证据列→source-mapping.md 固定映射→RealKsoaAdapter 落实。取证前 LIVE 采购同步保持 ERP_NOT_CERTIFIED 门禁。
```

(c) `docs/contracts/top-contract.md`（T4 创建）文件末尾追加：

```markdown

## M2 定稿状态（Task 15）

- 本文件所列接口入参/签名/限流限值/回执语义仍为**候选（_verified:false）**，M2 全程 fixture 通道验证，未取得平台合同证据（G-TOP）。
- M2 新增消费面：listupout / listupout.detail（上游下载，Task 5）、query.getbyrefentid / query.getentinfo（企业同步，Task 6）；fixture 见 `docs/contracts/fixtures/top/`。
- uploadinoutbill 102 载荷组装（Task 7 InboundTaskFactory）按候选合同生成并持久化；真实发送在 M3 upload-dispatch 落地且 G-TOP 取证前保持 TOP_NOT_CERTIFIED 禁用。
- 定稿条件：按设计附录 B 清单逐项平台测试工具核实并留存证据后，将对应行 _verified 置 true 并记录取证日期；未核实项不得进入 LIVE。
```

- [ ] **Step 4: 追踪矩阵 matrix.md 增补 M2 行**

`docs/traceability/matrix.md` 修改：

1. 标题行 `# TraceFlow 需求追踪矩阵（M0–M1）` → `# TraceFlow 需求追踪矩阵（M0–M2）`。
2. 基线段 `代码基线：分支 \`m1-source-master\`（自 m0-foundation），HEAD 以 M1 末提交为准（Task 1–16）。` → `代码基线：分支 \`m2-upstream-allocation\`（自 m1-source-master），HEAD 以 M2 末提交为准（Task 1–15）。`
3. 验证运行段更新为 M2 实测数字（Step 7 跑完后回填，禁止预填；格式沿用 M1：`Release 构建 0 警告 0 错误；单元测试 <N>/<N> 通过；集成测试默认 collation <N>/<N> + BIN2 变体 <N>/<N>（2019 降级）；前端 tsc -b && vite build 0 错误（<M> modules）`），并把 `docs/delivery/M1-gate-report.md` 引用改为 `docs/delivery/M1-gate-report.md`、`docs/delivery/M2-gate-report.md` 并列。
4. 既有行更新（仅下列行，其余不动）：
   - SCOPE-06：实现列改为 `InboundMatchService + AllocationService + InboundTaskFactory（0008 inbound_match；行级分配+资格冻结+bill_code 持久化）`；测试列 `InboundMatchTests.Unique_full_solution_auto_creates_task；AllocationConfirmTests.Confirm_commits_allocations_and_creates_ready_task（AT-024/031）`；状态 `M2 fixture 通道完成；LIVE 发送待 M3+G-TOP`。
   - G-TOP 行：实现列追加 `+ docs/contracts/top-contract.md（M2 定稿状态节）`；测试列 `TopGatewayTests（5 用例）；TopClientServiceTests（4 用例）；UpstreamDownloadTests.Live_env_rejected_as_not_certified`；仍"未取证"。
   - 其余 M2 相关既有行按下表逐一更新（**不新增需求 ID 行**——矩阵行语义以 PRD 为准：IN-01=触发/重排入队、IN-02=外部单号优先+7 天窗口、IN-03=AMBIGUOUS 阻断、IN-04=行级/码级分配、IN-05=分类门槛、IN-06=资格检查、IN-07=自动建任务/确认重验、IN-08=同事务入队/占用、IN-09=取消释放；UI-04/05 原标 M4，M2 提前交付页面本体，实际上传发送仍 M3）；M3/M4 计划行保持不动。
5. 覆盖统计节：行合计保持 **114**（无新增行）；追加一条：`- M2 更新 32 个既有需求行（上游下载/企业同步/限流预算/授权暂停/入库匹配/行级分配/确认冻结/取消回退/改绑/设置加固/两页 UI），均给出实际测试名；G-ERP/G-TOP 仍未取证，LIVE 通道保持禁用；2008 R2 运行时门禁（AT-070）仍未满足。`

M2 既有行更新表（证据列统一 `SQL 2019 集成（授权降级；2008 R2 待补验）`，涉及平台调用的行另加 `G-TOP 未取证（fixture-only）`，涉及 KSOA 的行另加 `G-ERP 未取证（fixture-only）`；"追加"= 在既有实现/测试列后补充，不删除 M0/M1 已有内容）：

| 需求 ID | 实现列（替换或追加） | 测试列（实际名） | 阶段/状态 |
| --- | --- | --- | --- |
| SYNC-03 | 替换"分页落库同事务逻辑未实现"→ `UpstreamDownloadService 列表/详情分页进度同事务 + 0008 download_progress.window_start/end` | UpstreamDownloadTests.Full_download_completes_bills_and_enqueues_match_same_bill_no_different_subjects_not_merged；Detail_page2_failure_marks_incomplete_never_complete；Duplicate_list_page_never_fakes_complete；Total_count_mismatch_never_fakes_complete（AT-006/022 fixture 通道） | M2 fixture 完成；LIVE 待 G-TOP |
| SYNC-04 | 追加 `+ upstream/enterprise/inbound-match 调度桶（upstream.pollSeconds；暂停时 inbound-match 仍入队）` | 追加 UpstreamDownloadTests.Handler_reads_payload_and_runs；Rate_limit_midrun_keeps_page1_progress_for_next_run；InboundMatchTests.Scheduler_enqueues_inbound_match_even_when_paused | M1 完成 + M2 扩展 |
| ENT-02 | 追加 `+ EnterpriseSyncService（getbyrefentid/getentinfo 下载，改名追加版本/角色分离/同主体幂等）` | 追加 EnterpriseSyncTests.Rename_creates_new_ent_version_and_old_row_untouched；Roles_stored_separately_and_no_supplier_mapping_auto_created；Same_identity_twice_does_not_duplicate_or_touch_observed_at；Live_env_rejected_and_handler_kind_correct；Page_failure_throws_for_job_backoff_no_partial_fake | M1 完成 + M2 fixture 下载 |
| UP-01 | 追加 `+ listupout 下载（UpstreamDownloadService）+ GET /upstream-bills（billNo/completeness 过滤）+ POST /{id}/redownload（LIVE 422 TOP_NOT_CERTIFIED）` | 追加 UpstreamDownloadTests.Full_download_completes_bills_and_enqueues_match_same_bill_no_different_subjects_not_merged；Live_env_rejected_as_not_certified；Missing_store_identity_skips_with_audit；InboundApiTests.Upstream_bills_list_detail_and_redownload（AT-022/023 fixture 通道） | M2 fixture 完成 |
| UP-02 | 追加 `+ listupout.detail 码级保存 + GET /upstream-bills/{id}（allocatedQuantity/remainingQuantity SQL 聚合，R13a）` | 追加 UpstreamDownloadTests.Detail_page2_failure_marks_incomplete_never_complete；InboundApiTests.Upstream_detail_shows_allocated_and_remaining | M2 fixture 完成 |
| IN-01 | 替换"（M2 计划…）"→ `POST /inbounds/{id}/rematch（202 jobId）+ InboundMatchJobHandler + up:/map:/periodic 触发（R8b/R8c：src: 不接线，周期扫描兜底）+ R8a 既有任务处置（已发送冻结/hash 变更重建）` | InboundApiTests.Rematch_enqueues_job；InboundMatchTests.Scheduler_enqueues_inbound_match_even_when_paused；Sent_task_frozen_on_rematch；New_snapshot_version_rebuilds_pre_send_task_same_bill_code | M2 完成（AT-024 触发面） |
| IN-02 | 替换→ `R8e 单据候选：supplier_mapping→ref_ent_id 过滤 + to_ent_id 排除 + ywdjbh 外部单号精确优先（候选字段，G-ERP 未取证）+ bill_date ±7 天窗口；药+数量相符批号不同→CONFLICT（AT-025）` | InboundMatchTests.Unique_full_solution_auto_creates_task；External_no_hit_but_batch_conflict；One_line_unmatched_partial | M2 fixture 完成 |
| IN-03 | 替换→ `R8d 唯一全解判定：每 REQUIRED 行恰一候选且上游行互不重复→COMPLETE；多候选/争用→AMBIGUOUS 不自动挑选` | InboundMatchTests.Multiple_candidates_ambiguous_no_auto_pick（AT-026） | M2 完成 |
| IN-04 | 替换→ `AllocationService.SaveAsync 行级/码级多对多分配（RESERVED 全量替换；大码本期不拆零→阻断；换算精确+证据；批号/药冲突校验）+ AllocationEditor` | AllocationServiceTests.Save_valid_reserves_claims_and_completes_match；Save_big_code_blocked；Save_conversion_inexact_rejected；Save_conversion_count_mismatch_rejected；Save_nonunit_ratio_requires_evidence；Save_drug_conflict_rejected；Save_replaces_previous_reserved（AT-027/028） | M2 完成 |
| IN-05 | 替换→ `R8f 分类门槛：全部 EXEMPT→NOT_REQUIRED 0 任务；任一 UNKNOWN/档案缺失→UNMATCHED；REQUIRED 数量≤0→ingest_exception` | InboundMatchTests.All_exempt_lines_not_required_zero_tasks；Unknown_classification_unmatched；Nonpositive_required_qty_writes_ingest_exception（AT-030） | M2 完成 |
| IN-06 | 替换→ `ConfirmAsync 资格复核（覆盖/数量/占用/映射版本逐项重验，AT-031）+ expectedMatchVersion 门禁（AT-067）` | AllocationConfirmTests.Confirm_rechecks_eligibility_after_tamper；Confirm_partial_coverage_rejected；Confirm_stale_version_conflict（AT-029/031/067） | M2 完成 |
| IN-07 | 替换→ `唯一全解自动建 102 任务 + 确认补配 COMMITTED→InboundTaskFactory（bill_code 生成即持久/复用/Cancelled 重激活）；确认提示"符合资格后将自动上传"，无手动上传按钮（R13b）` | InboundMatchTests.Unique_full_solution_auto_creates_task；AllocationConfirmTests.Confirm_commits_allocations_and_creates_ready_task；Confirm_after_cancel_reactivates_same_bill_code；Confirm_frozen_when_sent；InboundTaskFactoryTests.Create_reuses_existing_task_unchanged；Cancelled_task_reactivates_same_row_with_stable_bill_code；BillContractTests.Default_contract_is_TF_32；Prefix_setting_rejects_non_string；Over_length_bill_code_throws_without_truncation | M2 fixture 完成（发送 M3+G-TOP） |
| IN-08 | 追加 `+ 资格/冻结/快照同事务落地：CodeClaimWriter ACTIVE 占用 + CreateAsync 任务/part/outbox/绑定同事务（T2）` | ClaimAndCancelTests.Second_active_claim_same_code_and_scope_is_rejected；InboundTaskFactoryTests.Create_atomic_rollback_leaves_nothing；Create_writes_task_part_outbox_event_and_binds_claim；AllocationServiceTests.Save_code_contended_409（AT-032） | M2 完成 |
| IN-09 | 替换→ `CancelAsync + InboundCancelService：任务/分配/占用/outbox 同事务回退；已发送/发送流程中→409 FROZEN；0008 Cancelled 行政终态（仅 Blocked/Ready 可达）` | ClaimAndCancelTests.Cancel_pre_send_rolls_back_everything_atomically；Cancel_refused_after_upload_attempt；Cancel_refused_when_dispatch_job_running；Cancel_refused_when_task_dispatching；Cancel_without_task_returns_false；AllocationServiceTests.Cancel_reserved_releases_claims_and_unmatches；InboundApiTests.Cancel_allocation_flow；Migration0008Tests.Task_and_part_state_checks_accept_cancelled（AT-033/034） | M2 完成 |
| TOP-01 | 替换"（M2/M3 计划…）"→ `TopResponseParser 双层解析（error_response+业务子码）+ TopClientService 脱敏留证 + Fake/RealTopGateway/TopGatewayFactory/TopFixtureHandler` | TopResponseParserTests（12 用例，名单见下注）；TopGatewayTests.Fake_gateway_rejects_live_env；Real_gateway_is_not_certified_placeholder；Factory_unknown_mode_throws；Fixture_handler_matches_params_and_extracts_error_node；Fixture_handler_missing_file_defaults_success_and_transport_entry；TopClientServiceTests.Success_increments_budget_and_returns_outcome；Auth_error_pauses_scopes_audits_and_blocks_next_call_without_http；Rate_limit_registers_platform_wait_seconds_priority；Live_env_through_fake_gateway_is_not_certified | M2 fixture 完成；G-TOP 未取证 |
| IDEM-01 | 追加 `+ event_key=KSOA:{docKey}:PURCHASE_IN 生成落地（InboundTaskFactory）；Cancelled 重激活同 event_key 同 bill_code` | 追加 InboundTaskFactoryTests.Create_reuses_existing_task_unchanged；Cancelled_task_reactivates_same_row_with_stable_bill_code | M2 完成 |
| IDEM-02 | 追加 `+ top.billPrefix/top.billMaxLength 合同配置（默认 TF/32；超长抛错不截断）` | 追加 BillContractTests.Default_contract_is_TF_32；Prefix_setting_rejects_non_string；Over_length_bill_code_throws_without_truncation | M2 完成 |
| IDEM-03 | 追加 `+ 单 part 创建（part_index=0/code_set_hash/payload_hash 持久化）；多 part 拆分仍 M3（限额 G-TOP 取证后）` | 追加 InboundTaskFactoryTests.Create_writes_task_part_outbox_event_and_binds_claim；SourceValueObjectTests.Code_set_hash_order_insensitive；Code_set_hash_delimiter_does_not_collide；Code_set_hash_empty_set_stable | M2 部分（单 part）；拆分 M3 |
| JOB-03 | 替换"（M3 计划…）"→ `RateBudgetService 持久预算（日/分/秒计数+窗口+next_allowed_at，重启不清零；Live/Mock 隔离；限值 null=本地不设帽，G-TOP 取证后填实）+ 退避改期` | RateBudgetTests.Acquire_increments_counters_and_persists_across_connections；Second_limit_exhaustion_returns_wait_without_consuming；Window_roll_resets_counter；Next_allowed_wait_and_register_wait_max_semantics；Live_and_mock_budgets_isolated；RateBackoffTests.Backoff_formula；Backoff_jitter_within_0_to_5_seconds；TopClientServiceTests.Rate_limit_registers_platform_wait_seconds_priority；UpstreamDownloadTests.Rate_limit_midrun_keeps_page1_progress_for_next_run（AT-060 本地保障；平台限值待取证） | M2 完成（限值未取证） |
| JOB-05 | 追加 `+ ChannelPauseService 授权范围暂停（FAIL_BIZ_AUTH_ERROR→按接口/主体/凭证）+ sync.paused fail-closed（M1 T6e 携带项闭环）` | 追加 ChannelPauseTests.Pause_idempotent_scope_isolated_resume_and_repause；SettingsFailClosedTests.Garbage_pause_value_fails_closed；Boolean_false_stays_open；TopClientServiceTests.Auth_error_pauses_scopes_audits_and_blocks_next_call_without_http | M1 完成 + M2 加固 |
| UI-04 | 替换"（M4 计划…）"→ `UpstreamBillsPage（列表/详情码级/补拉；数量后端聚合 R13a；无"视为已入库"R13b）` | npm run build（tsc 0 错误）；浏览器验证清单第 1–3 点 | M2 完成（页面）；原 M4 归属提前 |
| UI-05 | 替换→ `InboundsPage（列表/详情/AllocationEditor/确认/取消；冻结只读 R13c；409 自动刷新 R13d；202 jobId 提示 R13e）` | InboundApiTests.Save_confirm_and_list_flow；Inbound_detail_line_shows_allocated_quantity；npm run build；浏览器验证清单第 4–8 点 | M2 完成（页面）；实际发送 M3 |
| UI-10 | 追加 `+ §4.7 加固：protectionScope 列 + 服务运行账号面板 + /top-budgets 预算/暂停只读视图（R14b/R14c）` | 追加 SettingsScopeApiTests.Secret_setting_exposes_protection_scope；Service_account_endpoint_returns_process_identity；Top_budgets_endpoint_lists_usage_and_pauses | M1 完成 + M2 加固 |
| UI-11 | 追加 `+ ApiError.fieldErrors 逐行 422 显示 + 分配 409 自动刷新（AT-067）+ ResolveDialog（商品/供应商改绑，R14a）` | AllocationServiceTests.Save_big_code_blocked（422 fieldErrors 后端）；浏览器验证第 5/6 点；npm run build | M2 完成 |
| DB-01 | 追加 `+ 0008（inbound_match/top_rate_budget/top_channel_pause；BIN2 键列；Cancelled CHECK）` | 追加 Migration0008Tests.M2_tables_exist_with_expected_columns；Inbound_match_unique_per_snapshot_and_state_check；Rate_budget_unique_per_env_key_and_pause_filtered_unique；Migration_0008_recorded_with_checksum | M2 完成（2008 R2 待补验） |
| DB-03 | 追加 `+ 0008 入扫描白名单` | Sql2008CompatibilityTests（含 0008 文本扫描） | 语法层通过；运行时待补验 |
| MATCH-04 | 追加 `+ 供应商解除守卫 dwbh 级（M1 携带项 2 闭环）` | 追加 M2CarryforwardApiTests.Supplier_disable_guard_is_dwbh_scoped | M1 完成 + M2 细化 |
| MATCH-05 | 追加 `+ POST /product-mappings/{id}/resolve + /supplier-mappings/{id}/resolve（CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED→MANUAL_CONFIRMED 锁定；M1 T12c 携带项闭环）+ ResolveDialog` | 追加 M2CarryforwardApiTests.Resolve_product_rebinds_conflict_mapping；Resolve_supplier_credit_mismatch_422；Resolve_active_mapping_rejected | M2 完成 |
| MD-01 | 追加 `+ trace-policy 接线：org 过滤 + 发送前任务 CancelPreSend + inbound-match 重入队；已发送不动（AT-057；M1 携带项 3/5 闭环）` | 追加 M2CarryforwardApiTests.Trace_policy_cancels_presend_task_and_enqueues_rematch；Trace_policy_sent_task_untouched | M1 完成 + M2 接线 |
| AUTH-01 | 追加 `+ M2 端点逐请求权限/CSRF/幂等键门禁` | 追加 M2ApiGateTests.Anonymous_gets_401_on_all_m2_endpoints；Viewer_reads_but_cannot_mutate；Admin_mutations_require_csrf_and_idempotency_key；Malformed_json_body_returns_400 | M0 完成 + M2 扩展 |

注：以上测试名全部来自本计划 Task 1–14 的测试代码（已逐一核对）；执行时若实现期对测试名做了改动，矩阵须同步改为实际名（R15a：矩阵不得引用不存在的测试）。TopResponseParserTests 12 用例：Transport_failure_is_uncertain_never_success / Success_keeps_result_raw_and_request_id / Error_envelope_auth_token_maps_auth_error / Error_envelope_rate_prefix_maps_rate_limited_with_retry_after / Error_envelope_other_maps_system_error / Business_failure_is_never_success / Missing_response_success_flag_is_business_rejected / Partial_success_token_maps_partial_evidence_even_when_flag_false / Duplicate_bill_code_sub_token_maps_duplicate / Business_level_auth_and_rate_tokens_also_mapped / Unparseable_json_is_system_error_not_success / Empty_envelope_is_system_error。

- [ ] **Step 5: 写 M2 裁决与携带项文档**

Create `docs/delivery/M2-rulings-and-carryforwards.md`（完整内容如下；执行期如有新裁决须追加，不得删改既有条目）：

```markdown
# M2 裁决与携带项（交接 M3）

基线：spec 2026-11-22 + PRD V1.0 + 验收清单；计划 2026-11-23-m2-upstream-allocation.md（Task 1–15）。冲突优先级：用户后续明确确认 > PRD 已确认业务要求 > PRD 默认方案 > 旧规划/原型。

## 裁决（执行期如偏离须记录新裁决）

1. **Cancelled 行政终态（IN-09）**：0008 为 upload_task/upload_part CHECK 增加 N'Cancelled'；状态机仅增 (Blocked,AllocationCancelled)→Cancelled、(Ready,AllocationCancelled)→Cancelled 两条边。纯本地行政终态：任何 UPLOAD 尝试后不可达；不是平台状态，不计入平台成功/失败口径。
2. **R7a Cancelled 复活**：UQ_task_event(org,env,event_key) 禁止同事件键第二任务；Cancelled 且从未有 UPLOAD 尝试的任务按原行重激活（state→Blocked/Ready，bill_code 不变，Reused=false）；有尝试记录的一律走受理未知/回查路径，不得复活。
3. **R7b mapping_version_snapshot**：nvarchar(200) 存"映射版本对 Ordinal 排序拼接"的 SHA256 大写 hex（64 字符）；版本对格式 product:{spid}:{version} / supplier:{dwbh}:{version}。
4. **R7c payload_json**：内部规范化 DTO（事件键/供应商身份/行/码集），不是平台请求参数；G-TOP 取证后 M3 由 DTO 构造平台参数。payload_hash=DTO JSON SHA256 大写 hex。
5. **R8a 既有任务处置**：已发送（有 UPLOAD 尝试）或状态不在 {Blocked,Ready,Cancelled}→冻结（match=COMPLETE 注明原因，不动任务）；pre-send 且 source_hash==快照 content_hash→无操作；pre-send 且 hash 不同→按旧任务自己的 snapshot_id CancelPreSend 后重建（bill_code 不变）。
6. **R8b 触发语义**：up:/map:/periodic 一律跑待匹配扫描；仅携带 snapshotId 的 rematch 定向单快照；billId 只作审计线索不反查。
7. **R8c 不接线 src: 触发**：不改 M1 SourceSyncService 构造；新 COMPLETE 快照由 ≤5 分钟周期扫描兜底；src: 意图命名空间保留未用。
8. **R8d 自动匹配证据门槛**：单位相同（1/1）、批号 Trim+Ordinal 相等且非空、数量精确相等、上游行码集全 MIN 包装级（候选值）+S8P20、码数==数量、任一码无 ACTIVE PURCHASE_IN 占用、码集内无父子边；每 REQUIRED 行恰一候选且上游行互不重复→COMPLETE+自动建任务；多候选/争用→AMBIGUOUS 不自动挑选；部分无候选→PARTIAL；全无→UNMATCHED。
9. **R8e 单据候选**：supplier_mapping active→ref_ent_id 过滤 COMPLETE 上游单；to_ent_id 非空且≠org.ent_id 排除；raw_header.ywdjbh（候选字段，G-ERP 未取证）→bill_no 精确命中，未命中→UNMATCHED；命中但"药+数量相符批号不同"→CONFLICT（AT-025）；无 ywdjbh→bill_date ±7 天窗口（业务时间缺失→UNMATCHED）。
10. **R8f 分类门槛**：任一来源行档案缺失或 UNKNOWN→UNMATCHED；全部 EXEMPT→NOT_REQUIRED 0 任务；REQUIRED 数量≤0→ingest_exception（QTY_NONPOSITIVE，OPEN 去重）+UNMATCHED。
11. **R9a 保存=全量替换**：SaveAsync 先撤销该快照全部 RESERVED（释放占用）；存在发送前任务先 CancelPreSend；已发送任务存在→409 FROZEN 拒绝保存。
12. **R9b 保存后匹配态**：全部 REQUIRED 行覆盖→COMPLETE 否则 PARTIAL；保存不建任务（任务只在确认时创建）。
13. **R9c 校验失败语义**：行级错误收集 fieldErrors {sourceLineId,code,message}，一次性 422 ALLOCATION_INVALID；不做部分保存。
14. **R9d 版本闸**：ExpectedMatchVersion（int 字符串）≠inbound_match.version→409 VERSION_CONFLICT（AT-067）；占用争用→409 CODE_CONTENDED（AT-032）。
15. **R10a 确认时任务态**：Confirm 时存在 Blocked/Ready 任务（并发自动匹配产物）→409 VERSION_CONFLICT 要求刷新；已发送→409 FROZEN。
16. **R10b factory 注入**：ConfirmAsync 以参数接收 InboundTaskFactory（端点从 DI 取）；AllocationService 构造保持 (IAuditLog)。
17. **R10c 确认复核（AT-031）**：Confirm 对每条分配重跑与 Save 相同的资格闸；复核失败逐项 422，不建任务。
18. **R10d redownload 窗口**：显式 start=now-90d、end=now + onlyBillId；LIVE→422 TOP_NOT_CERTIFIED；意图含 nonce 避免终态重入队缺陷（M1 携带项 1 的 M2 局部闭环）。
19. **R11a fail-closed**：sync.paused JSON 解析非 False 一律视为暂停（M1 T6e 携带项闭环）。
20. **R11b 改绑范围**：resolve 仅受理 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED；已确认态走 confirm/disable；供应商 targetId=ref_ent_id 取最新企业版本，信用代码 Ordinal 精确一致否则 422 CREDIT_MISMATCH（M1 T12c 携带项闭环）。
21. **R11c 分类接线**：分类变更后受影响发送前任务逐个 CancelPreSend（auditEnv 按任务环境）+按环境重入队 inbound-match；已发送任务不动（AT-057；M1 携带项"trace-policy 接线/affectedNonTerminalTasks 恒 0"闭环）。
22. **R11d 供应商解除守卫**：org 级计数改为 inbound_match.supplier_dwbh 关联计数（排除终态；M1 携带项 2 闭环）。
23. **R11e 请求体上限**：Kestrel MaxRequestBodySize=104_857_600（与 import.maxFileBytes 上界一致）；ImportStage 审计 env 改按作业环境（M1 携带项 2/3 闭环）。
24. **R13a 数量口径**：allocatedQuantity/remainingQuantity 一律后端 SQL 聚合（RESERVED+COMMITTED），前端仅 round6 显示，不做前端 decimal 运算。
25. **R13b 无捷径**：无"视为已入库"、无手动上传按钮；确认提示"符合资格后将自动上传"。
26. **R13c 冻结判定（UI）**：task 存在且 state∉{Blocked,Ready,Cancelled}→只读；仅 UI 提示，后端每次写操作独立复核。
27. **R13d 冲突提示**：409→"版本冲突或已冻结：数据可能已被他人修改，请刷新后重试"+自动重载（AT-067）；422 fieldErrors 逐行显示。
28. **R13e 202 语义**：rematch/redownload 返回 jobId 提示，不轮询作业结果（作业状态页 M4）。
29. **R14a 改绑入口**：ResolveDialog 按钮仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 且有映射行时显示。
30. **R14b 设置目录**：新 catalog 键（top.billPrefix/top.billMaxLength/upstream.pollSeconds 等）自动出现在设置表，无需逐键 UI。
31. **R14c 预算展示**：限值 NULL 显示"—（不设帽）"；暂停列表只读（恢复动作 M3 交付）。
32. **R15a 门禁诚实**：门禁报告只写真实运行输出；2008 R2 降级声明沿用 M0/M1 口径；矩阵不得引用不存在的测试。
33. **R15b 契约以实现为准**：openapi 旧桩（AllocationRequest.sourceLineKey/expectedVersion、ConfirmAllocationRequest.allocationVersion、LIST_ONLY/DETAIL_PARTIAL 等）按 M2 实现回写，不改实现迁就旧桩。
34. **R15c 合同候选**：top-contract.md 全部条目仍 _verified:false；G-TOP/G-ERP 取证前 LIVE 通道保持禁用。

## 携带项（M3 必须处理）

- upload-dispatch / bill-query 处理器（M0 表结构与恢复路径就绪；M2 任务只到 Blocked/Ready，不发送）。
- 逐码结果（含 UNKNOWN）/部分成功受控补救/受理未知回查决策（spec §2.6 转换表全边落地）。
- 渠道暂停恢复 UI 与审批动作（M2 只读展示；ChannelPauseService.ResumeAsync 已就绪）。
- 限流限值实数：G-TOP 取证后填 top_rate_budget day/minute/second_limit（M2 全部 NULL=不设帽，仅本地退避保障）。
- 消费者退货 116 / 采购退货 202 前序校验与首次人工确认（G-RETURN）。
- 零售核验管道（绑定来源版本/码集/规则版本；UNKNOWN 阻断；EXEMPT 按依据跳过）。
- 多 part 拆分（限额取证后；M2 单 part，超长 bill_code 抛错不截断）。
- src: 触发接线（SourceSyncService 注入 IJobQueue 的构造变更，M3 随零售管道一并做）。

## 携带项（M4 / 交付前）

- ImportWizard 预览分页（M1 携带项 4，M2 未做，如实保留"仅展示前 50 行"标注）。
- 2008 R2 真实实例补验全部集成测试（AT-070；替换 M0/M1/M2 门禁报告 ①③ 证据）。
- G-ERP 取证清单（ksoa-adapter-contract.md §3 + §5 ywdjbh）；G-TOP 合同验证（设计附录 B）；G-POS/G-RETURN/G-OPENING/G-DEPLOY。
- 十页闭环剩余页（POS 销售/退货/码台账/上报对账/工作台增强/审计导出）、日对账三方比对、故障注入与连续运行 ≥7 天试运行、备份恢复演练。
- 作业状态页（202 jobId 的后续查询入口）。

## 环境事实（不声称未验证能力）

- 集成测试运行于本机 SQL Server 2019（15.0.2000.5），TRACEFLOW_TEST_DB_ALLOW_NEWER=1 用户授权降级；双 collation（默认 + TRACEFLOW_TEST_DB_BIN2=1）各跑一遍。
- 上游下载/企业同步/102 载荷全部 fixture 通道（fixtures/top/*，_verified:false）；RealTopGateway 抛 TopNotCertifiedException；LIVE 环境 upstream/enterprise/redownload 一律 422 TOP_NOT_CERTIFIED。
- KSOA 侧 fixture-only（FixtureKsoaAdapter）；ywdjbh 为候选字段仅从 raw_header JSON 读取；RealKsoaAdapter 仍抛 AdapterNotCertifiedException。
- 浏览器人工验证使用独立库 TraceFlow_M2Manual（保留不删）；若执行环境无浏览器工具，须在门禁报告如实声明"浏览器验证未执行"，不得以构建通过冒充 UI 验证。
```

- [ ] **Step 6: 全量回归（双 collation，真实运行）**

仓库根依次执行并**记录真实输出**（供 Step 7 回填；任何失败先修复再重跑，不得带失败写报告）：

```bash
cd "/e/Viding/Retail Trace Code"
dotnet build traceflow/TraceFlow.sln -c Release
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 TRACEFLOW_TEST_DB_BIN2=1 dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release
cd traceflow/src/TraceFlow.Web && npm run build && cd ../../..
sqlcmd -S localhost -E -C -Q "SET NOCOUNT ON; SELECT @@VERSION" -W -f 65001
```

Expected: 构建 0 警告 0 错误；单元/集成（默认+BIN2）全绿；前端 tsc+vite 0 错误。集成总数 = M1 的 180 + M2 新增（Migration0008Tests 5、RateBudgetTests 5、ChannelPauseTests 1、TopClientServiceTests 4、UpstreamDownloadTests 8、EnterpriseSyncTests 5、ClaimAndCancelTests 6、InboundTaskFactoryTests 4、BillContractTests 3、InboundMatchTests 10、AllocationServiceTests 10、AllocationConfirmTests 6、M2ApiGateTests 4、InboundApiTests 6、SettingsFailClosedTests 2、M2CarryforwardApiTests 6、SettingsScopeApiTests 3 = 88）→ **268**（以实际运行为准，报告写实测数）。单元 = 136 + TopResponseParserTests 12 + RateBackoffTests 2（Theory 展开按实际）+ UploadStateMachineTests 新边用例 + SourceValueObjectTests 3 → 以实际运行为准。

另跑迁移账本核对（临时库真实运行，口径同 M1 报告 ③）：

```bash
sqlcmd -S localhost -E -C -Q "IF DB_ID('TraceFlow_M2Gate') IS NULL CREATE DATABASE TraceFlow_M2Gate"
dotnet run --project traceflow/db/tools/TraceFlow.DbMigrator -c Release -- "Server=localhost;Database=TraceFlow_M2Gate;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
sqlcmd -S localhost -E -C -d TraceFlow_M2Gate -Q "SET NOCOUNT ON; SELECT version,name,checksum,success FROM dbo.schema_version ORDER BY version" -W -f 65001
```

Expected: 0001–0008 全部 success=1；checksum 为真实 SHA-256（报告原样粘贴）。TraceFlow_M2Gate 验证后保留（与 TraceFlow_Dev/TraceFlow_M2Manual 同口径，不删库）。

- [ ] **Step 7: 写 M2 门禁报告（只写真实输出）**

Create `docs/delivery/M2-gate-report.md`，结构完全沿用 M1 报告（标题/结论/①@@VERSION/②五项运行/③迁移账本/④未满足项/缺口与边界），内容要求：

1. 标题 `# M2 门禁报告（TraceFlow 上游下载与入库分配）`；日期=执行日；分支 `m2-upstream-allocation`；HEAD=Step 6 回归时的真实 commit（`git rev-parse --short HEAD`）；配套文档列表（openapi M2 回写/data-dictionary 0008/ksoa-adapter-contract §5/top-contract M2 定稿状态/matrix M0–M2/M2-rulings-and-carryforwards）。
2. 结论段：**代码与测试完成（2019 降级运行全绿，双 collation 变体），2008 R2 实例门禁与 G-ERP/G-TOP 取证门禁未满足；不标记 M2 全门禁通过。** 降级声明与 G-ERP/G-TOP fixture-only 声明原样沿用 M1 报告措辞（含"不伪造 10.50.x 版本记录"）。
3. ①：粘贴 Step 6 的 @@VERSION 真实输出；负向验证（不设 ALLOW_NEWER 时门禁拒绝）若本次未重跑，引用 M1 报告已验证事实并注明引用，不声称本次运行。
4. ②：五个小节各贴真实命令与真实结果摘要（通过/失败计数、耗时、前端 modules 数）；数字一律来自 Step 6 输出，禁止预填。
5. ③：粘贴 schema_version 真实查询输出（0001–0008）。
6. ④ 未满足项：G-ERP/G-TOP/G-POS/G-RETURN/G-OPENING/G-DEPLOY 取证门禁；AT-070 运行时门禁；M3/M4 功能缺口（指向 matrix 与裁决文档携带项）。
7. 缺口与边界：浏览器验证执行情况如实记录（Task 13/14 的 8 点清单逐项结果；未执行则声明未执行）；TraceFlow_M2Manual/TraceFlow_M2Gate 保留说明；"本地保障 vs 平台事实"措辞——M2 不声称任何平台上传成功，102 任务仅创建到 Blocked/Ready。

- [ ] **Step 8: 提交文档（仅本次文件）**

```bash
cd "/e/Viding/Retail Trace Code"
git status --porcelain
git add docs/contracts/openapi.yaml docs/contracts/data-dictionary.md docs/contracts/ksoa-adapter-contract.md docs/contracts/top-contract.md docs/traceability/matrix.md docs/delivery/M2-rulings-and-carryforwards.md docs/delivery/M2-gate-report.md
git -c user.name=Hzsurpassday -c user.email=219255064+Hzsurpassday@users.noreply.github.com commit -m "docs(m2): contract writeback, traceability matrix M0-M2, rulings and gate report (2019 downgrade declared)"
git status --porcelain
```

Expected: 提交仅含上述 7 个文档；用户未跟踪文件（docs/PRD-* 原件、docs/research/、docs/review/、retail_tracecode/、zww-md/、.superpowers/ 等）一律不 add。若 top-contract.md 已由 Task 4 提交过，本次为其增量修改，正常 add。

**Task 15 完成判据**：openapi 与实现一致（R15b）；矩阵 32 行更新且测试名全部真实存在（R15a）；门禁报告五项运行+迁移账本为真实输出；裁决文档 34 条+携带项完整；双 collation 回归全绿。