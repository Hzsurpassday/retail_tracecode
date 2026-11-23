# M3 门禁报告（TraceFlow 零售上传闭环）

日期：2026-11-24。分支：`m3-sale-upload`，M3 代码基线 `0639b0d`（Task 9 故障注入修复轮；本报告 ② 的全部运行在该代码上执行，本报告随 Task 10 文档提交入库，docs 提交发生在运行之后）。
配套：`docs/contracts/openapi.yaml`（M3 回写）、`docs/contracts/top-contract.md`（M3 候选面）、`docs/contracts/data-dictionary.md`（0009 增量）、`docs/traceability/matrix.md`（M0–M3）、`docs/delivery/M3-rulings-and-carryforwards.md`（R-M3-1…22 与携带项）、`docs/superpowers/plans/2026-11-23-m4-closure.md`（M4 骨架）。

## 结论（不标记 M3 全门禁通过）

**代码与测试完成（SQL Server 2019 授权降级运行全绿，默认与 BIN2 双 collation 变体），2008 R2 实机门禁与 G-TOP 取证门禁未满足；M3 未向任何真实平台发送数据，所有发送/回查/逐码查询均为 MOCK/fixture，不声称任何平台受理、处理成功或对账结果。**

> **⚠ 降级声明（用户授权）：2008 R2 真实实例验证未完成（开发期授权降级至 SQL Server 2019；交付前必须真实 2008 R2 补验全部集成测试，AT-070）。**
> **G-TOP 未取证：** `uploadretail`（321/116）、`query.billstatus`、`query.codedetail` 的方法名、入参、错误码、限值与回执语义全部为候选（`_verified:false`），仅经本地 JSON fixture + Fake 网关验证；`RealTopGateway` 对 LIVE 一律 422 `TOP_NOT_CERTIFIED`。
> **受控重试默认关闭：** `top.retryPathCertified=false`（fail-closed），端点 422 `RETRY_PATH_NOT_CERTIFIED` 且零写入；测试中显式置 true 仅为走通状态机，不是平台事实。
> **202 采购退货采集未接入：** `PURCHASE_RETURN_202` 仅服务/测试路径（R-M3-4），KSOA 采购退货来源字段未取证（G-ERP）。
> **本地保障 ≠ 平台事实：** M3 的"受理未知先回查、不自动重发、逐码 UNKNOWN 保留、部分成功不整单重排、恢复不重传"均为本系统本地保障，不构成平台侧恰好一次或对账证据。

## ① 测试实例真实版本（`SELECT @@VERSION` 实际输出）

命令：`sqlcmd -S localhost -E -I -Q "SELECT @@VERSION" -h -1 -W`

真实输出：

```
Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64) 
	Sep 24 2019 13:48:23 
	Copyright (C) 2019 Microsoft Corporation
	Enterprise Edition (64-bit) on Windows 10 Pro 10.0 <X64> (Build 26300: ) (Hypervisor)
```

实际引擎为 SQL Server 2019（15.0.2000.5），非目标 2008 R2（10.50.x）。降级口径沿用 M0–M2：`TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 显式授权（用户裁决）。**2008 R2 未运行、未取证；本报告不伪造 10.50.x 记录。**

## ② 全量验证命令与真实结果

环境：Windows 10 Pro（Build 26300）、.NET SDK 8（net8.0）、Node v24.22.1、SQL Server 2019 本机实例。全部命令在仓库根 `E:\Viding\Retail Trace Code` 执行。测试实例连接参数由本地环境变量 `TRACEFLOW_TEST_DB` 提供（本报告不记录连接串）。五项必跑全绿；另附定向故障注入两轮。

### 1. 解决方案构建

```bash
dotnet build traceflow/TraceFlow.sln -c Release
```

真实输出（尾部）：

```
已成功生成。
    0 个警告
    0 个错误
```

**结果：0 警告 0 错误（TreatWarningsAsErrors 生效），全部项目编译；本次为增量构建，实测耗时 3 秒（wall clock）。**

### 2. 单元测试

```bash
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
```

真实输出：

```
已通过! - 失败:     0，通过:   251，已跳过:     0，总计:   251，持续时间: 141 ms - TraceFlow.UnitTests.dll (net8.0)
```

**结果：251/251 通过（wall clock 4 秒）。** `dotnet test --list-tests` 实测：**21 个测试类、251 个用例**（Theory 按展开计）。M3 新增单元类（逐类实测）：`SaleRulesTests` 11、`ReturnRulesTests` 8、`BillStatusParserTests` 14、`TaskStateAggregatorTests` 18、`UploadDispatchTaskStateTests` 14、`TopResponseSanitizerTests` 4；另 `TopResponseParserTests` 由 13 扩至 16（R-M3-2 `model` 回退）。

### 3. 集成测试（授权降级，默认 collation）

```bash
TRACEFLOW_TEST_DB="<本地测试实例连接串，按本地部署配置提供>" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release
```

真实输出：

```
已通过! - 失败:     0，通过:   429，已跳过:     0，总计:   429，持续时间: 3 m 7 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：429/429 通过（SQL 2019 15.0.2000.5，授权降级；2008 R2 待补验），wall clock 194 秒。** `--list-tests` 实测：**60 个测试类、429 个用例**。M3 新增/扩充类（逐类实测）：`SaleVerificationTests` 31、`RetailTaskTests` 17、`ReturnConfirmTests` 12、`UploadDispatchTests` 18、`BillQueryTests` 15、`FaultInjectionTests` 8、`SalesApiTests` 9、`UploadApiTests` 19、`Migration0009Tests` 5、`M3SettingsTests` 4。
每个测试类经 `Sql2008Fixture` 自建 `TraceFlow_Test_<guid>` 临时库并在 Dispose 时 DROP。

**首跑异常（如实记录）**：本轮首次执行时 4 个 M2 存量 Master 用例失败（`MappingConfirmTests.Confirm_candidate_sets_manual_locked`、`ClassificationTests.Products_list_includes_active_mapping_identity` 等），错误为 `SqlException: 无法获得数据库 'model' 上的排他锁`（并行 fixture 同时 `CREATE DATABASE` 的竞争，非 M3 代码失败）；随后单独重跑本轮全量，429/429 通过。该竞争为测试基础设施时序问题，已列入 M4 携带项。

### 4. 集成测试（授权降级，BIN2 变体）

同上命令追加 `TRACEFLOW_TEST_DB_BIN2=1`（fixture 以 `COLLATE Latin1_General_BIN2` 建库）。

真实输出（重跑）：

```
已通过! - 失败:     0，通过:   429，已跳过:     0，总计:   429，持续时间: 2 m 29 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：429/429 通过，wall clock 155 秒；与默认 collation 用例数一致，两变体均全绿。**
**首跑异常（如实记录）**：BIN2 首次执行 428/429，唯一失败为 M1 存量用例 `Api.ImportApiTests.Get_import_detail_shape_and_pagination`（`successRows` 断言 1≠0，后台作业回写时序竞态，M2 门禁报告已列为观察项）；整轮重跑 429/429 通过。该失败与 M3 改动无关，按约束未修改测试。

### 5. 前端构建

```bash
cd traceflow/src/TraceFlow.Web && npm run build   # tsc -b && vite build
```

真实输出（尾部）：

```
✓ 47 modules transformed.
../TraceFlow.Host/wwwroot/index.html                  0.30 kB │ gzip:  0.26 kB
../TraceFlow.Host/wwwroot/assets/index-DEmMc8Kg.js  249.84 kB │ gzip: 76.49 kB
✓ built in 752ms
```

**结果：tsc 0 错误，vite 产物输出至 `TraceFlow.Host/wwwroot`；47 modules（M2 为 44，新增销售页/退货页/上传中心页等模块），wall clock 4 秒。**

### 6. 定向故障注入（M3 Task 9，默认 + BIN2）

```bash
TRACEFLOW_TEST_DB="<本地测试实例连接串>" TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --filter "FullyQualifiedName~FaultInjectionTests"
```

真实输出（默认 collation）：

```
已通过! - 失败:     0，通过:     8，已跳过:     0，总计:     8，持续时间: 831 ms - TraceFlow.IntegrationTests.dll (net8.0)
```

追加 `TRACEFLOW_TEST_DB_BIN2=1` 后：

```
已通过! - 失败:     0，通过:     8，已跳过:     0，总计:     8，持续时间: 625 ms - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：8/8 通过（两变体），wall clock 分别 9 秒 / 10 秒。** 用例：`Kill_before_http_attempt_leaves_evidence_then_recovery_queries_first`、`Kill_after_http_before_t3_leaves_dispatching_and_recovery_never_reuploads`、`Kill_during_t3_rolls_back_and_attempt_stays_open`、`Late_worker_write_rejected_but_response_preserved`、`Duplicate_bill_code_error_only_triggers_query`、`Crash_before_receipt_commit_keeps_accepted_unknown_and_no_task_success`、`Same_business_intent_creates_exactly_one_task_and_part_bill_code`、`Mock_success_never_advances_live_state`。

## ③ 迁移账本与 checksum（真实数据）

M3 不新建数据库、不重跑迁移工具，也未触碰 `TraceFlow_Dev`／`TraceFlow_M2Manual`／`TraceFlow_M2Gate`／`TraceFlow_M3Ui`。checksum 证据来自两处只读核对：

1. 既有 M3 集成测试遗留库（`TraceFlow_Test_<guid>`，已成功应用 9 个迁移）的 `dbo.schema_version` 只读查询：

```sql
SELECT version, name, checksum, success FROM dbo.schema_version ORDER BY version;
```

真实结果（9/9 `success=1`）：

| version | name | checksum (SHA-256) |
| --- | --- | --- |
| 1 | 0001_core.sql | 3A7F4E3A4A1D910208FDA22B1BB083DDB10D1EE1D80E644328506788E59DEFB9 |
| 2 | 0002_source.sql | 90AABC500B52314EBB225AEB0CEA23991E608E8F38792A13723F63B68448FB43 |
| 3 | 0003_master.sql | 0E1FF67C4A777B20BF2589F7D6708E14875DC89ADD431D02DE8CF518D5A9D1DE |
| 4 | 0004_upstream.sql | DF82EAA935D8A38B402119D0DDD99350407FDE5ED709DD34C67B36DFE4938273 |
| 5 | 0005_codes.sql | 5E1E1EF199C62658BE10761319392A2AAC0E19771D64D8CCB8F388CE3EC4EEDC |
| 6 | 0006_upload.sql | 8150CED244D824BE4DD78D5E80C999EA8E702A3B68C7433E75740569F0928FBE |
| 7 | 0007_m1_source_master.sql | 96D399B8CE9D834DCC119729059D1676152A2108A25876C2A94FFCE0E8715A39 |
| 8 | 0008_m2_upstream_allocation.sql | F3DC59F6F51AAAB2F07AA1B09C46A36B59896EEDC51794163784F5CA166C6EF0 |
| 9 | 0009_m3_sale_upload.sql | 99643903AC84A930CB1FD9E633445081F5CFE3E23E91557C7F3952E13D51D6BB |

2. 对 `traceflow/db/migrations/*.sql` 当前文件字节做 SHA-256（PowerShell `Get-FileHash -Algorithm SHA256`），9 个文件与上表逐项一致。

**0001–0008 未变**：上表 1–8 与 `docs/delivery/M2-gate-report.md` ③ 登记的 checksum 逐字节相同；`Migration0009Tests.Migrations_0001_to_0008_checksums_match_m2_gate_report` 在 429 用例中通过。0009 为 M3 新增增量（R-M3-6：合并前修订并更新校验和断言），checksum 首次登记。

## ④ 浏览器证据（M3 Task 8，控制器执行；本任务未重跑）

浏览器验证使用独立库 `TraceFlow_M3Ui`（MOCK，保留不删），由控制器以 chrome-devtools 执行；本报告据实引用，不重复声称本次新跑。截图目录：`.superpowers/sdd/2026-11-23-m3-sale-upload/shots/`。

| 页面/场景 | 证据 |
| --- | --- |
| 销售列表（env/时间/核验态/任务态） | `t8-sales-list.png` |
| 销售详情（行/码/核验/任务分片） | `t8-sales-detail.png` |
| 退货列表（待确认/已确认/已驳回三 tab） | `t8-returns-list.png` |
| 退货确认对话框（候选单号/时间/码） | `t8-returns-dialog-filled.png` |
| 待确认候选（#12，最终复验） | `t8-returns-pending-candidates.png` |
| 上传任务列表（最终成功率口径/失败原因） | `t8-uploads-list.png` |
| 上传详情抽屉（逐码失败/尝试/人工处置） | `t8-uploads-detail-manual.png` |
| 受控重试门禁（disabled + 原因，抽屉 697.2px=视口） | `t8-upload-retry-gate-visible.png` |

`t8-upload-retry-gate.png` 为被裁切的早期取证，已被 `t8-upload-retry-gate-visible.png` 取代（Task 8 报告记录）。边界：浏览器证据只覆盖本地 UI 与本地 API，不涉及任何真实 TOP 调用。

## ⑤ 未满足项与显式声明

| 项 | 状态 | 后果/边界 |
| --- | --- | --- |
| SQL Server 2008 R2（AT-070） | **未验证**（本报告全部 DB 证据为 2019 授权降级） | 交付前必须真实 10.50.x 重跑全部 429 集成用例并替换 ①③ 证据；静态语法门禁不替代运行时 |
| G-TOP 合同 | **未取证** | 321/116 发送、billstatus、codedetail 全部候选 `_verified:false`；LIVE 一律 422 TOP_NOT_CERTIFIED |
| 真实平台发送 | **未发生** | 全部 MOCK/fixture；不声称平台受理/处理成功/恰好一次 |
| 受控重试 | `top.retryPathCertified=false`（默认） | 取证前端点 422 且零写入；UI 按后端事实禁用 |
| 202 采购退货采集 | **未接入**（G-ERP） | 仅服务/测试路径；UI 明示；不伪造采集入口 |
| G-ERP / G-POS / G-RETURN / G-OPENING / G-DEPLOY | 未取证 | 真实同步/采码/退货/期初/部署均禁用 |
| 日对账、码台账、异常队列、导出、备份恢复、性能与 ≥7 天试运行 | M4 | 本报告不声称 |
| 测试基础设施 flake | 已记录 | 首跑并行建库竞争（默认）与存量 ImportApi 时序竞态（BIN2）；重跑均全绿，未改测试 |

## ⑥ 控制器独立复核（2026-11-24，提交前）

在文档提交前，控制器于同一基线 `0639b0d` 独立重跑集成全量（默认 collation，命令同 ②.3）：

```
已通过! - 失败:     0，通过:   429，已跳过:     0，总计:   429，持续时间: 2 m 28 s - TraceFlow.IntegrationTests.dll (net8.0)
```

同时以 `--list-tests` 复核用例数（单元 251、集成 429）、以 `sha256sum` 复核 9 个迁移文件 checksum、以脚本比对矩阵引用的 147 个测试名（147/147 存在于实测测试列表）。以上均与本文 ②③ 一致。

## ⑦ 文档—代码差异与回写（本任务处理）

- 计划/brief 中的 `/sales`、`/sales/{id}`、`/returns/{id}`、`/verifications/{id}/recheck`、`/upload-tasks/{id}/query`、`/upload-tasks/{id}/retry` 未实现；实际为 `GET /api/v1/pos-sales`、`GET /api/v1/pos-sales/{id}`、`GET /api/v1/returns`、`POST /api/v1/returns/{id}/confirm`、`POST /api/v1/upload-tasks/{id}/manual-verify`、`POST /api/v1/upload-tasks/{id}/controlled-retry`。`openapi.yaml` 已按实现回写，未实现路径保留并标 `x-phase: M4`（计划契约）。
- `manual-verify`/`controlled-retry` 使用 `Idempotency-Key` 头；`returns/{id}/confirm` 的幂等键在请求体 `idemKey`（契约已如实区分）。
- 0009 不新建 `upload_task_part`/`upload_attempt`/`code_result`（实际表名为 0006 既有的 `upload_part`/`upload_attempt`/`code_result`）；`data-dictionary.md` 按实际 DDL 回写。
- 错误体统一为 `{code, message, fieldErrors, retryable, retryAfter, requestId}`；仅 429 与 409 `RETRY_IN_FLIGHT` 的 `retryable=true`，`Retry-After` 头仅 429。