# TraceFlow 试运行工具（Windows PowerShell 5.1）

本目录提供 ≥7 天试运行的启动、取证、停止与汇总脚本。全部脚本只连 `TraceFlow_` 目标库，
通道恒为 MOCK；连接串与管理员密码**只从环境变量读取**，脚本不内置任何凭据，也不会回显或落盘。

## 前置条件

- Windows + PowerShell 5.1（Windows 10/11 自带）；
- .NET 8 SDK，且已 `dotnet build traceflow/TraceFlow.sln -c Release`；
- 本机 SQL Server（集成认证）或由调用方自行提供连接串；
- 首次使用需要一个管理员账号（脚本可代建，密码只从环境变量读取）。

## 环境变量

| 变量 | 用途 |
| --- | --- |
| `TRACEFLOW_TRIAL_CONN` | 基础连接串（**不含** Initial Catalog，脚本自动拼目标库名） |
| `TRACEFLOW_TRIAL_ADMIN` | 取证登录用户名 |
| `TRACEFLOW_TRIAL_ADMIN_PASSWORD` | 管理员密码（≥10 位；仅进程内使用，不打印） |

## 命令顺序

```powershell
# 1) 启动试运行（库名默认 TraceFlow_Trial_<yyyyMMdd>，端口 5080，仅绑定 127.0.0.1）
.\Start-Trial.ps1 -SeedAdminUser trialadmin

# 2) 采集证据（先单次自检，再按 60 分钟周期采 7 天；Ctrl-C 结束）
.\Collect-Evidence.ps1 -Once
.\Collect-Evidence.ps1 -IntervalMinutes 60 -Days 7

# 3) 汇总证据
.\New-TrialSummary.ps1

# 4) 停止（保留库）
.\Stop-Trial.ps1
# 或停止并删库（仅 TraceFlow_Trial_* / TraceFlow_Perf_*）
.\Stop-Trial.ps1 -DropDb
```

## 参数速查

- `Start-Trial.ps1`：`-DbName`、`-BaseConn`、`-Port`、`-SeedAdminUser`
- `Collect-Evidence.ps1`：`-BaseUrl`（默认 `http://127.0.0.1:5080`）、`-User`、`-Password`、`-IntervalMinutes 60`、`-Days 7`、`-Once`
- `Stop-Trial.ps1`：`-DropDb`、`-DbName`、`-BaseConn`
- `New-TrialSummary.ps1`：`-EvidenceDir`、`-Out`、`-IntervalMinutes`、`-ExpectedDays`

## 证据与日志位置（均被 .gitignore 忽略）

- `.runtime/host.pid`、`.runtime/host.json`：宿主进程与库名记录；
- `.runtime/host-<时间戳>.log` / `.err.log`：宿主输出；
- `.runtime/evidence/YYYY-MM-DD.jsonl`：每行一次采样（`{tsUtc, ok, status, metrics?, alerts?, ...}`）；
- `.runtime/summary.md`：`New-TrialSummary.ps1` 生成的聚合摘要。

## 与宿主配置的关系

- `Start-Trial.ps1` 只给**子进程**注入环境变量（`ConnectionStrings__TraceFlow`、`TraceFlow__Env=MOCK`、
  `ASPNETCORE_URLS=http://127.0.0.1:<Port>`、`AllowedHosts=localhost;127.0.0.1`），不改仓库里的 `appsettings.json`。
- 宿主 `appsettings.json` 的 `AllowedHosts` 只有 `localhost`；试运行只绑回环地址 `127.0.0.1`，
  因此脚本会额外放行 `127.0.0.1`，否则健康检查与取证请求会被 HostFiltering 以 400 拒绝。

## 安全边界

- 保护库 `TraceFlow_Dev`、`TraceFlow_M2Manual`、`TraceFlow_M2Gate`、`TraceFlow_M3Ui` 与任何
  `TraceFlow_Test_*` 一律拒绝创建冲突与删除；`drop-db` 还需显式 `--yes`。
- 脚本只停止自己 PID 文件记录的进程（含其子进程）；进程名不是 `dotnet`/`TraceFlow.Host` 时拒绝强杀。
- 证据 JSONL 与日志不含密码、cookie、CSRF token。

完整协议与验收标准见 `docs/delivery/trial-run-plan.md`。