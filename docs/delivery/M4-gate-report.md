# M4 门禁报告（TraceFlow 收尾与交付）

日期：2026-11-24。分支：`m4-closure`，代码基线 `d0cac84`（fix pass 后）+ 本任务（Task 12）文档与演练脚本。
配套：`docs/delivery/deployment.md`（G-DEPLOY）、`docs/delivery/backup-restore.md`（NFR-07/08）、
`docs/delivery/backup-restore-drill.json`（演练实测）、`docs/delivery/trial-run-report.md`、`docs/delivery/perf-baseline.md`、
`docs/delivery/final-delivery.md`、`docs/traceability/matrix.md`、`.superpowers/sdd/2026-11-23-m4-closure/progress.md`（裁决账本）。

## 结论（不标记 M4 全门禁通过）

**工程与测试完成**：Release 构建 0 警告 0 错误；单元 322/322；集成 490/490（默认 collation 与 BIN2 双变体，
SQL Server 2019 授权降级）；前端 vitest 29/29 + 构建；性能基准 7/7 端点 p95 11.5–134.8 ms；
**备份/恢复真实演练通过**（本机 SQL 2019，8 项断言全绿）。

**仍未满足/未取证**：SQL Server 2008 R2（AT-070）、G-TOP/G-ERP/G-POS/G-RETURN/G-OPENING 取证、202 采购退货采集、
≥7 天试运行、G-DEPLOY 目标机取证、HTTPS 强制（代码注释待改）。

> **⚠ 降级声明（用户授权）：** 全部数据库集成证据来自本机 **SQL Server 2019（15.0.2000.5）**，经
> `TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 显式授权降级；**2008 R2 未运行、未取证（AT-070 未完成）**，本报告不伪造 10.50.x 记录。
> **平台未取证：** TOP 方法名/入参/错误码/限值/回执语义全部为候选（`_verified:false`），仅 fixture + Fake 网关验证；
> `RealTopGateway` 对 LIVE 一律 422 `TOP_NOT_CERTIFIED`；`top.retryPathCertified=false`（fail-closed）。
> **未发生真实平台发送：** 不声称任何平台受理、处理成功、恰好一次或对账结果。
> **试运行未执行 7 天：** 仅脚本冒烟（`trial-run-report.md`）。

## ① 测试实例真实版本（`SELECT @@VERSION` 实际输出）

命令：`sqlcmd -S localhost -E -I -h -1 -W -Q "SELECT @@VERSION"`

真实输出：

```
Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64) 
	Sep 24 2019 13:48:23 
	Copyright (C) 2019 Microsoft Corporation
	Enterprise Edition (64-bit) on Windows 10 Pro 10.0 <X64> (Build 26300: ) (Hypervisor)
```

实际引擎 **15.0.2000.5**（SQL Server 2019），非目标 2008 R2（10.50.x）。集成测试 fixture 对版本做硬门禁：
非 10.50.x 时必须显式 `TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 才运行，并在输出打印"2008 R2 未验证"横幅。

## ② 全量验证命令与真实结果

环境：Windows 10 Pro（Build 26300）、.NET SDK 8.0.425 / 运行时 8.0.31、Node v24.12.0（npm 11.6.2）、SQL Server 2019 本机实例。
全部命令在仓库根执行；测试实例连接由本地环境变量 `TRACEFLOW_TEST_DB` 提供（本报告不记录连接串）。

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

**结果：0 警告 0 错误（`TreatWarningsAsErrors` 生效）。**

### 2. 单元测试

```bash
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
```

真实输出：

```
已通过! - 失败:     0，通过:   322，已跳过:     0，总计:   322，持续时间: 128 ms - TraceFlow.UnitTests.dll (net8.0)
```

**结果：322/322 通过。**

### 3. 集成测试（授权降级，默认 collation）

```bash
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release
```

真实输出：

```
已通过! - 失败:     0，通过:   490，已跳过:     0，总计:   490，持续时间: 2 m 27 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：490/490 通过（SQL 2019 15.0.2000.5，授权降级；2008 R2 待补验）。**

> **首跑异常（如实记录）：** 本任务首次执行时与备份/恢复演练并行（演练在同一实例创建库、跑 9 个迁移、
> BACKUP/RESTORE），结果为 `失败 181 / 通过 393 / 总计 574`，失败全部落在 `Sql2008Fixture.DisposeAsync`
> 的 `ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE`（回滚等待/删库竞争），
> 非业务断言失败。隔离环境（不并行任何其它数据库操作）重跑即 490/490 全绿（上表为隔离重跑结果，含 TRX 结果文件）。
> 该竞争与此前 M3 门禁报告记录的 fixture 建库竞争同源，属测试基础设施时序问题。

### 4. 集成测试（授权降级，BIN2 变体）

同上命令追加 `TRACEFLOW_TEST_DB_BIN2=1`（fixture 以 `COLLATE Latin1_General_BIN2` 建库），并加 `--no-build`。

真实输出：

```
已通过! - 失败:     0，通过:   490，已跳过:     0，总计:   490，持续时间: 2 m 21 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：490/490 通过；与默认 collation 用例数一致，两变体均全绿。**

### 5. 前端测试与构建

```bash
cd traceflow/src/TraceFlow.Web && npm test && npm run build
```

真实输出（节选）：

```
 Test Files  4 passed (4)
      Tests  29 passed (29)

✓ 53 modules transformed.
../TraceFlow.Host/wwwroot/assets/index-BGU2kR7-.js  283.02 kB │ gzip: 86.30 kB
✓ built in 1.02s
```

**结果：vitest 29/29 通过（4 个文件）；`tsc -b` 0 错误，vite 53 modules 构建成功。**

### 6. 性能基准（NFR-03 / AT-075）

见 `docs/delivery/perf-baseline.md` + `traceflow/tools/TraceFlow.OpsTool/perf-results.json`
（`generatedAtUtc=2026-11-24T03:13:09Z`，库 `TraceFlow_Perf_20261124031241` 已自动删除）。
10000 商品 / 100000 销售行 / 200 个 `CONSUMER_RETURN_116` 退货任务（`/returns` 非空）：

| 端点 | p95 (ms) |
| --- | ---: |
| `GET /api/v1/pos-sales?page=1` | 131.9 |
| `GET /api/v1/pos-sales?page=20` | 110.8 |
| `GET /api/v1/returns?page=1` | 55.3 |
| `GET /api/v1/upload-tasks?page=1` | 19.3 |
| `GET /api/v1/codes?q=6900` | 134.8 |
| `GET /api/v1/ops/metrics` | 11.5 |
| `GET /api/v1/dashboard` | 21.4 |

**结果：7/7 PASS，p95 区间 11.5–134.8 ms（上限 2000 ms）；对账触发 `POST /api/v1/reconciliation-runs` 58.7 ms（不设门禁）。**

### 7. 备份/恢复演练（NFR-07 / NFR-08 / AT-072，真实执行）

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File traceflow/tools/trial/Backup-Restore-Drill.ps1 \
  -Base "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
```

真实输出（节选）：

```
[drill] 演练库 TraceFlow_Trial_Drill_20261124040342（实例 localhost）
[1/8] 读取 SQL Server 版本 ...
  Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64) (ProductVersion 15.0.2000.5)
[2/8] 预检演练库是否已存在（DB_ID），再创建并执行迁移 0001-0009 ...
  预检通过：演练库不存在，将由本次运行创建。
  已创建库 TraceFlow_Trial_Drill_20261124040342（默认排序规则）
  迁移成功行数：9
[3/8] 写入备份前标记数据 ... 种子完成：org_id=1 preTaskId=1
[4/8] 备份（COPY_ONLY + CHECKSUM）并 VERIFYONLY ...
  备份文件：...\TraceFlow_Trial_Drill_20261124040342.bak（4575232 字节，160.1 ms）
  VERIFYONLY 通过：文件 1 上的备份集有效。
[5/8] 备份后变更数据 ... 变更后：pre=0 post=1（恢复前）
[6/8] 单用户模式恢复（REPLACE + RECOVERY），再回多用户 ... 恢复完成（597.2 ms）
  DBCC CHECKDB 无错误。
[7/8] 恢复后断言 ...
  [PASS] pre_backup_marker_present：expected=1 actual=1
  [PASS] post_backup_marker_absent：expected=0 actual=0
  [PASS] organization_name_restored：expected=MATCH actual=MATCH
  [PASS] upload_task_same_pre_id_present：expected=1 actual=1
  [PASS] schema_version_rows_and_success：expected=9|9 actual=9|9
  [PASS] restore_verifyonly_checksum：expected=exit 0 actual=exit 0
  [PASS] dbcc_checkdb_clean：expected=exit 0 actual=exit 0
  [PASS] backup_file_nonempty：expected=>0 actual=4575232
[8/8] 写入实测 JSON ... 已写入：docs/delivery/backup-restore-drill.json
[清理] 已删除演练库 TraceFlow_Trial_Drill_20261124040342
[清理] 已删除备份文件 ...
[drill] 结论 PASS：备份/恢复断言全部通过（引擎 15.0.2000.5）。
```

**结果：8/8 断言通过；备份 4,575,232 字节（160.1 ms）、恢复 597.2 ms、`DBCC CHECKDB` 无错误、`schema_version` 9/9 `success=1`。
演练库与 .bak 已清理，`sys.databases` 无 `TraceFlow_Trial_Drill_*` 残留。实测值见 `docs/delivery/backup-restore-drill.json`
（`generatedAtUtc=2026-11-24T04:03:49.4162039Z`、`dbName=TraceFlow_Trial_Drill_20261124040342`、`engineVerified2008R2=false`）。**

**重复 `-Stamp` 的拒绝（exit 2）已实测**：先用 `-Keep -Stamp 20261124050000` 保留演练库，再用同一 `-Stamp`
不带 `-Keep` 重跑（`create-db` 幂等，不预检就会静默复用旧库）：

```
[2/8] 预检演练库是否已存在（DB_ID），再创建并执行迁移 0001-0009 ...
[拒绝] 演练库已存在（-Stamp 重复）；拒绝复用/删除，请换 -Stamp 或先人工确认：TraceFlow_Trial_Drill_20261124050000
[清理] 演练库未由本次运行创建，跳过删除。
```

进程退出码 **2**；`sys.databases` 中 `TraceFlow_Trial_Drill_20261124050000` 仍在（`MULTI_USER` / `ONLINE`，未被复用或删除），
随后由人工用 OpsTool `drop-db ... --yes` 删除。

### 8. 导出/审计算子（AT-069 / AT-074）

| 门禁 | 实现 | 证据（真实运行，通过） |
| --- | --- | --- |
| 公式注入防护（文本列） | 首字符 `= + - @` / TAB / CR 前置 `'`；单元格统一引号包裹、内部 `"` 双写、换行原样保留 | `CsvTests.Cell_prefixes_formula_lead_chars_with_single_quote`、`Cell_doubles_internal_quotes`、`Cell_keeps_newlines_verbatim_inside_quotes` |
| 数量列不走文本转义 | `Csv.Number` 纯十进制、不加 `'`、负号保留 | `CsvTests.Number_uses_plain_decimal_without_scientific_notation`、`Number_negative_value_is_not_formula_guarded` |
| 行数上限（写响应前拒绝） | `ExportPolicy` 公共常量/纯函数；超限 422 `EXPORT_TOO_LARGE` | `ExportApiTests.Export_row_limit_helper_guards_at_fifty_thousand` |
| 导出范围与审计 | 三端点（码台账/对账差异/审计日志）按宿主 env 与筛选；成功导出写 `data.exported`（env 恒 LIVE，reason 为筛选摘要+行数） | `ExportApiTests.Codes_export_streams_csv_with_bom_and_injected_formula_prefixed`、`Recon_differences_export_scoped_to_host_env_and_audits`、`Audit_logs_export_requires_audit_export_permission` |
| 人工操作审计 | 映射确认/改绑/解除、分类、设置更新、人工核对、受控重试、退货确认、异常留证关闭均写前后摘要/原因/对象，无明文密钥 | `PermissionAuditTests`、`SettingsServiceTests.Secret_is_write_only_never_echoed_and_audit_has_no_plaintext`、`ExceptionQueueTests.Ingest_resolve_requires_evidence_audits_live_and_conflicts_on_repeat`、`ReturnConfirmTests`、`UploadApiTests` 人工操作审计用例 |
| 导出中途失败不写审计 | 流式写出 200 后异常 → 日志 + `ctx.Abort()`，跳过 `data.exported` | `ExportEndpoints.StreamCsvAsync`（fix pass A1）；无新增失败注入用例（如实标注） |

## ③ 迁移账本（真实数据）

- 迁移 **0001–0009 字节未变**：本任务未修改 `traceflow/db/migrations/**`；checksum 以 M3 门禁报告 ③ 登记的 9 个
  SHA-256 为准，`Migration0009Tests.Migrations_0001_to_0008_checksums_match_m2_gate_report` 在 490 用例中通过。
- 演练库独立复核：`DBACKUP` 前完成 9 个迁移，恢复后 `schema_version` 仍为 **9 行且 `success=1`**（见 ②.7 断言）。
- 本任务**未触碰** `TraceFlow_Dev`、`TraceFlow_M2Manual`、`TraceFlow_M2Gate`、`TraceFlow_M3Ui` 与任何 `TraceFlow_Test_*`；
  创建的库仅 `TraceFlow_Trial_Drill_*`（演练后删除）。

## ④ 验收用例（AT）映射

| AT | 范围 | 实际证据 | 缺口 |
| --- | --- | --- | --- |
| AT-069 | 人工映射/补配/分类/配置/重试/导出审计 | 审计覆盖：映射确认与改绑（`MappingConfirmTests`、`M2CarryforwardApiTests.Resolve_*`）、分类（`ClassificationTests.Trace_policy_updates_and_audits`）、设置（`SettingsServiceTests.*`、`SettingsApiTests.*`）、人工核对/受控重试（`UploadApiTests.Manual_verify_*`、`Controlled_retry_*`）、退货确认（`ReturnConfirmTests.Confirm_creates_claims_events_and_dispatch_outbox`）、导出（`data.exported`）、异常关闭（`exception.resolved`）；密钥只写不回显、审计无明文 | 无（审计内容正确性以集成断言为证；未做人工抽查报告） |
| AT-070 | 全套迁移/分页/队列在 2008 R2 执行 | **未执行**：静态语法门禁 `Sql2008CompatibilityTests`（含目录级扫描）通过；运行时仅在 SQL 2019 | **AT-070 未完成**：须真实 10.50.x 重跑 490 集成用例 + `perf` 基准 |
| AT-071 | 无应用密钥/连接密码/完整签名泄露 | `SettingsServiceTests.Secret_is_write_only_never_echoed_and_audit_has_no_plaintext`、`SettingsApiTests.Secret_write_returns_has_secret_value_and_null_value_json`；前端无密钥；本任务文档/脚本 grep 无敏感值（§验证 4） | 目标机日志与部署配置的人工扫描未做（G-DEPLOY） |
| AT-072 | 业务库恢复到发送前旧备份：上传暂停、先对账修复、不重放已成功业务 | **数据层演练已完成**（②.7：备份→变更→恢复→8 项断言）；流程文档 `backup-restore.md` §4 强制"停发送→先对账→修复→再开放" | **LIVE 端到端未执行**：无平台可对账（G-TOP 未取证）；恢复后 `outbox_job` 重放防护靠"服务停止"运维约束，未做进程级演练 |
| AT-073 | 页面关闭后 Worker 独立处理，重开显示真实持久状态 | 作业为数据库持久队列（`outbox_job` 租约/fencing）+ 后台 `WorkerHostedService`，与浏览器会话无关；`JobQueueTests`（双 Worker 竞争、租约回收、token guard）、`OpsStatusTests`（作业状态读库）、`Dashboard_requires_trace_read_and_returns_real_counts` | **未做浏览器端到端运行**（本任务不新增浏览器声明）；页面关闭/重开行为属既有设计事实，未新跑 |
| AT-074 | CSV 文本以 `= + - @` 开头等 | ②.8 导出/审计算子表：`CsvTests` + `ExportApiTests` 全部通过（默认与 BIN2 变体） | 无（人工用 Excel 打开验收可另做） |
| AT-075 | 指定基准数据与并发：分页/入队指标、记录机器/索引/慢查询 | ②.6：7/7 p95 11.5–134.8 ms；机器/数据量/索引与偏差记录于 `perf-baseline.md`；慢查询未单独采集（未开启 Query Store/Profiler） | 真实 ERP 慢查询未取证（G-ERP）；2008 R2 基准未跑 |

## ⑤ 未完成 / 阻塞项

| 项 | 状态 | 后果/边界 |
| --- | --- | --- |
| SQL Server 2008 R2（AT-070） | **未验证** | 交付前须真实 10.50.x 重跑全部集成用例与 perf，并重做一次备份/恢复演练 |
| G-TOP 合同与真实发送 | **未取证** | LIVE 一律 fail-closed（422 `TOP_NOT_CERTIFIED`）；无平台受理/对账事实 |
| G-ERP 真实同步 / 只读账号 | **未取证** | ERP 同步与 `erp.connection` 真实连接禁用（fixture 通道） |
| 202 采购退货采集 | **未接入** | 仅服务/测试路径（`PURCHASE_RETURN_202`），来源字段未取证 |
| ≥7 天试运行 | **未执行** | `trial-run-report.md`：仅脚本冒烟；登记表待用户填写 |
| G-DEPLOY 目标机取证 | **未取证** | `deployment.md` §8 清单 8 项全部未取证 |
| HTTPS 强制 | **未实施**（代码注释待改） | `Program.cs:131 Cookie.SecurePolicy = SameAsRequest`，上线前须改 `Always` 并配置 TLS |
| 恢复后 outbox 重放防护 | **运维约束** | 依赖"恢复时服务必须停止"；未做进程级强制（如恢复后自动隔离 `RUNNING` 作业） |

## ⑥ 遗留裁决摘要

| 裁决 | 内容 | 指向 |
| --- | --- | --- |
| R-M4-1 | M4 从 M3 分支尖端创建（堆叠 PR）；M3 合并后如冲突需 rebase 到 main | `progress.md` |
| R-M4-2 | Task 0 改为逐任务详化（brief 为执行细节来源） | `progress.md` |
| R-M4-3 | 对账平台侧仅用已持久化证据，不主动调 TOP；分片缺终态即 INCOMPLETE | `progress.md` |
| R-M4-4 | 对账触发用 `system:manage`、读取 `audit:read`、差异解决 `system:manage`（不新增权限键） | `progress.md` |
| fix pass | Task 4–8 归档 Medium 全部修复（导出中止不写审计、LIKE 字面量、UTC `Z`、迁移器 `--dir`/env、`/ops?state` 深链、IME Escape、退货文案、`saleReasonZh` 单一来源、退货种子、`--out` 绝对路径、脚本 `host.json` 清理时序、sln 重复节点） | `progress.md`、`fix-pass-report.md` |
| 保留项 | Task 1–3 归档 Lows（`page*size` 溢出、claims TOP 200、死字段、抽屉陈旧详情、部分边界测试）；占用释放审批 `code:release` 工作流不在 M4 范围 | `progress.md` |
| 测试基础设施 | 并行 fixture 建库/删库竞争（§②.3 首跑异常）记录在案，未改测试 | 本报告 ②.3 |

## ⑦ 文档—实现差异（如实记录）

1. 健康检查实际路由为 **`/api/v1/health`**（brief 中的 `/health` 不存在）；无独立"版本页"端点。
2. `OpsTool` 只接受 `TraceFlow_Trial_*` / `TraceFlow_Perf_*`；**生产库建库不受该白名单约束**，须 DBA 手工执行并记录。
3. `DbMigrator` 本身不解析库名（无名称守卫），"只连 TraceFlow 目标库"是执行纪律；本任务已在 `deployment.md` 写明。
4. 单门店 `Organization` 行由启动引导 `OrgBootstrap.EnsureAsync` 幂等补齐（`org_code='DEFAULT'`），
   `ent_id`/`ref_ent_id` 仍须 DBA 直更（无 API）。
5. 代码中 `Program.cs:11 UseWindowsService()`、`Program.cs:131 Cookie.SecurePolicy` 与 brief 引用一致；
   brief 中 `Program.cs:11` 的行号在 `deployment.md` 中按实际文件引用。