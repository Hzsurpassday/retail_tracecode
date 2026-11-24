# M3 裁决与携带项（交接 M4）

基线：计划 `docs/superpowers/plans/2026-11-23-m3-sale-upload.md`（Task 1–10）+ 执行账本 `.superpowers/sdd/2026-11-23-m3-sale-upload/progress.md`。冲突优先级沿用 M2：用户后续明确确认 > PRD 已确认业务要求 > PRD 默认方案 > 旧规划/原型。

## 裁决（R-M3-1 … R-M3-22）

| # | 裁决 | 若错的代价 |
| --- | --- | --- |
| R-M3-1 | 执行顺序改为 T1 → T3 → T2 → T4 → T5 → T6 → T7 → T8 → T9 → T10（工厂先于核验服务落地，核验服务才可编译调用）。 | 仅提交顺序不同，可回滚分支重排。 |
| R-M3-2 | `TopResponseParser` 无 `result` 节点时回退 `model` 作为 ResultJson（M2 fixture 与 billstatus 候选结构均用 `model`）。 | 多一层兼容分支，无害。 |
| R-M3-3 | 仓库无 vitest 基建时不新增测试依赖，前端以构建 + chrome-devtools 浏览器实测截图作为证据。 | 组件测试覆盖推迟到 M4。**M4 Task 7 已关闭**：vitest 2.1.9 + RTL 16 + jsdom 25 基建落地，26 个用例（4 个测试文件）全通过，`tsc -b && vite build` 0 错误。 |
| R-M3-4 | 采购退货 202 只做服务/测试支持 + UI 明示"采购退货采集未接入（G-ERP）"，不伪造采集入口。 | 202 真实采集推迟到 G-ERP 取证后，不影响 116 闭环。 |
| R-M3-5 | 每个 Task 单独提交、按 Task 边界评审；报告与 brief 存 SDD 工作区。 | 仅多几次评审成本。 |
| R-M3-6 | 0009 在合并前修订（补索引与校验）并同步更新校验和断言——0009 未发布，0001–0008 不动。 | 仅 0009 一次未发布变更，历史迁移仍逐字节不变。 |
| R-M3-7 | 任务 event_key 类型化 `KSOA:{docKey}:{TaskType}`（混合销售/退货单需两任务）；核验 event_key 保持 `KSOA:{docKey}:SALE`。 | 同单据混合行无法建两任务，退货上传被销售任务吞并。 |
| R-M3-8 | CODE_CONTENDED / REUSE_STATE_INVALID / SOURCE_CHANGED 经 `AllocationConflictException` 统一 409。 | 前端无法统一按冲突刷新，错误处理分叉。 |
| R-M3-9 | 核验阻断绝不覆盖任务态 `Accepted/Processing/Succeeded/Failed/Cancelled`，只改核验/依赖字段并审计 `preservedState=true`。 | 平台进度被子本地重算掩盖；终态任务的人工处置提示偏晚。 |
| R-M3-10 | 依赖等待文档由确定性唤醒路径 + 每小时一次安全复查驱动；5 分钟轮询绝不复核（每等待文档 288 次平台调用/日不可接受）。 | 漏唤醒时核验最长延迟 1 小时。 |
| R-M3-11 | 已发 321 后源单据出新版本（含销售行）保持 FAILED + ManualAction 人工处理，不自动重建。 | 操作员需手工重录版本。 |
| R-M3-12 | 任务可停留 AcceptanceUnknown 而兄弟 Ready 分片继续发送；仅 Cancelled/Blocked/Succeeded/Failed/ManualAction 拒绝派发。 | 操作员看到任务聚合态滞后于分片。 |
| R-M3-13 | 成功回查结论永远生效（即使已超 20 次）；升级仅对仍在等待的结论。 | 平台迟到矛盾结论按收到的为准（合同待核）。 |
| R-M3-14 | 候选 billstatus 映射下 `QueryConfirmedNotAccepted` 不可达（result_type=4=Failed），不虚构；G-TOP 须确认是否存在独立"未受理"信号后才启用重排。 | 需受控重传的分片等待人工处置。 |
| R-M3-15 | result_type=2 但有码未被平台回答时降级 Partial 而非 AllSuccess——UNKNOWN 永不升级成功。 | 真完整分片需等待后续确认。 |
| R-M3-16 | REJECTED 退货确认对该任务版本终态；后续核验回收占位任务并新建，不重开已驳回。 | 操作员需重复确认已驳回码。 |
| R-M3-17 | 无核验记录的销售快照在 `/pos-sales` 的 LIVE 与 MOCK 视图都出现；env 是接入来源属性，不是核验属性。 | 合并两个 env 视图统计未核验销售会重复计数；单 env 视图仍正确。 |
| R-M3-18 | 受控重试按钮按后端 `retryPathCertified` 门禁（fail-closed），标志只读暴露在任务详情；前端不得自行判断合同取证。 | 只多一个只读字段。 |
| R-M3-19 | T8 UI 跟进（tab 服务端过滤、错误互斥、稳定幂等键、EnvBadge unknown、不预选、中文原因、aria）在 T7 修复轮之后执行，避免 SalesEndpoints/ReturnsPage 冲突。 | 多一轮评审。 |
| R-M3-20 | T7 修复轮 2（M1/M2/M3 + 低风险加固）在 T8 跟进完成后顺序执行，避免并发构建与文件冲突。 | 多一轮串行。 |
| R-M3-21 | 抽屉高度布局修复单独提交（单文件单 CSS 属性，已浏览器复测），不重开 T7/T8 修复循环。 | 分支多一个提交。 |
| R-M3-22 | 恢复流程在自己的事务内重算任务聚合（先取任务 UPDLOCK/HOLDLOCK），而非留给后续回查/扫描；用例 6 由此断言精确 AcceptanceUnknown。 | 恢复路径多一次任务行锁；死锁顺序已对照派发/回查/人工路径审计。 |

## 携带项（M4 必须处理）

- **InternalsVisibleTo 面过宽**：`TraceFlow.Infrastructure.csproj` 对 `TraceFlow.IntegrationTests` 开放全部 internal 成员（供故障注入 BeforeSend/AfterSend/BeforeCommit 钩子）。M4 收窄为独立 internal 接口 + 测试专用注册（`#if DEBUG`），或移入 `InternalsVisibleTo` 白名单类型。
- **恢复锁范围**：`SendRecoveryService` 在确认分片守卫成功前即锁任务行；当前无害，M4 复核锁顺序与吞吐（R-M3-22）。
- **用例 2/3 意图计数器**：FaultInjection 用例 2/3 的恢复后计数器结构性为 0，保留作意图文档；M4 改为可证伪断言或删除。
- **既有 ImportApi flake**：`ImportServiceTests` 提交用例曾两次观察到暂存区竞争（单跑/重跑均绿，M3 全量运行亦复现过一次 4 例 fixture 建库竞争），非 M3 代码缺陷；M4 与 CI 稳定性一并处理（fixture 建库串行化）。
- **全局 429 映射缺 `Retry-After`/`retryAfter`**（M2 既有行为，仅 `ApiErrors` 429 分支）：M4 补契约测试与头/字段一致性。
- **`ReturnConfirmService` 死分支**：`UQ_retconf_task` 唯一冲突 catch 在插入事务外不可达，极窄竞态会 500 而非 409；M4 加固。
- **M3 设置键未进 Settings API 目录**：`top.query.*` 等当前只能配置文件/直改库；M4 暴露给运维并补权限与审计。
- **分片收缩不释放同任务丢弃占用**、`DocPayloadJson` 规范化在核验/工厂两处重复、退货候选 >2100 码未分批：低风险，M4 统一。
- **前端遗留**：~~`fmtShanghai` 多页重复~~（M4 T7：收敛为 `src/utils/time.ts`，9 页改为导入）、~~Esc 关闭弹窗~~（M4 T7：六个 `role="dialog"` 弹窗接入 `useEscapeClose`）、~~组件测试（R-M3-3 推迟）~~（M4 T7：vitest 26 用例）、~~状态原因文案多源~~（M4 T7：`REASON_ZH` 收敛为 `src/utils/statusReason.ts`）、202 jobId 后续作业状态页（未完成）。
- **平台回传未知码入台账**（终审 Low7）：codedetail/回查中平台返回的未知码当前不写入 `code_result` 台账，M4 与日对账一并设计（需先确认台账口径）。
- **`/returns` 列表固定宿主 env**（终审 Low8）：退货列表按宿主 env 过滤、不开放 env 切换，保持现状；M4 若需跨 env 视图再扩展到与 `/pos-sales` 一致。
- **202 采购退货采集未接入**（G-ERP）；**真实 TOP 取证未完成**（G-TOP）；**2008 R2 实跑未验证**（AT-070）——均不得在 M4 前声称已满足。

## 环境事实（不声称未验证能力）

- 全部集成/故障注入运行于本机 SQL Server 2019（15.0.2000.5），`TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 用户授权降级；默认 collation 与 `TRACEFLOW_TEST_DB_BIN2=1` 各跑一遍。**2008 R2 未验证（AT-070 待补验）**。
- M3 发送（uploadretail/uploadinoutbill）、回查（billstatus）、逐码查询（codedetail）全部 MOCK/fixture 通道，`_verified: false`；`RealTopGateway` 对 LIVE 一律 422 TOP_NOT_CERTIFIED；`top.retryPathCertified=false` 默认 fail-closed。
- 浏览器验证使用独立库 TraceFlow_M3Ui（MOCK，保留不删）；证据见 `.superpowers/sdd/2026-11-23-m3-sale-upload/shots/` 与 `task-8-report.md`。
- 本文件不包含任何密钥、凭据、IP 或连接串。