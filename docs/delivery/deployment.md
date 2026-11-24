# 部署手册（G-DEPLOY）

日期：2026-11-24。分支：`m4-closure`（M4 基线 `d0cac84`）。适用：单门店 TraceFlow 本机/店内服务器部署。
配套：`docs/delivery/backup-restore.md`（备份/恢复与密钥恢复）、`docs/delivery/M4-gate-report.md`（质量证据）、
`docs/traceability/matrix.md`（需求追踪）。

> **事实边界（必须先读）**
> 1. 本手册全部命令来自本仓库真实代码与工具；**目标部署机（OS/账号/网络/证书/服务期）未取证**（G-DEPLOY）。
> 2. 数据库只在**本机 SQL Server 2019（15.0.2000.5）**上运行验证；**SQL Server 2008 R2 未验证（AT-070）**，
>    不得据此声称 2008 R2 已兼容。
> 3. 未执行任何真实平台（TOP/KSOA）调用；`TraceFlow:Env` 默认 `MOCK`，`LIVE` 在 G-TOP 取证前一律
>    fail-closed（上传命中 `TOP_NOT_CERTIFIED`）。
> 4. 文中 `<...>` 均为占位符。示例连接串一律 `Server=localhost;Integrated Security=true;...`；
>    本手册不含任何密码、应用密钥、完整 AppKey、内网 IP 或门店名称。

## 1. 目标环境假设（全部标为未取证）

| 项 | 假设 | 状态 |
| --- | --- | --- |
| 操作系统 | Windows Server 2016+ 或 Windows 10/11（x64） | **未取证** |
| 运行时 | .NET 8 Runtime（或 self-contained 发布，无需预装运行时） | 本机以 .NET SDK 8.0.425 / 运行时 8.0.31 验证；目标机**未取证** |
| 数据库 | SQL Server 2008 R2 SP3+（业务库独立实例/独立库） | 仅 SQL Server 2019 15.0.2000.5 验证；**2008 R2 未验证（AT-070）** |
| 宿主形态 | Kestrel + Windows 服务（`Program.cs:11 builder.Host.UseWindowsService()`） | 代码已实现；服务注册**未取证** |
| 浏览器 | 店内 PC 的 Chrome/Edge 当前版本 | 未取证（前端只经 `npm run build` 与 vitest 验证，未做目标机浏览器验收） |
| 网络 | 本机或店内局域网访问；不直接暴露公网 | 未取证；HTTPS/端口/证书均未取证 |

## 2. 前置检查（部署前逐项执行并记录输出）

```powershell
$RepoRoot = '<仓库绝对路径，例如部署包解压目录>'
$PublishDir = 'C:\TraceFlow\app'
$ServiceName = 'TraceFlow'
```

1. **SQL Server 版本与排序规则**（记录真实输出；10.50.x 才是 2008 R2）：

   ```powershell
   sqlcmd -S <实例名> -E -I -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128))"
   sqlcmd -S <实例名> -E -I -h -1 -W -Q "SET NOCOUNT ON; SELECT DATABASEPROPERTYEX(N'master', 'Collation')"
   ```

   门禁口径：集成测试按 2008 R2 方言编写，但**运行时只在 2019 验证**；目标机若为 2008 R2，须先完成 AT-070 补验
   （重跑全量集成测试与性能基准）再上线。

2. **Organization 单活行**：宿主启动时 `TraceFlow.Infrastructure/Bootstrap/OrgBootstrap.EnsureAsync` 仅在
   不存在 active 组织时插入 `org_code='DEFAULT', name='默认门店'`；`source_system` 同理补 `KSOA` 行。
   单活由 `UX_organization_single_active` 过滤唯一索引保证。

   `organization.ent_id` / `ref_ent_id`（平台企业主体）**不经 API 写入**，必须由 DBA 在部署时直接更新并留记录：

   ```sql
   -- 由 DBA 执行；值来自平台取证结果，禁止写入本仓库文档
   UPDATE dbo.organization
      SET ent_id = N'<平台 ent_id>', ref_ent_id = N'<平台 ref_ent_id>'
    WHERE is_active = 1 AND org_code = N'DEFAULT';
   ```

3. **迁移工具与脚本目录**：`traceflow/db/tools/TraceFlow.DbMigrator` 读取连接串（环境变量或位置参数）与
   `--dir <脚本目录>`，按 `schema_version` 校验和幂等应用 0001–0009。**迁移工具只允许连 TraceFlow 目标库**
   （运维规则；工具本身不解析库名，生产执行人须自行确认连接串 `Initial Catalog`）。

4. **AllowedHosts**：仓库 `appsettings.json` 默认 `"AllowedHosts": "localhost"`。用主机名/IP 访问会被
   HostFiltering 以 400 拒绝；店内网访问须显式追加（示例：`localhost;<主机名>`），不要用 `*`。
   该配置属部署配置，不入 Git。

5. **监听地址**：未设置时 Kestrel 默认 `http://localhost:5000`；显式设置 `ASPNETCORE_URLS`
   （如 `http://localhost:5000` 或反代下的 `http://127.0.0.1:5000`）。生产建议只绑回环 + IIS/反代终止 TLS，
   不要直接监听 `0.0.0.0`（G-DEPLOY 未取证）。

6. **环境**：`TraceFlow:Env` 仅接受 `MOCK`/`LIVE`（大小写不敏感），缺省 `MOCK`。**首次上线保持 MOCK**，
   直到 G-TOP 取证完成并有用户书面授权。

7. **权限**：确认部署账号对 `$PublishDir` 有写权限、对 SQL 目标库有建库（或由 DBA 代建）/迁移权限。

## 3. 安装步骤（可复制命令）

### 3.1 发布产物

```powershell
cd $RepoRoot

# 前端：先构建静态资源，产物写入 traceflow/src/TraceFlow.Host/wwwroot（随后随 publish 一起输出）
Push-Location traceflow\src\TraceFlow.Web
npm ci
npm run build          # tsc -b && vite build
Pop-Location

# 后端：发布 Host（含 wwwroot 与 fixtures）
dotnet publish traceflow\src\TraceFlow.Host\TraceFlow.Host.csproj -c Release -o $PublishDir
```

说明：`Program.cs` 读取 `ConnectionStrings:TraceFlow`；仓库 `appsettings.json` 中该值为空，**必须由部署配置提供**
（环境变量 `ConnectionStrings__TraceFlow`、`appsettings.Production.json` 或服务注册时写入），不得提交到 Git。

### 3.2 建库

- **试运行/验收库（有白名单）**：`OpsTool` 只接受 `^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$`，
  保护库（`TraceFlow_Dev`、`TraceFlow_M2Manual`、`TraceFlow_M2Gate`、`TraceFlow_M3Ui`）与任何
  `TraceFlow_Test_*`、系统库永久拒绝（退出码 2）：

  ```powershell
  $env:TRACEFLOW_TRIAL_CONN = 'Server=<实例名>;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
  dotnet run --project traceflow\tools\TraceFlow.OpsTool -c Release -- create-db TraceFlow_Trial_<环境标识>
  ```

- **生产库**：库名不受 OpsTool 白名单约束，**由 DBA 手工创建并记录**（库名、排序规则、文件位置、创建人/时间）。
  推荐排序规则与实例默认一致；本仓库测试覆盖默认 collation 与 `Latin1_General_BIN2` 两种库排序规则。

### 3.3 迁移

```powershell
# 连接串走环境变量：不出现在命令行参数/进程表/日志
$env:TRACEFLOW_MIGRATE_CONN = 'Server=<实例名>;Initial Catalog=<目标库>;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet run --project traceflow\db\tools\TraceFlow.DbMigrator -c Release -- --dir traceflow\db\migrations
```

预期输出：`Applied: 0001_core.sql, ... 0009_m3_sale_upload.sql`（首次）或全部 `Skipped:`（已是最新）。
迁移幂等：`schema_version` 记录每个脚本的 SHA-256 校验和；脚本字节变化会被拒绝执行（checksum 门禁）。
迁移前后按 `backup-restore.md` 做一次 `COPY_ONLY` 备份。

### 3.4 管理员初始化（无默认公开密码）

- 试运行库（OpsTool，仅 Trial/Perf 白名单）：

  ```powershell
  $env:TRACEFLOW_TRIAL_ADMIN_PASSWORD = '<≥10 位，仅进程内，不落盘>'
  dotnet run --project traceflow\tools\TraceFlow.OpsTool -c Release -- init-admin <用户名> --db TraceFlow_Trial_<环境标识>
  ```

- 生产库（宿主交互式，密码两次隐藏输入，不落盘、不回显）：

  ```powershell
  & "$PublishDir\TraceFlow.Host.exe" --init-admin
  ```

  初始权限仅 `system:manage` / `trace:read` / `audit:read`，其余权限按需由管理员授予（`POST /api/v1/admin/users/{id}/permissions`），
  全过程写 `audit_log`（`admin.init` / 授权变更）。系统**没有**任何默认用户名/密码。

### 3.5 部署配置（不含密钥）

在 `$PublishDir\appsettings.Production.json`（不入 Git，文件 ACL 仅服务账号+管理员可读）：

```json
{
  "ConnectionStrings": { "TraceFlow": "Server=<实例名>;Initial Catalog=<目标库>;Integrated Security=true;Encrypt=true;TrustServerCertificate=true" },
  "TraceFlow": { "Env": "MOCK" },
  "AllowedHosts": "localhost;<店内主机名>"
}
```

密钥类设置（`top.appkey` / `top.appsecret` / `erp.connection`）**只通过系统设置页写入**（DPAPI 加密、只写不回显），
不要写进任何 json/环境变量。`ASPNETCORE_ENVIRONMENT=Production`。

### 3.6 注册为 Windows 服务

```powershell
sc.exe create $ServiceName binPath= "`"$PublishDir\TraceFlow.Host.exe`"" start= auto
sc.exe description $ServiceName "TraceFlow 零售追溯（Kestrel + Windows 服务）"
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start $ServiceName
```

- 服务账号：用专用账号（`MACHINE\traceflow` 或域账号），见 §4；`sc.exe create` 默认 LocalSystem，**不推荐**。
  设置账号：`sc.exe config $ServiceName obj= "<MACHINE或域>\<账号>" password= "<由管理员交互输入，不入文档>"`。
- 进程内环境变量：服务注册表 `HKLM\SYSTEM\CurrentControlSet\Services\TraceFlow\Environment`（REG_MULTI_SZ）
  可注入 `ASPNETCORE_URLS` / `ConnectionStrings__TraceFlow`；也可只用 `appsettings.Production.json`。
- 单进程即单门店：`--web-only` / `--worker-only` 用于拆分 Web 与后台作业（`Program.cs:23-24`）。
  默认同时运行 Web 与 Worker（`WorkerHostedService` + `SyncSchedulerHostedService`）。

### 3.7 健康检查

```powershell
Invoke-WebRequest -Uri http://localhost:5000/api/v1/health -UseBasicParsing | Select-Object -ExpandProperty Content
# 期望：{"data":{"env":"MOCK","db":"OK","serverTimeUtc":"..."},"requestId":"..."}（匿名可访问）

Invoke-WebRequest -Uri http://localhost:5000/api/v1/auth/session -UseBasicParsing | Select-Object -ExpandProperty Content
# 期望：data.csrfToken 非空（未登录时 authenticated=false）
```

> 注：实现中的健康检查路由是 **`/api/v1/health`**（`HealthEndpoints.cs`），不存在裸 `/health`。
> 系统没有独立"版本页"端点；升级后以 `/api/v1/health` 的 `db=OK` + 发布目录版本记录（人工）作为启动证据。

## 4. 服务账号与 DPAPI

1. **专用账号**：形如 `MACHINE\traceflow` 或域账号，禁止用 LocalSystem/Administrator。授予：
   - `$PublishDir`：读 + 执行（如需写日志目录则另建目录并给写权限）；
   - SQL Server：仅目标库所需的登录/权限（`db_datareader`/`db_datawriter`/执行迁移所需的最小权限），
     **不得**给 ERP（KSOA）库写权限（源库只读，只读账号另配）；
   - 不需要交互登录权限。
2. **DPAPI 作用域为 CurrentUser**（`DpapiProtector`）：`top.appkey`/`top.appsecret`/`erp.connection` 密文与
   服务运行账号绑定。**换账号或换机后旧密文不可解密**，表现为：设置页显示"已保存"，但连接测试/上传调用失败
   （并留审计与告警）。
3. **恢复流程**：按 `backup-restore.md` §6 重新录入密钥（只写不回显、每次写入自动审计）；不要试图导出/拷贝密文。
4. **验证**：设置页"连接测试"成功 + `GET /api/v1/audit-logs` 有对应 `setting.updated` 记录 + 宿主日志无明文凭据。

## 5. HTTPS（上线前必改）

- 现状：`Program.cs:131` 为 `o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;`，注释已标
  "G-DEPLOY 上线时强制 HTTPS（Always）"。**明文 HTTP 下认证 Cookie 可被窃听重放**，仅限本机/受控网络验证期使用。
- 上线前必改（代码改动不在本任务范围）：
  1. 将 `SecurePolicy` 改为 `CookieSecurePolicy.Always`；
  2. 在 IIS/反向代理终止 TLS（推荐），Kestrel 只绑回环；
  3. 配置证书、端口（默认 443）、HSTS（由反代或 `UseHsts` 提供）；
  4. 前端/接口全部走 `https://`，`AllowedHosts` 只列真实主机名。
- 证书类型、端口、HSTS 策略、代理配置均属 **G-DEPLOY 未取证**。

## 6. 升级流程与回滚

升级（每次发布）：

1. `COPY_ONLY` 全库备份并 `RESTORE VERIFYONLY`（`backup-restore.md` §3）；
2. `sc.exe stop $ServiceName`（或按 NFR-08 先暂停发送：设置 `sync.paused=true`、确认渠道暂停生效）；
3. 迁移：`TRACEFLOW_MIGRATE_CONN=... dotnet run --project traceflow\db\tools\TraceFlow.DbMigrator -c Release -- --dir traceflow\db\migrations`（幂等、checksum 门禁）；
4. 切换发布目录：`$PublishDir` → 新目录（保留上一版目录用于回滚）；
5. `sc.exe start $ServiceName`；
6. 验证：`/api/v1/health` 返回 `db=OK`、`/api/v1/auth/session` 返回 csrfToken、登录后工作台计数正常、
   `/api/v1/ops/alerts` 无新增未解释告警；记录发布版本号与时间（人工台账）。

回滚（升级失败）：

1. `sc.exe stop $ServiceName`；
2. 按 `backup-restore.md` §4 恢复升级前备份（`WITH REPLACE, RECOVERY` + `DBCC CHECKDB`）；
3. 发布目录回退到上一版；
4. `sc.exe start` → 健康检查；
5. **NFR-08**：恢复后先跑对账、修复差异，再开放发送（禁止直接重跑历史发送）。

## 7. 日志与诊断

| 项 | 位置/方式 |
| --- | --- |
| 服务宿主日志 | Windows 事件日志（`UseWindowsService` 注册 EventLog 提供程序），来源为服务名；Interactive 运行时输出到控制台 |
| 应用日志级别 | `appsettings.json` 的 `Logging:LogLevel`（默认 `Information`，`Microsoft.AspNetCore` 为 `Warning`） |
| 作业状态 | `GET /api/v1/ops/metrics`（积压/失败率/租约回收/预算等待，权限 `trace:read`） |
| 告警 | `GET /api/v1/ops/alerts`（派生告警：积压、24h 失败、租约回收、渠道暂停、对账 INCOMPLETE、预算等待） |
| 作业分页 | `GET /api/v1/ops/jobs?state=&kind=&page=`（只读，`lastError` ≤200 字符） |
| 审计 | `GET /api/v1/audit-logs`（`audit:read`）；导出 `GET /api/v1/exports/audit-logs.csv`（`audit:export`） |

**运维注意（日对账补跑）**：漏跑的日对账窗口由 Worker 调度自动补跑——自最早作业活动日起、最多 7 天
（上海时区日窗，逐日 SALE/RETURN 各一次，幂等）；超过 7 天的更早缺口需人工 `POST /api/v1/reconciliation-runs`
（`system:manage`，无前端入口）。

**日志中不得出现凭据**：连接串/密码不在日志、错误响应、审计摘要中（`SettingsService` 只写不回显；
`TopClientService` 脱敏留证）。若需归档 stdout，请用服务包装器把控制台重定向到受限目录，不要写入 Web 可访问目录。
系统**未内置**滚动文件日志（如实说明）。

## 8. G-DEPLOY 取证清单（部署时必须逐项记录）

全部条目当前状态为**未取证**。取证方式：在目标机执行左列命令/操作，把真实输出或截图登记到交付证据目录
（建议 `.superpowers/sdd/<日期>-deploy-evidence/`），并由业务负责人签字确认。

| # | 取证项 | 取证命令/操作（记录真实输出） | 状态 |
| --- | --- | --- | --- |
| 1 | 目标 OS 版本与补丁 | `winver`、`Get-ComputerInfo \| Select WindowsProductName,OsVersion,OsBuildNumber` | 未取证 |
| 2 | SQL Server 版本/版本号/排序规则 | `SELECT @@VERSION`、`SERVERPROPERTY('ProductVersion')`、`DATABASEPROPERTYEX(<库>,'Collation')`；若为 2008 R2 须附 AT-070 补验记录 | 未取证 |
| 3 | 服务账号与权限 | `sc.exe qc TraceFlow`、`whoami /groups`（服务账号上下文）、SQL 登录权限脚本输出；确认无 ERP 写权限 | 未取证 |
| 4 | 网络与监听 | `netstat -ano \| findstr :<端口>`、`Get-NetTCPConnection -State Listen`；记录允许访问的主机名/网段（不入公开文档） | 未取证 |
| 5 | HTTPS/证书 | IIS 或反代站点绑定截图、证书颁发者/有效期/指纹、`curl.exe -I https://<主机名>/api/v1/health` 输出；确认 Cookie `Secure` 标志（`Set-Cookie: .tf.auth=...; secure`） | 未取证 |
| 6 | 备份/恢复 | 按 `backup-restore.md` 在目标机执行一次备份+恢复演练，记录 `docs/delivery/backup-restore-drill.json` 同结构结果与备份文件大小/耗时 | 未取证（本仓库仅有本机 SQL 2019 演练证据） |
| 7 | 平台主体与服务期 | 平台企业主体（ent_id/ref_ent_id）截图、TOP 应用授权与服务期、G-TOP 合同取证编号 | 未取证 |
| 8 | 业务负责人与当地操作要求 | 门店负责人、追溯操作 SOP、当地监管附加要求（如上传时限）书面确认 | 未取证 |

**取证完成前不得**：把 `TraceFlow:Env` 切到 `LIVE`、对外暴露端口、声称 G-DEPLOY 完成。