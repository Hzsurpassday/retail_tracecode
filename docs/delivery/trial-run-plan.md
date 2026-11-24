# ≥7 天试运行计划（M4 收尾 / Task 6）

> **事实声明（必须先读）**：**本次交付未实际执行 7 天试运行**。本文是可直接执行的试运行协议；
> 脚本已按 §8 的冒烟方式真实验证（2026-11-24，库 `TraceFlow_Trial_Smoke_04024924`，启动/取证/汇总/停止全部通过，
> 冒烟后库已删除）。完整 ≥7 天试运行需由用户按本计划执行并留存证据，任何"已完成 7 天试运行"的说法在没有
> 7 天证据文件前都不成立。

## 1. 目的与范围

- 目的：在可控的独立实例上连续运行 ≥7 天，证明作业队列、告警、对账与审计读数在真实时间跨度下可用、可解释。
- 范围：`traceflow/tools/trial/` 下的启动 / 取证 / 停止 / 汇总脚本与只读观测端点
  （`/api/v1/ops/metrics`、`/api/v1/ops/alerts`、`/api/v1/dashboard`、`/api/v1/health`）。
- 不在范围：真实平台（G-TOP/G-ERP/G-POS/G-RETURN）联调；试运行通道 **恒为 MOCK**。

## 2. 前置条件

| 项 | 要求 |
| --- | --- |
| 数据库 | 独立实例/独立库，库名 `TraceFlow_Trial_<yyyyMMdd>`（或 `TraceFlow_Trial_*`）；**不得**使用 `TraceFlow_Dev`、`TraceFlow_M2Manual`、`TraceFlow_M2Gate`、`TraceFlow_M3Ui` 与任何 `TraceFlow_Test_*` |
| 通道 | `TraceFlow__Env=MOCK`（脚本注入），禁止 LIVE，禁止向正式平台上传虚构交易 |
| 管理员 | 试运行专用账号，密码只从环境变量 `TRACEFLOW_TRIAL_ADMIN_PASSWORD` 读取（≥10 位，不入 Git/日志/文档） |
| 端口 | 默认 `5080`，只绑 `127.0.0.1`；端口可经 `-Port` 修改 |
| 构建 | `dotnet build traceflow/TraceFlow.sln -c Release`（0 警告） |
| 环境变量 | `TRACEFLOW_TRIAL_CONN`（基础连接串，**不含** Initial Catalog）、`TRACEFLOW_TRIAL_ADMIN`、`TRACEFLOW_TRIAL_ADMIN_PASSWORD` |

宿主的 `appsettings.json` 只允许 `AllowedHosts=localhost`；`Start-Trial.ps1` 会向子进程额外注入
`AllowedHosts=localhost;127.0.0.1`，否则回环地址上的健康检查与取证请求会被 HostFiltering 以 400 拒绝。
该注入只作用于子进程，不改仓库配置。

## 3. 启动（第 0 天）

```powershell
$env:TRACEFLOW_TRIAL_CONN = '<Server=...;不含 Initial Catalog>'
$env:TRACEFLOW_TRIAL_ADMIN = 'trialadmin'
$env:TRACEFLOW_TRIAL_ADMIN_PASSWORD = '<≥10 位，勿写入任何文件>'

cd traceflow\tools\trial
.\Start-Trial.ps1 -SeedAdminUser trialadmin -Port 5080      # 建库 → 迁移 0001–0009 → 建管理员 → 启动宿主
.\Collect-Evidence.ps1 -Once                                # 立即自检一次，确认 JSONL 出现 "ok":true
```

脚本行为：库名自检（正则 + 保护库黑名单）→ 创建库（已存在则复用）→ `TraceFlow.DbMigrator` 迁移 →
可选 `init-admin` → `Start-Process` 启动 `--web-only` 宿主 → 轮询 `/api/v1/health`（≤60s）→ 打印"就绪"。
PID 写入 `.runtime/host.pid`，宿主日志写入 `.runtime/host-<时间戳>.log`。健康检查失败时打印日志尾部并以非 0 退出（不回显连接串）。

启动后需要常驻采集（建议独立窗口 / 计划任务）：

```powershell
.\Collect-Evidence.ps1 -IntervalMinutes 60 -Days 7      # 每小时 1 次，采满 7 天；Ctrl-C 可提前结束
```

## 4. 每日检查项（建议每天同一时间，5–10 分钟）

| 检查项 | 数据来源 | 判读标准 |
| --- | --- | --- |
| 告警 | `GET /api/v1/ops/alerts`（证据 JSONL 的 `alerts`） | 每条告警可解释（有对应暂停/失败/积压/对账原因）；出现 `CHANNEL_PAUSED` 需记录原因与解除人 |
| 作业积压 | `GET /api/v1/ops/metrics` → `jobs.backlog` / `jobs.pending` | 不应持续增长；`backlog > 100` 会触发 `JOB_BACKLOG_HIGH`，需说明原因 |
| 24h 失败 | `jobs.failed` / `jobs.failureRate24h` / `failedJobs24h` | 失败数可以非 0，但必须能逐条解释；不允许"未解释 FAILED 累积" |
| 租约回收 | `leaseRecoveries24h` | 出现即需检查 Worker 存活与单次作业时长 |
| 对账 | `metrics.recon.incomplete24h`、`recon.openDifferences`、`reconciliation-runs?status=INCOMPLETE` | INCOMPLETE 必须落到具体窗口与平台证据；开放差异需有归属人与下一步 |
| 审计可读性 | `GET /api/v1/audit-logs`（需 `audit:read`） | 当日启动、作业失败/回收、对账、差异解决等动作可读且 actor/时间正确 |
| 采样连续性 | `New-TrialSummary.ps1` 的"缺口数" | 缺口（相邻样本间隔 > 2× 周期）应为 0；非 0 需写明原因 |

## 5. 中断演练（只在专用试运行实例上做）

### 5.1 宿主机重启（验证租约回收与作业续跑）

1. `.\Stop-Trial.ps1`（保留库，不删数据）；
2. `.\Start-Trial.ps1 -Port 5080`（同一 `-DbName`，不加 `-SeedAdminUser`）；
3. 观察 `GET /api/v1/ops/metrics`：`leaseRecoveries24h` 增加；`jobs.pending` 先降后归零；
4. 在 `GET /api/v1/ops/jobs` 与 `GET /api/v1/audit-logs` 中确认原作业由 PENDING/RUNNING 继续到 DONE，
   回收动作有 `job.lease.recovered` 审计；
5. 记录：停止时刻、恢复时刻、回收租约数、受影响作业数、最终状态。

### 5.2 数据库短暂不可用

1. 仅在**专用试运行实例**上，由 DBA 短暂停库/断连（≤2 分钟），**不得**对生产库或保护库操作；
2. 预期：宿主记录连接错误、`/api/v1/health` 的 `data.db` 变为 `UNAVAILABLE`、取证样本记为 `ok:false` 并带 `status`/`error`；
3. 恢复后：`/api/v1/health` 回到 `db=OK`，作业继续消化积压；
4. 记录：故障窗口、错误文本（已脱敏）、`health` 前后变化、恢复所需时间。

## 6. 取证、留存与汇总

- 证据：`traceflow/tools/trial/.runtime/evidence/YYYY-MM-DD.jsonl`，每行一次采样：
  `{tsUtc, ok, status, metrics?, alerts?, alertsCount?, jobsFailed24h?, backlog?, dashboard?, error?}`。
  失败采样只记录、不中断循环；**不含密码、cookie、CSRF token**。
- 日志：`traceflow/tools/trial/.runtime/host-<时间戳>.log` / `.err.log`；PID 与库名记录在 `.runtime/host.json`。
- `.runtime/` 已被 `traceflow/tools/trial/.gitignore` 忽略，**不要提交**；7 天结束后把证据目录整体拷贝到交付证据位置归档。
- 汇总：

```powershell
.\New-TrialSummary.ps1 -IntervalMinutes 60 -ExpectedDays 7
```

  生成 `.runtime/summary.md`：覆盖日期数、样本数/失败样本数与失败原因 Top、缺口数、
  backlog 最大/中位、alertsCount 最大、出现过的 alert code 集合、是否出现对账 INCOMPLETE。
  覆盖不足 7 天时摘要首行会写"—未满 7 天，不构成试运行验收"。
- 结束时：`.\Stop-Trial.ps1`（保留库以便复核）或 `.\Stop-Trial.ps1 -DropDb`（删库，仅限 `TraceFlow_Trial_*`/`TraceFlow_Perf_*`）。

## 7. 验收标准

1. **覆盖 7 天**：证据覆盖连续 ≥7 个自然日，样本间隔缺口为 0（或每个缺口都有书面原因）；
2. **无未解释 FAILED 累积**：`jobs.failed` 不呈单调增长；每一条失败都有原因与处置记录；
3. **告警可解释**：出现过的每个 alert code 都能对应到当日事实与处理动作；
4. **重启后作业恢复**：§5.1 演练后作业继续完成、租约回收可见、审计可读；
5. **对账可读**：INCOMPLETE 运行与开放差异均有窗口、证据与归属；
6. **证据完整**：7 天 JSONL + `summary.md` + 宿主日志归档留存。

以上任一不满足，试运行不通过；结论只能写实际观察到的事实。

## 8. 本次交付的脚本验证记录（冒烟，非 7 天试运行）

| 步骤 | 命令 | 实测结果 |
| --- | --- | --- |
| 启动 | `Start-Trial.ps1 -DbName TraceFlow_Trial_Smoke_04024924 -Port 5089 -SeedAdminUser smokeadmin` | 建库 OK、迁移 applied 0001–0009、管理员创建 OK、健康就绪，退出码 0 |
| 取证 | `Collect-Evidence.ps1 -BaseUrl http://127.0.0.1:5089 -User smokeadmin -Once` | 写出 `2026-11-24.jsonl`，`ok:true`、`status:200`，含 `metrics`（env MOCK / backlog 0 / failed 0）与 `alerts:[]` |
| 汇总 | `New-TrialSummary.ps1` | 生成 `summary.md`，首行"未满 7 天，不构成试运行验收"，覆盖 1 天 / 样本 1 / 缺口 0 |
| 停止 | `Stop-Trial.ps1 -DropDb` | 宿主进程退出（含子进程 2 个）、库 `TraceFlow_Trial_Smoke_04024924` 已删除、PID/元数据文件已清理 |

冒烟期间发现并修复：宿主 `AllowedHosts` 只含 `localhost` 时，`http://127.0.0.1:<Port>/api/v1/health` 会被
HostFiltering 以 400 拒绝，首轮冒烟因此在健康轮询处失败（库与宿主已由 `Stop-Trial.ps1 -DropDb` 清理）；
现由 `Start-Trial.ps1` 向子进程注入 `AllowedHosts=localhost;127.0.0.1` 解决，复跑全流程通过。

**本次未执行 7 天试运行**，上述仅为脚本可执行性验证。