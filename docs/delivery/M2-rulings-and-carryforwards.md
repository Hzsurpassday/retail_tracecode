# M2 裁决与携带项（交接 M3）

基线：spec 2026-11-22 + PRD V1.0 + 验收清单；计划 2026-11-23-m2-upstream-allocation.md（Task 1–15）。冲突优先级：用户后续明确确认 > PRD 已确认业务要求 > PRD 默认方案 > 旧规划/原型。

## 裁决（执行期如偏离须记录新裁决）

1. **Cancelled 行政终态（IN-09）**：0008 为 upload_task/upload_part CHECK 增加 N'Cancelled'；状态机仅增 (Blocked,AllocationCancelled)→Cancelled、(Ready,AllocationCancelled)→Cancelled 两条边。纯本地行政终态：任何 UPLOAD 尝试后不可达；不是平台状态，不计入平台成功/失败口径。
2. **R7a Cancelled 复活**：UQ_task_event(org,env,event_key) 禁止同事件键第二任务；Cancelled 且从未有 UPLOAD 尝试的任务按原行重激活（state→Blocked/Ready，bill_code 不变，Reused=false）；有尝试记录的一律走受理未知/回查路径，不得复活。
3. **R7b mapping_version_snapshot**：nvarchar(200) 存"映射版本对 Ordinal 排序拼接"的 SHA256 大写 hex（64 字符）；版本对格式 product:{spid}:{version} / supplier:{dwbh}:{version}。
4. **R7c payload_json**：内部规范化 DTO（事件键/供应商身份/行/码集），不是平台请求参数；G-TOP 取证后 M3 由 DTO 构造平台参数。payload_hash=DTO JSON SHA256 大写 hex。
5. **R8a 既有任务处置**：已发送（有 UPLOAD 尝试）或状态不在 {Blocked,Ready,Cancelled}→冻结（match=COMPLETE 注明原因，不动任务）；pre-send 且 source_hash==快照 content_hash→无操作；pre-send 且 hash 不同→按旧任务自己的 snapshot_id CancelPreSend 后重建（bill_code 不变）。
6. **R8b 触发语义**：up:/map:/periodic 一律跑待匹配扫描；仅携带 snapshotId 的 rematch 定向单快照；billId 只作审计线索不反查。
7. **R8c 不接线 src: 触发**：不改 M1 SourceSyncService 构造；新 COMPLETE 快照由 ≤5 分钟周期扫描兜底；src: 意图命名空间保留未用。
8. **R8d 自动匹配证据门槛**：单位相同（1/1）、批号 Trim+Ordinal 相等且非空、数量精确相等、上游行码集全 MIN 包装级（候选值）+S8P20、码数==数量、任一码无 ACTIVE PURCHASE_IN 占用、码集内无父子边；每 REQUIRED 行恰一候选且上游行互不重复→COMPLETE+自动建任务；多候选/争用→AMBIGUOUS 不自动挑选；部分无候选→PARTIAL；全无→UNMATCHED。
9. **R8e 单据候选**：supplier_mapping active→ref_ent_id 过滤 COMPLETE 上游单；to_ent_id 非空且≠org.ent_id 排除；raw_header.ywdjbh（候选字段，G-ERP 未取证）→bill_no 精确命中，未命中→UNMATCHED；命中但"药+数量相符批号不同"→CONFLICT（AT-025）；无 ywdjbh→bill_date ±7 天窗口（业务时间缺失→UNMATCHED）。
10. **R8f 分类门槛**：任一来源行档案缺失或 UNKNOWN→UNMATCHED；全部 EXEMPT→NOT_REQUIRED 0 任务；REQUIRED 数量≤0→ingest_exception（QTY_NONPOSITIVE，OPEN 去重）+UNMATCHED。
11. **R9a 保存=全量替换**：SaveAsync 先撤销该快照全部 RESERVED（释放占用）；存在发送前任务先 CancelPreSend；已发送任务存在→409 FROZEN 拒绝保存。
12. **R9b 保存后匹配态**：全部 REQUIRED 行覆盖→COMPLETE 否则 PARTIAL；保存不建任务（任务只在确认时创建）。
13. **R9c 校验失败语义**：行级错误收集 fieldErrors {sourceLineId,code,message}，一次性 422 ALLOCATION_INVALID；不做部分保存。
14. **R9d 版本闸**：ExpectedMatchVersion（int 字符串）≠inbound_match.version→409 VERSION_CONFLICT（AT-067）；占用争用→409 CODE_CONTENDED（AT-032）。
15. **R10a 确认时任务态**：Confirm 时存在 Blocked/Ready 任务（并发自动匹配产物）→409 VERSION_CONFLICT 要求刷新；已发送→409 FROZEN。
16. **R10b factory 注入**：ConfirmAsync 以参数接收 InboundTaskFactory（端点从 DI 取）；AllocationService 构造保持 (IAuditLog)。
17. **R10c 确认复核（AT-031）**：Confirm 对每条分配重跑与 Save 相同的资格闸；复核失败逐项 422，不建任务。
18. **R10d redownload 窗口**：显式 start=now-90d、end=now + onlyBillId；LIVE→422 TOP_NOT_CERTIFIED；意图含 nonce 避免终态重入队缺陷（M1 携带项 1 的 M2 局部闭环）。
19. **R11a fail-closed**：sync.paused JSON 解析非 False 一律视为暂停（M1 T6e 携带项闭环）。
20. **R11b 改绑范围**：resolve 仅受理 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED；已确认态走 confirm/disable；供应商 targetId=ref_ent_id 取最新企业版本，信用代码 Ordinal 精确一致否则 422 CREDIT_MISMATCH（M1 T12c 携带项闭环）。
21. **R11c 分类接线**：分类变更后受影响发送前任务逐个 CancelPreSend（auditEnv 按任务环境）+按环境重入队 inbound-match；已发送任务不动（AT-057；M1 携带项"trace-policy 接线/affectedNonTerminalTasks 恒 0"闭环）。
22. **R11d 供应商解除守卫**：org 级计数改为 inbound_match.supplier_dwbh 关联计数（排除终态；M1 携带项 2 闭环）。
23. **R11e 请求体上限**：Kestrel MaxRequestBodySize=104_857_600（与 import.maxFileBytes 上界一致）；ImportStage 审计 env 改按作业环境（M1 携带项 2/3 闭环）。
24. **R13a 数量口径**：allocatedQuantity/remainingQuantity 一律后端 SQL 聚合（RESERVED+COMMITTED），前端仅 round6 显示，不做前端 decimal 运算。
25. **R13b 无捷径**：无"视为已入库"、无手动上传按钮；确认提示"符合资格后将自动上传"。
26. **R13c 冻结判定（UI）**：task 存在且 state∉{Blocked,Ready,Cancelled}→只读；仅 UI 提示，后端每次写操作独立复核。
27. **R13d 冲突提示**：409→"版本冲突或已冻结：数据可能已被他人修改，请刷新后重试"+自动重载（AT-067）；422 fieldErrors 逐行显示。
28. **R13e 202 语义**：rematch/redownload 返回 jobId 提示，不轮询作业结果（作业状态页 M4）。
29. **R14a 改绑入口**：ResolveDialog 按钮仅 CONFLICT/REVIEW_REQUIRED/CANDIDATE/UNRESOLVED 且有映射行时显示。
30. **R14b 设置目录**：新 catalog 键（top.billPrefix/top.billMaxLength/upstream.pollSeconds 等）自动出现在设置表，无需逐键 UI。
31. **R14c 预算展示**：限值 NULL 显示"—（不设帽）"；暂停列表只读（恢复动作 M3 交付）。
32. **R15a 门禁诚实**：门禁报告只写真实运行输出；2008 R2 降级声明沿用 M0/M1 口径；矩阵不得引用不存在的测试。
33. **R15b 契约以实现为准**：openapi 旧桩（AllocationRequest.sourceLineKey/expectedVersion、ConfirmAllocationRequest.allocationVersion、LIST_ONLY/DETAIL_PARTIAL 等）按 M2 实现回写，不改实现迁就旧桩。
34. **R15c 合同候选**：top-contract.md 全部条目仍 _verified:false；G-TOP/G-ERP 取证前 LIVE 通道保持禁用。

## 携带项（M3 必须处理）

- upload-dispatch / bill-query 处理器（M0 表结构与恢复路径就绪；M2 任务只到 Blocked/Ready，不发送）。
- 逐码结果（含 UNKNOWN）/部分成功受控补救/受理未知回查决策（spec §2.6 转换表全边落地）。
- 渠道暂停恢复 UI 与审批动作（M2 只读展示；ChannelPauseService.ResumeAsync 已就绪）。
- 限流限值实数：G-TOP 取证后填 top_rate_budget day/minute/second_limit（M2 全部 NULL=不设帽，仅本地退避保障）。
- 消费者退货 116 / 采购退货 202 前序校验与首次人工确认（G-RETURN）。
- 零售核验管道（绑定来源版本/码集/规则版本；UNKNOWN 阻断；EXEMPT 按依据跳过）。
- 多 part 拆分（限额取证后；M2 单 part，超长 bill_code 抛错不截断）。
- src: 触发接线（SourceSyncService 注入 IJobQueue 的构造变更，M3 随零售管道一并做）。

## 携带项（M4 / 交付前）

- ImportWizard 预览分页（M1 携带项 4，M2 未做，如实保留"仅展示前 50 行"标注）。
- 2008 R2 真实实例补验全部集成测试（AT-070；替换 M0/M1/M2 门禁报告 ①③ 证据）。
- G-ERP 取证清单（ksoa-adapter-contract.md §3 + §5 ywdjbh）；G-TOP 合同验证（设计附录 B）；G-POS/G-RETURN/G-OPENING/G-DEPLOY。
- 十页闭环剩余页（POS 销售/退货/码台账/上报对账/工作台增强/审计导出）、日对账三方比对、故障注入与连续运行 ≥7 天试运行、备份恢复演练。
- 作业状态页（202 jobId 的后续查询入口）。

## 环境事实（不声称未验证能力）

- 集成测试运行于本机 SQL Server 2019（15.0.2000.5），TRACEFLOW_TEST_DB_ALLOW_NEWER=1 用户授权降级；双 collation（默认 + TRACEFLOW_TEST_DB_BIN2=1）各跑一遍。
- 上游下载/企业同步/102 载荷全部 fixture 通道（fixtures/top/*，_verified:false）；RealTopGateway 抛 TopNotCertifiedException；LIVE 环境 upstream/enterprise/redownload 一律 422 TOP_NOT_CERTIFIED。
- KSOA 侧 fixture-only（FixtureKsoaAdapter）；ywdjbh 为候选字段仅从 raw_header JSON 读取；RealKsoaAdapter 仍抛 AdapterNotCertifiedException。
- 浏览器人工验证使用独立库 TraceFlow_M2Manual（保留不删）；若执行环境无浏览器工具，须在门禁报告如实声明"浏览器验证未执行"，不得以构建通过冒充 UI 验证。
- **补充（Task 15 执行期事实）**：浏览器验证**已执行**（控制器执行，chrome-devtools MCP）：Task 13 流程 1–8 与 Task 14 核验清单 1–6（含 §4.7 设置面板与商品/供应商改绑）；证据见本目录 `task-13-report.md`、`task-13-fix1-report.md`、`task-14-report.md` §8 及截图目录 `t13-shots/`、`t14-shots/`。门禁报告据此引用，不重复声称本次新跑。
