# TraceFlow 交付说明（M4 收尾）

日期：2026-11-24　分支：`m4-closure`　PR：https://github.com/Hzsurpassday/retail_tracecode/pull/5

## 一、交付了什么

单门店零售追溯系统（POS 销售码台账 → TOP 上传任务 → 上游单据/入库/库存码 → 消费者退货 → 对账与异常处置），
包含前端管理台、后端 API、后台 Worker、数据库迁移（0001–0009）与运维工具。

**页面（前端路由，构建产物在 `TraceFlow.Host/wwwroot`）**

| 路由 | 页面 |
| --- | --- |
| `/` | 看板（真实计数：待上传、异常、差异等） |
| `/products`、`/imports`、`/suppliers` | 商品主数据与映射、导入向导、供应商 |
| `/upstream`、`/inbounds` | 上游单据、入库任务（匹配/分配/确认） |
| `/sales`、`/returns` | POS 销售、消费者退货（确认生成追溯事件与上传任务） |
| `/uploads`、`/ops` | 上传中心（人工核对/受控重试）、运维（作业、指标、告警、对账） |
| `/codes`、`/exceptions`、`/settings` | 码台账、异常队列（留证关闭）、设置（密钥只写） |
| 其它路由 | 显示"后续阶段交付"占位 |

**API 端点（`/api/v1` 前缀，共 40+ 个）**

- 认证/权限：`auth/session|login|logout`、`admin/users`、`admin/users/{id}/permissions`
- 主数据：`products`、`suppliers`、`product-mappings/{id}/confirm|resolve|disable`、`supplier-mappings/...`、`products/{id}/trace-policy`
- 导入/上游：`imports`、`imports/{id}[/commit]`、`upstream-bills[...]`、`inbounds[...]`（`rematch`/`allocations`/`confirm-allocation`/`cancel-allocation`）
- 业务：`pos-sales`、`returns`、`returns/{id}/confirm`
- 上传/作业：`upload-tasks[...]`、`upload-tasks/{id}/manual-verify|controlled-retry`、`sync-jobs`、`jobs/{id}`
- 对账：`reconciliation-runs`（触发/列表/详情）、`reconciliation-differences/{id}/resolve`
- 运维/审计：`ops/metrics`、`ops/jobs`、`ops/alerts`、`audit-logs`、`settings`、`top-budgets`、`health`
- 导出（CSV，含 BOM 与公式注入防护）：`exports/codes.csv`、`exports/reconciliation-runs/{id}/differences.csv`、`exports/audit-logs.csv`

## 二、如何启动

按 `docs/delivery/deployment.md` 执行（发布 → 建库 → 迁移 → 初始化管理员 → 注册服务 → 健康检查），
并先过 §2 预检查（SQL 版本与排序规则、`organization` 单活行、`AllowedHosts`、监听地址）。
本机快速自测可用 `traceflow/tools/trial/Start-Trial.ps1`（只建 `TraceFlow_Trial_*` 库，仅绑 `127.0.0.1`）。

## 三、默认 MOCK，LIVE 需显式开启且 fail-closed

- 未设置 `TraceFlow__Env=LIVE` 时一律 MOCK：不向任何真实平台发送，上传走 fixture 通道。
- LIVE 模式下 TOP 网关**默认拒绝发送**（422 `TOP_NOT_CERTIFIED`）；`top.retryPathCertified=false` 时受控重试同样拒绝。
  即"平台未取证之前，系统宁可停发也不误发"。
- ERP 连接（`erp.connection`）在真实凭据未配置/未取证前不可用。

## 四、质量数字（本机实测，详见 `M4-gate-report.md`）

| 项 | 结果 |
| --- | --- |
| Release 构建 | 0 警告 / 0 错误（`TreatWarningsAsErrors`） |
| 单元测试 | 322 / 322 |
| 集成测试 | 495 / 495（默认 collation）+ 495 / 495（BIN2 变体），SQL Server 2019 授权降级 |
| 前端 | vitest 29 / 29；`tsc -b && vite build` 通过（53 modules） |
| 性能基准 | 7 / 7 端点 p95 11.5–134.8 ms（上限 2000 ms，10000 商品 / 100000 销售行，`/returns` 非空） |
| 备份/恢复演练 | 8 / 8 断言通过；备份 4,575,232 字节（160.1 ms）、恢复 597.2 ms、`DBCC CHECKDB` 无错误；重复 `-Stamp` 拒绝 exit 2 |
| 迁移 | 0001–0009 双 collation（中文默认 + BIN2）回归通过，checksum 未变 |

## 五、用户待办

以下各项全部**未完成**，均需用户提供/确认：

1. **凭证与配置**：把下列项填进 `E:\Viding\Retail Trace Code\secrets\appsecrets.env`（该文件不入 Git，
   **不要在聊天/文档/截图中传播**）：
   - AppKey / AppSecret（`top.appkey` / `top.appsecret`）；
   - `ent_id` / `ref_ent_id`（平台侧企业标识，当前无 API，须 DBA 直更）；
   - 门店名称（`organization` 单活行的门店名称）；
   - KSOA 只读连接串与 KSOA 版本（`erp.connection`，只读账号）；
   - 初始管理员密码（`--init-admin` 初始化用）；
   - 服务账号（`MACHINE\traceflow` 或域账号）；
   - 监听地址（`ASPNETCORE_URLS` / `appsettings.Production.json`）。

   写入后仅能通过设置页写入（只写不回显），并确认审计无明文。
2. 提供一台**真实 SQL Server 2008 R2**（10.50.x）实例，重跑集成用例 + 性能基准 + 备份/恢复演练（AT-070 缺口）。
3. **平台取证配合**：G-TOP 方法名/入参/错误码/限值/回执样例、G-ERP 视图与只读账号，
   以及 G-POS / G-RETURN / G-OPENING 的字段与样例数据。
4. 提供 **202 采购退货**的来源字段与样例（当前只接服务/测试路径）。
5. 安排 **≥7 天试运行**，并填写 `trial-run-plan.md` 的登记表（当前仅"冒烟"一行，其余 `待填`）。
6. 在目标部署机完成 **G-DEPLOY 8 项取证**（`deployment.md` §8 清单全部未取证），**上线前必须**把
   `Program.cs` 的 `Cookie.SecurePolicy` 由 `SameAsRequest` 改为 `Always` 并配置 TLS。
7. 确认备份保留期 / RPO / RTO（`backup-restore.md` §3 为 `待用户确认`），并锁定备份目录访问权限。

## 六、安全边界

- 密钥（应用密钥、连接串密码）只写不回显、不入日志、不入审计明文；DPAPI `CurrentUser` 加密，
  **换服务账号或换机器后旧密文不可解**，须重新录入。
- 审计日志 `audit_log` 不可改删；导出/人工操作均留痕（前后摘要、原因、对象），导出成功才写审计。
- 服务账号为最小权限（应用库 db_owner 之外不给 sysadmin），系统**不向 ERP 写入**。
- CSV 导出防公式注入（`= + - @` 等前缀加 `'`），单次导出有行数上限（超限 422）。
- 所有 LIVE 发送受"认证闸门 + 暂停/预算 + 幂等键"约束，异常即停。

## 七、已知限制 / 不在本期范围

- **2008 R2 未验证**（AT-070）；G-TOP/G-ERP 未取证；7 天试运行未执行；G-DEPLOY 目标机未取证。
- 恢复后"不重放已成功业务"依赖运维顺序（恢复时服务必须停止，恢复后先对账再开放发送）；未做进程级强制。
- 占用释放审批（`code:release` 工作流）不在 M4 范围；多门店/多组织未实现（单门店 `DEFAULT`）。
- 慢查询采集未开启（Query Store/Profiler）；性能数据为单机基准，非真实门店并发。
- **对账无独立前端页面**（本阶段不交付）：日对账由 Worker 调度自动产出（含最多 7 天漏跑补跑），人工触发
  与运行历史/差异/审计三类 CSV 导出仅走 API；码台账详情不含本地 `verification_code` 段（该码的占用与逐码
  核验结论见 POS 销售详情的 codes 段）。
- 对账差异 CSV 与审计 CSV 无 UI 入口（仅码台账有导出按钮）；对账触发/历史页延后。