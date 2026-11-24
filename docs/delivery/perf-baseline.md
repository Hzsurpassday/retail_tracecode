# 性能基准（NFR-03 / AT-075）

> **事实声明（必须先读）**：本文数值来自本机 **SQL Server 2019（15.0.2000.5，用户授权的降级环境）** 的真实运行；
> **SQL Server 2008 R2 尚未验证（AT-070 未完成）**。本文不声称任何 2008 R2 运行记录，也不把降级环境的结果
> 当作 2008 R2 证据。

## 1. 运行信息

| 项 | 值 |
| --- | --- |
| 运行日期（UTC） | 2026-11-24 02:03（本地 2026-11-24 10:03） |
| 结果文件 | `traceflow/tools/TraceFlow.OpsTool/perf-results.json`（同目录提交，字段 `generatedAtUtc` 可核对） |
| 基准库 | `TraceFlow_Perf_20261124020247`（运行结束已自动删除，未给 `--keep`） |
| 引擎 `SERVERPROPERTY('ProductVersion')` | `15.0.2000.5`（SQL Server 2019；`engineVerified2008R2=false`） |
| 机器 | `Microsoft Windows 10.0.26300`，`ProcessorCount=16` |
| .NET | 运行时 `.NET 8.0.31`；SDK `8.0.425` |
| 迁移 | `MigrationRunner` 执行 `0001–0009` 全部应用（applied=9, skipped=0） |
| 种子耗时 | 10.8s（`SqlBulkCopy`，BatchSize=5000） |
| 业务时间窗 | 2026-10-25T16:03:48Z ~ 2026-11-24T02:02:48Z（最近 30 天分布） |

## 2. 数据量

| 表 | 行数 |
| --- | --- |
| `product` | 10,000（`spid=P0000001…`，`barcode=69…` 唯一） |
| `source_snapshot`（`document_type=SALE`，`completeness_status=COMPLETE`，`version=1`） | 2,000 |
| `source_line`（`event_kind=SALE`，每单 50 行） | 100,000 |
| `trace_code`（码串以 `69` 开头、全局唯一） | 100,000 |
| `code_event`（`SALE/OUT`，`env='MOCK'`） | 100,000 |
| `upload_task`（`Succeeded` / `match_state=COMPLETE`） | 2,000 |
| `verification`（`is_current=1`，指向上述任务） | 2,000 |

种子在写入后自检上述计数（任一不符即失败，不带假数据跑基准）。

## 3. 实测结果（每端点 warmup=3、iterations=30，顺序循环）

判定口径：`p95 = sorted[ceil(0.95*n)-1]`（nearest-rank，**不做插值**）；`pass = 所有样本 HTTP 2xx 且 p95 ≤ 2000ms`。

| 端点 | p50 (ms) | p95 (ms) | max (ms) | 样本 | 状态码 | 结论 |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| `GET /api/v1/pos-sales?page=1` | 114.8 | **125.6** | 127.7 | 30 | 200 | PASS |
| `GET /api/v1/pos-sales?page=20` | 116.7 | **126.3** | 129.6 | 30 | 200 | PASS |
| `GET /api/v1/returns?page=1` | 7.8 | **12.6** | 28.1 | 30 | 200 | PASS |
| `GET /api/v1/upload-tasks?page=1` | 12.8 | **19.5** | 20.2 | 30 | 200 | PASS |
| `GET /api/v1/codes?q=6900` | 113.5 | **129.3** | 132.2 | 30 | 200 | PASS |
| `GET /api/v1/ops/metrics` | 8.8 | **17.9** | 19.7 | 30 | 200 | PASS |
| `GET /api/v1/dashboard` | 15.5 | **20.8** | 28.7 | 30 | 200 | PASS |

- 7 个计时端点全部 PASS（p95 上限 2000ms，最差 129.3ms，余量约 15 倍）。
- 对账触发（不设 p95 门禁，仅记录）：`POST /api/v1/reconciliation-runs` = **39.2ms**，HTTP 202。
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

## 5. 复现命令

```bash
dotnet run --project traceflow/tools/TraceFlow.OpsTool -c Release -- perf \
  --base "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" \
  --products 10000 --documents 2000 --sale-lines 100000 --iterations 30 --warmup 3 \
  --out "traceflow/tools/TraceFlow.OpsTool/perf-results.json"
```

退出码：`0` = 全部端点 p95 ≤ 2s 且样本 2xx；`1` = 存在 FAIL（结果文件照常写出，不缩小数据量、不裁剪端点）。
加 `--keep` 可保留基准库（库名 `TraceFlow_Perf_<yyyyMMddHHmmss>`）供人工复核，用完必须用
`drop-db <dbName> --yes` 清理。

## 6. 结论与遗留

- **NFR-03（本机 SQL 2019 降级环境）：满足**——10000 商品 / 100000 销售行下 7 个核心只读端点 p95 均远低于 2s。
- **AT-070 / 2008 R2：未验证**（未完成）。需在真实 SQL Server 2008 R2 实例上重跑本基准与全量集成测试后方可关闭。
- 基准只覆盖只读查询与一次对账入队；平台侧（G-TOP/G-ERP）仍为 MOCK/fixture，真实平台时延不在本基准范围内。