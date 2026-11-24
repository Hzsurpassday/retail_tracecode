# 性能基准（NFR-03 / AT-075）

> **事实声明（必须先读）**：本文数值来自本机 **SQL Server 2019（15.0.2000.5，用户授权的降级环境）** 的真实运行；
> **SQL Server 2008 R2 尚未验证（AT-070 未完成）**。本文不声称任何 2008 R2 运行记录，也不把降级环境的结果
> 当作 2008 R2 证据。

## 1. 运行信息

| 项 | 值 |
| --- | --- |
| 运行日期（UTC） | 2026-11-24 03:13（本地 2026-11-24 11:13） |
| 结果文件 | `traceflow/tools/TraceFlow.OpsTool/perf-results.json`（同目录提交，字段 `generatedAtUtc` 可核对） |
| 基准库 | `TraceFlow_Perf_20261124031241`（运行结束已自动删除，未给 `--keep`） |
| 引擎 `SERVERPROPERTY('ProductVersion')` | `15.0.2000.5`（SQL Server 2019；`engineVerified2008R2=false`） |
| 机器 | `Microsoft Windows 10.0.26300`，`ProcessorCount=16` |
| .NET | 运行时 `.NET 8.0.31`；SDK `8.0.425` |
| 迁移 | `MigrationRunner` 执行 `0001–0009` 全部应用（applied=9, skipped=0） |
| 种子耗时 | 10.2s（`SqlBulkCopy`，BatchSize=5000，全程单事务） |
| 业务时间窗 | 2026-10-25T17:13:42Z ~ 2026-11-24T03:12:42Z（最近 30 天分布） |

## 2. 数据量

| 表 | 行数 |
| --- | --- |
| `product` | 10,000（`spid=P0000001…`，`barcode=69…` 唯一） |
| `source_snapshot`（`document_type=SALE`，`completeness_status=COMPLETE`，`version=1`） | 2,000 |
| `source_line`（`event_kind=SALE`，每单 50 行） | 100,000 |
| `trace_code`（码串以 `69` 开头、全局唯一） | 100,000 |
| `code_event`（`SALE/OUT`，`env='MOCK'`） | 100,000 |
| `upload_task`（`SALE`：`RETAIL_321` / `Succeeded` / `match_state=COMPLETE`） | 2,000 |
| `upload_task`（退货：`CONSUMER_RETURN_116`，`Succeeded`/`Failed`/`Blocked` 三态循环） | 200 |
| `upload_part`（退货任务各 1 个分片） | 200 |
| `return_confirmation`（`CONFIRMED` / `REJECTED`） | 133 |
| `verification`（`is_current=1`，指向上述 SALE 任务） | 2,000 |

种子在写入后自检上述计数（任一不符即失败，不带半套数据跑基准）。整批种子（含 `SqlBulkCopy`）在**一个
`SqlTransaction`** 内完成，自检通过才提交；失败整体回滚，且只有提交成功后 `--keep` 才会保留基准库。

**退货负载说明**：本轮种子新增 200 个 `CONSUMER_RETURN_116` 任务（每个 1 个分片；每 3 个一组为
已确认 / 已拒绝 / 待确认，其中前两组带 `return_confirmation` 行），因此 `GET /api/v1/returns` 不再是空表，
其 p95 从上一版的空表数值（12.6ms）上升到下表 55.3ms —— 这是**真实负载**下的数值，不是回退。

## 3. 实测结果（每端点 warmup=3、iterations=30，顺序循环）

判定口径：`p95 = sorted[ceil(0.95*n)-1]`（nearest-rank，**不做插值**）；`pass = 所有样本 HTTP 2xx 且 p95 ≤ 2000ms`。

| 端点 | p50 (ms) | p95 (ms) | max (ms) | 样本 | 状态码 | 结论 |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| `GET /api/v1/pos-sales?page=1` | 107.8 | **131.9** | 139.9 | 30 | 200 | PASS |
| `GET /api/v1/pos-sales?page=20` | 98.8 | **110.8** | 114.5 | 30 | 200 | PASS |
| `GET /api/v1/returns?page=1` | 50.2 | **55.3** | 57.9 | 30 | 200 | PASS |
| `GET /api/v1/upload-tasks?page=1` | 16.3 | **19.3** | 19.3 | 30 | 200 | PASS |
| `GET /api/v1/codes?q=6900` | 118.1 | **134.8** | 140.4 | 30 | 200 | PASS |
| `GET /api/v1/ops/metrics` | 8.6 | **11.5** | 14.1 | 30 | 200 | PASS |
| `GET /api/v1/dashboard` | 15.6 | **21.4** | 29.1 | 30 | 200 | PASS |

- 7 个计时端点全部 PASS（p95 上限 2000ms，最差 134.8ms，余量约 14.8 倍）。
- 对账触发（不设 p95 门禁，仅记录）：`POST /api/v1/reconciliation-runs` = **58.7ms**，HTTP 202。
- 进程最终退出码 `0`（全部 PASS）。

## 4. 偏差与说明（如实记录）

1. **码前缀用 `q=6900` 而非 `q=69`**：`CodesEndpoints.MinPrefixLength=4`，`q=69` 会被端点以 400 拒绝
   （`q 至少 4 个字符`）；种子码串均为 `69` + 12 位条码序号 + 6 位行序号，全部以 `6900` 开头，
   故 `q=6900` 命中全部 100,000 条码，仍覆盖 NFR-03 的前缀检索路径。
2. **对账路由为 `POST /api/v1/reconciliation-runs`**（不是 `/api/v1/reconciliation/runs`），并携带
   `Idempotency-Key` 头；body 为 `{windowStart, windowEnd, businessType}`，窗口取自种子中最新单据日的前一日。
3. 基准库名遵循 `^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$`；脚本/工具对 `TraceFlow_Dev`、
   `TraceFlow_M2Manual`、`TraceFlow_M2Gate`、`TraceFlow_M3Ui`、`TraceFlow_Test_*` 与系统库一律拒绝（退出码 2）。
4. 基准数据只写 `TraceFlow_Perf_*` 专用库，绝不写生产库；宿主以 `WebApplicationFactory` 进程内启动，
   `TraceFlow:Env=Mock`、移除全部 `IHostedService`（不跑后台作业、不外呼）。
5. `--out` 缺省时由工具向上查找含 `traceflow/db/migrations` 的仓库根，落到
   `<repo>/traceflow/tools/TraceFlow.OpsTool/perf-results.json` 并按**绝对路径**打印；找不到仓库根则退出码 2。

## 5. 复现命令

```bash
dotnet run --project traceflow/tools/TraceFlow.OpsTool -c Release -- perf \
  --base "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
  --products 10000 --documents 2000 --sale-lines 100000 --iterations 30 --warmup 3
  # 缺省输出即仓库根下 traceflow/tools/TraceFlow.OpsTool/perf-results.json（工具打印绝对路径）
```

退出码：`0` = 全部端点 p95 ≤ 2s 且样本 2xx；`1` = 存在 FAIL（结果文件照常写出，不缩小数据量、不裁剪端点）。
加 `--keep` 可保留基准库（库名 `TraceFlow_Perf_<yyyyMMddHHmmss>`）供人工复核，用完必须用
`drop-db <dbName> --yes` 清理；种子失败/回滚时即使给了 `--keep` 也照样删库。

## 6. 结论与遗留

- **NFR-03（本机 SQL 2019 降级环境）：满足**——10000 商品 / 100000 销售行（外加 200 个退货任务）下 7 个核心只读端点
  p95 均远低于 2s。
- **AT-070 / 2008 R2：未验证**（未完成）。需在真实 SQL Server 2008 R2 实例上重跑本基准与全量集成测试后方可关闭。
- 基准只覆盖只读查询与一次对账入队；平台侧（G-TOP/G-ERP）仍为 MOCK/fixture，真实平台时延不在本基准范围内。
