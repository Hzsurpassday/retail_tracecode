# M1 门禁报告（TraceFlow 来源同步与主数据匹配）

日期：2026-11-23。分支：`m1-source-master`，代码基线 HEAD `0a144c0`（Task 1–16；回归运行于 d6aaec9 + Task 16 携带测试，即 `0a144c0` 提交内容；本报告随 docs 提交入库）。
配套：docs/contracts/openapi.yaml（M1 回写）、data-dictionary.md（0007）、adapter-facts.md、source-mapping.md、ksoa-adapter-contract.md、docs/traceability/matrix.md（M0–M1）。

## 结论（不标记 M1 全门禁通过）

**代码与测试完成（2019 降级运行全绿，双 collation 变体），2008 R2 实 instance 门禁与 G-ERP 取证门禁未满足。**

> **⚠ 降级声明（用户授权）：2008 R2 真实实例验证未完成（用户授权开发期降级至 SQL Server 2019；交付前必须真实 2008 R2 补验全部集成测试，AT-070）。**
> 本报告不声称 2008 R2 兼容性已验证，不伪造 10.50.x 版本记录。
> **G-ERP fixture-only：M1 来源同步仅经 FixtureKsoaAdapter（本地 JSON fixture）验证；真实 KSOA 只读连接、键唯一性、主档来源取证未做，真实 ERP 同步通道禁用（RealKsoaAdapter 抛 AdapterNotCertifiedException，LIVE 环境拒绝同步）。**

## ① 测试实例真实版本（SELECT @@VERSION 实际输出）

命令：`sqlcmd -S localhost -E -C -Q "SET NOCOUNT ON; SELECT @@VERSION" -W -f 65001`

真实输出：

```
Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64)
	Sep 24 2019 13:48:23
	Copyright (C) 2019 Microsoft Corporation
	Enterprise Edition (64-bit) on Windows 10 Pro 10.0 <X64> (Build 26300: ) (Hypervisor)
```

实际引擎为 SQL Server 2019（15.0.2000.5），非目标 2008 R2（10.50.x）。降级开关口径同 M0 报告：`TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 显式授权（用户裁决）；**负向验证（本次真实运行）**：不设开关时 `Sql2008Fixture` 门禁拒绝建库，聚焦运行 MigratorTests 3 用例全部失败于：

```
System.InvalidOperationException : 集成门禁要求 SQL Server 2008 R2（10.50.x），实际 15.0.2000.5
   at TraceFlow.IntegrationTests.Sql2008Fixture.InitializeAsync() in E:\Viding\Retail Trace Code\traceflow/tests/TraceFlow.IntegrationTests/Sql2008Fixture.cs:line 43
```

## ② 全量验证命令与真实结果

环境：Windows 10 Pro（Build 26300）、.NET SDK 8（net8.0）、Node v24.22.1、SQL Server 2019 本机实例。全部命令在仓库根 `E:\Viding\Retail Trace Code` 执行。五项全量运行均绿；其后仅文档变更与 EOF 换行卫生，无代码语义变更（构建复核仍 0 警告 0 错误）。

### 1. 解决方案构建

```bash
dotnet build traceflow/TraceFlow.sln -c Release
```

真实输出（尾部）：

```
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:04.11
```

**结果：0 警告 0 错误（TreatWarningsAsErrors 生效），全部项目编译。**

### 2. 单元测试

```bash
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --no-build
```

真实输出：

```
已通过! - 失败: 0，通过: 136，已跳过: 0，总计: 136，持续时间: 115 ms - TraceFlow.UnitTests.dll (net8.0)
```

**结果：136/136 通过。** M1 新增测试类：CodeFormatStorageTests 6（含 Token_roundtrip Theory×3）、RowVerTests 5（含 Invalid_hex Theory×4）、DrugIdentityMatcherTests 6、ApprovalNoNormalizerTests 6（Theory×6）、SourceValueObjectTests 22（含 Expiry_precision/Business_time Theory）、CsvReaderTests 5，合计 50 用例；M0 基线 87 中占位 UnitTest1（1 用例）已在 T4 卫生提交移除（87−1+50=136）。UploadStateMachineTests 理论行 54 不变（另有 1 Fact，类合计 55）。

### 3. 集成测试（授权降级，默认 collation）

```bash
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build
```

真实输出：

```
已通过! - 失败: 0，通过: 180，已跳过: 0，总计: 180，持续时间: 1 m 19 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：180/180 通过（SQL Server 2019 15.0.2000.5，授权降级；2008 R2 待补验）。**
M1 新增测试类与用例数（20 类 136 用例）：FixtureKsoaAdapterTests 8、OrgBootstrapTests 2、SourceSyncTests 12、SourceStabilizationTests 4、SourceSyncJobHandlerTests 3、SyncSchedulerTests 4、SettingsServiceTests 12（9 方法，Int_bounds Theory×4）、ImportServiceTests 15、ClassificationTests 6、MatchingServiceTests 12、SupplierMatchingTests 9、MappingConfirmTests 8、Migration0007Tests 6、IdempotencyHardeningTests 4、InputValidationTests 6（含 Theory×3）、LeaseRecoveryAuditTests 2、M1ApiGateTests 11、SettingsApiTests 7、MasterPaginationApiTests 2、ImportApiTests 3。M0 基线 45 中占位 UnitTest1（1 用例）已移除（45−1+136=180）。
其中 Task 16 携带新增 18 用例：SettingsApiTests 7（HTTP 设置：camelCase/未持久化默认、互斥 422、版本 409、密钥只写、连接测试守卫、system:manage 门禁）、MasterPaginationApiTests 2（products/suppliers 页大小钳制与越界页空记录）、ImportApiTests 3（stage 202 字符串 ID、detail 形状/分页、commit 409/422/404）、M1ApiGateTests +3（审计页大小钳制/越界页、newest-first+id tiebreak 排序、精确过滤非子串）、ImportServiceTests +3（COMMITTED/FAILED 重暂存 JobId=0、handler 对 COMMITTED 重跑 no-op）。
每个测试类经 `Sql2008Fixture` 自建 `TraceFlow_Test_<guid>` 临时库并在 Dispose 时 DROP；运行后无残留（见 ③ 末尾核查）。

### 4. 集成测试（授权降级，BIN2 变体）

同上命令追加 `TRACEFLOW_TEST_DB_BIN2=1`（Sql2008Fixture 以 COLLATE Latin1_General_BIN2 建库）。

真实输出：

```
已通过! - 失败: 0，通过: 180，已跳过: 0，总计: 180，持续时间: 1 m 14 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：180/180 通过，与默认 collation 变体用例数一致，两变体均全绿。**

### 5. 前端构建

```bash
cd traceflow/src/TraceFlow.Web && npm run build   # tsc -b && vite build
```

真实输出（尾部）：

```
vite v5.4.21 building for production...
✓ 42 modules transformed.
../TraceFlow.Host/wwwroot/index.html                  0.30 kB │ gzip:  0.26 kB
../TraceFlow.Host/wwwroot/assets/index-BQ_j3J-w.js  188.92 kB │ gzip: 60.82 kB
✓ built in 626ms
```

**结果：tsc 0 错误，vite 产物输出至 TraceFlow.Host/wwwroot。**

## ③ 迁移 applied 列表与 checksum（临时库真实运行）

临时库生命周期（全部真实执行）：

1. `CREATE DATABASE [TraceFlow_GateReport_213633576]`（sqlcmd，退出码 0）。
2. 运行 DbMigrator（`traceflow/db/tools/TraceFlow.DbMigrator/Program.cs`，用法 `<连接串> [脚本目录]`）：

```bash
dotnet run --project traceflow/db/tools/TraceFlow.DbMigrator -c Release --no-build -- \
  "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Initial Catalog=TraceFlow_GateReport_213633576" \
  "E:/Viding/Retail Trace Code/traceflow/db/migrations"
```

真实输出：

```
Applied: 0001_core.sql, 0002_source.sql, 0003_master.sql, 0004_upstream.sql, 0005_codes.sql, 0006_upload.sql, 0007_m1_source_master.sql
Skipped:
```

3. 查询 `dbo.schema_version`（version,name,checksum,applied_at,success），真实行（7/7 success=1）：

```
version|name|checksum|applied_at|success
1|0001_core.sql|3A7F4E3A4A1D910208FDA22B1BB083DDB10D1EE1D80E644328506788E59DEFB9|2026-11-23 04:37:21.725|1
2|0002_source.sql|90AABC500B52314EBB225AEB0CEA23991E608E8F38792A13723F63B68448FB43|2026-11-23 04:37:21.759|1
3|0003_master.sql|0E1FF67C4A777B20BF2589F7D6708E14875DC89ADD431D02DE8CF518D5A9D1DE|2026-11-23 04:37:21.825|1
4|0004_upstream.sql|DF82EAA935D8A38B402119D0DDD99350407FDE5ED709DD34C67B36DFE4938273|2026-11-23 04:37:21.848|1
5|0005_codes.sql|5E1E1EF199C62658BE10761319392A2AAC0E19771D64D8CCB8F388CE3EC4EEDC|2026-11-23 04:37:21.889|1
6|0006_upload.sql|8150CED244D824BE4DD78D5E80C999EA8E702A3B68C7433E75740569F0928FBE|2026-11-23 04:37:21.931|1
7|0007_m1_source_master.sql|96D399B8CE9D834DCC119729059D1676152A2108A25876C2A94FFCE0E8715A39|2026-11-23 04:37:21.969|1
```

checksum 为迁移文件字节 SHA-256（MigrationRunner 计算）。**与 M0 报告快照的差异说明**：0002–0005 checksum 与 M0 报告不同，原因是 T4 卫生提交（2495e47，经控制器授权）为这四个文件补齐 EOF 换行（各 1 字节，无任何 DDL 语义变更，git diff 可核）；0001/0006 checksum 与 M0 一致。0007 为 M1 新增增量迁移，不修改 0001–0006 的 DDL 语义（MigrationRunner 校验摘要；对已部署 M0 库，0002–0005 的换行变更会触发 checksum 不一致即停——M1 交付面向新建库，存量 M0 库升级须按 checksum 账本流程处理，列入携带项说明）。二次运行幂等跳过与 checksum 不一致即停由 `MigratorTests.Second_run_is_idempotent_skip`、`Checksum_mismatch_stops_and_does_not_partially_apply` 覆盖（本次 180/180 中通过）。

4. 清理：`ALTER DATABASE [TraceFlow_GateReport_213633576] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [TraceFlow_GateReport_213633576]`（退出码 0）。
5. 残留核查（真实查询）：
   - `SELECT name FROM sys.databases WHERE name LIKE 'TraceFlow_Test%'` → **0 行**（双变体全量运行后无测试库残留）。
   - DROP 后 `WHERE name LIKE 'TraceFlow_Test%' OR name LIKE 'TraceFlow_GateReport%'` → **0 行**。
   - `WHERE name LIKE 'TraceFlow%'` → **1 行：TraceFlow_Dev**。**与简报"须 0 行"的偏差声明**：TraceFlow_Dev 为 Task 15 浏览器运行时验证建立的开发库（含验证数据，控制器明令保留不得删除），非测试残留；测试/门禁临时库（TraceFlow_Test_*、TraceFlow_GateReport_*）确认为 0 残留。

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
