# 备份与恢复手册（NFR-07 / NFR-08 / AT-072）

日期：2026-11-24。分支：`m4-closure`（M4 基线 `d0cac84`）。配套：`docs/delivery/deployment.md`（部署）、
`docs/delivery/backup-restore-drill.json`（本机真实演练实测值）、`docs/delivery/M4-gate-report.md`（门禁）。

> **事实边界（必须先读）**
> 1. 本文流程与命令按真实代码/迁移编写；**完整演练只在数据层执行**（本机 SQL Server 2019 15.0.2000.5，
>    见 `backup-restore-drill.json`），**SQL Server 2008 R2 未验证（AT-070）**。
> 2. **LIVE 平台相关步骤未执行**：G-TOP/G-ERP 未取证，没有任何真实平台对账/发送证据。
> 3. 保留期、RPO、RTO、目标备份硬件均为 **待用户确认**（PRD 未给出）。
> 4. 示例连接串一律 `Server=localhost;Integrated Security=true;...`；本文不含密码、密钥、内网 IP、门店名称。

## 1. 备份对象

| 对象 | 内容 | 说明 |
| --- | --- | --- |
| 业务库（单个） | 全部业务表：`organization`、`source_snapshot`/`source_line`、`product`/`supplier`/映射、`upstream_bill*`、`inbound_*`、`trace_code`/`code_event`/`code_claim`、`verification*`、`upload_task`/`upload_part`/`upload_attempt`/`code_result`、`outbox_job`、`reconciliation_*`、`audit_log`、`schema_version` 等 | **成功单号、占用历史、`audit_log`、`source_snapshot` 一律不得自动清理**；备份是唯一保留手段 |
| 部署配置 | `appsettings.Production.json`（不含密钥）、服务注册表环境变量清单、`AllowedHosts`/`ASPNETCORE_URLS` 取值 | 单独归档；**密钥不在此** |
| 密钥 | `top.appkey` / `top.appsecret` / `erp.connection` 的 DPAPI 密文**随库** | 密文绑定服务账号与机器（`CurrentUser` 作用域），**不可导出/不可跨机解密**；不在配置文件备份内 |
| 发布产物 | `$PublishDir` 版本目录 | 建议保留上一版用于回滚（可选） |

## 2. 备份命令（T-SQL）

```sql
-- 全库备份（日常/升级前）。<backupDir> 必须是 SQL Server 服务账号可写目录。
BACKUP DATABASE [TraceFlow]
  TO DISK = N'<backupDir>\TraceFlow-full-yyyyMMddHHmm.bak'
  WITH INIT, CHECKSUM, COMPRESSION, STATS = 10;

-- 校验备份集可读（必做，失败即视为备份无效）
RESTORE VERIFYONLY
  FROM DISK = N'<backupDir>\TraceFlow-full-yyyyMMddHHmm.bak'
  WITH CHECKSUM;
```

- `COPY_ONLY`：**升级前临时备份**必须加 `COPY_ONLY`，否则会打断既有差异备份链：
  `BACKUP DATABASE [TraceFlow] TO DISK = N'<backupDir>\TraceFlow-prefly-yyyyMMddHHmm.bak' WITH INIT, COPY_ONLY, CHECKSUM;`
- `COMPRESSION`：SQL Server 2008 R2 支持 `BACKUP ... WITH COMPRESSION`，但**非 Enterprise 版可能不可用**
  （2008 R2 标准版不支持备份压缩）。实例版本/授权不支持时直接**去掉 `COMPRESSION`**，其余子句不变。
- 备份文件落在 SQL Server 服务账号可写目录（实例默认备份目录或专用备份盘），不要落在 Web 可访问目录。
- **保留期 / RPO / RTO / 备份硬件：待用户确认**（PRD 未给出）。在用户确认前，按"每日全备 + 升级前 COPY_ONLY"
  执行，并至少保留最近 30 天与每月首份（临时建议，需用户签字）。

## 3. 恢复流程

```sql
-- 1) 停止服务/上传（关键：恢复期间不得有作业写入）
--    sc.exe stop TraceFlow   或  设置 sync.paused=true 并确认所有渠道暂停生效

-- 2) 单用户模式 + 恢复（WITH REPLACE, RECOVERY）
USE master;
ALTER DATABASE [TraceFlow] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [TraceFlow]
  FROM DISK = N'<backupDir>\TraceFlow-full-yyyyMMddHHmm.bak'
  WITH REPLACE, RECOVERY, CHECKSUM;
ALTER DATABASE [TraceFlow] SET MULTI_USER;

-- 3) 完整性检查（必须无错误）
DBCC CHECKDB(N'TraceFlow') WITH NO_INFOMSGS;

-- 4) 迁移（若恢复的是旧版本库；幂等 + checksum 门禁，向后兼容检查）
--    TRACEFLOW_MIGRATE_CONN='...' dotnet run --project traceflow\db\tools\TraceFlow.DbMigrator -c Release -- --dir traceflow\db\migrations

-- 5) 启动服务并核对健康
--    sc.exe start TraceFlow
--    GET /api/v1/health  -> data.db = OK
--    GET /api/v1/auth/session -> csrfToken 非空
```

要点：

- 恢复**必须先 `SET SINGLE_USER WITH ROLLBACK IMMEDIATE`**，恢复后**立即回 `MULTI_USER`**（否则后续连接/作业全失败）；
- `RESTORE ... WITH REPLACE, RECOVERY` 之后**必须** `DBCC CHECKDB`；任一失败即停止并上报，不得"先上线再说"；
- 恢复期间服务必须停止：`outbox_job` 中恢复出的 `RUNNING` 行若在未对账前被重新执行，可能造成**重复发送**；
- 恢复的是**全库快照**，恢复点之后的一切业务（含审计）都会丢失——这是 RPO 的物理含义，须让业务方知情。

## 4. NFR-08 恢复后顺序（强制）

恢复旧库 **禁止直接重跑历史发送**。严格按下列顺序：

1. **保持发送停止**：服务/发送作业保持停止；若在 LIVE 运行态恢复，先确认所有 `top_channel_pause` 生效
   （`GET /api/v1/top-budgets` 查看暂停范围），并确认 `sync.paused=true` 已落库；
2. **先对账**：触发对账 `POST /api/v1/reconciliation-runs`（权限 `system:manage`，body
   `{windowStart, windowEnd, businessType}`，带 `Idempotency-Key`）。**宿主 `TraceFlow:Env` 必须与备份来源一致**
   （MOCK 备份用 MOCK 宿主对账；跨 env 会出现"找不到/差异假象"）；
   对账窗口必须覆盖**备份时间点 → 停机期间**平台已受理的全部单号；
3. **在系统中修复差异、补录证据**：`GET /api/v1/exceptions`（`RECON`/`TASK` 分类）逐条处置；
   差异解决走 `POST /api/v1/exceptions/{category}/{id}/resolve` 或
   `POST /api/v1/reconciliation-differences/{id}/resolve`（仅 `OPEN→RESOLVED`，**证据必填**，审计 env 恒 `LIVE`）；
4. **再开放发送**：解除渠道暂停、`sync.paused=false`、重启 Worker；此后才允许新的发送/回查作业运行。

补充约束：

- 对账只使用**已持久化证据**（`upload_task`/`upload_part`/`upload_attempt`/`code_result`），不主动调用 TOP；
  任一分片处于非终态即 `INCOMPLETE`（`platformSummary.unavailable=true`），**不显示"无差异"、不自动重传**；
- 平台侧"查不到"不等于"可以重发"：先经 `billstatus` 取证（G-TOP 取证后）再决定人工处置；
- 恢复出来的 `outbox_job`（尤其 `RUNNING`/`DISPATCHING`）在完成对账前不得被重新执行；必要时先将相关作业
  置为 `ManualAction` 并在异常队列留证。

## 5. 密钥恢复

| 场景 | 行为 |
| --- | --- |
| 同机同账号恢复 | DPAPI 密文可直接解密，设置页无需重录；恢复后做一次连接测试确认 |
| 换机 / 换服务账号 | **旧密文不可解密**（`CurrentUser` 作用域）。表现为设置页显示"已保存"但调用失败（`DpapiProtector.Unprotect` 抛 `SecretUndecryptableException`，连接测试失败 + 告警） |

重录步骤：

1. 以 `system:manage` 登录 → 设置页；
2. 重新录入 `top.appkey`、`top.appsecret`、`erp.connection`（表单**只写不回显**；提交后接口只返回
   `hasSecretValue=true, value=null`）；
3. 每次写入自动审计（`setting.updated`，不含明文）；
4. 验证：
   - 设置页"连接测试"成功（`POST /api/v1/settings/connections/test`）；
   - `GET /api/v1/audit-logs` 有对应记录且 `after_digest` 不含明文；
   - 宿主日志无明文凭据（人工抽查）。

**密文不可导出、不可拷贝到另一台机器**——这是设计约束，不是缺陷。

## 6. 本次演练记录（真实执行）

| 项 | 值 |
| --- | --- |
| 演练脚本 | `traceflow/tools/trial/Backup-Restore-Drill.ps1`（PowerShell 5.1，`sqlcmd -b -I -h -1 -W`） |
| 执行命令 | `powershell -NoProfile -ExecutionPolicy Bypass -File traceflow/tools/trial/Backup-Restore-Drill.ps1 -Base "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"` |
| 引擎 | SQL Server 2019（`ProductVersion` 15.0.2000.5）；**2008 R2 未验证（AT-070）** |
| 演练库 | `TraceFlow_Trial_Drill_<UTC 时间戳>`（运行结束已删除，无残留） |
| 备份 | `BACKUP ... WITH INIT, COPY_ONLY, CHECKSUM` + `RESTORE VERIFYONLY ... WITH CHECKSUM`；文件字节数与耗时见 JSON |
| 恢复 | 单用户 → `RESTORE ... WITH REPLACE, RECOVERY, CHECKSUM` → 多用户；`DBCC CHECKDB` 无错误 |
| 断言 | 备份前标记存在 / 备份后标记不存在 / organization 名称恢复 / `upload_task` 同 id 恢复 / `schema_version` 9 行且 `success=1` / VERIFYONLY 与 CHECKDB 均 exit 0 |
| 实测结果 | 全部通过，见 `docs/delivery/backup-restore-drill.json`（`checks` 数组逐项记录 expected/actual/method） |

边界说明：

- 本演练只覆盖**数据库层**（备份、校验、恢复、完整性、迁移账本）；**未执行**任何 TOP/KSOA 调用，
  也**未执行** §4 的 LIVE 对账顺序（无平台可对账）；
- 7 天试运行未执行（见 `trial-run-report.md`）；
- 目标部署机上的备份目录权限/保留策略未取证（G-DEPLOY）。