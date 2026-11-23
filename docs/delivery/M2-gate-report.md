# M2 门禁报告（TraceFlow 上游单据下载与入库码级分配）

日期：2026-11-23。分支：`m2-upstream-allocation`，代码基线 HEAD `66cb33d`（Task 1–15；本报告全部运行即在该 HEAD 上执行，本报告随 docs 提交入库，提交发生在运行之后）。
配套：docs/contracts/openapi.yaml（M2 回写）、data-dictionary.md（0008 增量）、ksoa-adapter-contract.md（§5 ywdjbh）、top-contract.md（M2 定稿状态）、docs/traceability/matrix.md（M0–M2）、docs/delivery/M2-rulings-and-carryforwards.md（34 条裁决与携带项）。

## 结论（不标记 M2 全门禁通过）

**代码与测试完成（2019 降级运行全绿，双 collation 变体），2008 R2 实 instance 门禁与 G-ERP/G-TOP 取证门禁未满足；M2 未向任何真实平台发送数据，不声称任何上传成功或平台受理结果。**

> **⚠ 降级声明（用户授权）：2008 R2 真实实例验证未完成（用户授权开发期降级至 SQL Server 2019；交付前必须真实 2008 R2 补验全部集成测试，AT-070）。**
> 本报告不声称 2008 R2 兼容性已验证，不伪造 10.50.x 版本记录。
> **G-ERP／G-TOP fixture-only：M2 上游下载、企业同步、限流预算、错误码解析仅经本地 JSON fixture 验证（FixtureTopGateway／FixtureKsoaAdapter）；真实 KSOA 只读连接、平台 listupout/getentinfo/getbyrefentid 实测、限值与计费口径取证均未做，LIVE 通道保持禁用（RealTopGateway／RealKsoaAdapter 拒绝认证，LIVE 环境请求被拒）。**
> **本地保障 ≠ 平台事实：本报告的"资格冻结／预算计数／渠道暂停／任务状态"全部为本系统内部的本地保障性结论，不构成任何平台侧受理、成功或对账证据。**

## ① 测试实例真实版本（SELECT @@VERSION 实际输出）

命令：`sqlcmd -S localhost -E -C -Q "SET NOCOUNT ON; SELECT @@VERSION" -W -f 65001`

真实输出：

```
Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64)
	Sep 24 2019 13:48:23
	Copyright (C) 2019 Microsoft Corporation
	Enterprise Edition (64-bit) on Windows 10 Pro 10.0 <X64> (Build 26300: ) (Hypervisor)
```

实际引擎为 SQL Server 2019（15.0.2000.5），非目标 2008 R2（10.50.x）。降级开关口径同 M0/M1 报告：`TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 显式授权（用户裁决）。**负向验证（本次 Task 15 真实重跑）**：不设该开关、仅设 `TRACEFLOW_TEST_DB` 时运行 `--filter "FullyQualifiedName~MigratorTests"`，三个用例均在建库门禁处失败，真实输出：

```
  失败 TraceFlow.IntegrationTests.Migrations.MigratorTests.Second_run_is_idempotent_skip [1 ms]
  System.InvalidOperationException : 集成门禁要求 SQL Server 2008 R2（10.50.x），实际 15.0.2000.5
    at TraceFlow.IntegrationTests.Sql2008Fixture.InitializeAsync() in E:\Viding\Retail Trace Code\traceflow\tests\TraceFlow.IntegrationTests\Sql2008Fixture.cs:line 43
失败!  - 失败:     6，通过:     0，已跳过:     0，总计:     6，持续时间: 11 ms - TraceFlow.IntegrationTests.dll (net8.0)
```

（汇总行计数为 6：同一组三个用例名在失败与清理报告中各出现一次，无任何用例通过；该次运行未创建测试库，未产生残留。）

## ② 全量验证命令与真实结果

环境：Windows 10 Pro（Build 26300）、.NET SDK 8（net8.0）、Node v24.12.0（本机实测；任务环境声明为 v24.22.1，以实测为准）、SQL Server 2019 本机实例。全部命令在仓库根 `E:\Viding\Retail Trace Code` 执行。五项全量运行均绿；其后仅文档变更与 EOF 换行卫生，无代码语义变更。

### 1. 解决方案构建

```bash
dotnet build traceflow/TraceFlow.sln -c Release
```

真实输出（尾部）：

```
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:03.13
```

**结果：0 警告 0 错误（TreatWarningsAsErrors 生效），全部项目编译。**

### 2. 单元测试

```bash
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release --no-build
```

真实输出：

```
已通过! - 失败: 0，通过: 168，已跳过: 0，总计: 168，持续时间: 181 ms - TraceFlow.UnitTests.dll (net8.0)
```

**结果：168/168 通过**（M1 为 136，M2 净增 32）。M2 相关新增/扩充单元类（逐类用例数经 `dotnet test --list-tests` 实测核对，Theory 按 `InlineData` 展开后的用例数计；全部单元类合计 168，与上条汇总一致）：TopResponseParserTests 13、RateBackoffTests 2 methods / 6 cases（1 Theory×5 + 1 Fact）、TopGatewayTests 5（源码在 `traceflow/tests/TraceFlow.UnitTests/Top/TopGatewayTests.cs`）、SourceValueObjectTests 扩充至 25 用例（含 Daily_window/Gap_backfill/Code_set_hash 等 M2 值对象用例）。

### 3. 集成测试（授权降级，默认 collation）

```bash
TRACEFLOW_TEST_DB="Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
TRACEFLOW_TEST_DB_ALLOW_NEWER=1 \
dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release --no-build
```

真实输出：

```
已通过! - 失败: 0，通过: 287，已跳过: 0，总计: 287，持续时间: 1 m 27 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：287/287 通过（SQL Server 2019 15.0.2000.5，授权降级；2008 R2 待补验）。** M1 为 180，M2 净增 107。
M2 新增/扩充集成类（逐类用例数经 `dotnet test --list-tests` 实测核对，测试名与 matrix.md 一致）：AllocationServiceTests 11、AllocationConfirmTests 7、ClaimAndCancelTests 6、InboundMatchTests 12、InboundTaskFactoryTests 4、BillContractTests 3、UpstreamDownloadTests 10、EnterpriseSyncTests 7、InboundApiTests 8、M2ApiGateTests 4、M2CarryforwardApiTests 7、SettingsScopeApiTests 7、RateBudgetTests 5、TopClientServiceTests 4、ChannelPauseTests 1（`TraceFlow.IntegrationTests/Top/ChannelPauseTests.cs`）、Migration0008Tests 5；SettingsFailClosedTests 3 为存量类在 M2 口径下的复跑。
每个测试类经 `Sql2008Fixture` 自建 `TraceFlow_Test_<guid>` 临时库并在 Dispose 时 DROP（见 ③ 末尾残留核查）。

### 4. 集成测试（授权降级，BIN2 变体）

同上命令追加 `TRACEFLOW_TEST_DB_BIN2=1`（Sql2008Fixture 以 COLLATE Latin1_General_BIN2 建库）。

真实输出：

```
已通过! - 失败: 0，通过: 287，已跳过: 0，总计: 287，持续时间: 1 m 11 s - TraceFlow.IntegrationTests.dll (net8.0)
```

**结果：287/287 通过，与默认 collation 变体用例数一致，两变体均全绿**（码/外部 ID 的 BIN2 语义在 M2 的分配占用、上游单据 identity、企业改名版本化用例上同样成立）。默认 collation 轮尾部为其自身运行的记录（287/287 全部通过，数字正确）；上一版两轮粘贴的耗时同为 1 m 27 s 系同一机器连续运行的巧合，本轮仅将 BIN2 轮按重新实测的尾部校正为 1 m 11 s，其余数字未动。

### 5. 前端构建

```bash
cd traceflow/src/TraceFlow.Web && npm run build   # tsc -b && vite build
```

真实输出（尾部节选）：

```
✓ 44 modules transformed.
../TraceFlow.Host/wwwroot/assets/index-CmtvQHZt.js 214.99 kB │ gzip: 67.45 kB
✓ built in 1.07s
```

**结果：tsc 0 错误，vite 产物输出至 TraceFlow.Host/wwwroot（M1 为 42 modules，M2 新增上游单据页、入库页、改绑对话框等模块）。**

### 6. 浏览器端验证（控制器执行；本次 Task 15 未重跑）

M2 的页面级验证**已执行**，由控制器以 chrome-devtools MCP 在 `TraceFlow_M2Manual` 库上完成，非本报告作者在 Task 15 新增执行，本报告据实引用、不重复声称：

- **Task 13 流程 1–8**（上游单据导航与徽标、列表/详情码级数量、入库列表过滤、DJ-MAN-C 单据 分配→确认→建任务 TF2、双会话 409、已发送冻结只读、取消、控制台无错误），截图 `t13-shots/` 01–15；记录见 `.superpowers/sdd/2026-11-23-m2-upstream-allocation/task-13-report.md`。
- **Task 13-fix1**（上海时区格式修复后复验；44 modules 构建、287 集成、168 单元），记录见同目录 `task-13-fix1-report.md`。
- **Task 14 核验清单 1–6/§8 增补 1–7**（设置页服务账号与 DPAPI 面板、NULL 限值显示 `—（不设帽）`、预算/暂停只读视图、appsecret 掩码与 protectionScope=CurrentUser、商品/供应商改绑 422/REBIND、控制台干净），记录见 `task-14-report.md` §8，截图 `t14-shots/`。

**边界：浏览器验证覆盖的是本地 UI 与本地 API 行为，不涉及任何真实 TOP 调用；上述报告中的 409/422/冻结/取消均为本地链路证据。**

## ③ 迁移 applied 列表与 checksum（真实运行）

库生命周期（全部真实执行）：

1. `TraceFlow_M2Gate` 为本任务专用门禁库（`create_date` = 2026-11-23 22:36:06），运行前为空库；**按约束保留，不 DROP**。
2. 运行 DbMigrator（`traceflow/db/tools/TraceFlow.DbMigrator/Program.cs`，用法 `<连接串> [脚本目录]`）：

```bash
dotnet run --project traceflow/db/tools/TraceFlow.DbMigrator -c Release --no-build -- \
  "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Initial Catalog=TraceFlow_M2Gate" \
  "E:/Viding/Retail Trace Code/traceflow/db/migrations"
```

真实输出：

```
Applied: 0001_core.sql, 0002_source.sql, 0003_master.sql, 0004_upstream.sql, 0005_codes.sql, 0006_upload.sql, 0007_m1_source_master.sql, 0008_m2_upstream_allocation.sql
Skipped:
```

**执行期适配（如实记录）**：首次按简报命令（不传脚本目录）运行时，`Program.cs` 默认脚本目录为 `AppContext.BaseDirectory/db/migrations`，在 Release 输出目录下不存在，进程以 `DirectoryNotFoundException` 终止，未产生任何数据库变更；补传显式脚本目录后如上成功。0001–0008 的迁移文件字节在 Task 15 期间未改动（约束：迁移字节不可变）。

3. 查询 `dbo.schema_version`（version,name,checksum,applied_at,success），真实行（8/8 success=1）：

```
version|name|checksum|applied_at|success
1|0001_core.sql|3A7F4E3A4A1D910208FDA22B1BB083DDB10D1EE1D80E644328506788E59DEFB9|2026-11-23 14:36:25.302|1
2|0002_source.sql|90AABC500B52314EBB225AEB0CEA23991E608E8F38792A13723F63B68448FB43|2026-11-23 14:36:25.464|1
3|0003_master.sql|0E1FF67C4A777B20BF2589F7D6708E14875DC89ADD431D02DE8CF518D5A9D1DE|2026-11-23 14:36:25.576|1
4|0004_upstream.sql|DF82EAA935D8A38B402119D0DDD99350407FDE5ED709DD34C67B36DFE4938273|2026-11-23 14:36:25.623|1
5|0005_codes.sql|5E1E1EF199C62658BE10761319392A2AAC0E19771D64D8CCB8F388CE3EC4EEDC|2026-11-23 14:36:25.736|1
6|0006_upload.sql|8150CED244D824BE4DD78D5E80C999EA8E702A3B68C7433E75740569F0928FBE|2026-11-23 14:36:25.930|1
7|0007_m1_source_master.sql|96D399B8CE9D834DCC119729059D1676152A2108A25876C2A94FFCE0E8715A39|2026-11-23 14:36:25.984|1
8|0008_m2_upstream_allocation.sql|F3DC59F6F51AAAB2F07AA1B09C46A36B59896EEDC51794163784F5CA166C6EF0|2026-11-23 14:36:26.020|1
```

checksum 为迁移文件字节 SHA-256（MigrationRunner 计算）。**时区说明**：`applied_at` 由 `MigrationRunner` 以 `SYSUTCDATETIME()` 写入（UTC，见 `traceflow/db/tools/TraceFlow.DbMigrator/MigrationRunner.cs`），而上表对照的 `sys.databases.create_date`（22:36:06）为服务器本地时间（UTC+8），故 14:36 与 22:36 相差 8 小时属预期时区偏移，不是账本被篡改。**与 M1 报告快照的差异说明**：0001–0007 的 checksum 与 M1 报告完全一致（0002–0005 的 EOF 换行变更已在 M1 说明并入库）；0008 为 M2 新增增量迁移，其 checksum 为首次登记。二次运行幂等跳过与 checksum 不一致即停由 `MigratorTests.Second_run_is_idempotent_skip`、`Checksum_mismatch_stops_and_does_not_partially_apply` 覆盖（本次 287/287 中通过）。

4. 残留与保留核查（真实查询）：
   - `SELECT name, create_date FROM sys.databases WHERE name LIKE 'TraceFlow%'` → `TraceFlow_Dev`（11:29:06）、`TraceFlow_M2Gate`（22:36:06）、`TraceFlow_M2Manual`（21:35:21），另有 **29 个 `TraceFlow_Test_<guid>`**，其 `create_date` **全部为 2026-11-23 17:37:39**。
   - **偏差声明（如实记录，不符"0 残留"）**：这 29 个测试库均建于本次 Task 15 回归运行（22:2x 之后，见 M2Gate 22:36:06）之前约 5 小时，属**上一次会话遗留**，非本次双变体运行产生——本次默认与 BIN2 两轮全量运行前后计数一致（29→29），本次运行未新增残留。按约束未做任何 DROP（含未授权清理），遗留库保持原样并在此声明。
   - `TraceFlow_Dev`、`TraceFlow_M2Manual`、`TraceFlow_M2Gate` 为开发/手工验证/门禁用库，按控制器约束**保留不删**；`TraceFlow_Dev` 与 `TraceFlow_M2Manual` 本次只读/未触碰（本报告仅查询 `TraceFlow_M2Gate` 账本与 `sys.databases`）。

## ④ 未满足项清单

### 取证门禁

| 门禁 | 状态 | 禁用后果 |
| --- | --- | --- |
| G-ERP | 未取证（fixture-only） | 真实 ERP 同步禁用；采购单头供应商编号 `ywdjbh` 仅列候选（ksoa-adapter-contract.md §5），不作键或过滤依据 |
| G-TOP | 未取证 | 真实平台发送/上游下载/企业下载禁用；限值（top-budgets）为本地不设帽，fixture 形状非平台事实 |
| G-POS | 未取证 | 销售生产自动上报禁用 |
| G-RETURN | 未取证 | 退货生产上报禁用（M2 仅 fixture 事件链） |
| G-OPENING | 未取证 | 期初歧义业务阻断 |
| G-DEPLOY | 未取证 | 不启用生产发送；服务账号/HTTPS/备份恢复未核实 |

### 运行时门禁

- **2008 R2 真实实例验证未完成（AT-070）**：全部 DB 证据来自 SQL 2019（授权降级，双 collation 变体）。交付前必须在真实 10.50.x 实例重跑全部 287 集成用例并替换本报告 ①③ 证据。静态语法门禁（`Sql2008CompatibilityTests`，目录级扫描覆盖 0008）通过不能替代运行时验证。

### M2 范围内未满足/未闭环项

- **平台侧零证据**：M2 未发出任何真实 TOP 请求，无受理、成功、失败码、限额计费与对账证据；102 任务仅处于 Blocked/Ready（无 Sent/Succeeded 生产记录）。
- **限流预算**：`RateBudgetService` 的日/分/秒帽在未取证时保持 NULL（本地不设帽），仅验证计数、窗口、隔离与退避语义；平台真实限值待 G-TOP。
- **批次拆分**：`IDEM-03` 仅单 part 创建；多 part 拆分依赖平台单号/限额取证，顺延 M3。
- **ywdjbh 与供应商定位**：为候选列，`_verified:false`；G-ERP 取证前不作为唯一键。
- **已发送任务冻结**：本地状态机与取消守卫已覆盖；平台侧"发送中"真实语义（回查窗口、受理未知判定）待 M3+G-TOP。

## 缺口与边界（必须原样声明）

- **2008 R2 交付前补验**：本报告 ①③ 全部 DB 证据为 SQL 2019 15.0.2000.5（授权降级），AT-070 未完成，不得据此声称 2008 R2 兼容已验证。
- **契约跟随实现（R15b）**：openapi.yaml 的 M2 回写以已实现端点的真实响应形状为准（AllocationSaveResponse／ConfirmAllocationResponse／CancelAllocationResponse／MappingResolveResponse／ServiceAccountResponse／TopBudgetsResponse），未按旧 stub 反向修改代码。
- **浏览器验证归属**：Task 13/13-fix1/14 的页面验证由控制器执行并留证（t13-shots/、t14-shots/、对应报告 §8）；本报告不声称 Task 15 新跑浏览器验证，也不超出上述报告的事实范围。
- **本地保障 vs 平台事实**：M2 交付的是本地资格冻结、占用互斥、版本门禁（AT-067）、预算计数、渠道暂停与取消回退；平台受理与对账属 M3/M4，M2 不作任何平台成功声明。
- **DB-03 语法门禁口径**：0008 已进入 `Sql2008CompatibilityTests` 的目录级逐词扫描，但"必须存在"的 presence 白名单仍为 0001–0007，矩阵按实际测试代码如实标注。
- **遗留测试库**：29 个 `TraceFlow_Test_*`（2026-11-23 17:37:39 批次）为前次会话遗留，本次未清理、未新增，已如实声明。
- **连续运行/备份恢复/性能基准**属 M4，M2 不声称；`TraceFlow_M2Manual`／`TraceFlow_M2Gate` 按约束保留。
