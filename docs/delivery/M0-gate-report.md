# M0 门禁报告（TraceFlow 单门店追溯系统）

日期：2026-11-22。分支：`m0-foundation`，代码基线 HEAD `73f3d6f`（Task 1–14）+ Task 15 文档交付。
配套：`docs/contracts/openapi.yaml`、`adapter-facts.md`、`source-mapping.md`、`data-dictionary.md`、`fixtures/`（8 个）、`docs/traceability/matrix.md`（114 需求行）。

## 结论（不标记 M0 全门禁通过）

**代码与测试完成（2019 降级运行全绿），2008 R2 实 instance 门禁与 G-* 取证门禁未满足。**

> **⚠ 降级声明（用户授权）：2008 R2 真实实例验证未完成（用户授权开发期降级至 SQL Server 2019；交付前必须真实 2008 R2 补验全部集成测试）。**
> 本报告不声称 2008 R2 兼容性已验证，不伪造 10.50.x 版本记录。AT-070（全套迁移/分页/队列在真实 2008 R2 执行）仍为未满足门禁。

## ① 测试实例真实版本（`SELECT @@VERSION` 实际输出）

命令：`sqlcmd -S localhost -E -C -Q "SET NOCOUNT ON; SELECT @@VERSION" -W -f 65001`

```
Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64)
	Sep 24 2019 13:48:23
	Copyright (C) 2019 Microsoft Corporation
	Enterprise Edition (64-bit) on Windows 10 Pro 10.0 <X64> (Build 26300: ) (Hypervisor)
```

实际引擎 **15.0.2000.5（SQL Server 2019）**，不是 10.50.x。集成测试经 `Sql2008Fixture` 的显式 opt-in 开关 `TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 在此引擎运行（用户授权的开发期降级）。

**降级开关负向验证（真实运行）**：不设 `TRACEFLOW_TEST_DB_ALLOW_NEWER` 时，版本门禁如实拒绝 2019：

```
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
  dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~MigratorTests"
→ 失败（预期）：System.InvalidOperationException : 集成门禁要求 SQL Server 2008 R2（10.50.x），实际 15.0.2000.5
```

**静态语法门禁（已运行并通过）**：`Sql2008CompatibilityTests` 3 用例全部通过——
`No_high_version_syntax_in_any_migration_or_embedded_sql`、`No_high_version_syntax_in_migration_scripts_full_constraint_list`（禁 OFFSET/FETCH、SEQUENCE、JSON_VALUE、OPENJSON、STRING_AGG、temporal、CREATE OR ALTER 等全清单）、`Row_number_pagination_works_on_2008r2`（ROW_NUMBER 分页在测试实例运行）。
**口径：语法门禁通过（syntax-gate-passed），2008 R2 运行时验证待补（runtime-2008-R2-pending）。**

## ② 全量验证命令与真实结果

环境：Windows 10 Pro（Build 26300）、.NET SDK 8（net8.0）、Node v24.22.1、SQL Server 2019 本机实例。全部命令在仓库根 `E:\Viding\Retail Trace Code` 执行。

### 1. 解决方案构建

```bash
dotnet build traceflow/TraceFlow.sln -c Release
```

真实输出（尾部）：

```
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:03.16
```

**结果：0 警告 0 错误（TreatWarningsAsErrors 生效），7 个项目全部编译。**

### 2. 单元测试

```bash
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --no-build
```

真实输出：

```
已通过! - 失败: 0，通过: 84，已跳过: 0，总计: 84，持续时间: 177 ms - TraceFlow.UnitTests.dll (net8.0)
```

**结果：84/84 通过。**（BillCodeGeneratorTests 6、TraceCodeNormalizerTests 7、QtyTests 8、UnitRatioTests 5、DpapiProtectorTests 2、UploadStateMachineTests 理论行 55、UnitTest1 1）

### 3. 集成测试（授权降级运行）

```bash
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build
```

真实输出：

```
已通过! - 失败: 0，通过: 44，已跳过: 0，总计: 44，持续时间: 2 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：44/44 通过（SQL Server 2019 15.0.2000.5，授权降级；2008 R2 待补验）。**
覆盖：ApiGateTests 7（含 AT-065/AT-066）、JobQueueTests 9、SendRecoveryTests 5、CodeConstraintTests 5、MasterConstraintTests 3、MigratorTests 3、Sql2008CompatibilityTests 3、UploadConstraintTests 4、IdempotencyStoreTests 2、PermissionAuditTests 2、UnitTest1 1。
每个测试类经 `Sql2008Fixture` 自建 `TraceFlow_Test_<guid>` 临时库并在 Dispose 时 DROP；运行后无残留（见 ③ 末尾核查）。

### 4. 前端构建

```bash
cd traceflow/src/TraceFlow.Web && npm run build   # tsc -b && vite build
```

真实输出（尾部）：

```
vite v5.4.21 building for production...
✓ 38 modules transformed.
../TraceFlow.Host/wwwroot/index.html                 0.30 kB │ gzip:  0.26 kB
../TraceFlow.Host/wwwroot/assets/index-Bm-HoEx3.js 167.81 kB │ gzip: 55.35 kB
✓ built in 632ms
```

**结果：tsc 0 错误，vite 产物输出至 TraceFlow.Host/wwwroot。**

## ③ 迁移 applied 列表与 checksum（临时库真实运行）

临时库生命周期（全部真实执行）：

1. `CREATE DATABASE [TraceFlow_GateReport_1209413116]`（sqlcmd，退出码 0）。
2. 运行 DbMigrator（`traceflow/db/tools/TraceFlow.DbMigrator/Program.cs`，用法 `<连接串> [脚本目录]`）：

```bash
dotnet run --project traceflow/db/tools/TraceFlow.DbMigrator -c Release --no-build -- \
  "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Initial Catalog=TraceFlow_GateReport_1209413116" \
  "E:/Viding/Retail Trace Code/traceflow/db/migrations"
```

真实输出：

```
Applied: 0001_core.sql, 0002_source.sql, 0003_master.sql, 0004_upstream.sql, 0005_codes.sql, 0006_upload.sql
Skipped:
```

3. 查询 `dbo.schema_version`（version,name,checksum,applied_at,success），真实行：

```
version|name|checksum|applied_at|success
1|0001_core.sql|3A7F4E3A4A1D910208FDA22B1BB083DDB10D1EE1D80E644328506788E59DEFB9|2026-11-22 07:29:31.253|1
2|0002_source.sql|F76D2A9D1FEB729E0D3604A112B3D24D520297851924D41AE80A9816387E3A54|2026-11-22 07:29:31.281|1
3|0003_master.sql|244EF6B9D3E294DDA74FA0397001458714D2AD51045093D25160507A58B481A5|2026-11-22 07:29:31.332|1
4|0004_upstream.sql|D51BF11958F15AD22429DF50072FB7A5E3E486388EF1CCD5297603DBE9DCB5DD|2026-11-22 07:29:31.352|1
5|0005_codes.sql|EA85C6822452E594F1453501FFE6960D44163BF208BEAB999F50A61056BBF384|2026-11-22 07:29:31.389|1
6|0006_upload.sql|8150CED244D824BE4DD78D5E80C999EA8E702A3B68C7433E75740569F0928FBE|2026-11-22 07:29:31.431|1
```

checksum 为迁移文件字节 SHA-256（MigrationRunner 计算）；6/6 success=1。二次运行幂等跳过与 checksum 不一致即停由 `MigratorTests.Second_run_is_idempotent_skip`、`Checksum_mismatch_stops_and_does_not_partially_apply` 覆盖（本次 44/44 中通过）。

4. 清理：`ALTER DATABASE [TraceFlow_GateReport_1209413116] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [TraceFlow_GateReport_1209413116]`（退出码 0）。
5. 残留核查（真实查询）：`SELECT name FROM sys.databases WHERE name LIKE 'TraceFlow%'` → **0 行**（无 TraceFlow_GateReport_*、无 TraceFlow_Test_* 残留）。说明：DROP 后首次列表查询曾短暂捕获到该库名（teardown 时序），复查确认已删除，最终核查为空。

## ④ 未满足项清单

### 取证门禁（全部未取证 → 对应真实通道禁用）

| 门禁 | 状态 | 禁用后果 |
| --- | --- | --- |
| G-ERP | 未取证 | 真实 ERP 生产同步禁用；KSOA 列映射全部"候选"（source-mapping.md），不猜字段 |
| G-TOP | 未取证 | 真实平台发送禁用；fixtures/top/* 为 Mock 形状（_verified:false），不声称平台合同事实 |
| G-POS | 未取证 | 销售生产自动上报禁用（SCOPE-09：POS 尚未扫码落库样例） |
| G-RETURN | 未取证 | 未验证退货类型不生产上报；采购退货显示未接入（DEF-03） |
| G-OPENING | 未取证 | 期初歧义业务阻断，不虚构历史采购 |
| G-DEPLOY | 未取证 | 不启用生产发送；HTTPS 强制/服务账号/备份恢复待部署核实 |

### 运行时门禁

- **2008 R2 真实实例验证未完成（AT-070）**：本报告全部数据库证据来自 SQL Server 2019（授权降级）。交付前必须在真实 10.50.x 实例重跑全部 44 个集成测试并替换本报告 ①③ 证据。静态语法门禁已通过，不能替代运行时验证。

### M1–M4 功能缺口（按追踪矩阵逐条列明，此处汇总）

- **M1 未实现**：来源只读同步（SYNC-01/02/04/05）、主档来源与分类管理（SRC-03/MD-01/02）、自动匹配与人工确认流程（MATCH-01/02/04/05、ENT-01）、导入暂存/预览/提交（MD-03）、设置/审计日志/作业查询 API、ERP 只读账号取证（AT-001）。
- **M2 未实现**：上游下载（UP-01/02、SYNC-03 逻辑）、入库自动关联与码级分配（IN-01…07/09）、资格冻结与自动建 102 任务的完整业务事务（IN-08 仅具备事务内入队与占用唯一基础）、补配 UI（UI-11）。
- **M3 未实现**：销售接收/核验/依赖/自动 321（SALE-01…05）、退货识别与人工确认（RET-01…04）、平台上传/回查/部分成功/未知受理的发送协议（TOP-01…04、IDEM-03/05 判定逻辑）、限流预算与回查退避（JOB-03/04）、通道暂停（JOB-05）。M0 的 `upload-dispatch`/`bill-query` 作业种类只有表结构与恢复路径，无处理器。
- **M4 未实现**：十页操作闭环（UI-02…10/13）、日对账（REC-01/03 逻辑）、CSV 导出（DEF-07）、性能基准（NFR-03/04）、连续运行 ≥7 天、备份恢复演练与文档（NFR-07/08）、完整指标告警（NFR-06）。
- **文档缺口**：AUTH-02 初始化管理员方式已实现（`--init-admin`），写入正式部署文档属 G-DEPLOY/M4 交付。

## 缺口与边界（M0 交付报告必须原样声明）

- G-ERP/G-TOP/G-POS/G-RETURN/G-OPENING/G-DEPLOY 全部未取证：真实 ERP 同步、真实平台发送、销售自动上报、退货上报均禁用；M0 只有 Mock/fixture 能力。
- 上传发送协议（send_attempt 短事务→HTTP→T3）、限流预算、TOP 签名在 M2/M3 实现；M0 的 `upload-dispatch`/`bill-query` 作业种类只有表结构与恢复路径，无处理器。
- 连续运行 ≥7 天、备份恢复演练、性能基准属 M4；M0 不声称。