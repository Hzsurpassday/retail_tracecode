# TraceFlow M0 基础 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付 .NET 8 重建的可运行基础：完整 SQL Server 2008 R2 DDL 与迁移工具、12 态上传状态机、持久任务引擎（原子认领/租约/fencing/恢复）、认证权限审计幂等、Windows 服务宿主 + React 壳、OpenAPI/ADR/适配事实矩阵/fixture/需求追踪矩阵。

**Architecture:** 单一发布单元（Kestrel + UseWindowsService），同一二进制支持 `--worker-only`/`--web-only`；应用业务层为唯一规则实现；Dapper + 手写 T-SQL 访问独立业务库（兼容 SQL Server 2008 R2）；后台作业全部落库为持久任务，多实例安全认领。

**Tech Stack:** .NET 8 / ASP.NET Core 8 / Dapper 2.x / Microsoft.Data.SqlClient 5.x / xUnit / React 18 + TypeScript + Vite 5。

**Spec:** `docs/superpowers/specs/2026-11-22-retail-pharmacy-trace-design.md`（已批设计）。同时必读：`docs/PRD-TraceFlow-单门店-V1.0.md`、`docs/PRD-TraceFlow-验收与代码审查清单.md`、`docs/PRD-TraceFlow-开发Agent交接说明.md`、`docs/superpowers/plans/2026-11-22-roadmap.md`（对齐决策）。

## Global Constraints

- 业务库兼容 **SQL Server 2008 R2**：禁止 OFFSET/FETCH、SEQUENCE、JSON_VALUE/OPENJSON、STRING_AGG、temporal table、CREATE OR ALTER（DB-03）；分页用 ROW_NUMBER/键集；全部 SQL 在真实 2008 R2 实例验证（AT-070）。
- 数据访问只用 **Dapper + 手写 T-SQL**，不用 EF Core（设计 §1.3）。
- KSOA 源库**只读**：源连接只允许 SELECT，不含 DML/DDL/NOLOCK/触发器（SRC-01/SRC-02）；迁移工具只连 TraceFlow 目标库。
- 外部 ID/单号/码一律字符串，索引键列 `COLLATE Latin1_General_BIN2`；数量 `decimal(18,6)`，禁 float 计量；内部 UTC `datetime2(3)`；版本 `rowversion` 序列化为字符串（DB-01/DB-02/API-01）。
- AppKey/AppSecret/连接密码不落 Git、日志、前端、错误响应（NFR-05/AT-071）；凭据 DPAPI 加密，密文不可解密→停调用+告警，无默认凭据兜底。
- 上传状态机 12 态按 PRD §9.2（路线图映射表）；逐码结果含 UNKNOWN；非法转换拒绝。
- 普通测试一律假平台/fixture；**不得向正式平台上传虚构交易、不得写 ERP 库**；真实平台调用仅限经用户授权的真实业务。
- 无默认公开管理员密码（AUTH-02）；匿名拒绝、逐请求权限校验（AUTH-01）；审计不可变（AUTH-03/AUD-01）。
- 前端遵循 NFR-02 React 规范：独立请求并行、长表分页、不滥用 useMemo、不在组件内定义组件、无副作用派生状态、不变更 props/state。
- 时间显示 Asia/Shanghai；API 前缀 `/api/v1`；错误统一 `code/message/fieldErrors/retryable/requestId`；401/403/409/422/429/503 语义按 API-01。
- 保留既有用户资料：`retail_tracecode/`（首轮 TS 参考代码）、`zww-md/`、`docs/` 现有文件一律不覆盖不删除。
- 未取证适配能力显示"未接入/未验证"并禁用对应发送；缺口按需求 ID 记录，不静默降级。

## File Structure（M0 落定）

```
E:\Viding\Retail Trace Code\
  .gitignore                          # 新建（Task 1）
  traceflow\                          # .NET 解决方案根（全部新代码在此）
    TraceFlow.sln
    Directory.Build.props
    src\
      TraceFlow.Domain\               # 枚举/状态机/值对象，零依赖
        Uploads\UploadState.cs, UploadEvent.cs, UploadStateMachine.cs
        Codes\TraceCodeNormalizer.cs, CodeFormat.cs
        Quantities\Qty.cs, UnitRatio.cs
        Billing\BillCodeGenerator.cs, BillCodeContract.cs
      TraceFlow.Application\          # 业务规则服务 + 端口接口
        Abstractions\IJobQueue.cs, IJobHandler.cs, IAuditLog.cs, IIdempotencyStore.cs, IPermissionService.cs
        Jobs\JobQueue.cs, SendRecoveryService.cs
        Security\PermissionCatalog.cs
      TraceFlow.Infrastructure\       # Dapper 仓储/SQL/DPAPI
        Db\DbConnectionFactory.cs, Sql2008.cs
        Jobs\JobQueueSql.cs（JobQueue 实现）
        Security\PermissionService.cs, AuditLog.cs, IdempotencyStore.cs, DpapiProtector.cs
      TraceFlow.Host\                 # ASP.NET Core 宿主 + Worker
        Program.cs, WorkerHostedService.cs
        Endpoints\AuthEndpoints.cs, AdminEndpoints.cs, DashboardEndpoints.cs, HealthEndpoints.cs
        Security\RequirePermissionAttribute.cs, PermissionHandler.cs, ApiErrors.cs
        wwwroot\                      # React 构建产物（gitignore，发布时生成）
        appsettings.json
    tests\
      TraceFlow.UnitTests\
      TraceFlow.IntegrationTests\     # 真实 SQL 2008 R2（环境变量 TRACEFLOW_TEST_DB）
        Sql2008Fixture.cs
    db\
      migrations\0001_core.sql … 0006_upload.sql
      tools\TraceFlow.DbMigrator\     # 独立部署权限控制台
  docs\
    adr\ADR-0001-dotnet8-dapper-sql2008r2.md
    contracts\openapi.yaml, adapter-facts.md, source-mapping.md
    contracts\fixtures\ksoa\*.json, top\*.json
    traceability\matrix.md
    delivery\M0-gate-report.md
```

依赖方向：Host → Application → Domain；Infrastructure → Application（实现端口）；Host → Infrastructure（仅 DI 装配）。Domain 不引用任何包。

---

### Task 1: 仓库与解决方案骨架

**Files:**
- Create: `E:\Viding\Retail Trace Code\.gitignore`
- Create: `traceflow\TraceFlow.sln`、`traceflow\Directory.Build.props`、5 个 csproj + 2 个测试 csproj + DbMigrator csproj（命令生成）
- Create: `docs\adr\ADR-0001-dotnet8-dapper-sql2008r2.md`

**Interfaces:**
- Produces: 可 `dotnet build` 的空解决方案；后续所有任务的项目名/路径以此为准。

- [ ] **Step 1: 检查现有仓库与未提交改动（用户规则：不能声称已提交）**

Run: `cd "E:/Viding/Retail Trace Code" && git status`
Expected: 若输出 `not a git repository` → 执行 `git init`；若已有仓库 → 记录 `git status --short` 结果，**不触碰已有未提交改动**。首轮 TS 目录 `retail_tracecode/` 保持未跟踪（参考代码，不纳入本次提交范围）。

- [ ] **Step 2: 写 .gitignore**

```gitignore
# .NET
[Bb]in/
[Oo]bj/
*.user
# Node / React
node_modules/
traceflow/src/TraceFlow.Host/wwwroot/
#  secrets & local config
appsettings.Local.json
*.pfx
.env
# 首轮参考代码与用户资料不跟踪不删除
retail_tracecode/
zww-md/
```

- [ ] **Step 3: 生成解决方案与项目**

```bash
cd "E:/Viding/Retail Trace Code"
mkdir -p traceflow/src traceflow/tests traceflow/db/migrations traceflow/db/tools
cd traceflow
dotnet new sln -n TraceFlow
dotnet new classlib -n TraceFlow.Domain -o src/TraceFlow.Domain -f net8.0
dotnet new classlib -n TraceFlow.Application -o src/TraceFlow.Application -f net8.0
dotnet new classlib -n TraceFlow.Infrastructure -o src/TraceFlow.Infrastructure -f net8.0
dotnet new web -n TraceFlow.Host -o src/TraceFlow.Host -f net8.0
dotnet new console -n TraceFlow.DbMigrator -o db/tools/TraceFlow.DbMigrator -f net8.0
dotnet new xunit -n TraceFlow.UnitTests -o tests/TraceFlow.UnitTests -f net8.0
dotnet new xunit -n TraceFlow.IntegrationTests -o tests/TraceFlow.IntegrationTests -f net8.0
dotnet sln add src/TraceFlow.Domain src/TraceFlow.Application src/TraceFlow.Infrastructure src/TraceFlow.Host db/tools/TraceFlow.DbMigrator tests/TraceFlow.UnitTests tests/TraceFlow.IntegrationTests
dotnet add src/TraceFlow.Application reference src/TraceFlow.Domain
dotnet add src/TraceFlow.Infrastructure reference src/TraceFlow.Application
dotnet add src/TraceFlow.Host reference src/TraceFlow.Application src/TraceFlow.Infrastructure
dotnet add db/tools/TraceFlow.DbMigrator package Dapper --version 2.1.35
dotnet add db/tools/TraceFlow.DbMigrator package Microsoft.Data.SqlClient --version 5.2.2
dotnet add src/TraceFlow.Infrastructure package Dapper --version 2.1.35
dotnet add src/TraceFlow.Infrastructure package Microsoft.Data.SqlClient --version 5.2.2
dotnet add src/TraceFlow.Infrastructure package System.Security.Cryptography.ProtectedData --version 8.0.0
dotnet add src/TraceFlow.Application package Microsoft.Extensions.Identity.Core --version 8.0.11
dotnet add src/TraceFlow.Host package Microsoft.Extensions.Hosting.WindowsServices --version 8.0.1
dotnet add tests/TraceFlow.UnitTests reference src/TraceFlow.Domain src/TraceFlow.Application
dotnet add tests/TraceFlow.IntegrationTests reference src/TraceFlow.Infrastructure src/TraceFlow.Application db/tools/TraceFlow.DbMigrator
dotnet add tests/TraceFlow.IntegrationTests package Dapper --version 2.1.35
dotnet add tests/TraceFlow.IntegrationTests package Microsoft.Data.SqlClient --version 5.2.2
rm src/TraceFlow.Domain/Class1.cs src/TraceFlow.Application/Class1.cs src/TraceFlow.Infrastructure/Class1.cs
```

- [ ] **Step 4: 写 Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: 写 ADR-0001**（`docs/adr/ADR-0001-dotnet8-dapper-sql2008r2.md`）

内容必须包含：① .NET 8 + Dapper 手写 T-SQL 的选型理由（EF Core 8 SQL Server 基线高于 2008 R2；2008 R2 语法禁区清单）；② 单一发布单元 + `--worker-only`/`--web-only` 模式与 PRD NFR-01"独立 Worker"的关系（用户批准方案 A 优先；持久队列不依赖进程内存，多实例安全认领满足 JOB-01）；③ TOP 协议客户端自研（官方 .NET SDK 年久失修，签名算法以 G-TOP 合同验证为准，M2 实现）；④ 认证用 Cookie + 自管权限表 + `PasswordHasher<T>`（不用 EF Identity）；⑤ Microsoft.Data.SqlClient 5.x 默认 Encrypt=True，2008 R2 TLS 组合属 G-DEPLOY 门禁，连接串显式配置、禁止静默降级；⑥ 状态机采用 PRD §9.2 12 态全集及与设计 §2.6 的映射（抄路线图映射表）。

- [ ] **Step 6: 构建验证**

Run: `dotnet build traceflow/TraceFlow.sln -c Release`
Expected: 0 error 0 warning（TreatWarningsAsErrors）。

- [ ] **Step 7: Commit**

```bash
git add .gitignore traceflow docs/adr/ADR-0001-dotnet8-dapper-sql2008r2.md docs/superpowers/plans docs/superpowers/specs
git commit -m "chore(M0): scaffold .NET 8 solution, ADR-0001, plans and approved spec"
```

---

### Task 2: 上传状态机（12 态全转换表）

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Uploads/UploadState.cs`
- Create: `traceflow/src/TraceFlow.Domain/Uploads/UploadEvent.cs`
- Create: `traceflow/src/TraceFlow.Domain/Uploads/UploadStateMachine.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Uploads/UploadStateMachineTests.cs`

**Interfaces:**
- Produces: `enum UploadState`、`enum UploadEvent`（成员名见下，后续所有任务引用）；`UploadStateMachine.TryGetNext(UploadState current, UploadEvent evt, out UploadState next)` 返回 bool；`UploadStateMachine.AllTransitions` 为 `IReadOnlyList<(UploadState From, UploadEvent Event, UploadState To)>`。
- 注意：状态机只判定**转换合法性**；证据条件（未受理观察窗、合同证据、快照未变化、token 有效）由 Application 层在调用前校验（M2/M3 实现），此处不得内置"自动放行"。

- [ ] **Step 1: 写失败测试（全边覆盖 + 非法边）**

```csharp
using TraceFlow.Domain.Uploads;
using Xunit;

namespace TraceFlow.UnitTests.Uploads;

public class UploadStateMachineTests
{
    public static TheoryData<UploadState, UploadEvent, UploadState> LegalEdges => new()
    {
        { UploadState.Blocked, UploadEvent.EligibilitySatisfied, UploadState.Ready },
        { UploadState.Ready, UploadEvent.Claimed, UploadState.Dispatching },
        { UploadState.Ready, UploadEvent.EligibilityLost, UploadState.Blocked },
        { UploadState.Dispatching, UploadEvent.ReceiptAccepted, UploadState.Accepted },
        { UploadState.Dispatching, UploadEvent.PartialEvidence, UploadState.PartialSuccess },
        { UploadState.Dispatching, UploadEvent.Rejected, UploadState.Failed },
        { UploadState.Dispatching, UploadEvent.TransportUncertain, UploadState.AcceptanceUnknown },
        { UploadState.Dispatching, UploadEvent.DuplicateBillCode, UploadState.AcceptanceUnknown },
        { UploadState.Dispatching, UploadEvent.LeaseLost, UploadState.AcceptanceUnknown },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedAccepted, UploadState.Accepted },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedNotAccepted, UploadState.RetryWait },
        { UploadState.AcceptanceUnknown, UploadEvent.ManualEscalate, UploadState.ManualAction },
        { UploadState.Accepted, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.Accepted, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.Accepted, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.Accepted, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.Accepted, UploadEvent.QueryUnresolved, UploadState.ResultUnresolved },
        { UploadState.Processing, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.Processing, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.Processing, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.Processing, UploadEvent.QueryUnresolved, UploadState.ResultUnresolved },
        { UploadState.Processing, UploadEvent.ObservationExpired, UploadState.ManualAction },
        { UploadState.RetryWait, UploadEvent.RetryDue, UploadState.Ready },
        { UploadState.PartialSuccess, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.PartialSuccess, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.PartialSuccess, UploadEvent.ManualEscalate, UploadState.ManualAction },
        { UploadState.ResultUnresolved, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.ResultUnresolved, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.ResultUnresolved, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.ResultUnresolved, UploadEvent.QueryFailed, UploadState.Failed },
        { UploadState.ResultUnresolved, UploadEvent.ObservationExpired, UploadState.ManualAction },
        { UploadState.Failed, UploadEvent.ControlledRetryApproved, UploadState.RetryWait },
        { UploadState.Failed, UploadEvent.ManualEscalate, UploadState.ManualAction },
        { UploadState.ManualAction, UploadEvent.EvidenceVerifiedAccepted, UploadState.Accepted },
        { UploadState.ManualAction, UploadEvent.EvidenceVerifiedProcessing, UploadState.Processing },
        { UploadState.ManualAction, UploadEvent.QueryConfirmedNotAccepted, UploadState.RetryWait },
        { UploadState.ManualAction, UploadEvent.QueryProcessing, UploadState.Processing },
        { UploadState.ManualAction, UploadEvent.QueryAllSuccess, UploadState.Succeeded },
        { UploadState.ManualAction, UploadEvent.QueryPartial, UploadState.PartialSuccess },
        { UploadState.ManualAction, UploadEvent.QueryFailed, UploadState.Failed },
    };

    [Theory]
    [MemberData(nameof(LegalEdges))]
    public void Legal_edge_transitions(UploadState from, UploadEvent evt, UploadState to)
    {
        Assert.True(UploadStateMachine.TryGetNext(from, evt, out var next));
        Assert.Equal(to, next);
        Assert.Contains((from, evt, to), UploadStateMachine.AllTransitions);
    }

    public static TheoryData<UploadState, UploadEvent> IllegalEdges => new()
    {
        { UploadState.Succeeded, UploadEvent.Claimed },
        { UploadState.Succeeded, UploadEvent.QueryAllSuccess },
        { UploadState.Succeeded, UploadEvent.ManualEscalate },
        { UploadState.Dispatching, UploadEvent.RetryDue },          // 超时不得直接回 READY
        { UploadState.Processing, UploadEvent.Claimed },            // 处理中不得重新上传
        { UploadState.AcceptanceUnknown, UploadEvent.Claimed },     // 受理未知不得盲目重发
        { UploadState.PartialSuccess, UploadEvent.RetryDue },       // 部分成功禁整单重排
        { UploadState.Accepted, UploadEvent.ReceiptAccepted },
        { UploadState.Blocked, UploadEvent.Claimed },
        { UploadState.ManualAction, UploadEvent.EligibilitySatisfied }, // 人工不能直接设成功/就绪
    };

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public void Illegal_edge_rejected(UploadState from, UploadEvent evt)
    {
        Assert.False(UploadStateMachine.TryGetNext(from, evt, out _));
    }

    [Fact]
    public void Succeeded_is_terminal_and_every_other_state_has_exit()
    {
        Assert.DoesNotContain(UploadStateMachine.AllTransitions, t => t.From == UploadState.Succeeded);
        foreach (UploadState s in Enum.GetValues<UploadState>())
            if (s != UploadState.Succeeded)
                Assert.Contains(UploadStateMachine.AllTransitions, t => t.From == s);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter UploadStateMachineTests`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现枚举与状态机**

`UploadState.cs`：

```csharp
namespace TraceFlow.Domain.Uploads;

/// <summary>PRD §9.2 平台子任务状态（12 态）。设计 §2.6 中文态映射见路线图。</summary>
public enum UploadState
{
    Blocked, Ready, Dispatching, Accepted, AcceptanceUnknown, Processing,
    RetryWait, PartialSuccess, ResultUnresolved, Succeeded, Failed, ManualAction
}
```

`UploadEvent.cs`：

```csharp
namespace TraceFlow.Domain.Uploads;

public enum UploadEvent
{
    EligibilitySatisfied, EligibilityLost, Claimed,
    ReceiptAccepted, PartialEvidence, Rejected, TransportUncertain, DuplicateBillCode, LeaseLost,
    QueryProcessing, QueryAllSuccess, QueryPartial, QueryFailed, QueryUnresolved,
    QueryConfirmedAccepted, QueryConfirmedNotAccepted,
    ObservationExpired, RetryDue, ManualEscalate, ControlledRetryApproved,
    EvidenceVerifiedAccepted, EvidenceVerifiedProcessing
}
```

`UploadStateMachine.cs`：

```csharp
namespace TraceFlow.Domain.Uploads;

/// <summary>
/// 唯一转换表。只判定合法性；证据条件（观察窗/合同证据/快照未变/token）由 Application 层前置校验。
/// QueryConfirmedNotAccepted 仅当满足"一致性延迟窗口+合同证据"后才允许由调用方触发（IDEM-05）。
/// </summary>
public static class UploadStateMachine
{
    private static readonly Dictionary<(UploadState, UploadEvent), UploadState> Table = new()
    {
        [(UploadState.Blocked, UploadEvent.EligibilitySatisfied)] = UploadState.Ready,
        [(UploadState.Ready, UploadEvent.Claimed)] = UploadState.Dispatching,
        [(UploadState.Ready, UploadEvent.EligibilityLost)] = UploadState.Blocked,
        [(UploadState.Dispatching, UploadEvent.ReceiptAccepted)] = UploadState.Accepted,
        [(UploadState.Dispatching, UploadEvent.PartialEvidence)] = UploadState.PartialSuccess,
        [(UploadState.Dispatching, UploadEvent.Rejected)] = UploadState.Failed,
        [(UploadState.Dispatching, UploadEvent.TransportUncertain)] = UploadState.AcceptanceUnknown,
        [(UploadState.Dispatching, UploadEvent.DuplicateBillCode)] = UploadState.AcceptanceUnknown,
        [(UploadState.Dispatching, UploadEvent.LeaseLost)] = UploadState.AcceptanceUnknown,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedAccepted)] = UploadState.Accepted,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.AcceptanceUnknown, UploadEvent.QueryConfirmedNotAccepted)] = UploadState.RetryWait,
        [(UploadState.AcceptanceUnknown, UploadEvent.ManualEscalate)] = UploadState.ManualAction,
        [(UploadState.Accepted, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.Accepted, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.Accepted, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.Accepted, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.Accepted, UploadEvent.QueryUnresolved)] = UploadState.ResultUnresolved,
        [(UploadState.Processing, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.Processing, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.Processing, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.Processing, UploadEvent.QueryUnresolved)] = UploadState.ResultUnresolved,
        [(UploadState.Processing, UploadEvent.ObservationExpired)] = UploadState.ManualAction,
        [(UploadState.RetryWait, UploadEvent.RetryDue)] = UploadState.Ready,
        [(UploadState.PartialSuccess, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.PartialSuccess, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.PartialSuccess, UploadEvent.ManualEscalate)] = UploadState.ManualAction,
        [(UploadState.ResultUnresolved, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.ResultUnresolved, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.ResultUnresolved, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.ResultUnresolved, UploadEvent.QueryFailed)] = UploadState.Failed,
        [(UploadState.ResultUnresolved, UploadEvent.ObservationExpired)] = UploadState.ManualAction,
        [(UploadState.Failed, UploadEvent.ControlledRetryApproved)] = UploadState.RetryWait,
        [(UploadState.Failed, UploadEvent.ManualEscalate)] = UploadState.ManualAction,
        [(UploadState.ManualAction, UploadEvent.EvidenceVerifiedAccepted)] = UploadState.Accepted,
        [(UploadState.ManualAction, UploadEvent.EvidenceVerifiedProcessing)] = UploadState.Processing,
        [(UploadState.ManualAction, UploadEvent.QueryConfirmedNotAccepted)] = UploadState.RetryWait,
        [(UploadState.ManualAction, UploadEvent.QueryProcessing)] = UploadState.Processing,
        [(UploadState.ManualAction, UploadEvent.QueryAllSuccess)] = UploadState.Succeeded,
        [(UploadState.ManualAction, UploadEvent.QueryPartial)] = UploadState.PartialSuccess,
        [(UploadState.ManualAction, UploadEvent.QueryFailed)] = UploadState.Failed,
    };

    public static IReadOnlyList<(UploadState From, UploadEvent Event, UploadState To)> AllTransitions { get; } =
        Table.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();

    public static bool TryGetNext(UploadState current, UploadEvent evt, out UploadState next)
        => Table.TryGetValue((current, evt), out next);
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter UploadStateMachineTests`
Expected: 全部 PASS（44 合法边 + 10 非法边 + 1 终态断言）。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Domain/Uploads traceflow/tests/TraceFlow.UnitTests/Uploads
git commit -m "feat(M0): 12-state upload state machine with full transition table (PRD 9.2)"
```

---

### Task 3: bill_code 确定性生成器

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Billing/BillCodeContract.cs`
- Create: `traceflow/src/TraceFlow.Domain/Billing/BillCodeGenerator.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Billing/BillCodeGeneratorTests.cs`

**Interfaces:**
- Produces: `record BillCodeContract(string Prefix, int MaxLength)`（限值来自 G-TOP 合同验证，未验证时 Live 通道禁用——本任务只管生成合法性）；`BillCodeGenerator.Generate(BillCodeContract contract, long taskId, int partIndex)` → string。`partIndex=0` 表示整单（无分片后缀），`>=1` 为确定性分片。生成结果在任务创建时持久化（IDEM-02），重试/重启/回查永远复用，不重算。

- [ ] **Step 1: 写失败测试**

```csharp
using TraceFlow.Domain.Billing;
using Xunit;

namespace TraceFlow.UnitTests.Billing;

public class BillCodeGeneratorTests
{
    private static readonly BillCodeContract Contract = new("TF", 32);

    [Fact]
    public void Deterministic_for_same_task_and_part()
    {
        Assert.Equal(BillCodeGenerator.Generate(Contract, 12345, 0),
                     BillCodeGenerator.Generate(Contract, 12345, 0));
    }

    [Fact]
    public void Different_tasks_different_codes()
    {
        Assert.NotEqual(BillCodeGenerator.Generate(Contract, 1, 0),
                        BillCodeGenerator.Generate(Contract, 2, 0));
    }

    [Fact]
    public void Parts_have_stable_suffix_and_no_suffix_for_whole_bill()
    {
        var whole = BillCodeGenerator.Generate(Contract, 12345, 0);
        var p1 = BillCodeGenerator.Generate(Contract, 12345, 1);
        var p2 = BillCodeGenerator.Generate(Contract, 12345, 2);
        Assert.DoesNotContain("P", whole);
        Assert.Equal(whole + "P1", p1);
        Assert.Equal(whole + "P2", p2);
    }

    [Fact]
    public void Alphanumeric_only()
    {
        var code = BillCodeGenerator.Generate(Contract, long.MaxValue, 3);
        Assert.Matches("^[0-9A-Z]+$", code);
    }

    [Fact]
    public void Over_length_throws_not_truncates()
    {
        var tight = new BillCodeContract("TF", 5);
        Assert.Throws<InvalidOperationException>(() => BillCodeGenerator.Generate(tight, long.MaxValue, 0));
    }

    [Fact]
    public void Invalid_contract_rejected()
    {
        Assert.Throws<ArgumentException>(() => BillCodeGenerator.Generate(new BillCodeContract("tf-", 32), 1, 0));
        Assert.Throws<ArgumentException>(() => BillCodeGenerator.Generate(new BillCodeContract("TF", 0), 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BillCodeGenerator.Generate(Contract, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BillCodeGenerator.Generate(Contract, 1, -1));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter BillCodeGeneratorTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`BillCodeContract.cs`：

```csharp
namespace TraceFlow.Domain.Billing;

/// <summary>平台单号合同：字母数字前缀 + 最大长度。限值必须来自 G-TOP 验证，不得凭旧文档假定。</summary>
public sealed record BillCodeContract(string Prefix, int MaxLength);
```

`BillCodeGenerator.cs`：

```csharp
using System.Globalization;

namespace TraceFlow.Domain.Billing;

public static class BillCodeGenerator
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Generate(BillCodeContract contract, long taskId, int partIndex)
    {
        if (contract.MaxLength <= 0 || string.IsNullOrEmpty(contract.Prefix) ||
            !contract.Prefix.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            throw new ArgumentException("合同前缀必须为大写字母数字且 MaxLength>0", nameof(contract));
        if (taskId <= 0) throw new ArgumentOutOfRangeException(nameof(taskId));
        if (partIndex < 0) throw new ArgumentOutOfRangeException(nameof(partIndex));

        var body = Base36(taskId);
        var code = partIndex == 0
            ? contract.Prefix + body
            : contract.Prefix + body + "P" + partIndex.ToString(CultureInfo.InvariantCulture);
        if (code.Length > contract.MaxLength)
            throw new InvalidOperationException($"bill_code 长度 {code.Length} 超合同上限 {contract.MaxLength}，禁止截断");
        return code;
    }

    private static string Base36(long value)
    {
        Span<char> buf = stackalloc char[13];
        var i = buf.Length;
        while (value > 0) { buf[--i] = Digits[(int)(value % 36)]; value /= 36; }
        return new string(buf[i..]);
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter BillCodeGeneratorTests`
Expected: 6 PASS。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Domain/Billing traceflow/tests/TraceFlow.UnitTests/Billing
git commit -m "feat(M0): deterministic bill_code generator (IDEM-02)"
```

---

### Task 4: 数量/单位换算与追溯码规范化值对象

**Files:**
- Create: `traceflow/src/TraceFlow.Domain/Quantities/Qty.cs`
- Create: `traceflow/src/TraceFlow.Domain/Quantities/UnitRatio.cs`
- Create: `traceflow/src/TraceFlow.Domain/Codes/CodeFormat.cs`
- Create: `traceflow/src/TraceFlow.Domain/Codes/TraceCodeNormalizer.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Quantities/QtyTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Codes/TraceCodeNormalizerTests.cs`

**Interfaces:**
- Produces: `readonly record struct Qty(decimal Value)`（decimal(18,6) 域校验，构造越界抛 `ArgumentOutOfRangeException`）；`sealed record UnitRatio(long Numerator, long Denominator)`（目标单位/来源单位的有向精确比例，均 >0）；`UnitRatio.Convert(Qty source)` → `Qty`，**不整除或超精度抛 `UnitConversionException`，绝不四舍五入**（SRC-05/AT-029）；`enum CodeFormat { Empty, Supported8Prefix20, Unsupported }`；`record NormalizedCode(string Raw, string? Normalized, CodeFormat Format)`；`TraceCodeNormalizer.Normalize(string? raw)` → `NormalizedCode`（SRC-04：只移除首尾空白与已验证扫码控制字符；不截断、不转数值、非 8 开头判 Unsupported 而非"不存在"）。

- [ ] **Step 1: 写失败测试**

`QtyTests.cs`：

```csharp
using TraceFlow.Domain.Quantities;
using Xunit;

namespace TraceFlow.UnitTests.Quantities;

public class QtyTests
{
    [Fact]
    public void Accepts_six_decimal_places()
        => Assert.Equal(1.234567m, new Qty(1.234567m).Value);

    [Fact]
    public void Rejects_more_than_six_decimals()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Qty(0.0000001m));

    [Fact]
    public void Rejects_overflow_of_decimal_18_6()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Qty(1_000_000_000_000m)); // 13 位整数超限

    [Fact]
    public void Exact_conversion_succeeds()
    {
        var ratio = new UnitRatio(10, 1); // 1 大包装 = 10 最小单位
        Assert.Equal(new Qty(30m), ratio.Convert(new Qty(3m)));
    }

    [Fact]
    public void Inexact_conversion_throws_not_rounds()
    {
        var ratio = new UnitRatio(1, 3);
        Assert.Throws<UnitConversionException>(() => ratio.Convert(new Qty(1m)));
    }

    [Fact]
    public void Conversion_scale_overflow_throws()
    {
        var ratio = new UnitRatio(1, 1_000_000_0); // 结果需要 >6 位小数
        Assert.Throws<UnitConversionException>(() => ratio.Convert(new Qty(1m)));
    }

    [Fact]
    public void Ratio_direction_matters()
    {
        var bigToSmall = new UnitRatio(10, 1);
        var smallToBig = new UnitRatio(1, 10);
        Assert.Equal(new Qty(100m), bigToSmall.Convert(new Qty(10m)));
        Assert.Equal(new Qty(1m), smallToBig.Convert(new Qty(10m)));
    }

    [Fact]
    public void Invalid_ratio_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitRatio(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitRatio(1, -2));
    }
}
```

`TraceCodeNormalizerTests.cs`：

```csharp
using TraceFlow.Domain.Codes;
using Xunit;

namespace TraceFlow.UnitTests.Codes;

public class TraceCodeNormalizerTests
{
    [Fact]
    public void Valid_8_prefix_20_digit_supported()
    {
        var r = TraceCodeNormalizer.Normalize("81234567890123456789");
        Assert.Equal(CodeFormat.Supported8Prefix20, r.Format);
        Assert.Equal("81234567890123456789", r.Normalized);
    }

    [Fact]
    public void Leading_zeros_preserved_as_string()
    {
        var r = TraceCodeNormalizer.Normalize("80000000000000000001");
        Assert.Equal("80000000000000000001", r.Normalized);
    }

    [Fact]
    public void Edge_whitespace_and_scanner_control_chars_removed()
    {
        var r = TraceCodeNormalizer.Normalize("\r\n 81234567890123456789\u001d\t ");
        Assert.Equal(CodeFormat.Supported8Prefix20, r.Format);
        Assert.Equal("\r\n 81234567890123456789\u001d\t ", r.Raw); // raw 永不改写
    }

    [Fact]
    public void Overlong_code_not_truncated()
    {
        var raw = "8123456789012345678901234"; // 25 位
        var r = TraceCodeNormalizer.Normalize(raw);
        Assert.Equal(raw, r.Normalized);
        Assert.Equal(CodeFormat.Unsupported, r.Format); // 异常保存，不截 20 位"修复"
    }

    [Fact]
    public void Non_8_prefix_is_unsupported_not_absent()
    {
        var r = TraceCodeNormalizer.Normalize("91234567890123456789");
        Assert.Equal(CodeFormat.Unsupported, r.Format);
    }

    [Fact]
    public void Internal_control_char_unsupported_not_silently_stripped()
    {
        var r = TraceCodeNormalizer.Normalize("8123456789\u00000123456789");
        Assert.Equal(CodeFormat.Unsupported, r.Format);
    }

    [Fact]
    public void Empty_is_empty_not_exempt()
    {
        Assert.Equal(CodeFormat.Empty, TraceCodeNormalizer.Normalize(null).Format);
        Assert.Equal(CodeFormat.Empty, TraceCodeNormalizer.Normalize("").Format);
        Assert.Equal(CodeFormat.Empty, TraceCodeNormalizer.Normalize("   ").Format);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter "QtyTests|TraceCodeNormalizerTests"`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`Qty.cs`：

```csharp
namespace TraceFlow.Domain.Quantities;

/// <summary>decimal(18,6) 域：12 位整数 + 6 位小数。禁 float；越界报错不舍入吞量（SRC-05）。</summary>
public readonly record struct Qty
{
    public const int Scale = 6;
    public static readonly Qty Zero = new(0m);
    public decimal Value { get; }

    public Qty(decimal value)
    {
        if (decimal.Round(value, Scale) != value)
            throw new ArgumentOutOfRangeException(nameof(value), "数量精度超过 6 位小数");
        if (value >= 1_000_000_000_000m || value <= -1_000_000_000_000m)
            throw new ArgumentOutOfRangeException(nameof(value), "数量超出 decimal(18,6) 范围");
        Value = value;
    }
}
```

`UnitRatio.cs`：

```csharp
namespace TraceFlow.Domain.Quantities;

public sealed class UnitConversionException : Exception
{
    public UnitConversionException(string message) : base(message) { }
}

/// <summary>有向精确比例：1 来源单位 = Numerator/Denominator 目标单位。换算必须精确相等（SRC-05/AT-029）。</summary>
public sealed record UnitRatio
{
    public long Numerator { get; }
    public long Denominator { get; }

    public UnitRatio(long numerator, long denominator)
    {
        if (numerator <= 0 || denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "比例分子分母必须为正");
        Numerator = numerator;
        Denominator = denominator;
    }

    public Qty Convert(Qty source)
    {
        decimal scaled = source.Value * Numerator;
        if (scaled % Denominator != 0m)
            throw new UnitConversionException($"换算不整除：{source.Value}×{Numerator}/{Denominator}，禁止舍入");
        decimal result = scaled / Denominator;
        try { return new Qty(result); }
        catch (ArgumentOutOfRangeException ex)
        { throw new UnitConversionException($"换算结果超精度/范围：{result}"); }
    }
}
```

`CodeFormat.cs`：

```csharp
namespace TraceFlow.Domain.Codes;

public enum CodeFormat { Empty, Supported8Prefix20, Unsupported }

public sealed record NormalizedCode(string Raw, string? Normalized, CodeFormat Format);
```

`TraceCodeNormalizer.cs`：

```csharp
namespace TraceFlow.Domain.Codes;

/// <summary>
/// SRC-04：raw 永不改写；规范化仅移除首尾空白与已验证扫码控制字符（C0 控制符/DEL/GS）。
/// 不截断为 20 位、不转数值；内部控制符属未验证情形→Unsupported 异常保存。
/// 码制支持范围以 G-TOP 合同验证为准，当前仅 8 开头 20 位数字。
/// </summary>
public static class TraceCodeNormalizer
{
    public static NormalizedCode Normalize(string? raw)
    {
        raw ??= "";
        if (raw.Length == 0) return new NormalizedCode(raw, null, CodeFormat.Empty);

        var normalized = raw.Trim().Trim(EdgeControlChars);
        if (normalized.Length == 0) return new NormalizedCode(raw, null, CodeFormat.Empty);

        var format = IsSupported(normalized) ? CodeFormat.Supported8Prefix20 : CodeFormat.Unsupported;
        return new NormalizedCode(raw, normalized, format);
    }

    private static readonly char[] EdgeControlChars =
        Enumerable.Range(0, 32).Select(i => (char)i).Append('\u007f').ToArray();

    private static bool IsSupported(string s)
        => s.Length == 20 && s[0] == '8' && s.All(char.IsAsciiDigit);
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter "QtyTests|TraceCodeNormalizerTests"`
Expected: 15 PASS。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Domain/Quantities traceflow/src/TraceFlow.Domain/Codes traceflow/tests/TraceFlow.UnitTests/Quantities traceflow/tests/TraceFlow.UnitTests/Codes
git commit -m "feat(M0): exact quantity/unit-ratio and trace-code normalization (SRC-04/05)"
```

---

### Task 5: 迁移工具 + 集成测试基座 + 0001 核心表

**Files:**
- Create: `traceflow/db/tools/TraceFlow.DbMigrator/MigrationRunner.cs`
- Modify: `traceflow/db/tools/TraceFlow.DbMigrator/Program.cs`（模板生成的壳替换）
- Create: `traceflow/db/migrations/0001_core.sql`
- Create: `traceflow/tests/TraceFlow.IntegrationTests/Sql2008Fixture.cs`
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj`（复制迁移脚本到输出）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/MigratorTests.cs`

**Interfaces:**
- Produces: `MigrationRunner(string connectionString, string scriptsDirectory)`；`Task<MigrationReport> RunAsync(CancellationToken ct = default)`；`record MigrationReport(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped)`；失败抛 `MigrationException`（含版本与内部异常）。`Sql2008Fixture : IAsyncLifetime`，属性 `string ConnectionString`，方法 `Task<SqlConnection> OpenAsync()`；每次测试类实例创建独立测试库并在结束时删除。**环境变量 `TRACEFLOW_TEST_DB`**：指向真实 SQL Server 2008 R2 测试实例的连接串（不含 Initial Catalog），缺失时测试抛明确错误——集成门禁不允许其他引擎替代（AT-070）。
- 迁移工具只连 TraceFlow 目标库（SRC-01）；运行账号无 DDL 权限，升级用独立部署凭据（设计 §5.1）。

- [ ] **Step 1: 写失败测试**

```csharp
using Dapper;
using TraceFlow.DbMigrator;
using TraceFlow.IntegrationTests;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class MigratorTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public MigratorTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Second_run_is_idempotent_skip()
    {
        var scripts = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        var report = await new MigrationRunner(_fx.ConnectionString, scripts).RunAsync();
        Assert.Empty(report.Applied);
        Assert.NotEmpty(report.Skipped);
    }

    [Fact]
    public async Task Checksum_mismatch_stops_and_does_not_partially_apply()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        var tampered = Path.Combine(Path.GetTempPath(), "tf_tamper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tampered);
        foreach (var f in Directory.GetFiles(src, "*.sql")) File.Copy(f, Path.Combine(tampered, Path.GetFileName(f)));
        var first = Directory.GetFiles(tampered, "*.sql").Order().First();
        await File.AppendAllTextAsync(first, "\n-- tampered\n");
        await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(_fx.ConnectionString, tampered).RunAsync());
        Directory.Delete(tampered, true);
    }

    [Fact]
    public async Task Core_tables_exist_and_single_active_org_enforced()
    {
        await using var conn = await _fx.OpenAsync();
        foreach (var t in new[] { "organization", "source_system", "app_setting", "app_user", "user_permission", "audit_log", "idempotency_record" })
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name=@t", new { t }));

        await conn.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'T1',N'店1')");
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            conn.ExecuteAsync("INSERT INTO dbo.organization(org_code,name) VALUES(N'T2',N'店2')"));
        Assert.True(ex.Number is 2601 or 2627); // 过滤唯一索引：仅一个 active 门店
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB="Server=<测试机>;User Id=<部署测试账号>;Password=<略>;TrustServerCertificate=True" dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter MigratorTests`
Expected: 编译失败（MigrationRunner/Sql2008Fixture 不存在）。真实连接串由部署者经环境变量提供，不写入任何文件。

- [ ] **Step 3: 实现 MigrationRunner**

```csharp
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.DbMigrator;

public sealed class MigrationException : Exception
{
    public MigrationException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record MigrationReport(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);

public sealed class MigrationRunner
{
    private static readonly Regex FilePattern = new(@"^(\d{4})_.+\.sql$", RegexOptions.IgnoreCase);
    private static readonly Regex GoSplit = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private readonly string _conn;
    private readonly string _dir;

    public MigrationRunner(string connectionString, string scriptsDirectory)
    {
        _conn = connectionString;
        _dir = scriptsDirectory;
    }

    public async Task<MigrationReport> RunAsync(CancellationToken ct = default)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        await using (var conn = new SqlConnection(_conn))
        {
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(@"
IF OBJECT_ID(N'dbo.schema_version', N'U') IS NULL
CREATE TABLE dbo.schema_version (
  version int NOT NULL CONSTRAINT PK_schema_version PRIMARY KEY,
  name nvarchar(200) NOT NULL,
  checksum char(64) NOT NULL,
  applied_at datetime2(3) NOT NULL,
  success bit NOT NULL
);");
        }

        var files = Directory.GetFiles(_dir, "*.sql")
            .Where(f => FilePattern.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var version = int.Parse(name[..4]);
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));

            await using var conn = new SqlConnection(_conn);
            await conn.OpenAsync(ct);
            var existing = await conn.QuerySingleOrDefaultAsync<(string Checksum, bool Success)?>(
                "SELECT checksum AS Checksum, success AS Success FROM dbo.schema_version WHERE version=@version", new { version });
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.Checksum.Trim(), checksum, StringComparison.OrdinalIgnoreCase))
                    throw new MigrationException($"迁移 {name} 校验摘要不一致，停止启动（设计 §5.1：失败须停止，不得部分升级后继续）");
                if (!existing.Value.Success)
                    throw new MigrationException($"迁移 {name} 上次执行失败，需人工修复后重跑");
                skipped.Add(name);
                continue;
            }

            var sql = await File.ReadAllTextAsync(file, ct);
            var batches = GoSplit.Split(sql).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var batch in batches)
                    await conn.ExecuteAsync(batch, transaction: tx, commandTimeout: 300);
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.schema_version(version,name,checksum,applied_at,success) VALUES(@version,@name,@checksum,SYSUTCDATETIME(),1)",
                    new { version, name, checksum }, tx);
                await tx.CommitAsync(ct);
                applied.Add(name);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.schema_version(version,name,checksum,applied_at,success) VALUES(@version,@name,@checksum,SYSUTCDATETIME(),0)",
                    new { version, name, checksum });
                throw new MigrationException($"迁移 {name} 执行失败，已回滚", ex);
            }
        }
        return new MigrationReport(applied, skipped);
    }
}
```

`Program.cs`（薄壳；失败退出码非 0 → 宿主启动脚本据此停止启动/进入维护模式）：

```csharp
using TraceFlow.DbMigrator;

var conn = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("TRACEFLOW_MIGRATE_CONN");
var dir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "db", "migrations");
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("用法: TraceFlow.DbMigrator <连接串> [脚本目录]，或设置 TRACEFLOW_MIGRATE_CONN。只允许连接 TraceFlow 目标库。");
    return 2;
}
try
{
    var report = await new MigrationRunner(conn, dir).RunAsync();
    Console.WriteLine($"Applied: {string.Join(", ", report.Applied)}");
    Console.WriteLine($"Skipped: {string.Join(", ", report.Skipped)}");
    return 0;
}
catch (MigrationException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ex.InnerException?.Message);
    return 1;
}
```

- [ ] **Step 4: 写 Sql2008Fixture 与 csproj 脚本复制**

`Sql2008Fixture.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.DbMigrator;
using Xunit;

namespace TraceFlow.IntegrationTests;

public sealed class Sql2008Fixture : IAsyncLifetime
{
    private string _masterConn = "";
    private string _dbName = "";
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("TRACEFLOW_TEST_DB")
            ?? throw new InvalidOperationException(
                "必须设置 TRACEFLOW_TEST_DB 指向真实 SQL Server 2008 R2 测试实例（不含 Initial Catalog）。AT-070 门禁不允许其他引擎替代。");
        var builder = new SqlConnectionStringBuilder(baseConn);
        builder.InitialCatalog = "master";
        _masterConn = builder.ConnectionString;
        _dbName = "TraceFlow_Test_" + Guid.NewGuid().ToString("N")[..12];

        await using (var master = new SqlConnection(_masterConn))
        {
            await master.OpenAsync();
            var ver = await master.ExecuteScalarAsync<string>(
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64))");
            if (ver is null || !ver.StartsWith("10.50."))
                throw new InvalidOperationException($"集成门禁要求 SQL Server 2008 R2（10.50.x），实际 {ver}");
            await master.ExecuteAsync($"CREATE DATABASE [{_dbName}]");
        }

        builder.InitialCatalog = _dbName;
        ConnectionString = builder.ConnectionString;
        var scripts = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
        await new MigrationRunner(ConnectionString, scripts).RunAsync();
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task DisposeAsync()
    {
        if (_dbName.Length == 0) return;
        await using var master = new SqlConnection(_masterConn);
        await master.OpenAsync();
        await master.ExecuteAsync(
            $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];");
    }
}
```

csproj 追加：

```xml
<ItemGroup>
  <None Include="..\..\db\migrations\*.sql" CopyToOutputDirectory="PreserveNewest" LinkBase="db\migrations" />
</ItemGroup>
```

- [ ] **Step 5: 写 0001_core.sql**

```sql
-- 0001 核心：门店/来源系统/设置/用户权限/审计/幂等
CREATE TABLE dbo.organization (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_organization PRIMARY KEY,
  org_code nvarchar(32) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(200) NOT NULL,
  ref_ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NULL,
  ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NULL,
  time_zone nvarchar(64) NOT NULL CONSTRAINT DF_organization_tz DEFAULT N'Asia/Shanghai',
  is_active bit NOT NULL CONSTRAINT DF_organization_active DEFAULT 1,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_organization_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_organization_code UNIQUE (org_code)
);
CREATE UNIQUE INDEX UX_organization_single_active ON dbo.organization(is_active) WHERE is_active = 1;

CREATE TABLE dbo.source_system (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_source_system PRIMARY KEY,
  system_code nvarchar(32) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(100) NOT NULL,
  adapter_contract_version nvarchar(32) NOT NULL,
  config_json nvarchar(max) NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_source_system_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_source_system_code UNIQUE (system_code)
);

CREATE TABLE dbo.app_setting (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_app_setting PRIMARY KEY,
  setting_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  value_json nvarchar(max) NULL,
  secret_protected varbinary(max) NULL,
  is_secret bit NOT NULL CONSTRAINT DF_app_setting_secret DEFAULT 0,
  protection_scope nvarchar(200) NULL,
  updated_by nvarchar(64) NOT NULL,
  updated_at datetime2(3) NOT NULL CONSTRAINT DF_app_setting_updated DEFAULT SYSUTCDATETIME(),
  reason nvarchar(500) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_app_setting_key UNIQUE (setting_key),
  CONSTRAINT CK_app_setting_secret CHECK (is_secret = 0 OR (secret_protected IS NOT NULL AND value_json IS NULL))
);

CREATE TABLE dbo.app_user (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_app_user PRIMARY KEY,
  username nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  display_name nvarchar(100) NOT NULL,
  password_hash nvarchar(500) NOT NULL,
  is_active bit NOT NULL CONSTRAINT DF_app_user_active DEFAULT 1,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_app_user_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_app_user_username UNIQUE (username)
);

CREATE TABLE dbo.user_permission (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_user_permission PRIMARY KEY,
  user_id bigint NOT NULL CONSTRAINT FK_user_permission_user REFERENCES dbo.app_user(id),
  permission nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  granted_by nvarchar(64) NOT NULL,
  granted_at datetime2(3) NOT NULL CONSTRAINT DF_user_permission_at DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_user_permission UNIQUE (user_id, permission)
);

CREATE TABLE dbo.audit_log (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_audit_log PRIMARY KEY,
  occurred_at datetime2(3) NOT NULL CONSTRAINT DF_audit_at DEFAULT SYSUTCDATETIME(),
  actor nvarchar(64) NOT NULL,
  actor_kind nvarchar(16) NOT NULL,
  action nvarchar(64) NOT NULL,
  object_type nvarchar(64) NOT NULL,
  object_id nvarchar(128) NULL,
  reason nvarchar(1000) NULL,
  before_digest nvarchar(max) NULL,
  after_digest nvarchar(max) NULL,
  request_id nvarchar(64) NULL,
  rule_version nvarchar(32) NULL,
  env char(4) NOT NULL CONSTRAINT DF_audit_env DEFAULT 'LIVE'
);
CREATE INDEX IX_audit_log_time ON dbo.audit_log(occurred_at);
CREATE INDEX IX_audit_log_object ON dbo.audit_log(object_type, object_id);

CREATE TABLE dbo.idempotency_record (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_idempotency PRIMARY KEY,
  idem_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  request_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  user_id bigint NOT NULL,
  response_status int NOT NULL,
  response_body nvarchar(max) NOT NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_idempotency_created DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_idempotency_key UNIQUE (idem_key)
);
```

- [ ] **Step 6: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter MigratorTests`
Expected: 3 PASS（真实 2008 R2）。

- [ ] **Step 7: Commit**

```bash
git add traceflow/db traceflow/tests/TraceFlow.IntegrationTests
git commit -m "feat(M0): versioned checksummed migrator + core DDL + real SQL 2008 R2 test fixture"
```

---

### Task 6: 0002 来源镜像 + 0003 主数据与映射 DDL

**Files:**
- Create: `traceflow/db/migrations/0002_source.sql`
- Create: `traceflow/db/migrations/0003_master.sql`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/MasterConstraintTests.cs`

**Interfaces:**
- Produces: 表 `source_snapshot/source_line/sync_checkpoint/ingest_exception/product/supplier/platform_drug/platform_enterprise_version/product_mapping/product_mapping_history/supplier_mapping/supplier_mapping_history/drug_prefix_index/import_batch/import_row`。映射有效态 = `AUTO_CONFIRMED`/`MANUAL_CONFIRMED`；**双向有效唯一由两个过滤唯一索引在数据库层保证**（DB-04/AT-015），应用层不得作为唯一保障。

- [ ] **Step 1: 写失败测试（约束先于实现）**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class MasterConstraintTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public MasterConstraintTests(Sql2008Fixture fx) => _fx = fx;

    private static async Task<(long orgId, long sysId, long p1, long p2, long drugId)> SeedAsync(SqlConnection c)
    {
        var orgId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.organization(org_code,name) OUTPUT inserted.id VALUES(N'O1',N'店')");
        var sysId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.source_system(system_code,name,adapter_contract_version) OUTPUT inserted.id VALUES(N'KSOA',N'KSOA',N'v0')");
        var p1 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@orgId,@sysId,N'SP1',N'药1')", new { orgId, sysId });
        var p2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.product(org_id,source_system_id,spid,name) OUTPUT inserted.id VALUES(@orgId,@sysId,N'SP2',N'药2')", new { orgId, sysId });
        var drugId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id) OUTPUT inserted.id VALUES(N'D1')");
        return (orgId, sysId, p1, p2, drugId);
    }

    [Fact]
    public async Task Two_active_mappings_to_same_platform_drug_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var (_, _, p1, p2, drugId) = await SeedAsync(c);
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(1,@p1,@drugId,N'MANUAL_CONFIRMED')", new { p1, drugId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(1,@p2,@drugId,N'AUTO_CONFIRMED')", new { p2, drugId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Two_active_mappings_for_same_local_product_rejected()
    {
        await using var c = await _fx.OpenAsync();
        var (_, _, p1, _, drugId) = await SeedAsync(c);
        var drug2 = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.platform_drug(platform_drug_id) OUTPUT inserted.id VALUES(N'D2')");
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(1,@p1,@drugId,N'MANUAL_CONFIRMED')", new { p1, drugId });
        await c.ExecuteAsync("INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(1,@p1,@drug2,N'CANDIDATE')", new { p1, drug2 }); // 非有效态允许
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.product_mapping(org_id,product_id,platform_drug_id,state) VALUES(1,@p1,@drug2,N'MANUAL_CONFIRMED')", new { p1, drug2 }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Snapshot_version_unique_and_history_appendable()
    {
        await using var c = await _fx.OpenAsync();
        var (orgId, sysId, _, _, _) = await SeedAsync(c);
        await c.ExecuteAsync(@"INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
                               VALUES(@orgId,@sysId,N'L1',N'SALE',1,REPLICATE('a',64))", new { orgId, sysId });
        await c.ExecuteAsync(@"INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
                               VALUES(@orgId,@sysId,N'L1',N'SALE',2,REPLICATE('b',64))", new { orgId, sysId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.source_snapshot(org_id,source_system_id,source_document_key,document_type,version,content_hash)
              VALUES(@orgId,@sysId,N'L1',N'SALE',1,REPLICATE('c',64))", new { orgId, sysId }));
        Assert.True(ex.Number is 2601 or 2627);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter MasterConstraintTests`
Expected: FAIL（表不存在）。

- [ ] **Step 3: 写 0002_source.sql**

```sql
-- 0002 来源镜像：不可变版本 + 摘要 + 完整性 + 检查点 + 接入异常
CREATE TABLE dbo.source_snapshot (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_source_snapshot PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_snapshot_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_snapshot_sys REFERENCES dbo.source_system(id),
  source_document_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  document_type nvarchar(16) NOT NULL,
  version int NOT NULL,
  business_datetime datetime2(3) NULL,
  source_time_raw nvarchar(64) NULL,
  source_time_zone nvarchar(64) NULL,
  completeness_status nvarchar(16) NOT NULL CONSTRAINT DF_snapshot_completeness DEFAULT N'DISCOVERED',
  content_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  raw_header nvarchar(max) NULL,
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_snapshot_observed DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_snapshot_key_version UNIQUE (source_system_id, source_document_key, version),
  CONSTRAINT CK_snapshot_completeness CHECK (completeness_status IN (N'DISCOVERED',N'STABILIZING',N'COMPLETE',N'SOURCE_CHANGED',N'INVALID'))
);

CREATE TABLE dbo.source_line (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_source_line PRIMARY KEY,
  snapshot_id bigint NOT NULL CONSTRAINT FK_line_snapshot REFERENCES dbo.source_snapshot(id),
  source_line_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  product_source_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  quantity_original decimal(18,6) NOT NULL,
  unit_original nvarchar(32) NULL,
  batch_raw nvarchar(100) NULL,
  expiry_raw nvarchar(32) NULL,
  expiry_precision nvarchar(8) NOT NULL CONSTRAINT DF_line_expiry DEFAULT N'UNKNOWN',
  trace_code_raw nvarchar(200) NULL,
  trace_code_normalized nvarchar(200) COLLATE Latin1_General_BIN2 NULL,
  code_format nvarchar(16) NOT NULL CONSTRAINT DF_line_codefmt DEFAULT N'Empty',
  line_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  event_kind nvarchar(16) NOT NULL,
  raw_line nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_line_key UNIQUE (snapshot_id, source_line_key),
  CONSTRAINT CK_line_expiry CHECK (expiry_precision IN (N'DAY',N'MONTH',N'UNKNOWN')),
  CONSTRAINT CK_line_event CHECK (event_kind IN (N'SALE',N'CONSUMER_RETURN',N'NO_EVENT',N'PURCHASE_IN'))
);

CREATE TABLE dbo.sync_checkpoint (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_sync_checkpoint PRIMARY KEY,
  source_system_id bigint NOT NULL CONSTRAINT FK_checkpoint_sys REFERENCES dbo.source_system(id),
  entity nvarchar(32) NOT NULL,
  checkpoint_kind nvarchar(16) NOT NULL,
  cursor_value nvarchar(200) NULL,
  window_start datetime2(3) NULL,
  window_end datetime2(3) NULL,
  last_success_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_checkpoint UNIQUE (source_system_id, entity, checkpoint_kind)
);

CREATE TABLE dbo.ingest_exception (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ingest_exception PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_ingest_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_ingest_sys REFERENCES dbo.source_system(id),
  source_document_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  source_line_key nvarchar(128) COLLATE Latin1_General_BIN2 NULL,
  exception_type nvarchar(32) NOT NULL,
  detail nvarchar(max) NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_ingest_status DEFAULT N'OPEN',
  resolved_by nvarchar(64) NULL,
  resolved_at datetime2(3) NULL,
  resolution_evidence nvarchar(max) NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_ingest_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT CK_ingest_status CHECK (status IN (N'OPEN',N'RESOLVED'))
);
CREATE INDEX IX_ingest_exception_open ON dbo.ingest_exception(status, created_at);
```

- [ ] **Step 4: 写 0003_master.sql**

```sql
-- 0003 主数据：档案/平台身份/映射（双向有效唯一）/前缀索引/导入
CREATE TABLE dbo.product (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_product PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_product_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_product_sys REFERENCES dbo.source_system(id),
  spid nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  display_code nvarchar(64) NULL,
  barcode nvarchar(64) NULL,
  name nvarchar(200) NOT NULL,
  approval_no_raw nvarchar(100) NULL,
  approval_no_norm nvarchar(100) NULL,
  manufacturer_raw nvarchar(200) NULL,
  manufacturer_identity nvarchar(128) NULL,
  dosage_form nvarchar(64) NULL,
  strength nvarchar(100) NULL,
  package_spec nvarchar(100) NULL,
  unit nvarchar(32) NULL,
  trace_classification nvarchar(8) NOT NULL CONSTRAINT DF_product_class DEFAULT N'UNKNOWN',
  classification_reason nvarchar(500) NULL,
  raw_json nvarchar(max) NULL,
  updated_at datetime2(3) NOT NULL CONSTRAINT DF_product_updated DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_product_source UNIQUE (org_id, source_system_id, spid),
  CONSTRAINT CK_product_class CHECK (trace_classification IN (N'UNKNOWN',N'REQUIRED',N'EXEMPT'))
);

CREATE TABLE dbo.supplier (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_supplier PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_supplier_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_supplier_sys REFERENCES dbo.source_system(id),
  dwbh nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(200) NOT NULL,
  credit_code nvarchar(64) NULL,
  role nvarchar(32) NULL,
  raw_json nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_supplier_source UNIQUE (org_id, source_system_id, dwbh)
);

CREATE TABLE dbo.platform_drug (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_platform_drug PRIMARY KEY,
  platform_drug_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  approval_no_norm nvarchar(100) NULL,
  manufacturer_identity nvarchar(128) NULL,
  dosage_form nvarchar(64) NULL,
  strength nvarchar(100) NULL,
  package_spec nvarchar(100) NULL,
  identity_json nvarchar(max) NULL,
  source_kind nvarchar(32) NOT NULL,
  source_time datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_platform_drug UNIQUE (platform_drug_id)
);

CREATE TABLE dbo.platform_enterprise_version (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_platform_ent_ver PRIMARY KEY,
  ref_ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(200) NOT NULL,
  credit_code nvarchar(64) NULL,
  audit_status nvarchar(32) NULL,
  role nvarchar(32) NULL,
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_ent_ver_observed DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_ent_version UNIQUE (ref_ent_id, ent_id)
);

CREATE TABLE dbo.product_mapping (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_product_mapping PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_pmap_org REFERENCES dbo.organization(id),
  product_id bigint NOT NULL CONSTRAINT FK_pmap_product REFERENCES dbo.product(id),
  platform_drug_id bigint NOT NULL CONSTRAINT FK_pmap_drug REFERENCES dbo.platform_drug(id),
  state nvarchar(16) NOT NULL CONSTRAINT DF_pmap_state DEFAULT N'UNRESOLVED',
  is_locked bit NOT NULL CONSTRAINT DF_pmap_locked DEFAULT 0,
  rule_version nvarchar(32) NULL,
  evidence_json nvarchar(max) NULL,
  confirmed_by nvarchar(64) NULL,
  confirmed_at datetime2(3) NULL,
  version int NOT NULL CONSTRAINT DF_pmap_version DEFAULT 1,
  ver rowversion NOT NULL,
  CONSTRAINT CK_pmap_state CHECK (state IN (N'UNRESOLVED',N'CANDIDATE',N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED',N'CONFLICT',N'REVIEW_REQUIRED',N'DISABLED'))
);
CREATE UNIQUE INDEX UX_pmap_active_local ON dbo.product_mapping(product_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');
CREATE UNIQUE INDEX UX_pmap_active_platform ON dbo.product_mapping(platform_drug_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');

CREATE TABLE dbo.product_mapping_history (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_pmap_history PRIMARY KEY,
  mapping_id bigint NOT NULL,
  product_id bigint NOT NULL,
  platform_drug_id bigint NOT NULL,
  state nvarchar(16) NOT NULL,
  rule_version nvarchar(32) NULL,
  evidence_json nvarchar(max) NULL,
  changed_by nvarchar(64) NOT NULL,
  changed_at datetime2(3) NOT NULL CONSTRAINT DF_pmap_hist_at DEFAULT SYSUTCDATETIME(),
  reason nvarchar(500) NULL
);

CREATE TABLE dbo.supplier_mapping (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_supplier_mapping PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_smap_org REFERENCES dbo.organization(id),
  supplier_id bigint NOT NULL CONSTRAINT FK_smap_supplier REFERENCES dbo.supplier(id),
  enterprise_version_id bigint NOT NULL CONSTRAINT FK_smap_entver REFERENCES dbo.platform_enterprise_version(id),
  state nvarchar(16) NOT NULL CONSTRAINT DF_smap_state DEFAULT N'UNRESOLVED',
  is_locked bit NOT NULL CONSTRAINT DF_smap_locked DEFAULT 0,
  evidence_json nvarchar(max) NULL,
  version int NOT NULL CONSTRAINT DF_smap_version DEFAULT 1,
  ver rowversion NOT NULL,
  CONSTRAINT CK_smap_state CHECK (state IN (N'UNRESOLVED',N'CANDIDATE',N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED',N'CONFLICT',N'REVIEW_REQUIRED',N'DISABLED'))
);
CREATE UNIQUE INDEX UX_smap_active_local ON dbo.supplier_mapping(supplier_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');
CREATE UNIQUE INDEX UX_smap_active_platform ON dbo.supplier_mapping(org_id, enterprise_version_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');

CREATE TABLE dbo.supplier_mapping_history (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_smap_history PRIMARY KEY,
  mapping_id bigint NOT NULL,
  supplier_id bigint NOT NULL,
  enterprise_version_id bigint NOT NULL,
  state nvarchar(16) NOT NULL,
  evidence_json nvarchar(max) NULL,
  changed_by nvarchar(64) NOT NULL,
  changed_at datetime2(3) NOT NULL CONSTRAINT DF_smap_hist_at DEFAULT SYSUTCDATETIME(),
  reason nvarchar(500) NULL
);

CREATE TABLE dbo.drug_prefix_index (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_drug_prefix PRIMARY KEY,
  prefix char(7) COLLATE Latin1_General_BIN2 NOT NULL,
  platform_drug_id bigint NOT NULL CONSTRAINT FK_prefix_drug REFERENCES dbo.platform_drug(id),
  evidence_json nvarchar(max) NULL,
  conflict_state nvarchar(16) NOT NULL CONSTRAINT DF_prefix_conflict DEFAULT N'OK',
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_prefix_observed DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_drug_prefix ON dbo.drug_prefix_index(prefix); -- 非唯一：prefix 不是全局药品外键（MATCH-03）

CREATE TABLE dbo.import_batch (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_import_batch PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_import_org REFERENCES dbo.organization(id),
  file_kind nvarchar(32) NOT NULL,
  file_name nvarchar(260) NOT NULL,
  file_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  total_rows int NOT NULL,
  success_rows int NOT NULL CONSTRAINT DF_import_success DEFAULT 0,
  failed_rows int NOT NULL CONSTRAINT DF_import_failed DEFAULT 0,
  status nvarchar(16) NOT NULL CONSTRAINT DF_import_status DEFAULT N'STAGED',
  staged_by nvarchar(64) NOT NULL,
  staged_at datetime2(3) NOT NULL CONSTRAINT DF_import_staged DEFAULT SYSUTCDATETIME(),
  committed_by nvarchar(64) NULL,
  committed_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_import_intent UNIQUE (org_id, file_kind, file_hash),
  CONSTRAINT CK_import_status CHECK (status IN (N'STAGED',N'COMMITTED',N'FAILED'))
);

CREATE TABLE dbo.import_row (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_import_row PRIMARY KEY,
  batch_id bigint NOT NULL CONSTRAINT FK_import_row_batch REFERENCES dbo.import_batch(id),
  row_number int NOT NULL,
  raw_json nvarchar(max) NOT NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_import_row_status DEFAULT N'OK',
  error nvarchar(1000) NULL,
  mapping_version int NULL,
  CONSTRAINT UQ_import_row UNIQUE (batch_id, row_number),
  CONSTRAINT CK_import_row_status CHECK (status IN (N'OK',N'ERROR',N'APPLIED'))
);
```

- [ ] **Step 5: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter MasterConstraintTests`
Expected: 3 PASS。

- [ ] **Step 6: Commit**

```bash
git add traceflow/db/migrations/0002_source.sql traceflow/db/migrations/0003_master.sql traceflow/tests/TraceFlow.IntegrationTests/Migrations/MasterConstraintTests.cs
git commit -m "feat(M0): source mirror + master data DDL with DB-level bidirectional active mapping uniqueness"
```

---

### Task 7: 0004 上游单 + 0005 码台账/占用/分配/核验 DDL

**Files:**
- Create: `traceflow/db/migrations/0004_upstream.sql`
- Create: `traceflow/db/migrations/0005_codes.sql`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/CodeConstraintTests.cs`

**Interfaces:**
- Produces: 表 `upstream_bill/upstream_bill_line/upstream_bill_code/download_progress/trace_code/code_relation/code_event/code_claim/inbound_allocation/allocation_code/verification`。**同店同码同冲突事件范围的 active claim 数据库唯一**（DB-05/AT-032）；`env` 列实现 Live/Mock 占用隔离；事件+码唯一（事件非全历史唯一，AT-046）。

- [ ] **Step 1: 写失败测试**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class CodeConstraintTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public CodeConstraintTests(Sql2008Fixture fx) => _fx = fx;

    private static async Task<long> SeedCodeAsync(SqlConnection c, string code)
        => await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id VALUES(@code,@code,N'Supported8Prefix20')", new { code });

    [Fact]
    public async Task Active_claim_unique_per_store_env_code_scope()
    {
        await using var c = await _fx.OpenAsync();
        var codeId = await SeedCodeAsync(c, "81234567890123456701");
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(1,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { codeId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(1,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { codeId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Mock_and_live_claims_isolated()
    {
        await using var c = await _fx.OpenAsync();
        var codeId = await SeedCodeAsync(c, "81234567890123456702");
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(1,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { codeId });
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(1,N'MOCK',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { codeId }); // 不冲突
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.code_claim WHERE code_id=@codeId", new { codeId }));
    }

    [Fact]
    public async Task Released_claim_allows_new_active_claim()
    {
        await using var c = await _fx.OpenAsync();
        var codeId = await SeedCodeAsync(c, "81234567890123456703");
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(1,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'RELEASED')", new { codeId });
        await c.ExecuteAsync("INSERT INTO dbo.code_claim(org_id,env,code_id,event_scope,claim_state) VALUES(1,N'LIVE',@codeId,N'OUTBOUND_FLOW',N'ACTIVE')", new { codeId });
    }

    [Fact]
    public async Task Same_code_multiple_events_allowed_but_event_code_unique()
    {
        await using var c = await _fx.OpenAsync();
        var codeId = await SeedCodeAsync(c, "81234567890123456704");
        await c.ExecuteAsync("INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_line_id) VALUES(1,N'LIVE',@codeId,N'PURCHASE_IN',N'IN',1)", new { codeId });
        await c.ExecuteAsync("INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_line_id) VALUES(1,N'LIVE',@codeId,N'SALE',N'OUT',2)", new { codeId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            "INSERT INTO dbo.code_event(org_id,env,code_id,event_type,direction,source_line_id) VALUES(1,N'LIVE',@codeId,N'SALE',N'OUT',2)", new { codeId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Same_bill_no_different_upstream_subjects_not_merged()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync("INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no) VALUES(1,N'LIVE',N'E1',N'B1',N'NO-1')");
        await c.ExecuteAsync("INSERT INTO dbo.upstream_bill(org_id,env,upstream_ref_ent_id,platform_bill_id,bill_no) VALUES(1,N'LIVE',N'E2',N'B1',N'NO-1')");
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.upstream_bill WHERE bill_no=N'NO-1'"));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter CodeConstraintTests`
Expected: FAIL（表不存在）。

- [ ] **Step 3: 写 0004_upstream.sql**

```sql
-- 0004 上游出库单：主体组合唯一 + 列表/详情分页进度分离
CREATE TABLE dbo.upstream_bill (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upstream_bill PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_upbill_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  upstream_ref_ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  platform_bill_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  bill_no nvarchar(64) NULL,
  bill_date datetime2(3) NULL,
  from_ent_id nvarchar(64) NULL,
  to_ent_id nvarchar(64) NULL,
  owner_ent_id nvarchar(64) NULL,
  deliver_ent_id nvarchar(64) NULL,
  completeness nvarchar(16) NOT NULL CONSTRAINT DF_upbill_complete DEFAULT N'LIST_ONLY',
  list_raw nvarchar(max) NULL,
  source_version nvarchar(64) NULL,
  fetched_at datetime2(3) NOT NULL CONSTRAINT DF_upbill_fetched DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_upbill_identity UNIQUE (org_id, env, upstream_ref_ent_id, platform_bill_id),
  CONSTRAINT CK_upbill_complete CHECK (completeness IN (N'LIST_ONLY',N'DETAIL_PARTIAL',N'COMPLETE',N'INCOMPLETE')),
  CONSTRAINT CK_upbill_env CHECK (env IN (N'LIVE',N'MOCK'))
);

CREATE TABLE dbo.upstream_bill_line (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upstream_line PRIMARY KEY,
  bill_id bigint NOT NULL CONSTRAINT FK_upline_bill REFERENCES dbo.upstream_bill(id),
  platform_line_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  platform_drug_id nvarchar(64) NULL,
  batch_no nvarchar(100) NULL,
  quantity decimal(18,6) NULL,
  unit nvarchar(32) NULL,
  package_level nvarchar(16) NULL,
  raw_json nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_upline UNIQUE (bill_id, platform_line_id)
);

CREATE TABLE dbo.upstream_bill_code (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upstream_code PRIMARY KEY,
  bill_id bigint NOT NULL CONSTRAINT FK_upcode_bill REFERENCES dbo.upstream_bill(id),
  line_id bigint NOT NULL CONSTRAINT FK_upcode_line REFERENCES dbo.upstream_bill_line(id),
  code_normalized nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  code_raw nvarchar(200) NOT NULL,
  package_level nvarchar(16) NULL,
  parent_code nvarchar(200) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_upcode UNIQUE (bill_id, code_normalized)
);

CREATE TABLE dbo.download_progress (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_download_progress PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_download_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  job_kind nvarchar(32) NOT NULL,
  bill_id bigint NOT NULL CONSTRAINT DF_download_bill DEFAULT 0, -- 0=列表级进度
  page_cursor nvarchar(200) NULL,
  pages_done int NOT NULL CONSTRAINT DF_download_pages DEFAULT 0,
  total_pages int NULL,
  total_count_expected int NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_download_status DEFAULT N'RUNNING',
  last_success_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_download UNIQUE (org_id, env, job_kind, bill_id),
  CONSTRAINT CK_download_status CHECK (status IN (N'RUNNING',N'COMPLETE',N'FAILED'))
);
```

- [ ] **Step 4: 写 0005_codes.sql**

```sql
-- 0005 码台账：身份/真实父子边/不可变事件流/占用/行级分配/核验
CREATE TABLE dbo.trace_code (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_trace_code PRIMARY KEY,
  code_normalized nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  code_raw nvarchar(200) NOT NULL,
  format nvarchar(16) NOT NULL,
  first_seen_at datetime2(3) NOT NULL CONSTRAINT DF_code_seen DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_trace_code UNIQUE (code_normalized)
);

CREATE TABLE dbo.code_relation (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_code_relation PRIMARY KEY,
  parent_code_id bigint NOT NULL CONSTRAINT FK_rel_parent REFERENCES dbo.trace_code(id),
  child_code_id bigint NOT NULL CONSTRAINT FK_rel_child REFERENCES dbo.trace_code(id),
  relation_source nvarchar(32) NOT NULL,
  evidence_json nvarchar(max) NULL,
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_rel_observed DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_code_relation UNIQUE (parent_code_id, child_code_id, relation_source)
);

CREATE TABLE dbo.code_event (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_code_event PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_event_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  code_id bigint NOT NULL CONSTRAINT FK_event_code REFERENCES dbo.trace_code(id),
  event_type nvarchar(16) NOT NULL,
  direction nvarchar(8) NOT NULL,
  source_snapshot_id bigint NULL,
  source_line_id bigint NOT NULL CONSTRAINT DF_event_line DEFAULT 0, -- 0=无来源行（外部证据事件）
  upload_task_id bigint NULL,
  predecessor_event_id bigint NULL CONSTRAINT FK_event_pred REFERENCES dbo.code_event(id),
  occurred_at datetime2(3) NOT NULL CONSTRAINT DF_event_at DEFAULT SYSUTCDATETIME(),
  business_datetime datetime2(3) NULL,
  evidence_json nvarchar(max) NULL,
  CONSTRAINT UQ_event_code UNIQUE (org_id, env, code_id, event_type, source_line_id),
  CONSTRAINT CK_event_type CHECK (event_type IN (N'PURCHASE_IN',N'SALE',N'CONSUMER_RETURN',N'PURCHASE_RETURN')),
  CONSTRAINT CK_event_dir CHECK (direction IN (N'IN',N'OUT'))
);
CREATE INDEX IX_code_event_code ON dbo.code_event(code_id, occurred_at);

CREATE TABLE dbo.code_claim (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_code_claim PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_claim_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  code_id bigint NOT NULL CONSTRAINT FK_claim_code REFERENCES dbo.trace_code(id),
  event_scope nvarchar(32) NOT NULL,
  upload_task_id bigint NULL,
  allocation_id bigint NULL,
  claim_state nvarchar(16) NOT NULL CONSTRAINT DF_claim_state DEFAULT N'ACTIVE',
  predecessor_evidence nvarchar(max) NULL,
  claimed_at datetime2(3) NOT NULL CONSTRAINT DF_claim_at DEFAULT SYSUTCDATETIME(),
  released_at datetime2(3) NULL,
  released_by nvarchar(64) NULL,
  release_evidence nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT CK_claim_state CHECK (claim_state IN (N'ACTIVE',N'RELEASED'))
);
CREATE UNIQUE INDEX UX_claim_active ON dbo.code_claim(org_id, env, code_id, event_scope) WHERE claim_state = N'ACTIVE';

CREATE TABLE dbo.inbound_allocation (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_inbound_alloc PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_alloc_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  source_line_id bigint NOT NULL CONSTRAINT FK_alloc_line REFERENCES dbo.source_line(id),
  upstream_line_id bigint NOT NULL CONSTRAINT FK_alloc_upline REFERENCES dbo.upstream_bill_line(id),
  quantity decimal(18,6) NOT NULL,
  unit_ratio_numerator bigint NOT NULL,
  unit_ratio_denominator bigint NOT NULL,
  conversion_evidence nvarchar(max) NULL,
  package_level nvarchar(16) NULL,
  state nvarchar(16) NOT NULL CONSTRAINT DF_alloc_state DEFAULT N'TENTATIVE',
  version int NOT NULL CONSTRAINT DF_alloc_version DEFAULT 1,
  created_by nvarchar(64) NOT NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_alloc_at DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT CK_alloc_state CHECK (state IN (N'TENTATIVE',N'RESERVED',N'COMMITTED',N'CANCELLED'))
);
CREATE UNIQUE INDEX UX_alloc_pair_active ON dbo.inbound_allocation(source_line_id, upstream_line_id) WHERE state IN (N'TENTATIVE',N'RESERVED',N'COMMITTED');

CREATE TABLE dbo.allocation_code (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_alloc_code PRIMARY KEY,
  allocation_id bigint NOT NULL CONSTRAINT FK_alloccode_alloc REFERENCES dbo.inbound_allocation(id),
  code_id bigint NOT NULL CONSTRAINT FK_alloccode_code REFERENCES dbo.trace_code(id),
  CONSTRAINT UQ_alloc_code UNIQUE (allocation_id, code_id)
);
-- “一个采购入库来源码不被两个有效入库同时消费”由 code_claim(event_scope='PURCHASE_IN') 的 active 唯一索引保证（DB-05）。

CREATE TABLE dbo.verification (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_verification PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_verif_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  event_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  upload_task_id bigint NULL,
  source_snapshot_id bigint NOT NULL CONSTRAINT FK_verif_snapshot REFERENCES dbo.source_snapshot(id),
  source_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  code_set_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  rule_version nvarchar(32) NOT NULL,
  state nvarchar(16) NOT NULL CONSTRAINT DF_verif_state DEFAULT N'NOT_STARTED',
  result_json nvarchar(max) NULL,
  verified_at datetime2(3) NULL,
  is_current bit NOT NULL CONSTRAINT DF_verif_current DEFAULT 1,
  ver rowversion NOT NULL,
  CONSTRAINT CK_verif_state CHECK (state IN (N'NOT_STARTED',N'QUEUED',N'RUNNING',N'WAIT_RETRY',N'PASSED',N'FAILED',N'UNRESOLVED'))
);
CREATE UNIQUE INDEX UX_verif_current ON dbo.verification(org_id, env, event_key) WHERE is_current = 1;
```

- [ ] **Step 5: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter CodeConstraintTests`
Expected: 5 PASS。

- [ ] **Step 6: Commit**

```bash
git add traceflow/db/migrations/0004_upstream.sql traceflow/db/migrations/0005_codes.sql traceflow/tests/TraceFlow.IntegrationTests/Migrations/CodeConstraintTests.cs
git commit -m "feat(M0): upstream + code ledger DDL; env-isolated active claim uniqueness (DB-05)"
```

---

### Task 8: 0006 上传任务/尝试/逐码结果/队列/对账 DDL + 2008 R2 兼容门禁

**Files:**
- Create: `traceflow/db/migrations/0006_upload.sql`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/UploadConstraintTests.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Migrations/Sql2008CompatibilityTests.cs`

**Interfaces:**
- Produces: 表 `upload_task/upload_part/upload_attempt/code_result/outbox_job/reconciliation_run/reconciliation_difference`。业务事件键唯一（IDEM-01）；平台单号唯一（IDEM-02）；`code_result.result ∈ SUCCESS/FAILED/UNKNOWN`（TOP-03）；`outbox_job` 含 `lease_owner/lease_until/fencing_token/next_run_at/attempt_count/job_state/last_error`（JOB-01 字段全集）。Task 9 的认领 SQL 依赖本表。

- [ ] **Step 1: 写失败测试**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class UploadConstraintTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public UploadConstraintTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Business_event_key_unique()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
                               VALUES(1,N'LIVE',N'O1|KSOA|L1|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')");
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
              VALUES(1,N'LIVE',N'O1|KSOA|L1|SALE',N'RETAIL_321',REPLICATE('c',64),REPLICATE('d',64),N'BLOCKED')"));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Mock_task_with_same_event_key_isolated()
    {
        await using var c = await _fx.OpenAsync();
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
                               VALUES(1,N'LIVE',N'O1|KSOA|L2|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')");
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
                               VALUES(1,N'MOCK',N'O1|KSOA|L2|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')");
    }

    [Fact]
    public async Task Bill_code_unique_and_part_index_unique()
    {
        await using var c = await _fx.OpenAsync();
        var taskId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
            OUTPUT inserted.id VALUES(1,N'LIVE',N'O1|KSOA|L3|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')");
        await c.ExecuteAsync(@"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_hash,code_set_hash,state)
                               VALUES(@taskId,1,N'LIVE',0,N'TF1',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId });
        var ex = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(
            @"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_hash,code_set_hash,state)
              VALUES(@taskId,1,N'LIVE',1,N'TF1',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId }));
        Assert.True(ex.Number is 2601 or 2627);
    }

    [Fact]
    public async Task Code_result_unknown_allowed()
    {
        await using var c = await _fx.OpenAsync();
        var taskId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
            OUTPUT inserted.id VALUES(1,N'LIVE',N'O1|KSOA|L4|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'BLOCKED')");
        var partId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_hash,code_set_hash,state)
            OUTPUT inserted.id VALUES(@taskId,1,N'LIVE',0,N'TF2',REPLICATE('b',64),REPLICATE('c',64),N'BLOCKED')", new { taskId });
        var codeId = await c.ExecuteScalarAsync<long>("INSERT INTO dbo.trace_code(code_normalized,code_raw,format) OUTPUT inserted.id VALUES(N'81234567890123456705',N'81234567890123456705',N'Supported8Prefix20')");
        var attemptId = await c.ExecuteScalarAsync<long>(@"INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token)
            OUTPUT inserted.id VALUES(@taskId,@partId,1,N'QUERY',N'LIVE',1)", new { taskId, partId });
        await c.ExecuteAsync(@"INSERT INTO dbo.code_result(task_id,part_id,code_id,result,attempt_id)
                               VALUES(@taskId,@partId,@codeId,N'UNKNOWN',@attemptId)", new { taskId, partId, codeId, attemptId });
    }
}
```

`Sql2008CompatibilityTests.cs`（AT-070 静态+运行双证据）：

```csharp
using Dapper;
using Xunit;

namespace TraceFlow.IntegrationTests.Migrations;

public class Sql2008CompatibilityTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public Sql2008CompatibilityTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public void No_high_version_syntax_in_any_migration_or_embedded_sql()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "db", "migrations"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src")),
        };
        var forbidden = new[] { @"\bOFFSET\b", @"\bFETCH\s+NEXT\b", @"\bSEQUENCE\b", @"\bJSON_VALUE\b", @"\bOPENJSON\b", @"\bSTRING_AGG\b", @"CREATE\s+OR\s+ALTER", @"\bNOLOCK\b" };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".sql") || f.EndsWith(".cs")))
            {
                var text = File.ReadAllText(file);
                foreach (var pattern in forbidden)
                    Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                        $"{file} 含 2008 R2 禁用语法: {pattern}");
            }
    }

    [Fact]
    public async Task Row_number_pagination_works_on_2008r2()
    {
        await using var c = await _fx.OpenAsync();
        var rows = await c.QueryAsync<long>(@"
;WITH numbered AS (
  SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn FROM dbo.audit_log
)
SELECT id FROM numbered WHERE rn > @skip AND rn <= @take ORDER BY rn", new { skip = 0, take = 50 });
        Assert.NotNull(rows);
    }
}
```

注意：静态扫描会命中测试/注释中的字面词。实现约定——迁移与生产 SQL 中绝不出现禁词；本测试文件自身用拼接构造 pattern（如上 `\b` 正则字符串不含完整禁词字面量，`NOLOCK` 一词仅以正则片段出现）；若扫描误报注释，调整注释措辞而不是放宽扫描。

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter "UploadConstraintTests|Sql2008CompatibilityTests"`
Expected: UploadConstraintTests FAIL（表不存在）。

- [ ] **Step 3: 写 0006_upload.sql**

```sql
-- 0006 上传：逻辑任务/分片/尝试/逐码结果/持久队列/对账
CREATE TABLE dbo.upload_task (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upload_task PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_task_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  event_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  task_type nvarchar(24) NOT NULL,
  source_snapshot_id bigint NULL,
  source_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  mapping_version_snapshot nvarchar(200) NULL,
  payload_json nvarchar(max) NULL,
  payload_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  state nvarchar(24) NOT NULL CONSTRAINT DF_task_state DEFAULT N'Blocked',
  state_reason nvarchar(500) NULL,
  source_integrity nvarchar(16) NOT NULL CONSTRAINT DF_task_src DEFAULT N'DISCOVERED',
  match_state nvarchar(16) NOT NULL CONSTRAINT DF_task_match DEFAULT N'UNMATCHED',
  verification_state nvarchar(16) NOT NULL CONSTRAINT DF_task_verif DEFAULT N'NOT_STARTED',
  dependency_state nvarchar(16) NOT NULL CONSTRAINT DF_task_dep DEFAULT N'NONE',
  created_by nvarchar(64) NOT NULL CONSTRAINT DF_task_by DEFAULT N'SYSTEM',
  created_at datetime2(3) NOT NULL CONSTRAINT DF_task_at DEFAULT SYSUTCDATETIME(),
  version int NOT NULL CONSTRAINT DF_task_version DEFAULT 1,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_task_event UNIQUE (org_id, env, event_key),
  CONSTRAINT CK_task_type CHECK (task_type IN (N'PURCHASE_IN_102',N'RETAIL_321',N'CONSUMER_RETURN_116',N'PURCHASE_RETURN_202')),
  CONSTRAINT CK_task_state CHECK (state IN (N'Blocked',N'Ready',N'Dispatching',N'Accepted',N'AcceptanceUnknown',N'Processing',N'RetryWait',N'PartialSuccess',N'ResultUnresolved',N'Succeeded',N'Failed',N'ManualAction')),
  CONSTRAINT CK_task_env CHECK (env IN (N'LIVE',N'MOCK'))
);
CREATE INDEX IX_upload_task_state ON dbo.upload_task(env, state);

CREATE TABLE dbo.upload_part (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upload_part PRIMARY KEY,
  task_id bigint NOT NULL CONSTRAINT FK_part_task REFERENCES dbo.upload_task(id),
  org_id bigint NOT NULL,
  env char(4) NOT NULL,
  part_index int NOT NULL,
  bill_code nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  payload_json nvarchar(max) NOT NULL,
  payload_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  code_set_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  split_rule_version nvarchar(32) NULL,
  state nvarchar(24) NOT NULL CONSTRAINT DF_part_state DEFAULT N'Blocked',
  ver rowversion NOT NULL,
  CONSTRAINT UQ_part_index UNIQUE (task_id, part_index),
  CONSTRAINT UQ_part_billcode UNIQUE (org_id, env, bill_code),
  CONSTRAINT CK_part_state CHECK (state IN (N'Blocked',N'Ready',N'Dispatching',N'Accepted',N'AcceptanceUnknown',N'Processing',N'RetryWait',N'PartialSuccess',N'ResultUnresolved',N'Succeeded',N'Failed',N'ManualAction'))
);

CREATE TABLE dbo.upload_attempt (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upload_attempt PRIMARY KEY,
  task_id bigint NOT NULL CONSTRAINT FK_attempt_task REFERENCES dbo.upload_task(id),
  part_id bigint NULL CONSTRAINT FK_attempt_part REFERENCES dbo.upload_part(id),
  attempt_no int NOT NULL,
  attempt_kind nvarchar(16) NOT NULL,
  env char(4) NOT NULL,
  lease_owner nvarchar(64) NULL,
  fencing_token bigint NOT NULL,
  started_at datetime2(3) NOT NULL CONSTRAINT DF_attempt_start DEFAULT SYSUTCDATETIME(),
  finished_at datetime2(3) NULL,
  http_status int NULL,
  request_id nvarchar(64) NULL,
  platform_task_id nvarchar(64) NULL,
  raw_response nvarchar(max) NULL, -- 脱敏后保存；禁 AppSecret/完整签名
  error_kind nvarchar(32) NULL,
  is_stale_evidence bit NOT NULL CONSTRAINT DF_attempt_stale DEFAULT 0,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_attempt UNIQUE (task_id, attempt_kind, attempt_no),
  CONSTRAINT CK_attempt_kind CHECK (attempt_kind IN (N'UPLOAD',N'QUERY'))
);

CREATE TABLE dbo.code_result (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_code_result PRIMARY KEY,
  task_id bigint NOT NULL CONSTRAINT FK_result_task REFERENCES dbo.upload_task(id),
  part_id bigint NOT NULL CONSTRAINT FK_result_part REFERENCES dbo.upload_part(id),
  code_id bigint NOT NULL CONSTRAINT FK_result_code REFERENCES dbo.trace_code(id),
  result nvarchar(8) NOT NULL,
  reason nvarchar(500) NULL,
  platform_evidence nvarchar(max) NULL,
  attempt_id bigint NOT NULL CONSTRAINT FK_result_attempt REFERENCES dbo.upload_attempt(id),
  recorded_at datetime2(3) NOT NULL CONSTRAINT DF_result_at DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_code_result UNIQUE (part_id, code_id, attempt_id),
  CONSTRAINT CK_code_result CHECK (result IN (N'SUCCESS',N'FAILED',N'UNKNOWN'))
);

CREATE TABLE dbo.outbox_job (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_outbox_job PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_job_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  job_kind nvarchar(32) NOT NULL,
  intent_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  payload_json nvarchar(max) NULL,
  job_state nvarchar(16) NOT NULL CONSTRAINT DF_job_state DEFAULT N'PENDING',
  next_run_at datetime2(3) NOT NULL CONSTRAINT DF_job_next DEFAULT SYSUTCDATETIME(),
  attempt_count int NOT NULL CONSTRAINT DF_job_attempts DEFAULT 0,
  max_attempts int NOT NULL CONSTRAINT DF_job_max DEFAULT 10,
  lease_owner nvarchar(64) NULL,
  lease_until datetime2(3) NULL,
  fencing_token bigint NOT NULL CONSTRAINT DF_job_token DEFAULT 0,
  last_error nvarchar(max) NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_job_created DEFAULT SYSUTCDATETIME(),
  finished_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_job_intent UNIQUE (org_id, env, job_kind, intent_key),
  CONSTRAINT CK_job_state CHECK (job_state IN (N'PENDING',N'RUNNING',N'DONE',N'FAILED',N'CANCELLED'))
);
CREATE INDEX IX_outbox_job_claim ON dbo.outbox_job(job_state, next_run_at);

CREATE TABLE dbo.reconciliation_run (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_recon_run PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_recon_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  window_start datetime2(3) NOT NULL,
  window_end datetime2(3) NOT NULL,
  business_type nvarchar(16) NOT NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_recon_status DEFAULT N'RUNNING',
  local_summary nvarchar(max) NULL,
  platform_summary nvarchar(max) NULL,
  started_at datetime2(3) NOT NULL CONSTRAINT DF_recon_start DEFAULT SYSUTCDATETIME(),
  finished_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT CK_recon_status CHECK (status IN (N'RUNNING',N'COMPLETE',N'INCOMPLETE'))
);

CREATE TABLE dbo.reconciliation_difference (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_recon_diff PRIMARY KEY,
  run_id bigint NOT NULL CONSTRAINT FK_diff_run REFERENCES dbo.reconciliation_run(id),
  diff_type nvarchar(32) NOT NULL,
  severity nvarchar(8) NOT NULL,
  object_ref nvarchar(200) NOT NULL,
  evidence_json nvarchar(max) NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_diff_status DEFAULT N'OPEN',
  owner_role nvarchar(32) NULL,
  next_action nvarchar(200) NULL,
  resolved_by nvarchar(64) NULL,
  resolved_at datetime2(3) NULL,
  resolution_evidence nvarchar(max) NULL,
  found_at datetime2(3) NOT NULL CONSTRAINT DF_diff_found DEFAULT SYSUTCDATETIME(),
  CONSTRAINT CK_diff_type CHECK (diff_type IN (N'SOURCE_MISSING',N'UPSTREAM_INCOMPLETE',N'MATCH_REQUIRED',N'MISSING_CODE',N'WAIT_VERIFY',N'WAIT_DEPENDENCY',N'PLATFORM_MISSING',N'ACCEPTANCE_UNKNOWN',N'PARTIAL_SUCCESS',N'SOURCE_CHANGED',N'RESULT_UNRESOLVED')),
  CONSTRAINT CK_diff_status CHECK (status IN (N'OPEN',N'RESOLVED'))
);
```

- [ ] **Step 4: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests`
Expected: 全部 PASS（含此前任务）。记录 `SELECT @@VERSION` 输出到 `docs/delivery/M0-gate-report.md`（Task 15 汇总）。

- [ ] **Step 5: Commit**

```bash
git add traceflow/db/migrations/0006_upload.sql traceflow/tests/TraceFlow.IntegrationTests/Migrations
git commit -m "feat(M0): upload/outbox/reconciliation DDL + SQL 2008 R2 compatibility gate (AT-070)"
```

---

### Task 9: 持久任务队列（原子认领/租约/fencing/守卫写回）

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Abstractions/IJobQueue.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/JobQueue.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Jobs/JobQueueTests.cs`

**Interfaces:**
- Consumes: `dbo.outbox_job`（Task 8）。
- Produces（后续所有作业管道依赖）:

```csharp
namespace TraceFlow.Application.Abstractions;

public sealed record ClaimedJob(long Id, string JobKind, string IntentKey, string? PayloadJson,
    long FencingToken, DateTime LeaseUntilUtc, int AttemptCount);

public interface IJobQueue
{
    // 在调用方事务内入队（T1/T2 同事务要求）；同意图幂等返回已有 id
    Task<long> EnqueueAsync(Microsoft.Data.SqlClient.SqlConnection conn, Microsoft.Data.SqlClient.SqlTransaction tx,
        long orgId, string env, string jobKind, string intentKey, string? payloadJson,
        DateTime? nextRunAtUtc, CancellationToken ct);
    Task<ClaimedJob?> ClaimAsync(string workerId, string env, IReadOnlyCollection<string> jobKinds, TimeSpan lease, CancellationToken ct);
    Task<bool> CompleteAsync(long jobId, string workerId, long fencingToken, CancellationToken ct);
    Task<bool> RescheduleAsync(long jobId, string workerId, long fencingToken, DateTime nextRunAtUtc, string? error, CancellationToken ct);
    Task<bool> FailAsync(long jobId, string workerId, long fencingToken, string error, CancellationToken ct);
    // 通用租约回收：upload-dispatch 除外（由 Task 10 发送恢复处理，不得重新直接上传）
    Task<int> RecoverExpiredLeasesAsync(CancellationToken ct);
}
```

- [ ] **Step 1: 写失败测试（两个独立连接，AT-058/059 机制）**

```csharp
using Dapper;
using TraceFlow.Infrastructure.Jobs;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

public class JobQueueTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public JobQueueTests(Sql2008Fixture fx) => _fx = fx;

    private async Task<long> SeedJobAsync(string intent)
    {
        await using var c = await _fx.OpenAsync();
        var q = new JobQueue(_fx.ConnectionString);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var id = await q.EnqueueAsync(c, tx, 1, "LIVE", "test-job", intent, null, null, default);
        await tx.CommitAsync();
        return id;
    }

    [Fact]
    public async Task Two_workers_claim_same_job_only_one_wins()
    {
        var id = await SeedJobAsync("claim-race-" + Guid.NewGuid());
        var q1 = new JobQueue(_fx.ConnectionString);
        var q2 = new JobQueue(_fx.ConnectionString);
        var t1 = q1.ClaimAsync("w1", "LIVE", new[] { "test-job" }, TimeSpan.FromMinutes(5), default);
        var t2 = q2.ClaimAsync("w2", "LIVE", new[] { "test-job" }, TimeSpan.FromMinutes(5), default);
        var results = await Task.WhenAll(t1, t2);
        // 队列中可能有其他测试遗留 job；断言：同一 id 至多被一个 worker 认领
        var winners = results.Where(r => r is not null && r.Id == id).ToList();
        Assert.Single(winners);
        Assert.Equal(1, winners[0]!.FencingToken);
    }

    [Fact]
    public async Task Complete_with_correct_token_succeeds_stale_token_rejected()
    {
        var id = await SeedJobAsync("complete-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var claim = await q.ClaimAsync("w1", "LIVE", new[] { "test-job" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        Assert.False(await q.CompleteAsync(claim!.Id, "w1", claim.FencingToken + 99, default)); // 旧/错 token
        Assert.True(await q.CompleteAsync(claim.Id, "w1", claim.FencingToken, default));
        Assert.False(await q.CompleteAsync(claim.Id, "w1", claim.FencingToken, default)); // 已 DONE
    }

    [Fact]
    public async Task Expired_lease_reclaimed_token_increments_old_worker_rejected()
    {
        var id = await SeedJobAsync("lease-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var first = await q.ClaimAsync("w1", "LIVE", new[] { "test-job" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(first);
        await using (var c = await _fx.OpenAsync()) // 模拟租约过期（时钟前拨）
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@id", new { id = first!.Id });
        var recovered = await q.RecoverExpiredLeasesAsync(default);
        Assert.True(recovered >= 1);
        var second = await q.ClaimAsync("w2", "LIVE", new[] { "test-job" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(first.FencingToken + 1, second.FencingToken);
        Assert.False(await q.CompleteAsync(first.Id, "w1", first.FencingToken, default)); // 旧 Worker 写回被拒
        Assert.True(await q.CompleteAsync(second.Id, "w2", second.FencingToken, default));
    }

    [Fact]
    public async Task Enqueue_same_intent_idempotent()
    {
        var intent = "idem-" + Guid.NewGuid();
        var id1 = await SeedJobAsync(intent);
        var id2 = await SeedJobAsync(intent);
        Assert.Equal(id1, id2);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'test-job' AND intent_key=@intent", new { intent }));
    }

    [Fact]
    public async Task Reschedule_keeps_token_guard()
    {
        var id = await SeedJobAsync("resched-" + Guid.NewGuid());
        var q = new JobQueue(_fx.ConnectionString);
        var claim = await q.ClaimAsync("w1", "LIVE", new[] { "test-job" }, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        Assert.False(await q.RescheduleAsync(claim!.Id, "wX", claim.FencingToken, DateTime.UtcNow.AddMinutes(1), "err", default));
        Assert.True(await q.RescheduleAsync(claim.Id, "w1", claim.FencingToken, DateTime.UtcNow.AddMinutes(1), "err", default));
        await using var c = await _fx.OpenAsync();
        Assert.Equal("PENDING", await c.ExecuteScalarAsync<string>("SELECT job_state FROM dbo.outbox_job WHERE id=@id", new { id }));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter JobQueueTests`
Expected: 编译失败（JobQueue 不存在）。

- [ ] **Step 3: 实现 IJobQueue（上方 Interfaces 代码原样落盘）与 JobQueue**

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class JobQueue : IJobQueue
{
    private readonly string _conn;
    public JobQueue(string connectionString) => _conn = connectionString;

    public async Task<long> EnqueueAsync(SqlConnection conn, SqlTransaction tx, long orgId, string env,
        string jobKind, string intentKey, string? payloadJson, DateTime? nextRunAtUtc, CancellationToken ct)
    {
        try
        {
            return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.outbox_job(org_id,env,job_kind,intent_key,payload_json,next_run_at)
OUTPUT inserted.id
VALUES(@orgId,@env,@jobKind,@intentKey,@payloadJson,COALESCE(@nextRunAt,SYSUTCDATETIME()))",
                new { orgId, env, jobKind, intentKey, payloadJson, nextRunAtUtc }, tx);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return await conn.ExecuteScalarAsync<long>(
                "SELECT id FROM dbo.outbox_job WHERE org_id=@orgId AND env=@env AND job_kind=@jobKind AND intent_key=@intentKey",
                new { orgId, env, jobKind, intentKey }, tx);
        }
    }

    public async Task<ClaimedJob?> ClaimAsync(string workerId, string env, IReadOnlyCollection<string> jobKinds,
        TimeSpan lease, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        // 2008 R2 可更新 CTE + UPDLOCK/READPAST：原子认领，网络调用不持锁（JOB-01）
        return await conn.QuerySingleOrDefaultAsync<ClaimedJob>(@"
WITH candidate AS (
  SELECT TOP (1) id, job_kind, intent_key, payload_json, job_state, lease_owner, lease_until, fencing_token, attempt_count
  FROM dbo.outbox_job WITH (UPDLOCK, READPAST)
  WHERE job_state = N'PENDING' AND env = @env AND next_run_at <= SYSUTCDATETIME() AND job_kind IN @jobKinds
  ORDER BY next_run_at, id
)
UPDATE candidate
SET job_state = N'RUNNING',
    lease_owner = @workerId,
    lease_until = DATEADD(millisecond, @leaseMs, SYSUTCDATETIME()),
    fencing_token = fencing_token + 1,
    attempt_count = attempt_count + 1
OUTPUT inserted.id AS Id, inserted.job_kind AS JobKind, inserted.intent_key AS IntentKey,
       inserted.payload_json AS PayloadJson, inserted.fencing_token AS FencingToken,
       inserted.lease_until AS LeaseUntilUtc, inserted.attempt_count AS AttemptCount;",
            new { env, jobKinds, workerId, leaseMs = (long)lease.TotalMilliseconds });
    }

    public async Task<bool> CompleteAsync(long jobId, string workerId, long fencingToken, CancellationToken ct)
        => await GuardedUpdateAsync(jobId, workerId, fencingToken,
            "job_state=N'DONE', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL", null, ct);

    public async Task<bool> RescheduleAsync(long jobId, string workerId, long fencingToken,
        DateTime nextRunAtUtc, string? error, CancellationToken ct)
        => await GuardedUpdateAsync(jobId, workerId, fencingToken,
            "job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=@nextRunAt, last_error=@error",
            new { nextRunAtUtc, error }, ct);

    public async Task<bool> FailAsync(long jobId, string workerId, long fencingToken, string error, CancellationToken ct)
        => await GuardedUpdateAsync(jobId, workerId, fencingToken,
            "job_state=N'FAILED', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL, last_error=@error",
            new { error }, ct);

    private async Task<bool> GuardedUpdateAsync(long jobId, string workerId, long fencingToken,
        string setClause, object? extra, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        var p = new DynamicParameters(extra);
        p.Add("id", jobId); p.Add("workerId", workerId); p.Add("fencingToken", fencingToken);
        var rows = await conn.ExecuteAsync(
            $"UPDATE dbo.outbox_job SET {setClause} WHERE id=@id AND lease_owner=@workerId AND fencing_token=@fencingToken AND job_state=N'RUNNING'", p);
        return rows == 1;
    }

    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        // upload-dispatch 失租不在此回收：必须走发送恢复→受理未知→回查（JOB-02）
        return await conn.ExecuteAsync(@"
UPDATE dbo.outbox_job
SET job_state=N'PENDING', lease_owner=NULL, lease_until=NULL, next_run_at=SYSUTCDATETIME()
WHERE job_state=N'RUNNING' AND lease_until < SYSUTCDATETIME() AND job_kind <> N'upload-dispatch'");
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter JobQueueTests`
Expected: 5 PASS（真实 2008 R2、独立连接并发）。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Application/Abstractions/IJobQueue.cs traceflow/src/TraceFlow.Infrastructure/Jobs/JobQueue.cs traceflow/tests/TraceFlow.IntegrationTests/Jobs
git commit -m "feat(M0): persistent job queue with atomic claim, lease, fencing token (JOB-01/02, AT-058/059)"
```

---

### Task 10: 发送恢复 + 旧 Worker 迟到回执留证

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Abstractions/ISendRecoveryService.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Jobs/SendRecoveryService.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Jobs/SendRecoveryTests.cs`

**Interfaces:**
- Consumes: `IJobQueue`（Task 9）、`UploadStateMachine`（Task 2）、`upload_part/upload_attempt/outbox_job`（Task 8）。
- Produces: `ISendRecoveryService.RecoverAsync(CancellationToken)` → `Task<int>`（恢复的分片数）；`ISendRecoveryService.AppendStaleEvidenceAsync(long taskId, long partId, int attemptNo, string attemptKind, long fencingToken, string? rawResponse, CancellationToken)` → `Task`（只追加 `is_stale_evidence=1` 的尝试记录，**绝不推进任务/分片/占用状态**，设计 §2.7）。恢复规则：`Dispatching` 分片且无有效租约的 `upload-dispatch` 作业 → 状态机 `LeaseLost→AcceptanceUnknown` + 入队 `bill-query` 作业（intent `query:part:{id}`）+ 原 dispatch 作业置 FAILED；**不入队任何重新上传作业**。

- [ ] **Step 1: 写失败测试**

```csharp
using Dapper;
using TraceFlow.Infrastructure.Jobs;
using Xunit;

namespace TraceFlow.IntegrationTests.Jobs;

public class SendRecoveryTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public SendRecoveryTests(Sql2008Fixture fx) => _fx = fx;

    private async Task<(long taskId, long partId)> SeedDispatchingPartAsync(string suffix, bool leaseExpired)
    {
        await using var c = await _fx.OpenAsync();
        var taskId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_task(org_id,env,event_key,task_type,source_hash,payload_hash,state)
OUTPUT inserted.id VALUES(1,N'LIVE',N'O1|KSOA|REC-'+@suffix+N'|SALE',N'RETAIL_321',REPLICATE('a',64),REPLICATE('b',64),N'Dispatching')", new { suffix });
        var partId = await c.ExecuteScalarAsync<long>(@"
INSERT INTO dbo.upload_part(task_id,org_id,env,part_index,bill_code,payload_json,payload_hash,code_set_hash,state)
OUTPUT inserted.id VALUES(@taskId,1,N'LIVE',0,N'TFREC'+@suffix,N'{}',REPLICATE('b',64),REPLICATE('c',64),N'Dispatching')", new { taskId, suffix });
        var q = new JobQueue(_fx.ConnectionString);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        var jobId = await q.EnqueueAsync(c, tx, 1, "LIVE", "upload-dispatch", $"part:{partId}", null, null, default);
        await tx.CommitAsync();
        var claim = await q.ClaimAsync("dead-worker", "LIVE", new[] { "upload-dispatch" }, TimeSpan.FromMinutes(5), default);
        if (leaseExpired)
            await c.ExecuteAsync("UPDATE dbo.outbox_job SET lease_until=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE id=@jobId", new { jobId });
        return (taskId, partId);
    }

    [Fact]
    public async Task Expired_dispatch_becomes_acceptance_unknown_and_query_only()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        var n = await svc.RecoverAsync(default);
        Assert.True(n >= 1);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("AcceptanceUnknown", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k AND job_state=N'PENDING'", new { k = $"query:part:{partId}" }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state=N'PENDING'", new { k = $"part:{partId}" }));
    }

    [Fact]
    public async Task Valid_lease_dispatch_untouched()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: false);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        await svc.RecoverAsync(default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
    }

    [Fact]
    public async Task Recovery_idempotent_second_run_no_duplicate_query_job()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: true);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        await svc.RecoverAsync(default);
        await svc.RecoverAsync(default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.outbox_job WHERE job_kind=N'bill-query' AND intent_key=@k", new { k = $"query:part:{partId}" }));
    }

    [Fact]
    public async Task Stale_receipt_preserved_as_evidence_without_state_change()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (taskId, partId) = await SeedDispatchingPartAsync(suffix, leaseExpired: false);
        var svc = new SendRecoveryService(_fx.ConnectionString, new JobQueue(_fx.ConnectionString));
        await svc.AppendStaleEvidenceAsync(taskId, partId, 99, "UPLOAD", 1, "{\"response_success\":true}", default);
        await using var c = await _fx.OpenAsync();
        Assert.Equal("Dispatching", await c.ExecuteScalarAsync<string>("SELECT state FROM dbo.upload_part WHERE id=@partId", new { partId }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.upload_attempt WHERE task_id=@taskId AND is_stale_evidence=1", new { taskId }));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter SendRecoveryTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`ISendRecoveryService.cs`：

```csharp
namespace TraceFlow.Application.Abstractions;

public interface ISendRecoveryService
{
    Task<int> RecoverAsync(CancellationToken ct);
    Task AppendStaleEvidenceAsync(long taskId, long partId, int attemptNo, string attemptKind,
        long fencingToken, string? rawResponse, CancellationToken ct);
}
```

`SendRecoveryService.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Domain.Uploads;

namespace TraceFlow.Infrastructure.Jobs;

public sealed class SendRecoveryService : ISendRecoveryService
{
    private readonly string _conn;
    private readonly IJobQueue _queue;
    public SendRecoveryService(string connectionString, IJobQueue queue) { _conn = connectionString; _queue = queue; }

    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        if (!UploadStateMachine.TryGetNext(UploadState.Dispatching, UploadEvent.LeaseLost, out var next) ||
            next != UploadState.AcceptanceUnknown)
            throw new InvalidOperationException("状态机缺少 LeaseLost 恢复边");

        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        var candidates = (await conn.QueryAsync<(long PartId, long TaskId, string Env, long OrgId)>(@"
SELECT p.id AS PartId, p.task_id AS TaskId, p.env AS Env, p.org_id AS OrgId
FROM dbo.upload_part p
WHERE p.state = N'Dispatching'
  AND NOT EXISTS (
    SELECT 1 FROM dbo.outbox_job j
    WHERE j.job_kind = N'upload-dispatch' AND j.job_state = N'RUNNING'
      AND j.lease_until >= SYSUTCDATETIME()
      AND j.intent_key = N'part:' + CAST(p.id AS nvarchar(20)))")).ToList();

        var recovered = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            // 版本守卫：仅当仍是 Dispatching 才推进（防止与新认领者竞争）
            var rows = await conn.ExecuteAsync(
                "UPDATE dbo.upload_part SET state=@next WHERE id=@id AND state=N'Dispatching'",
                new { id = c.PartId, next = next.ToString() }, tx);
            if (rows == 1)
            {
                await conn.ExecuteAsync(@"
UPDATE dbo.outbox_job SET job_state=N'FAILED', finished_at=SYSUTCDATETIME(), lease_owner=NULL, lease_until=NULL,
       last_error=N'lease expired; part moved to AcceptanceUnknown; query-first'
WHERE job_kind=N'upload-dispatch' AND intent_key=@k AND job_state=N'RUNNING'",
                    new { k = $"part:{c.PartId}" }, tx);
                await _queue.EnqueueAsync(conn, tx, c.OrgId, c.Env, "bill-query", $"query:part:{c.PartId}", null, null, ct);
                await conn.ExecuteAsync(@"
INSERT INTO dbo.audit_log(actor,actor_kind,action,object_type,object_id,reason,env)
VALUES(N'SYSTEM',N'Worker',N'send-recovery',N'upload_part',@obj,N'失租→受理未知，先回查',@env)",
                    new { obj = c.PartId.ToString(), env = c.Env }, tx);
                recovered++;
            }
            await tx.CommitAsync(ct);
        }
        return recovered;
    }

    public async Task AppendStaleEvidenceAsync(long taskId, long partId, int attemptNo, string attemptKind,
        long fencingToken, string? rawResponse, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_conn);
        await conn.OpenAsync(ct);
        // 只读证据：不触碰任务/分片/占用状态（设计 §2.7）
        await conn.ExecuteAsync(@"
INSERT INTO dbo.upload_attempt(task_id,part_id,attempt_no,attempt_kind,env,fencing_token,raw_response,is_stale_evidence,finished_at)
SELECT @taskId,@partId,@attemptNo,@attemptKind,t.env,@fencingToken,@rawResponse,1,SYSUTCDATETIME()
FROM dbo.upload_task t WHERE t.id=@taskId",
            new { taskId, partId, attemptNo, attemptKind, fencingToken, rawResponse });
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter SendRecoveryTests`
Expected: 4 PASS。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Application/Abstractions/ISendRecoveryService.cs traceflow/src/TraceFlow.Infrastructure/Jobs/SendRecoveryService.cs traceflow/tests/TraceFlow.IntegrationTests/Jobs/SendRecoveryTests.cs
git commit -m "feat(M0): send recovery to acceptance-unknown + stale receipt evidence (JOB-02, IDEM-04)"
```

---

### Task 11: 认证、权限、审计、DPAPI 凭据保护

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Security/PermissionCatalog.cs`
- Create: `traceflow/src/TraceFlow.Application/Abstractions/IPermissionService.cs`、`IAuditLog.cs`、`ISecretProtector.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Security/PermissionService.cs`、`AuditLog.cs`、`DpapiProtector.cs`
- Create: `traceflow/src/TraceFlow.Host/Security/RequirePermissionAttribute.cs`、`PermissionHandler.cs`
- Create: `traceflow/src/TraceFlow.Host/Endpoints/AuthEndpoints.cs`、`AdminEndpoints.cs`
- Create: `traceflow/src/TraceFlow.Host/Security/AdminInitializer.cs`
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Security/PermissionAuditTests.cs`
- Test: `traceflow/tests/TraceFlow.UnitTests/Security/DpapiProtectorTests.cs`

**Interfaces:**
- Produces: `PermissionCatalog.All`（`IReadOnlyList<string>`，PRD §3 全集 + 设计新增 `code:release`）；`IPermissionService.HasAsync(long userId, string permission)`、`GrantAsync(conn, tx, userId, permission, grantedBy)`、`RevokeAsync(...)`、`ForUserAsync(userId)`；`IAuditLog.AppendAsync(SqlConnection, SqlTransaction?, AuditEntry)`，`record AuditEntry(string Actor, string ActorKind, string Action, string ObjectType, string? ObjectId, string? Reason, string? BeforeDigest, string? AfterDigest, string? RequestId, string? RuleVersion, string Env)`；`ISecretProtector.Protect(string)/Unprotect(byte[])`，不可解密抛 `SecretUndecryptableException`（调用方必须停调用+告警，禁止默认凭据兜底）；`[RequirePermission("xxx")]` 特性 + `PermissionHandler`（逐请求查库，AUTH-01）。
- 权限全集：`trace:read, master:match, master:classify, inbound:allocate, job:rematch, upload:query, upload:retry, return:confirm, data:export, audit:read, audit:export, system:manage, code:release`。

- [ ] **Step 1: 写失败测试**

`PermissionAuditTests.cs`：

```csharp
using Dapper;
using TraceFlow.Application.Abstractions;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Security;

public class PermissionAuditTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public PermissionAuditTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Grant_check_revoke_and_duplicate_grant_idempotent()
    {
        await using var c = await _fx.OpenAsync();
        var userId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(N'u1',N'用户1',N'x')");
        var svc = new PermissionService(_fx.ConnectionString);
        Assert.False(await svc.HasAsync(userId, "master:match"));
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.GrantAsync(c, tx, userId, "master:match", "admin");
            await svc.GrantAsync(c, tx, userId, "master:match", "admin"); // 幂等
            await tx.CommitAsync();
        }
        Assert.True(await svc.HasAsync(userId, "master:match"));
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await svc.RevokeAsync(c, tx, userId, "master:match", "admin");
            await tx.CommitAsync();
        }
        Assert.False(await svc.HasAsync(userId, "master:match"));
    }

    [Fact]
    public async Task Audit_append_stores_before_after_and_actor()
    {
        var log = new AuditLog();
        await using var c = await _fx.OpenAsync();
        await log.AppendAsync(c, null, new AuditEntry("admin", "User", "product-mapping.confirm",
            "product_mapping", "42", "证据充分", "{\"v\":1}", "{\"v\":2}", "req-1", null, "LIVE"));
        var row = await c.QuerySingleAsync<(string Actor, string Before, string After)>(
            "SELECT actor AS Actor, before_digest AS Before, after_digest AS After FROM dbo.audit_log WHERE object_id=N'42'");
        Assert.Equal("admin", row.Actor);
        Assert.Equal("{\"v\":1}", row.Before);
        Assert.Equal("{\"v\":2}", row.After);
    }
}
```

`DpapiProtectorTests.cs`：

```csharp
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.UnitTests.Security;

public class DpapiProtectorTests
{
    [Fact]
    public void Roundtrip_under_current_user()
    {
        var p = new DpapiProtector();
        var cipher = p.Protect("app-secret-测试");
        Assert.Equal("app-secret-测试", p.Unprotect(cipher));
        Assert.NotEqual("app-secret-测试"u8.ToArray(), cipher);
    }

    [Fact]
    public void Tampered_ciphertext_throws_no_fallback()
    {
        var p = new DpapiProtector();
        var cipher = p.Protect("secret");
        cipher[^1] ^= 0xFF;
        Assert.Throws<SecretUndecryptableException>(() => p.Unprotect(cipher));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter DpapiProtectorTests` 与 `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter PermissionAuditTests`
Expected: 编译失败。

- [ ] **Step 3: 实现端口与基础设施**

`PermissionCatalog.cs`：

```csharp
namespace TraceFlow.Application.Security;

public static class PermissionCatalog
{
    public const string TraceRead = "trace:read";
    public const string MasterMatch = "master:match";
    public const string MasterClassify = "master:classify";
    public const string InboundAllocate = "inbound:allocate";
    public const string JobRematch = "job:rematch";
    public const string UploadQuery = "upload:query";
    public const string UploadRetry = "upload:retry";
    public const string ReturnConfirm = "return:confirm";
    public const string DataExport = "data:export";
    public const string AuditRead = "audit:read";
    public const string AuditExport = "audit:export";
    public const string SystemManage = "system:manage";
    public const string CodeRelease = "code:release"; // 设计 §4.2：占用释放独立权限

    public static readonly IReadOnlyList<string> All = new[]
    {
        TraceRead, MasterMatch, MasterClassify, InboundAllocate, JobRematch, UploadQuery,
        UploadRetry, ReturnConfirm, DataExport, AuditRead, AuditExport, SystemManage, CodeRelease
    };
}
```

`IPermissionService.cs` / `IAuditLog.cs` / `ISecretProtector.cs`：

```csharp
// IPermissionService.cs
using Microsoft.Data.SqlClient;
namespace TraceFlow.Application.Abstractions;
public interface IPermissionService
{
    Task<bool> HasAsync(long userId, string permission);
    Task<IReadOnlySet<string>> ForUserAsync(long userId);
    Task GrantAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy);
    Task RevokeAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy);
}
```

```csharp
// IAuditLog.cs
using Microsoft.Data.SqlClient;
namespace TraceFlow.Application.Abstractions;
public sealed record AuditEntry(string Actor, string ActorKind, string Action, string ObjectType,
    string? ObjectId, string? Reason, string? BeforeDigest, string? AfterDigest,
    string? RequestId, string? RuleVersion, string Env);
public interface IAuditLog
{
    Task AppendAsync(SqlConnection conn, SqlTransaction? tx, AuditEntry entry);
}
```

```csharp
// ISecretProtector.cs
namespace TraceFlow.Application.Abstractions;
public sealed class SecretUndecryptableException : Exception
{
    public SecretUndecryptableException(string message, Exception inner) : base(message, inner) { }
}
public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] cipher); // 失败抛 SecretUndecryptableException：停调用+告警，无默认凭据
}
```

`PermissionService.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class PermissionService : IPermissionService
{
    private readonly string _conn;
    public PermissionService(string connectionString) => _conn = connectionString;

    public async Task<bool> HasAsync(long userId, string permission)
    {
        await using var c = new SqlConnection(_conn);
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.user_permission WHERE user_id=@userId AND permission=@permission",
            new { userId, permission }) > 0;
    }

    public async Task<IReadOnlySet<string>> ForUserAsync(long userId)
    {
        await using var c = new SqlConnection(_conn);
        var rows = await c.QueryAsync<string>(
            "SELECT permission FROM dbo.user_permission WHERE user_id=@userId", new { userId });
        return rows.ToHashSet();
    }

    public async Task GrantAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy)
        => await conn.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.user_permission WHERE user_id=@userId AND permission=@permission)
INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@userId,@permission,@grantedBy)",
            new { userId, permission, grantedBy }, tx);

    public async Task RevokeAsync(SqlConnection conn, SqlTransaction tx, long userId, string permission, string grantedBy)
        => await conn.ExecuteAsync(
            "DELETE FROM dbo.user_permission WHERE user_id=@userId AND permission=@permission",
            new { userId, permission }, tx);
}
```

`AuditLog.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class AuditLog : IAuditLog
{
    public async Task AppendAsync(SqlConnection conn, SqlTransaction? tx, AuditEntry e)
        => await conn.ExecuteAsync(@"
INSERT INTO dbo.audit_log(actor,actor_kind,action,object_type,object_id,reason,before_digest,after_digest,request_id,rule_version,env)
VALUES(@Actor,@ActorKind,@Action,@ObjectType,@ObjectId,@Reason,@BeforeDigest,@AfterDigest,@RequestId,@RuleVersion,@Env)", e, tx);
}
```

`DpapiProtector.cs`（作用域 = CurrentUser，绑定服务运行账号；换机/恢复→重新录入向导，ADR-0001 记录）：

```csharp
using System.Security.Cryptography;
using System.Text;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TraceFlow.v1");

    public byte[] Protect(string plaintext)
        => ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] cipher)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException ex)
        {
            throw new SecretUndecryptableException(
                "凭据密文不可解密（换机/恢复/服务账号变更？）。停止对应调用并告警；走重新录入向导，禁止默认凭据兜底。", ex);
        }
    }
}
```

- [ ] **Step 4: 实现 Host 侧鉴权组件与端点**

`RequirePermissionAttribute.cs` + `PermissionHandler.cs`：

```csharp
// RequirePermissionAttribute.cs
using Microsoft.AspNetCore.Authorization;
namespace TraceFlow.Host.Security;
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission) => Policy = "perm:" + permission;
}
```

```csharp
// PermissionHandler.cs
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Security;

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionService _perms;
    public PermissionHandler(IPermissionService perms) => _perms = perms;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (long.TryParse(idValue, out var userId) && await _perms.HasAsync(userId, requirement.Permission))
            context.Succeed(requirement); // 逐请求查库；隐藏按钮不能代替鉴权（AUTH-01）
    }
}
```

`AuthEndpoints.cs`（Cookie + Antiforgery；登录/退出/失败留痕 AUTH-02）：

```csharp
using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app, string connectionString)
    {
        var hasher = new PasswordHasher<string>();

        app.MapGet("/api/v1/auth/session", async (HttpContext ctx, IAntiforgery af, IPermissionService perms) =>
        {
            var tokens = af.GetAndStoreTokens(ctx);
            if (ctx.User.Identity?.IsAuthenticated != true)
                return Results.Ok(new { data = new { authenticated = false, csrfToken = tokens.RequestToken }, requestId = ctx.TraceIdentifier });
            var userId = long.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Results.Ok(new { data = new { authenticated = true, username = ctx.User.Identity!.Name,
                permissions = await perms.ForUserAsync(userId), csrfToken = tokens.RequestToken }, requestId = ctx.TraceIdentifier });
        });

        app.MapPost("/api/v1/auth/login", async (HttpContext ctx, LoginRequest req, IAntiforgery af, IAuditLog audit) =>
        {
            await af.ValidateRequestAsync(ctx);
            await using var conn = new SqlConnection(connectionString);
            var user = await conn.QuerySingleOrDefaultAsync<(long Id, string Hash, bool Active)?>(
                "SELECT id AS Id, password_hash AS Hash, is_active AS Active FROM dbo.app_user WHERE username=@u", new { u = req.Username });
            if (user is null || !user.Value.Active ||
                hasher.VerifyHashedPassword(req.Username, user.Value.Hash, req.Password) == PasswordVerificationResult.Failed)
            {
                await audit.AppendAsync(conn, null, new AuditEntry(req.Username, "User", "auth.login.failed", "app_user", null, "登录失败", null, null, ctx.TraceIdentifier, null, "LIVE"));
                return Results.Json(new { code = "AUTH_FAILED", message = "用户名或密码错误", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 401);
            }
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Value.Id.ToString()), new Claim(ClaimTypes.Name, req.Username) };
            await ctx.SignInAsync("tf", new ClaimsPrincipal(new ClaimsIdentity(claims, "tf")));
            await audit.AppendAsync(conn, null, new AuditEntry(req.Username, "User", "auth.login", "app_user", user.Value.Id.ToString(), null, null, null, ctx.TraceIdentifier, null, "LIVE"));
            return Results.Ok(new { data = new { username = req.Username }, requestId = ctx.TraceIdentifier });
        });

        app.MapPost("/api/v1/auth/logout", async (HttpContext ctx, IAntiforgery af, IAuditLog audit) =>
        {
            await af.ValidateRequestAsync(ctx);
            var name = ctx.User.Identity?.Name ?? "?";
            await ctx.SignOutAsync("tf");
            await using var conn = new SqlConnection(connectionString);
            await audit.AppendAsync(conn, null, new AuditEntry(name, "User", "auth.logout", "app_user", null, null, null, null, ctx.TraceIdentifier, null, "LIVE"));
            return Results.Ok(new { data = new { }, requestId = ctx.TraceIdentifier });
        });
    }

    public sealed record LoginRequest(string Username, string Password);
}
```

`AdminEndpoints.cs`（用户创建/授权，`system:manage`；Task 12 会包上幂等执行器）：

```csharp
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app, string connectionString)
    {
        app.MapPost("/api/v1/admin/users", async (HttpContext ctx, CreateUserRequest req, IAuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 10)
                return Results.Json(new { code = "VALIDATION", message = "用户名必填且密码至少 10 位", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
            var hasher = new PasswordHasher<string>();
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            long id;
            try
            {
                id = await conn.ExecuteScalarAsync<long>(
                    "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(@u,@d,@h)",
                    new { u = req.Username, d = req.DisplayName ?? req.Username, h = hasher.HashPassword(req.Username, req.Password) }, tx);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await tx.RollbackAsync();
                return Results.Json(new { code = "CONFLICT", message = "用户名已存在", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 409);
            }
            await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User", "admin.user.create",
                "app_user", id.ToString(), req.Reason, null, $"{{\"username\":\"{req.Username}\"}}", ctx.TraceIdentifier, null, "LIVE"));
            await tx.CommitAsync();
            return Results.Ok(new { data = new { id }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));

        app.MapPost("/api/v1/admin/users/{id:long}/permissions", async (HttpContext ctx, long id, GrantRequest req, IPermissionService perms, IAuditLog audit) =>
        {
            if (!PermissionCatalog.All.Contains(req.Permission))
                return Results.Json(new { code = "VALIDATION", message = "未知权限项", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            if (req.Grant) await perms.GrantAsync(conn, tx, id, req.Permission, ctx.User.Identity!.Name!);
            else await perms.RevokeAsync(conn, tx, id, req.Permission, ctx.User.Identity!.Name!);
            await audit.AppendAsync(conn, tx, new AuditEntry(ctx.User.Identity!.Name!, "User",
                req.Grant ? "admin.permission.grant" : "admin.permission.revoke",
                "app_user", id.ToString(), req.Reason, null, $"{{\"permission\":\"{req.Permission}\"}}", ctx.TraceIdentifier, null, "LIVE"));
            await tx.CommitAsync();
            return Results.Ok(new { data = new { }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.SystemManage));
    }

    public sealed record CreateUserRequest(string Username, string Password, string? DisplayName, string? Reason);
    public sealed record GrantRequest(string Permission, bool Grant, string? Reason);
}
```

`AdminInitializer.cs`（`--init-admin <username>`，控制台双次输入密码，无默认密码 AUTH-02；部署文档引用）：

```csharp
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;

namespace TraceFlow.Host.Security;

public static class AdminInitializer
{
    public static async Task<int> RunAsync(string[] args, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var i = Array.IndexOf(args, "--init-admin");
        var username = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        if (username.Length == 0) { Console.Error.WriteLine("用法: --init-admin <username>"); return 2; }
        Console.Write("密码（≥10位）: "); var p1 = ReadHidden();
        Console.Write("再次输入: "); var p2 = ReadHidden();
        if (p1 != p2 || p1.Length < 10) { Console.Error.WriteLine("两次不一致或长度不足"); return 2; }
        var conn = config.GetConnectionString("TraceFlow")!;
        var hasher = new PasswordHasher<string>();
        await using var c = new SqlConnection(conn);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(@u,@u,@h)",
            new { u = username, h = hasher.HashPassword(username, p1) }, tx);
        foreach (var perm in new[] { PermissionCatalog.SystemManage, PermissionCatalog.TraceRead, PermissionCatalog.AuditRead })
            await c.ExecuteAsync("INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'init')", new { id, p = perm }, tx);
        await c.ExecuteAsync(@"INSERT INTO dbo.audit_log(actor,actor_kind,action,object_type,object_id,reason)
                               VALUES(N'init',N'System',N'admin.init',N'app_user',@id,N'初始化管理员')", new { id = id.ToString() }, tx);
        await tx.CommitAsync();
        Console.WriteLine($"管理员 {username} 已创建（仅 system:manage/trace:read/audit:read，其余权限按需分配）");
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
```

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test traceflow/tests/TraceFlow.UnitTests --filter DpapiProtectorTests` 与 `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter PermissionAuditTests`
Expected: 4 PASS。

- [ ] **Step 6: Commit**

```bash
git add traceflow/src/TraceFlow.Application/Security traceflow/src/TraceFlow.Application/Abstractions traceflow/src/TraceFlow.Infrastructure/Security traceflow/src/TraceFlow.Host/Security traceflow/src/TraceFlow.Host/Endpoints traceflow/tests/TraceFlow.IntegrationTests/Security traceflow/tests/TraceFlow.UnitTests/Security
git commit -m "feat(M0): per-request permissions, immutable audit, DPAPI secrets, no-default-password admin init (AUTH-01/02/03)"
```

---

### Task 12: 幂等执行器（API-02，AT-066）

**Files:**
- Create: `traceflow/src/TraceFlow.Application/Abstractions/IIdempotencyStore.cs`
- Create: `traceflow/src/TraceFlow.Infrastructure/Security/IdempotencyStore.cs`
- Create: `traceflow/src/TraceFlow.Host/Security/IdempotencyExecutor.cs`
- Modify: `traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs`（POST /admin/users 包上幂等执行器）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Security/IdempotencyStoreTests.cs`

**Interfaces:**
- Produces: `IIdempotencyStore.TryGetAsync(conn, tx, key, requestHash, userId)` → `Task<IdempotencyHit?>`（同键不同 hash 抛 `IdempotencyConflictException`）；`SaveAsync(conn, tx, key, requestHash, userId, status, body)`；`record IdempotencyHit(int StatusCode, string Body)`。**幂等记录与业务提交同事务**；`IdempotencyExecutor.RunAsync(HttpContext, connectionString, Func<SqlConnection, SqlTransaction, CancellationToken, Task<(int Status, string Body)>> action)`：要求 `Idempotency-Key` 头（缺失 422），hash = SHA256(method + path + 规范化 body)。

- [ ] **Step 1: 写失败测试**

```csharp
using Dapper;
using TraceFlow.Infrastructure.Security;
using Xunit;

namespace TraceFlow.IntegrationTests.Security;

public class IdempotencyStoreTests : IClassFixture<Sql2008Fixture>
{
    private readonly Sql2008Fixture _fx;
    public IdempotencyStoreTests(Sql2008Fixture fx) => _fx = fx;

    [Fact]
    public async Task Same_key_same_hash_replays_same_key_different_hash_conflicts()
    {
        var store = new IdempotencyStore();
        await using var c = await _fx.OpenAsync();
        var key = "k-" + Guid.NewGuid();
        var hash = new string('a', 64);
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            Assert.Null(await store.TryGetAsync(c, tx, key, hash, 1, default));
            await store.SaveAsync(c, tx, key, hash, 1, 200, "{\"ok\":true}", default);
            await tx.CommitAsync();
        }
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            var hit = await store.TryGetAsync(c, tx, key, hash, 1, default);
            Assert.NotNull(hit);
            Assert.Equal(200, hit!.StatusCode);
            await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
                store.TryGetAsync(c, tx, key, new string('b', 64), 1, default));
            await tx.CommitAsync();
        }
    }

    [Fact]
    public async Task Rollback_removes_idempotency_record_with_business()
    {
        var store = new IdempotencyStore();
        await using var c = await _fx.OpenAsync();
        var key = "k-" + Guid.NewGuid();
        await using (var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync())
        {
            await store.SaveAsync(c, tx, key, new string('a', 64), 1, 200, "{}", default);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.idempotency_record WHERE idem_key=@key", new { key }));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter IdempotencyStoreTests`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`IIdempotencyStore.cs`：

```csharp
using Microsoft.Data.SqlClient;
namespace TraceFlow.Application.Abstractions;
public sealed record IdempotencyHit(int StatusCode, string Body);
public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException() : base("同幂等键不同请求内容") { }
}
public interface IIdempotencyStore
{
    Task<IdempotencyHit?> TryGetAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, CancellationToken ct);
    Task SaveAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, int status, string body, CancellationToken ct);
}
```

`IdempotencyStore.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Infrastructure.Security;

public sealed class IdempotencyStore : IIdempotencyStore
{
    public async Task<IdempotencyHit?> TryGetAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(string Hash, int Status, string Body)?>(
            "SELECT request_hash AS Hash, response_status AS Status, response_body AS Body FROM dbo.idempotency_record WHERE idem_key=@key",
            new { key }, tx);
        if (row is null) return null;
        if (!string.Equals(row.Value.Hash.Trim(), requestHash, StringComparison.OrdinalIgnoreCase))
            throw new IdempotencyConflictException();
        return new IdempotencyHit(row.Value.Status, row.Value.Body);
    }

    public async Task SaveAsync(SqlConnection conn, SqlTransaction tx, string key, string requestHash, long userId, int status, string body, CancellationToken ct)
        => await conn.ExecuteAsync(
            "INSERT INTO dbo.idempotency_record(idem_key,request_hash,user_id,response_status,response_body) VALUES(@key,@requestHash,@userId,@status,@body)",
            new { key, requestHash, userId, status, body }, tx);
}
```

`IdempotencyExecutor.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using TraceFlow.Infrastructure.Security;

namespace TraceFlow.Host.Security;

public static class IdempotencyExecutor
{
    public static async Task<IResult> RunAsync(HttpContext ctx, string connectionString,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<(int Status, string Body)>> action)
    {
        var key = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is 0 or > 128)
            return Results.Json(new { code = "VALIDATION", message = "缺少或超长 Idempotency-Key", retryable = false, requestId = ctx.TraceIdentifier }, statusCode: 422);
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Method + "|" + ctx.Request.Path + "|" + body)));
        var userId = long.Parse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        var store = new IdempotencyStore();
        var hit = await store.TryGetAsync(conn, tx, key, hash, userId, ctx.RequestAborted);
        if (hit is not null)
        {
            await tx.CommitAsync();
            return Results.Content(hit.Body, "application/json", statusCode: hit.StatusCode);
        }
        var (status, resultBody) = await action(conn, tx, ctx.RequestAborted);
        await store.SaveAsync(conn, tx, key, hash, userId, status, resultBody, ctx.RequestAborted);
        await tx.CommitAsync(); // 幂等记录与业务同事务（API-02）
        return Results.Content(resultBody, "application/json", statusCode: status);
    }
}
```

修改 `AdminEndpoints.cs` 的 POST /admin/users：把原 handler 主体改为经 `IdempotencyExecutor.RunAsync(ctx, connectionString, async (conn, tx, ct) => { ...原业务逻辑，返回 (status, JsonSerializer.Serialize(response))... })`；原 `Results.Json(...)` 分支改为返回对应 `(int, string)` 元组。冲突（用户名重复）在事务内抛 `SqlException 2601/2627` 时返回 `(409, ...)` 并 `tx.RollbackAsync()` 后由执行器外层捕获——实现约定：业务函数内部自行回滚并返回错误元组时，执行器检测到 `tx.Connection is null` 则跳过 Save/Commit 直接返回。

- [ ] **Step 4: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter IdempotencyStoreTests`
Expected: 2 PASS。API 级 AT-066 在 Task 13 的端到端测试覆盖。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Application/Abstractions/IIdempotencyStore.cs traceflow/src/TraceFlow.Infrastructure/Security/IdempotencyStore.cs traceflow/src/TraceFlow.Host/Security/IdempotencyExecutor.cs traceflow/src/TraceFlow.Host/Endpoints/AdminEndpoints.cs traceflow/tests/TraceFlow.IntegrationTests/Security/IdempotencyStoreTests.cs
git commit -m "feat(M0): transactional idempotency executor (API-02, AT-066)"
```

---

### Task 13: 宿主（Windows 服务 + Web + Worker）与 API 门禁测试

**Files:**
- Modify: `traceflow/src/TraceFlow.Host/Program.cs`（整体替换）
- Create: `traceflow/src/TraceFlow.Host/WorkerHostedService.cs`、`Security/ApiErrors.cs`、`Endpoints/DashboardEndpoints.cs`、`Endpoints/HealthEndpoints.cs`
- Create: `traceflow/src/TraceFlow.Host/appsettings.json`（替换模板）
- Create: `traceflow/src/TraceFlow.Application/Abstractions/IJobHandler.cs`
- Modify: `traceflow/tests/TraceFlow.IntegrationTests/TraceFlow.IntegrationTests.csproj`（加 Mvc.Testing + Host 引用）
- Test: `traceflow/tests/TraceFlow.IntegrationTests/Api/ApiGateTests.cs`

**Interfaces:**
- Consumes: Task 9–12 全部组件。
- Produces: `IJobHandler { string Kind; Task HandleAsync(ClaimedJob job, CancellationToken ct); }`（M1+ 各管道实现此接口注册）；`WorkerHostedService`（30s 周期：通用租约回收 + 发送恢复；2s 认领循环只认领已注册 Kind）；`public partial class Program`（供 WebApplicationFactory）；配置键 `ConnectionStrings:TraceFlow`、`TraceFlow:Env`（默认 `Mock`——安全默认，Live 需门禁后显式开启）、`TraceFlow:WorkerOnly/WebOnly` 由命令行 `--worker-only/--web-only` 控制。错误契约 API-01：`{code,message,fieldErrors,retryable,requestId}`；异常处理器不得输出连接串/堆栈。

- [ ] **Step 1: 写失败测试（AT-065/066 + 匿名拒绝 + CSRF）**

csproj 追加：

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.11" />
  <ProjectReference Include="..\..\src\TraceFlow.Host\TraceFlow.Host.csproj" />
</ItemGroup>
```

```csharp
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TraceFlow.IntegrationTests.Api;

public class ApiGateTests : IClassFixture<Sql2008Fixture>, IAsyncLifetime
{
    private readonly Sql2008Fixture _fx;
    private WebApplicationFactory<Program> _factory = null!;
    public ApiGateTests(Sql2008Fixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TraceFlow"] = _fx.ConnectionString,
                ["TraceFlow:Env"] = "Mock",
            })));
        // 种一个只有 trace:read 的用户（密码 Test-Password-123）
        await using var c = await _fx.OpenAsync();
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(N'viewer',N'只读',@h)",
            new { h = hasher.HashPassword("viewer", "Test-Password-123") });
        await c.ExecuteAsync("INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,N'trace:read',N'test')", new { id });
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<(HttpClient client, string csrf)> LoginAsync(string user, string pass)
    {
        var client = _factory.CreateClient();
        var session = await client.GetFromJsonAsync<SessionResp>("/api/v1/auth/session");
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = user, password = pass });
        resp.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.Data.CsrfToken);
        return (client, session.Data.CsrfToken);
    }

    private sealed record SessionResp(SessionData Data);
    private sealed record SessionData(bool Authenticated, string CsrfToken);

    [Fact]
    public async Task Anonymous_mutation_rejected_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/users", new { username = "x", password = "1234567890" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task No_permission_confirm_returns_403_and_db_unchanged() // AT-065
    {
        var (client, _) = await LoginAsync("viewer", "Test-Password-123");
        var resp = await client.PostAsJsonAsync("/api/v1/admin/users",
            new { username = "hacker", password = "1234567890", reason = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.app_user WHERE username=N'hacker'"));
    }

    [Fact]
    public async Task Missing_csrf_rejected()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/api/v1/auth/session"); // 取 cookie 但不带 X-CSRF-TOKEN
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "viewer", password = "Test-Password-123" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dashboard_requires_trace_read_and_returns_real_counts()
    {
        var (client, _) = await LoginAsync("viewer", "Test-Password-123");
        var resp = await client.GetAsync("/api/v1/dashboard");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("openIngestExceptions", body);
        Assert.Contains("Mock", body); // 环境标记持续可见
    }
}
```

AT-066 的 API 级用例（需要 system:manage 用户）：在 `InitializeAsync` 再种一个 `admin` 用户（密码 `Admin-Password-123`，授予 `system:manage`），新增测试：

```csharp
    [Fact]
    public async Task Idempotent_user_create_same_key_same_body_replays_different_body_409() // AT-066
    {
        var (client, _) = await LoginAsync("admin", "Admin-Password-123");
        var key = Guid.NewGuid().ToString();
        var body = new { username = "idem-user-" + key[..8], password = "1234567890", reason = "t" };
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users") { Content = JsonContent.Create(body) };
        req1.Headers.Add("Idempotency-Key", key);
        var r1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users") { Content = JsonContent.Create(body) };
        req2.Headers.Add("Idempotency-Key", key);
        var r2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(await r1.Content.ReadAsStringAsync(), await r2.Content.ReadAsStringAsync());
        var req3 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/users")
        { Content = JsonContent.Create(new { username = "other-" + key[..8], password = "1234567890", reason = "t" }) };
        req3.Headers.Add("Idempotency-Key", key);
        var r3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.Conflict, r3.StatusCode);
        await using var c = await _fx.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.app_user WHERE username=@u", new { u = body.username }));
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter ApiGateTests`
Expected: FAIL（Program 未装配）。

- [ ] **Step 3: 实现 Program.cs**

```csharp
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using TraceFlow.Application.Abstractions;
using TraceFlow.Host.Endpoints;
using TraceFlow.Host.Security;
using TraceFlow.Infrastructure.Jobs;
using TraceFlow.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

if (args.Contains("--init-admin"))
    return await AdminInitializer.RunAsync(args, builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("TraceFlow")
    ?? throw new InvalidOperationException("缺少 ConnectionStrings:TraceFlow（部署配置提供，不入 Git）");
var env = builder.Configuration["TraceFlow:Env"] ?? "Mock"; // 安全默认 Mock；Live 须门禁后显式开启
var workerOnly = args.Contains("--worker-only");
var webOnly = args.Contains("--web-only");

builder.Services.AddSingleton(new DbOptions(connectionString));
builder.Services.AddSingleton(new EnvOptions(env));
builder.Services.AddSingleton<IJobQueue>(new JobQueue(connectionString));
builder.Services.AddSingleton<ISendRecoveryService>(sp => new SendRecoveryService(connectionString, sp.GetRequiredService<IJobQueue>()));
builder.Services.AddSingleton<IPermissionService>(new PermissionService(connectionString));
builder.Services.AddSingleton<IAuditLog, AuditLog>();
builder.Services.AddSingleton<ISecretProtector, DpapiProtector>();
builder.Services.AddSingleton<IIdempotencyStore, IdempotencyStore>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

if (!workerOnly)
{
    builder.Services.AddAuthentication("tf").AddCookie("tf", o =>
    {
        o.Cookie.Name = ".tf.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // G-DEPLOY 上线时强制 HTTPS（Always）
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
    builder.Services.AddAuthorization(o =>
    {
        foreach (var p in TraceFlow.Application.Security.PermissionCatalog.All)
            o.AddPolicy("perm:" + p, pb => pb.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(p)));
    });
    builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
}
if (!webOnly)
    builder.Services.AddHostedService<WorkerHostedService>();

var app = builder.Build();
app.UseExceptionHandler(errApp => errApp.Run(ApiErrors.HandleAsync));

if (!workerOnly)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    // 全局 CSRF 防护（AUTH-02）：已认证请求的所有 /api 变更方法必须带有效 antiforgery 令牌；
    // 匿名请求先由端点鉴权返回 401；登录/退出端点内部自行 ValidateRequestAsync（防登录 CSRF）。
    app.Use(async (ctx, next) =>
    {
        var m = ctx.Request.Method;
        if (ctx.User.Identity?.IsAuthenticated == true &&
            ctx.Request.Path.StartsWithSegments("/api") && (m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH"))
            await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
        await next();
    });
    app.MapAuthEndpoints(connectionString);
    app.MapAdminEndpoints(connectionString);
    app.MapDashboardEndpoints(connectionString, env);
    app.MapHealthEndpoints(connectionString, env);
    app.MapFallbackToFile("index.html");
}
app.Run();
return 0;

public partial class Program { }

public sealed record DbOptions(string ConnectionString);
public sealed record EnvOptions(string Env);
```

`WorkerHostedService.cs`：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host;

public sealed class WorkerHostedService : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly ISendRecoveryService _recovery;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly string _env;
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private DateTime _lastRecovery = DateTime.MinValue;

    public WorkerHostedService(IJobQueue queue, ISendRecoveryService recovery, IEnumerable<IJobHandler> handlers, EnvOptions envOptions)
    {
        _queue = queue; _recovery = recovery; _env = envOptions.Env;
        _handlers = handlers.ToDictionary(h => h.Kind);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _lastRecovery > TimeSpan.FromSeconds(30))
                {
                    await _queue.RecoverExpiredLeasesAsync(stoppingToken);
                    await _recovery.RecoverAsync(stoppingToken); // 失租发送→受理未知→只回查（JOB-02）
                    _lastRecovery = DateTime.UtcNow;
                }
                if (_handlers.Count > 0)
                {
                    var job = await _queue.ClaimAsync(_workerId, _env, _handlers.Keys.ToList(), TimeSpan.FromMinutes(5), stoppingToken);
                    if (job is not null && _handlers.TryGetValue(job.JobKind, out var handler))
                    {
                        try
                        {
                            await handler.HandleAsync(job, stoppingToken);
                            await _queue.CompleteAsync(job.Id, _workerId, job.FencingToken, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            // 有限退避（JOB-03/04 参数化在 M2/M3 细化）；不 tight loop
                            var delay = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(job.AttemptCount, 5))));
                            await _queue.RescheduleAsync(job.Id, _workerId, job.FencingToken, DateTime.UtcNow + delay, ex.Message, stoppingToken);
                        }
                        continue; // 立即尝试下一个
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 数据库瞬断：保留任务，下轮重试（JOB-05） */ }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
```

`IJobHandler.cs`：

```csharp
namespace TraceFlow.Application.Abstractions;
public interface IJobHandler
{
    string Kind { get; }
    Task HandleAsync(ClaimedJob job, CancellationToken ct);
}
```

`ApiErrors.cs`（API-01 错误契约；不泄露机密）：

```csharp
using TraceFlow.Application.Abstractions;

namespace TraceFlow.Host.Security;

public static class ApiErrors
{
    public static async Task HandleAsync(HttpContext ctx)
    {
        var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        var (status, code, retryable) = ex switch
        {
            IdempotencyConflictException => (409, "IDEMPOTENCY_CONFLICT", false),
            SecretUndecryptableException => (503, "SECRET_UNAVAILABLE", false),
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => (400, "CSRF_INVALID", false),
            BadHttpRequestException => (400, "BAD_REQUEST", false),
            _ => (500, "INTERNAL", true),
        };
        ctx.Response.StatusCode = status;
        // 日志记录完整异常（服务端），响应只给安全信息；禁连接串/堆栈（NFR-05）
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiErrors")
            .LogError(ex, "request failed {RequestId}", ctx.TraceIdentifier);
        await ctx.Response.WriteAsJsonAsync(new { code, message = ex switch
        {
            IdempotencyConflictException or SecretUndecryptableException => ex.Message,
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException => "CSRF 令牌缺失或无效",
            _ => "服务内部错误",
        }, fieldErrors = Array.Empty<object>(), retryable, requestId = ctx.TraceIdentifier });
    }
}
```

`DashboardEndpoints.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;
using TraceFlow.Host.Security;

namespace TraceFlow.Host.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/dashboard", async (HttpContext ctx) =>
        {
            await using var conn = new SqlConnection(connectionString);
            var openExceptions = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ingest_exception WHERE status=N'OPEN'");
            var pendingJobs = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.outbox_job WHERE job_state=N'PENDING' AND env=@env", new { env });
            var taskStates = (await conn.QueryAsync<(string State, int N)>(
                "SELECT state AS State, COUNT(*) AS N FROM dbo.upload_task WHERE env=@env GROUP BY state", new { env }))
                .ToDictionary(x => x.State, x => x.N);
            var lastAudit = await conn.ExecuteScalarAsync<DateTime?>("SELECT MAX(occurred_at) FROM dbo.audit_log");
            return Results.Ok(new { data = new { env, openIngestExceptions = openExceptions, pendingJobs,
                uploadTasksByState = taskStates, lastActivityUtc = lastAudit }, requestId = ctx.TraceIdentifier });
        }).WithMetadata(new RequirePermissionAttribute(PermissionCatalog.TraceRead));
    }
}
```

`HealthEndpoints.cs`：

```csharp
using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Host.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app, string connectionString, string env)
    {
        app.MapGet("/api/v1/health", async (HttpContext ctx) =>
        {
            string db;
            try
            {
                await using var conn = new SqlConnection(connectionString);
                db = await conn.ExecuteScalarAsync<string>("SELECT N'OK'");
            }
            catch { db = "UNAVAILABLE"; } // 不回显连接细节
            return Results.Ok(new { data = new { env, db, serverTimeUtc = DateTime.UtcNow }, requestId = ctx.TraceIdentifier });
        });
    }
}
```

`appsettings.json`：

```json
{
  "ConnectionStrings": { "TraceFlow": "" },
  "TraceFlow": { "Env": "Mock" },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "localhost"
}
```

真实连接串经 `appsettings.Local.json`（gitignore）或环境变量 `ConnectionStrings__TraceFlow` 提供。

- [ ] **Step 4: 运行确认通过**

Run: `TRACEFLOW_TEST_DB=... dotnet test traceflow/tests/TraceFlow.IntegrationTests --filter ApiGateTests`
Expected: 5 PASS（401/403+库不变/CSRF/dashboard/幂等 AT-066）。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Host traceflow/src/TraceFlow.Application/Abstractions/IJobHandler.cs traceflow/tests/TraceFlow.IntegrationTests
git commit -m "feat(M0): Windows-service host with web/worker modes, API error contract, gate tests (AT-065/066)"
```

---

### Task 14: React 前端壳（登录/布局/环境标记/真实工作台）

**Files:**
- Create: `traceflow/src/TraceFlow.Web/package.json`、`vite.config.ts`、`tsconfig.json`、`index.html`
- Create: `traceflow/src/TraceFlow.Web/src/main.tsx`、`src/api.ts`、`src/App.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/components/AppShell.tsx`、`src/components/EnvBadge.tsx`
- Create: `traceflow/src/TraceFlow.Web/src/pages/LoginPage.tsx`、`src/pages/DashboardPage.tsx`

**Interfaces:**
- Consumes: `/api/v1/auth/session|login|logout`、`/api/v1/dashboard`、`/api/v1/health`（Task 13）。
- Produces: 构建产物进 `TraceFlow.Host/wwwroot`；`api<T>(path, init)` 封装（credentials include、CSRF 头、API-01 错误解析）；后续阶段页面沿用 `AppShell` 导航与 `api` 封装。NFR-02：独立请求并行（Promise.all）、不在组件内定义组件、无副作用派生状态、交互逻辑在事件处理器。

- [ ] **Step 1: 脚手架文件**

`package.json`：

```json
{
  "name": "traceflow-web",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "scripts": { "dev": "vite", "build": "tsc -b && vite build" },
  "dependencies": { "react": "^18.3.1", "react-dom": "^18.3.1", "react-router-dom": "^6.28.0" },
  "devDependencies": { "@types/react": "^18.3.12", "@types/react-dom": "^18.3.1", "@vitejs/plugin-react": "^4.3.4", "typescript": "^5.6.3", "vite": "^5.4.11" }
}
```

`vite.config.ts`：

```ts
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../TraceFlow.Host/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:5080' } },
});
```

`tsconfig.json`：

```json
{
  "compilerOptions": {
    "target": "ES2022", "lib": ["ES2022", "DOM", "DOM.Iterable"], "module": "ESNext",
    "moduleResolution": "bundler", "jsx": "react-jsx", "strict": true, "noEmit": true,
    "skipLibCheck": true, "isolatedModules": true
  },
  "include": ["src"]
}
```

`index.html`：

```html
<!doctype html>
<html lang="zh-CN">
  <head><meta charset="UTF-8" /><meta name="viewport" content="width=device-width, initial-scale=1.0" /><title>TraceFlow 追溯管理</title></head>
  <body><div id="root"></div><script type="module" src="/src/main.tsx"></script></body>
</html>
```

- [ ] **Step 2: api.ts 与入口**

`src/api.ts`：

```ts
export interface SessionData {
  authenticated: boolean;
  username?: string;
  permissions?: string[];
  csrfToken: string;
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryable: boolean) {
    super(message);
  }
}

let csrfToken = '';

export async function loadSession(): Promise<SessionData> {
  const resp = await fetch('/api/v1/auth/session', { credentials: 'include' });
  const json = await resp.json();
  csrfToken = json.data.csrfToken;
  return json.data as SessionData;
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers);
  if (method !== 'GET' && method !== 'HEAD') headers.set('X-CSRF-TOKEN', csrfToken);
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const resp = await fetch(path, { ...init, headers, credentials: 'include' });
  const json = await resp.json().catch(() => null);
  if (!resp.ok) {
    throw new ApiError(resp.status, json?.code ?? 'HTTP_' + resp.status, json?.message ?? '请求失败', json?.retryable ?? false);
  }
  return json.data as T;
}
```

`src/main.tsx`：

```tsx
import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import App from './App';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </React.StrictMode>,
);
```

- [ ] **Step 3: App / 登录 / 布局 / 工作台**

`src/App.tsx`：

```tsx
import { useEffect, useState } from 'react';
import { Route, Routes } from 'react-router-dom';
import { loadSession, type SessionData } from './api';
import AppShell from './components/AppShell';
import LoginPage from './pages/LoginPage';
import DashboardPage from './pages/DashboardPage';

export default function App() {
  const [session, setSession] = useState<SessionData | null>(null);

  useEffect(() => {
    loadSession().then(setSession).catch(() => setSession({ authenticated: false, csrfToken: '' }));
  }, []);

  if (session === null) return <p>加载中…</p>;
  if (!session.authenticated) return <LoginPage onLoggedIn={async () => setSession(await loadSession())} />;
  return (
    <AppShell session={session} onLoggedOut={async () => setSession(await loadSession())}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="*" element={<p>该模块在后续阶段交付（见路线图），当前未接入。</p>} />
      </Routes>
    </AppShell>
  );
}
```

`src/pages/LoginPage.tsx`：

```tsx
import { useState, type FormEvent } from 'react';
import { api, ApiError } from '../api';

export default function LoginPage({ onLoggedIn }: { onLoggedIn: () => Promise<void> }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError('');
    try {
      await api('/api/v1/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) });
      await onLoggedIn();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '网络错误');
    }
  }

  return (
    <form onSubmit={handleSubmit}>
      <h1>TraceFlow 登录</h1>
      <input value={username} onChange={(e) => setUsername(e.target.value)} placeholder="用户名" autoComplete="username" />
      <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="密码" autoComplete="current-password" />
      <button type="submit">登录</button>
      {error && <p role="alert">{error}</p>}
    </form>
  );
}
```

`src/components/EnvBadge.tsx`：

```tsx
export default function EnvBadge({ env }: { env: string }) {
  const isLive = env === 'LIVE';
  return (
    <span style={{ padding: '2px 10px', borderRadius: 4, fontWeight: 700,
      background: isLive ? '#c0392b' : '#2980b9', color: '#fff' }}>
      {isLive ? 'LIVE 正式环境' : 'MOCK 模拟环境'}
    </span>
  );
}
```

`src/components/AppShell.tsx`：

```tsx
import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { api, type SessionData } from '../api';
import EnvBadge from './EnvBadge';

const NAV = [
  { to: '/', label: '工作台', ready: true },
  { to: '/products', label: '商品映射', ready: false },
  { to: '/suppliers', label: '供应商映射', ready: false },
  { to: '/upstream', label: '上游出库单', ready: false },
  { to: '/inbounds', label: '入库关联', ready: false },
  { to: '/sales', label: 'POS 销售', ready: false },
  { to: '/returns', label: '退货', ready: false },
  { to: '/codes', label: '码台账', ready: false },
  { to: '/uploads', label: '上报对账', ready: false },
  { to: '/exceptions', label: '人工补配与异常', ready: false },
  { to: '/settings', label: '设置', ready: false },
  { to: '/audit', label: '审计', ready: false },
];

export default function AppShell({ session, onLoggedOut, children }: {
  session: SessionData; onLoggedOut: () => Promise<void>; children: ReactNode;
}) {
  const [env, setEnv] = useState('');
  useEffect(() => {
    api<{ env: string }>('/api/v1/health').then((h) => setEnv(h.env)).catch(() => setEnv('?'));
  }, []);

  async function handleLogout() {
    await api('/api/v1/auth/logout', { method: 'POST' });
    await onLoggedOut();
  }

  return (
    <div style={{ display: 'flex', minHeight: '100vh' }}>
      <nav style={{ width: 200, borderRight: '1px solid #ddd', padding: 12 }}>
        <h2>TraceFlow</h2>
        {NAV.map((item) =>
          item.ready ? (
            <p key={item.to}><Link to={item.to}>{item.label}</Link></p>
          ) : (
            <p key={item.to} style={{ color: '#999' }}>{item.label}（未接入）</p>
          ),
        )}
      </nav>
      <main style={{ flex: 1, padding: 16 }}>
        <header style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 16 }}>
          <EnvBadge env={env} />
          <span>{session.username} <button onClick={handleLogout}>退出</button></span>
        </header>
        {children}
      </main>
    </div>
  );
}
```

`src/pages/DashboardPage.tsx`：

```tsx
import { useEffect, useState } from 'react';
import { api } from '../api';

interface Dashboard {
  env: string;
  openIngestExceptions: number;
  pendingJobs: number;
  uploadTasksByState: Record<string, number>;
  lastActivityUtc: string | null;
}

export default function DashboardPage() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const d = await api<Dashboard>('/api/v1/dashboard');
        if (active) { setData(d); setError(''); }
      } catch {
        if (active) setError('加载失败，10 秒后重试');
      }
    }
    load();
    const timer = setInterval(load, 10000); // 轮询本系统，不直接调用 TOP（UI-12）
    return () => { active = false; clearInterval(timer); };
  }, []);

  if (error) return <p role="alert">{error}</p>;
  if (data === null) return <p>加载中…</p>;
  const succeeded = data.uploadTasksByState['Succeeded'] ?? 0;
  const total = Object.values(data.uploadTasksByState).reduce((a, b) => a + b, 0);
  return (
    <section>
      <h1>工作台</h1>
      <ul>
        <li>接入异常（待处理）：{data.openIngestExceptions}</li>
        <li>排队作业：{data.pendingJobs}</li>
        <li>上传任务总数：{total}；最终全部成功：{succeeded}（成功率口径=最终全部成功，部分成功/处理中/受理未知单列）</li>
      </ul>
      <table>
        <thead><tr><th>上传状态</th><th>数量</th></tr></thead>
        <tbody>
          {Object.entries(data.uploadTasksByState).map(([state, n]) => (
            <tr key={state}><td>{state}</td><td>{n}</td></tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
```

- [ ] **Step 4: 构建验证**

Run: `cd traceflow/src/TraceFlow.Web && npm install && npm run build`
Expected: tsc 0 error；产物出现在 `TraceFlow.Host/wwwroot/index.html`。再 `dotnet build traceflow/TraceFlow.sln -c Release` 确认宿主正常。

- [ ] **Step 5: Commit**

```bash
git add traceflow/src/TraceFlow.Web
git commit -m "feat(M0): React shell with login, env badge, real dashboard polling (UI-12, NFR-02)"
```

---

### Task 15: OpenAPI、适配事实矩阵、fixture、数据字典、需求追踪矩阵、M0 门禁报告

**Files:**
- Create: `docs/contracts/openapi.yaml`
- Create: `docs/contracts/adapter-facts.md`
- Create: `docs/contracts/source-mapping.md`
- Create: `docs/contracts/data-dictionary.md`
- Create: `docs/contracts/fixtures/ksoa/sale-normal.json`、`sale-missing-plh.json`、`sale-mixed-return.json`、`purchase-in.json`
- Create: `docs/contracts/fixtures/top/upload-accepted.json`、`result-type-1.json`、`partial-flag-52.json`、`error-response.json`
- Create: `docs/traceability/matrix.md`
- Create: `docs/delivery/M0-gate-report.md`

**Interfaces:**
- Produces: M0 文档门禁交付物（PRD §15.2 M0 清单）。OpenAPI 为全量资源契约（PRD §12），M1+ 端点标注 `x-phase`；fixture 全部标注验证状态，**未验证的 TOP fixture 是 Mock 形状，不声称平台事实**。

- [ ] **Step 1: 写 openapi.yaml**

覆盖 PRD §12 全部路径（`/dashboard`、`/products`、`/suppliers`、`/imports`、`/imports/{id}/commit`、`/product-mappings/{id}/confirm`、`/supplier-mappings/{id}/confirm`、`/products/{id}/trace-policy`、`/upstream-bills`、`/upstream-bills/{id}`、`/sync-jobs`、`/inbounds`、`/inbounds/{id}`、`/inbounds/{id}/rematch`、`/inbounds/{id}/allocations`、`/inbounds/{id}/confirm-allocation`、`/sales`、`/sales/{id}`、`/returns`、`/returns/{id}`、`/verifications/{id}/recheck`、`/returns/{id}/confirm`、`/trace-codes/{code}/events`、`/upload-tasks`、`/upload-tasks/{id}`、`/upload-tasks/{id}/query`、`/upload-tasks/{id}/retry`、`/reconciliation-runs`、`/jobs/{id}`、`/audit-logs`、`/settings/connections/test`、`/settings/*`）+ M0 已实现端点（auth/admin/health）。骨架如下，**每个路径必须展开为完整 operation（parameters/requestBody/responses 引用组件），不得留空**：

```yaml
openapi: 3.0.3
info:
  title: TraceFlow 内部 API
  version: 0.1.0
  description: >
    PRD §12 最低资源契约。金额/数量/外部ID/rowversion 一律 JSON 字符串（API-01）。
    变更接口要求 Idempotency-Key 头与 expectedVersion（API-02）。x-phase 标注实现阶段。
servers: [{ url: /api/v1 }]
components:
  securitySchemes:
    cookieAuth: { type: apiKey, in: cookie, name: .tf.auth }
  parameters:
    Page: { name: page, in: query, schema: { type: integer, minimum: 1, default: 1 } }
    PageSize: { name: pageSize, in: query, schema: { type: integer, minimum: 1, maximum: 200, default: 50 } }
    IdempotencyKey: { name: Idempotency-Key, in: header, required: true, schema: { type: string, maxLength: 128 } }
  schemas:
    Error:
      type: object
      required: [code, message, retryable, requestId]
      properties:
        code: { type: string }
        message: { type: string }
        fieldErrors: { type: array, items: { type: object } }
        retryable: { type: boolean }
        requestId: { type: string }
    PageMeta:
      type: object
      required: [records, total]
      properties:
        records: { type: array, items: { type: object } }
        total: { type: integer }
    ConfirmMappingRequest:
      type: object
      required: [targetId, expectedVersion, reason]
      properties:
        targetId: { type: string }
        expectedVersion: { type: string, description: rowversion 字符串 }
        reason: { type: string }
        evidenceIds: { type: array, items: { type: string } }
    TracePolicyRequest:
      type: object
      required: [classification, reason, expectedVersion]
      properties:
        classification: { type: string, enum: [REQUIRED, EXEMPT] }
        reason: { type: string }
        expectedVersion: { type: string }
    AllocationRequest:
      type: object
      required: [lines, expectedVersion, reason]
      properties:
        lines:
          type: array
          items:
            type: object
            required: [sourceLineKey, upstreamLineId, codes, quantity]
            properties:
              sourceLineKey: { type: string }
              upstreamLineId: { type: string }
              codes: { type: array, items: { type: string } }
              quantity: { type: string, description: decimal(18,6) 字符串 }
              unitRatio: { type: string, description: "分子/分母" }
              conversionEvidence: { type: string }
        expectedVersion: { type: string }
        reason: { type: string }
    ReturnConfirmRequest:
      type: object
      required: [originalEventKey, codes, expectedVersion, reason]
      properties:
        originalEventKey: { type: string }
        codes: { type: array, items: { type: string } }
        evidenceIds: { type: array, items: { type: string } }
        expectedVersion: { type: string }
        reason: { type: string }
    TaskActionRequest:
      type: object
      required: [reason]
      properties:
        reason: { type: string }
        expectedVersion: { type: string }
  responses:
    Unauthorized: { description: 未登录, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
    Forbidden: { description: 无权限, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
    Conflict: { description: 版本/占用/幂等冲突, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
    Unprocessable: { description: 业务不满足, content: { application/json: { schema: { $ref: '#/components/schemas/Error' } } } }
security: [{ cookieAuth: [] }]
paths:
  /auth/session:
    get:
      x-phase: M0
      security: []
      responses: { '200': { description: 会话与 CSRF 令牌 } }
  /auth/login:
    post:
      x-phase: M0
      security: []
      requestBody:
        required: true
        content: { application/json: { schema: { type: object, required: [username, password], properties: { username: { type: string }, password: { type: string } } } } }
      responses: { '200': { description: 登录成功 }, '401': { $ref: '#/components/responses/Unauthorized' } }
  /dashboard:
    get:
      x-phase: M0
      responses: { '200': { description: 待办计数/同步健康/深链条件 }, '401': { $ref: '#/components/responses/Unauthorized' } }
  /products:
    get:
      x-phase: M1
      parameters: [{ $ref: '#/components/parameters/Page' }, { $ref: '#/components/parameters/PageSize' },
        { name: keyword, in: query, schema: { type: string } },
        { name: classification, in: query, schema: { type: string, enum: [UNKNOWN, REQUIRED, EXEMPT] } },
        { name: mappingState, in: query, schema: { type: string } }]
      responses: { '200': { description: 分页商品, content: { application/json: { schema: { $ref: '#/components/schemas/PageMeta' } } } } }
  # …其余路径按同一模式逐一展开：
  # /suppliers(GET,M1) /imports(POST,M1,202+IdempotencyKey) /imports/{id}/commit(POST,M1,expectedVersion)
  # /product-mappings/{id}/confirm(POST,M1,ConfirmMappingRequest,409强冲突) /supplier-mappings/{id}/confirm(POST,M1)
  # /products/{id}/trace-policy(POST,M1,TracePolicyRequest) /upstream-bills(GET,M2) /upstream-bills/{id}(GET,M2)
  # /sync-jobs(POST,M1,202有界参数) /inbounds(GET,M2) /inbounds/{id}(GET,M2) /inbounds/{id}/rematch(POST,M2,202)
  # /inbounds/{id}/allocations(POST,M2,AllocationRequest,409争用) /inbounds/{id}/confirm-allocation(POST,M2,提示自动上传)
  # /sales(GET,M3) /sales/{id}(GET,M3) /returns(GET,M3) /returns/{id}(GET,M3)
  # /verifications/{id}/recheck(POST,M3,202) /returns/{id}/confirm(POST,M3,ReturnConfirmRequest)
  # /trace-codes/{code}/events(GET,M4) /upload-tasks(GET,M3) /upload-tasks/{id}(GET,M3)
  # /upload-tasks/{id}/query(POST,M3,只排回查) /upload-tasks/{id}/retry(POST,M3,后端判定未受理否则409/422)
  # /reconciliation-runs(POST,M4,202) /jobs/{id}(GET,M1) /audit-logs(GET,M1,audit:read)
  # /settings/connections/test(POST,M1,仅已保存配置ID,拒绝任意目标) /settings/{key}(PUT,M1,system:manage,敏感只写)
```

**执行要求：上方注释列出的每个路径都必须写成与 `/products` 同等完整度的 operation（含参数、请求体 schema、200/401/403/409/422 响应引用），注释只是索引不是交付内容。**

- [ ] **Step 2: 写 adapter-facts.md（交接说明格式）**

```markdown
# KSOA/TOP 适配事实矩阵（M0 初版）

状态约定：所有"核验证据"为空 = 未取证；对应真实通道禁用（G-ERP/G-TOP/G-POS/G-RETURN）。

| 事项 | 已知信息 | 核验证据/日期 | 采用规则 | 未核实时开关 |
| --- | --- | --- | --- | --- |
| 销售来源键 | salemast.lshh + salemxls.plh 候选（PRD SRC-02） | 待取证：NULL/重复/跨日复用/跨表关联查询 | 核实后固定；plh 空→接入异常 | 真实销售同步禁用 |
| 入库来源键 | jh_rkhz.djbh + jh_rkmx.dj_sn 候选 | 同上 | 核实后固定 | 真实入库发送禁用 |
| jlgg/sxrq/dw | 包装换算/效期/单位，所属对象待验证 | 待取证：单位与样例 | 明确方向与精度（UnitRatio） | 相关行阻断 |
| 主档表/分类 | 未给出（SRC-03） | 待取证：schema+脱敏样例 | 不猜表名 | UNKNOWN 阻断 |
| is_fpwc/yishj | 类型含义待验证 | 待取证 | 不作写回标记、不假设递增 | 不用于增量判断 |
| POS 采码 | salemxls.TraceCode varchar(50) 已预留；PRD SCOPE-09 称尚未采码，用户口述已扫码 | 待取证：逐盒扫码落库样例（G-POS） | 以样例为准 | 销售生产自动上报禁用 |
| TOP 合同 | 零售权限包指南已读；字段未全验证 | 待取证：附录 B 8 项 | connector 映射 | 真实发送禁用 |
| 消费者退货 | uploadretail/116（PRD） | 待取证：完整传参 | 人工首次确认 | 未验证不发送 |
| 采购退货 | uploadinoutbill/202；来源表未给出（DEF-03） | 待取证 | 显示未接入 | 首次上报禁用 |
| 负数退货 | salemxls shl<0 代表退货 | 待取证：原码与原单样例 | 人工首次确认 | 未验证类型不发送 |
```

- [ ] **Step 3: 写 source-mapping.md 与 data-dictionary.md**

`source-mapping.md`：抄录 PRD §4.2 归一化内部契约五个对象（SourceDocument/SourceLine/InboundDocument 扩展/Product/Supplier）字段表，并给出 KSOA 候选列→内部字段映射（salemast.lshh→sourceDocumentKey 候选、salemxls.plh→sourceLineKey 候选、spid→productId、shl→quantityOriginal、TraceCode→traceCodeRaw、jh_rkhz.dwbh→supplierId、pihao→batchRaw），每行标注"候选，待 G-ERP 取证"。

`data-dictionary.md`：PRD §11.2 逻辑实体→物理表映射表：

| PRD 实体 | 物理表 | 关键约束 |
| --- | --- | --- |
| Organization/SourceSystem | organization / source_system | 单 active 门店过滤唯一索引 |
| SourceSnapshot/SourceLine | source_snapshot / source_line | 键+版本唯一；历史不可改 |
| Product/Supplier | product / supplier | 门店+来源+稳定 ID 唯一 |
| PlatformDrug/PlatformEnterpriseVersion | platform_drug / platform_enterprise_version | 平台身份(+版本)唯一 |
| ActiveProductMapping/MappingHistory | product_mapping / product_mapping_history | 双向有效唯一（两个过滤索引） |
| ActiveSupplierMapping/MappingHistory | supplier_mapping / supplier_mapping_history | 同上 |
| DrugPrefixIndex | drug_prefix_index | prefix 非唯一 |
| UpstreamBill/Line/Code | upstream_bill / upstream_bill_line / upstream_bill_code | 主体组合唯一 |
| Allocation/AllocationCode | inbound_allocation / allocation_code | 有效对唯一；码占用经 code_claim |
| TraceCode/CodeRelation/CodeEvent | trace_code / code_relation / code_event | 事件+码唯一；事件非全历史唯一 |
| CodeClaim | code_claim | active 唯一（含 env 隔离） |
| Verification | verification | 当前有效摘要唯一（is_current 过滤） |
| UploadTask/UploadPart | upload_task / upload_part | 事件键唯一；平台号唯一；part index 唯一 |
| UploadAttempt/CodeResult | upload_attempt / code_result | 追加保存；三态含 UNKNOWN |
| OutboxJob/SyncCheckpoint | outbox_job / sync_checkpoint（上游分页进度另见 download_progress） | 意图幂等；检查点不领先数据 |
| ReconciliationRun/Difference | reconciliation_run / reconciliation_difference | 快照不可静默覆盖 |
| ImportBatch/ImportRow | import_batch / import_row | 导入意图幂等 |
| User/Role/Permission/AuditLog | app_user / user_permission / audit_log（角色为权限模板，见 PermissionCatalog 与 PRD §3） | 权限唯一；不可逆哈希；审计不可变 |

- [ ] **Step 4: 写 fixture（脱敏样例，非真实业务数据）**

`fixtures/ksoa/sale-normal.json`：

```json
{
  "_fixture": "KSOA 销售候选结构（未取证，G-ERP 前仅用于 Mock）",
  "salemast": [{ "lshh": "000123", "rq": "2026-11-20", "ontime": "14:32:05" }],
  "salemxls": [
    { "lshh": "000123", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456789" },
    { "lshh": "000123", "plh": "2", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456790" },
    { "lshh": "000123", "plh": "3", "spid": "SP0002", "shl": 2, "TraceCode": null }
  ]
}
```

`fixtures/ksoa/sale-missing-plh.json`：

```json
{
  "_fixture": "plh 空行：必须进接入异常，不得丢行或造行号（SRC-02）",
  "salemast": [{ "lshh": "000124", "rq": "2026-11-20", "ontime": "15:01:00" }],
  "salemxls": [{ "lshh": "000124", "plh": null, "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456791" }]
}
```

`fixtures/ksoa/sale-mixed-return.json`：

```json
{
  "_fixture": "混合正负小票：分 SALE/CONSUMER_RETURN 两事件，不净额抵消（DEF-06/RET-01）",
  "salemast": [{ "lshh": "000125", "rq": "2026-11-21", "ontime": "09:10:00" }],
  "salemxls": [
    { "lshh": "000125", "plh": "1", "spid": "SP0001", "shl": 1, "TraceCode": "81234567890123456792" },
    { "lshh": "000125", "plh": "2", "spid": "SP0001", "shl": -1, "TraceCode": "81234567890123456789" },
    { "lshh": "000125", "plh": "3", "spid": "SP0003", "shl": 0, "TraceCode": null }
  ]
}
```

`fixtures/ksoa/purchase-in.json`：

```json
{
  "_fixture": "采购入库候选结构（未取证）；jlgg 换算方向待验证",
  "jh_rkhz": [{ "djbh": "RK20261120001", "rq": "2026-11-20", "ontime": "10:00:00", "yanshr": "验收员原值", "dwbh": "DW0001" }],
  "jh_rkmx": [
    { "djbh": "RK20261120001", "dj_sn": "1", "spid": "SP0001", "pihao": "P20260101", "shl": 10 },
    { "djbh": "RK20261120001", "dj_sn": "2", "spid": "SP0002", "pihao": "P20260202", "shl": 5 }
  ]
}
```

`fixtures/top/upload-accepted.json`（**UNVERIFIED：Mock 形状，非平台合同事实**）：

```json
{
  "_verified": false,
  "_note": "G-TOP 验证后替换为真实脱敏响应；当前仅供假平台",
  "alibaba_alihealth_drugtrace_top_lsyd_uploadinoutbill_response": {
    "response_success": true,
    "msg_code": "SUCCESS",
    "sub_msg_code": null,
    "model": { "bill_code": "TF1", "task_id": "MOCK-TASK-1" },
    "request_id": "mock-req-1"
  }
}
```

`fixtures/top/result-type-1.json`：

```json
{
  "_verified": false,
  "_note": "result_type=1 上传成功≠最终业务成功（TOP-02/AT-048）",
  "alibaba_alihealth_drugtrace_top_lsyd_query_billstatus_response": {
    "response_success": true,
    "model": { "result_type": "1", "sub_process_flag": null },
    "request_id": "mock-req-2"
  }
}
```

`fixtures/top/partial-flag-52.json`：

```json
{
  "_verified": false,
  "_note": "result_type=3 + flag=52 = PARTIAL_SUCCESS（TOP-02/AT-049）",
  "alibaba_alihealth_drugtrace_top_lsyd_query_billstatus_response": {
    "response_success": true,
    "model": { "result_type": "3", "sub_process_flag": "52" },
    "request_id": "mock-req-3"
  }
}
```

`fixtures/top/error-response.json`：

```json
{
  "_verified": false,
  "_note": "HTTP 200 + response_success=false 必须判失败（TOP-01/AT-047）",
  "alibaba_alihealth_drugtrace_top_lsyd_uploadretail_response": {
    "response_success": false,
    "msg_code": "FAIL_BIZ_ERROR",
    "sub_msg_code": "MOCK_INVALID_CODE",
    "request_id": "mock-req-4"
  }
}
```

- [ ] **Step 5: 写需求追踪矩阵（docs/traceability/matrix.md）**

每行：需求 ID → 实现模块/物理对象 → 测试 → 门禁证据 → 阶段。M0 行填实际文件与测试名；后续阶段行填计划归属与"未实现"。必须逐条覆盖：SCOPE-01…10、DEF-01…07、AUTH-01…03、SRC-01…05、SYNC-01…05、MD-01…03、MATCH-01…05、ENT-01…03、UP-01…02、IN-01…09、SALE-01…05、RET-01…04、TOP-01…04、IDEM-01…05、JOB-01…05、UI-01…13、DB-01…05、API-01…03、REC-01…03、AUD-01、NFR-01…08、G-ERP/G-TOP/G-POS/G-RETURN/G-OPENING/G-DEPLOY。M0 示例行：

| 需求 ID | 实现 | 测试 | 门禁证据 | 阶段/状态 |
| --- | --- | --- | --- | --- |
| DB-03 | db/migrations/*.sql | Sql2008CompatibilityTests | 真实 2008 R2 运行记录 | M0 完成 |
| JOB-01 | Infrastructure/Jobs/JobQueue.cs | JobQueueTests（双连接并发） | 2008 R2 集成 | M0 完成 |
| JOB-02 | SendRecoveryService.cs | SendRecoveryTests | 同上 | M0 完成（上传动作恢复在 M3） |
| IDEM-02 | Domain/Billing/BillCodeGenerator.cs + upload_part.UQ_part_billcode | BillCodeGeneratorTests/UploadConstraintTests | — | M0 完成 |
| AUTH-01 | PermissionHandler + RequirePermission | ApiGateTests.No_permission… | API 集成 | M0 完成 |
| API-02 | IdempotencyExecutor/Store | IdempotencyStoreTests/ApiGateTests.Idempotent… | API 集成 | M0 完成 |
| SRC-04 | TraceCodeNormalizer | TraceCodeNormalizerTests | — | M0 完成（接入在 M1） |
| SCOPE-09 | adapter-facts.md POS 采码行 | — | G-POS 待取证 | 销售生产上报禁用 |
| SALE-01…05 | （M3 计划） | AT-035…041 | G-POS | 未实现 |

（其余 ID 按同格式逐条写全，不得省略。）

- [ ] **Step 6: 写 M0 门禁报告并执行全量验证**

`docs/delivery/M0-gate-report.md` 记录：① `SELECT @@VERSION` 真实输出（必须 10.50.x）；② 全量测试命令与结果；③ 迁移 applied 列表与 checksum；④ 未满足项清单（G-ERP/G-TOP/G-POS/G-RETURN 全部未取证→对应真实通道禁用；M1–M4 功能未实现按矩阵列缺口）。

Run:

```bash
dotnet build traceflow/TraceFlow.sln -c Release
dotnet test traceflow/tests/TraceFlow.UnitTests -c Release
TRACEFLOW_TEST_DB="<真实2008R2测试实例>" dotnet test traceflow/tests/TraceFlow.IntegrationTests -c Release
cd traceflow/src/TraceFlow.Web && npm run build
```

Expected: 全部通过；任何失败不得标记 M0 完成。

- [ ] **Step 7: Commit**

```bash
git add docs/contracts docs/traceability docs/delivery
git commit -m "docs(M0): OpenAPI contract, adapter fact matrix, fixtures, data dictionary, traceability, gate report"
```

---

## Self-Review 记录（计划作者自查）

1. **Spec 覆盖**：M0 对应 PRD §15.2 M0 清单——ADR（Task 1）、完整 DDL（Task 5–8，覆盖 §11.2 全部逻辑实体，映射见数据字典）、OpenAPI（Task 15）、适配事实矩阵+fixture（Task 15）、2008 R2 兼容连接（Sql2008Fixture 强制 10.50.x）。设计 §2.6/§2.7/§2.8/§2.9 的状态机、fencing、bill_code、发送恢复在 Task 2/3/9/10 落地；T1/T2 的完整业务事务在 M1/M2（M0 提供 EnqueueAsync 事务内入队能力）；T3 回执协议在 M3。DPAPI（Task 11）、Live/Mock env 隔离列（Task 7/8）、权限审计幂等（Task 11–13）均有着落。
2. **占位符扫描**：OpenAPI Step 1 的路径索引注释已显式声明"必须展开为完整 operation"，执行者不得交付注释；其余步骤均含完整代码/SQL/命令。
3. **类型一致性**：`ClaimedJob`/`IJobQueue`/`IJobHandler`/`AuditEntry`/`IdempotencyHit` 签名在 Task 9–13 间一致；`UploadState` 枚举名与 DDL CHECK 约束字符串一致（PascalCase）；DI 用 `DbOptions`/`EnvOptions` 包装记录避免 string 歧义；CSRF 全局中间件只拦已认证请求（匿名先 401，登录端点自验防登录 CSRF），`AntiforgeryValidationException` 映射 400。Task 12 的事务回滚约定已写明。

## 缺口与边界（M0 交付报告必须原样声明）

- G-ERP/G-TOP/G-POS/G-RETURN/G-OPENING/G-DEPLOY 全部未取证：真实 ERP 同步、真实平台发送、销售自动上报、退货上报均禁用；M0 只有 Mock/fixture 能力。
- 上传发送协议（send_attempt 短事务→HTTP→T3）、限流预算、TOP 签名在 M2/M3 实现；M0 的 `upload-dispatch`/`bill-query` 作业种类只有表结构与恢复路径，无处理器。
- 连续运行 ≥7 天、备份恢复演练、性能基准属 M4；M0 不声称。