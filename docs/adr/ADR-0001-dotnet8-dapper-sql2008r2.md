# ADR-0001: .NET 8 + Dapper + SQL Server 2008 R2 技术基线

- 日期：2026-11-22
- 状态：已接受（M0 基础阶段裁定，写入路线图"已裁定的对齐决策"）
- 适用范围：TraceFlow 零售药店追溯系统 .NET 8 重建（全阶段 M0–M4）
- 依据：已批设计 `docs/superpowers/specs/2026-11-22-retail-pharmacy-trace-design.md` §1–2；PRD `docs/PRD-TraceFlow-单门店-V1.0.md`；路线图 `docs/superpowers/plans/2026-11-22-roadmap.md`

## 背景

单体药店店内部署，业务库必须兼容客户现存的 **SQL Server 2008 R2**（设计 §1.3）。首轮 Node/TS 代码（`retail_tracecode/`）仅作参考，按已批设计以 .NET 8 重建。本 ADR 固化六项基础技术决策，后续所有任务以此为准。

## 决策

### ① 数据访问：.NET 8 + Dapper + 手写 T-SQL（弃用 EF Core 8）

- **EF Core 8 的 SQL Server 提供程序支持基线高于 SQL Server 2008 R2**（其生成的分页等语句依赖 `OFFSET/FETCH` 等 2012+ 语法），无法满足业务库兼容要求，故弃用（设计 §1.3）。
- 采用 **Dapper 2.1.35 + 手写 T-SQL**：分页、原子认领、租约回收等全部语句按 2008 R2 语法编写，并**在真实 SQL Server 2008 R2 实例上验证**（DB-03 门禁；单测不足以证明认领原子性与事务正确性，数据库集成为必做项）。
- **2008 R2 语法禁区清单**（全部 DDL/DML 禁用，迁移脚本同样适用）：
  - `OFFSET … FETCH`（2012+；分页用 `ROW_NUMBER() OVER(...)` + 子查询/CTE）
  - `SEQUENCE` / `NEXT VALUE FOR`（2012+）
  - JSON 函数（`JSON_VALUE`/`JSON_QUERY`/`OPENJSON` 等，2016+）
  - `STRING_AGG`（2017+；聚合拼接用 `FOR XML PATH`）
  - `CREATE OR ALTER`（2016 SP1+；用存在性判断 + `CREATE`/`ALTER` 分支）
  - 其余 2008 R2 之后引入的语法一并禁用：`IIF`/`CONCAT`/`FORMAT`/`TRY_CONVERT`/`TRY_PARSE`（2012+）、`THROW`（2012+，用 `RAISERROR`）、`DROP IF EXISTS`（2016+）、`TRIM`（2017+）、`AT TIME ZONE`（2016+）。
- 依赖方向约束：`Domain` 零包引用；`Infrastructure → Application`（实现其接口）；`Host → Application`、`Host → Infrastructure`（仅 DI 装配）。

### ② 部署形态：单一发布单元 + `--worker-only` / `--web-only` 运行模式

- **单一发布单元**：Windows 服务自宿主（Kestrel + `UseWindowsService`，包 `Microsoft.Extensions.Hosting.WindowsServices` 8.0.1），同一二进制通过启动参数支持 `--worker-only`（仅后台作业）与 `--web-only`（仅 Web/API）模式，默认全功能。
- **与 PRD NFR-01"独立 Worker"的关系**：NFR-01 默认方案是 Web 与 Worker 分进程部署；**用户已批准方案 A（单一发布单元 + 运行模式开关）优先于 PRD 默认方案**。冲突优先级：用户明确确认 > PRD 已确认业务要求 > PRD 默认方案。
- **JOB-01 多实例要求仍然满足**：任务队列为**持久队列（SQL Server 表），不依赖进程内存**；认领采用原子 UPDATE + 租约 + fencing token，多个实例（如一台 `--web-only` + 一台 `--worker-only`，或多 Worker）并发认领安全，旧 Worker 迟到写回因 token 不符被拒绝。因此"独立 Worker"的实质诉求（持续运行、崩溃恢复、多实例隔离）由持久队列与模式开关共同满足，而非物理上强制两个发布包。

### ③ TOP 协议客户端：自研（M2 实现）

- 码上放心（阿里健康）TOP 协议客户端**自研**：官方 .NET SDK 年久失修（多年未维护，不适配 .NET 8 与现代 HTTP 栈），不引入。
- 自研范围：TOP 公共参数 + 签名、网关调用（`eco.taobao.com/router/rest`）、按接口独立限流（日/分钟/秒）+ AppKey 共享预算（计数器持久化，重启不清零）、错误分类。
- **签名算法与接口入参以 G-TOP 合同验证结果为准**：开发前用平台测试工具核实并留存证据，不凭记忆或旧 SDK 猜测；**实现排期在 M2**（`2026-11-22-m2-upstream-inbound.md`），M0 不实现。

### ④ 认证与授权：Cookie + 自管权限表 + `PasswordHasher<T>`

- 认证采用 **ASP.NET Core Cookie 认证**（店内部署、浏览器访问，无跨域 API 消费方，不需要 JWT）。
- 用户/角色/权限使用**自管权限表**（本计划 DDL 定义），**不用 EF Core Identity**（EF Core 8 与 2008 R2 不兼容，见 ①；Identity 的存储层默认绑定 EF）。
- 口令哈希使用 `Microsoft.Extensions.Identity.Core` 8.0.11 中的 **`PasswordHasher<T>`**（PBKDF2，独立可用，不依赖 EF）；只取哈希组件，不引入 Identity 其余部分。

### ⑤ 连接安全：Microsoft.Data.SqlClient 5.x 默认 Encrypt=True

- 驱动采用 **Microsoft.Data.SqlClient 5.2.2**。注意 **5.x 起默认 `Encrypt=True`**（4.x 及以前默认 False），而 SQL Server 2008 R2 默认仅支持旧 TLS 版本，未打 TLS 1.2 补丁时与新驱动的加密握手可能失败。
- **驱动 × 操作系统 × SQL Server 补丁 × TLS 组合的实际验证属 G-DEPLOY 上线门禁**（设计 §7）：在真实客户环境验证连接可行性，包括 2008 R2 未打 TLS 1.2 补丁时的兼容处理。
- **连接串显式配置**加密相关参数（`Encrypt`/`TrustServerCertificate`），按环境验证结果落定；**禁止连接失败后静默降级**（如自动改 `Encrypt=False` 重试）——任何降级必须是显式、经批准、留证的配置决策。
- AppKey/AppSecret/连接密码不落 Git（`appsettings.Local.json`、`.env` 已入 `.gitignore`；Windows 侧机密用 DPAPI，`System.Security.Cryptography.ProtectedData` 8.0.0）。

### ⑥ 上传状态机：PRD §9.2 12 态全集及与设计 §2.6 的映射

实现 **PRD §9.2 全部 12 态**：`BLOCKED` / `READY` / `DISPATCHING` / `ACCEPTED` / `ACCEPTANCE_UNKNOWN` / `PROCESSING` / `RETRY_WAIT` / `PARTIAL_SUCCESS` / `RESULT_UNRESOLVED` / `SUCCEEDED` / `FAILED` / `MANUAL_ACTION`。设计 §2.6 转换表是其子集，语义一致。

路线图（`2026-11-22-roadmap.md`"已裁定的对齐决策"）映射表抄录如下：

| 事项 | 决策 | 依据 |
| --- | --- | --- |
| 上传状态机 | 实现 PRD §9.2 全部 12 态（BLOCKED/READY/DISPATCHING/ACCEPTED/ACCEPTANCE_UNKNOWN/PROCESSING/RETRY_WAIT/PARTIAL_SUCCESS/RESULT_UNRESOLVED/SUCCEEDED/FAILED/MANUAL_ACTION）；设计 §2.6 转换表是其子集，语义一致。设计"已排队"=READY，"发送中"=DISPATCHING，"已受理"=ACCEPTED，"处理中"=PROCESSING，"全部成功"=SUCCEEDED，"部分成功"=PARTIAL_SUCCESS，"结果无法判定"=RESULT_UNRESOLVED，"确定失败"=FAILED，"受理未知"=ACCEPTANCE_UNKNOWN；资格未满足=BLOCKED，转人工=MANUAL_ACTION，可重试等待=RETRY_WAIT | PRD §9.2 + 设计 §2.6 |

逐态对照（由上表展开，便于实现核对）：

| 设计 §2.6 状态（中文） | PRD §9.2 状态（枚举） |
| --- | --- |
| 已排队 | READY |
| 发送中 | DISPATCHING |
| 已受理 | ACCEPTED |
| 处理中 | PROCESSING |
| 全部成功 | SUCCEEDED |
| 部分成功 | PARTIAL_SUCCESS |
| 结果无法判定 | RESULT_UNRESOLVED |
| 确定失败 | FAILED |
| 受理未知 | ACCEPTANCE_UNKNOWN |
| 资格未满足 | BLOCKED |
| 转人工 | MANUAL_ACTION |
| 可重试等待 | RETRY_WAIT |

## 后果

- 所有 SQL（含 DDL 迁移）须通过真实 2008 R2 验证门禁；开发者本机需可达 2008 R2 实例（后续任务的集成测试基座）。
- `TreatWarningsAsErrors=true` 全局生效（`traceflow/Directory.Build.props`），构建必须 0 error 0 warning。
- 后续任务的项目名/路径以本任务创建的解决方案为契约：`traceflow/src/TraceFlow.{Domain,Application,Infrastructure,Host}`、`traceflow/db/tools/TraceFlow.DbMigrator`、`traceflow/tests/TraceFlow.{UnitTests,IntegrationTests}`、`traceflow/db/migrations`。
- 单一发布单元简化部署与备份，但要求队列/租约/fencing 实现严格正确（M0 Task 9/10 的验收重点）。
- TOP 签名与连接 TLS 两处外部不确定性分别由 G-TOP、G-DEPLOY 门禁兜底，未取证前不得声称对应能力可用。