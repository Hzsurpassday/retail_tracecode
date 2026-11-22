# TraceFlow 需求追踪矩阵（M0）

基线：PRD V1.0（`docs/PRD-TraceFlow-单门店-V1.0.md`）+ 验收清单（AT-*）+ 开发交接说明。代码基线：分支 `m0-foundation`，HEAD `73f3d6f`（Task 1–14）+ 本 Task 15 文档。

**门禁证据口径（用户授权降级裁决）**：M0 集成测试运行于本地 SQL Server 2019（15.0.2000.5），经 `TRACEFLOW_TEST_DB_ALLOW_NEWER=1` 显式授权降级；**2008 R2 真实实例验证未完成，交付前必须真实 2008 R2 补验全部集成测试（AT-070）**。因此凡涉及数据库集成的证据一律标注"SQL 2019 集成（授权降级；2008 R2 待补验）"，不声称 2008 R2 运行记录。`Sql2008CompatibilityTests` 静态语法门禁已运行并通过（语法层通过，运行时 2008 R2 待补验）。

验证运行（详见 `docs/delivery/M0-gate-report.md`）：Release 构建 0 警告 0 错误；单元测试 84/84 通过；集成测试 44/44 通过（2019 降级）；前端 `tsc -b && vite build` 0 错误。

测试名均为实际存在的测试（`traceflow/tests/TraceFlow.UnitTests`、`traceflow/tests/TraceFlow.IntegrationTests`）；AT-* 为验收清单用例编号，"计划"表示该用例尚未实现。文件路径相对仓库根。

| 需求 ID | 实现模块/物理对象 | 测试 | 门禁证据 | 阶段/状态 |
| --- | --- | --- | --- | --- |
| SCOPE-01 | 0001_core.sql organization（单 active 门店）；Host/Program.cs（--web-only/--worker-only 单 Web+Worker）；Web 壳无多门店 UI | MigratorTests.Core_tables_exist_and_single_active_org_enforced | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（基础） |
| SCOPE-02 | docs/contracts/adapter-facts.md、source-mapping.md（KSOA 候选契约）；真实 ERP 连接未实现 | — | G-ERP 未取证 | M0 文档完成；真实同步 M1 未实现 |
| SCOPE-03 | db/tools/TraceFlow.DbMigrator/Program.cs（只允许连接 TraceFlow 目标库）；MigrationRunner.cs；独立业务库 DDL | MigratorTests（3 用例）；AT-001（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（迁移工具约束；ERP 只读账号验证 M1） |
| SCOPE-04 | adapter-facts.md TOP 合同行（仅零售权限包）；代码无 WES/saveent/全国全量下载能力 | — | G-TOP 未取证 | M0 文档完成；平台能力 M2/M3 未实现 |
| SCOPE-05 | Web/src/pages 仅 LoginPage/DashboardPage，无独立扫码页面 | npm run build（tsc 0 错误） | — | M0 完成（壳）；十页闭环 M4 |
| SCOPE-06 | （M2 计划：IN-06/07 完全匹配自动上传）；upload_task PURCHASE_IN_102 类型 DDL 就绪 | AT-024/031（计划） | G-ERP/G-TOP 未取证 | 未实现（M2） |
| SCOPE-07 | （M3 计划：支付后异步同步→核验→自动上传，查询超时等待） | AT-037/038（计划） | G-POS/G-TOP 未取证 | 未实现（M3） |
| SCOPE-08 | 0003_master.sql product_mapping.is_locked/state CHECK；双向有效唯一索引；自动匹配与锁定逻辑未实现 | MasterConstraintTests（2 映射唯一用例） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 DDL 完成；匹配逻辑 M1/M2 未实现 |
| SCOPE-09 | docs/contracts/adapter-facts.md POS 采码行 | — | G-POS 待取证 | 销售生产上报禁用 |
| SCOPE-10 | 0002_source.sql source_snapshot 版本不可变 + completeness_status SOURCE_CHANGED；检测逻辑未实现 | MasterConstraintTests.Snapshot_version_unique_and_history_appendable | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 DDL 完成；变化检测 M1 未实现 |
| DEF-01 | （M3 计划：整任务上报，禁按正常行拆单） | AT-038（计划） | G-POS/G-TOP 未取证 | 未实现（M3） |
| DEF-02 | （M3 计划：退货首次人工确认）；openapi.yaml /returns/{id}/confirm 契约已列 | AT-043/044（计划） | G-RETURN 未取证 | 未实现（M3） |
| DEF-03 | adapter-facts.md 采购退货行（来源未给出，显示未接入）；upload_task CK_task_type 含 PURCHASE_RETURN_202（仅 DDL） | AT-045（计划） | G-RETURN 未取证 | M0 文档完成；首次上报禁用 |
| DEF-04 | Domain/Uploads/UploadStateMachine.cs（无"强制成功"边）；API 无放行后门（API-03） | UploadStateMachineTests.Illegal_edge_rejected、Succeeded_is_terminal_and_every_other_state_has_exit | — | M0 完成（状态机层）；业务放行校验 M2/M3 |
| DEF-05 | Application/Security/PermissionCatalog.cs（13 权限项模板）；Host/Security/AdminInitializer.cs（最小初始权限） | PermissionAuditTests.Grant_check_revoke_and_duplicate_grant_idempotent | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| DEF-06 | 0002_source.sql source_line CK_line_event（SALE/CONSUMER_RETURN/NO_EVENT）；fixtures/ksoa/sale-mixed-return.json；拆分逻辑未实现 | AT-042（计划） | G-ERP 未取证 | M0 DDL+fixture 完成；事件拆分 M3 未实现 |
| DEF-07 | （M4 计划：UTF-8 CSV 导出、防公式注入、权限与下载审计） | AT-074（计划） | — | 未实现（M4） |
| AUTH-01 | Host/Security/PermissionHandler.cs + RequirePermissionAttribute.cs + Program.cs 逐请求策略 | ApiGateTests.Anonymous_mutation_rejected_401、No_permission_confirm_returns_403_and_db_unchanged（AT-065）、Dashboard_requires_trace_read_and_returns_real_counts | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| AUTH-02 | AdminInitializer.cs（--init-admin 交互式密码，无默认公开密码）；Program.cs Cookie .tf.auth（HttpOnly/Strict）+ 全局 CSRF 中间件；AuthEndpoints.cs 登录自验 CSRF、失败留痕 | ApiGateTests.Missing_csrf_rejected、Authenticated_mutation_without_csrf_header_rejected_400、Overlength_username_login_401_and_failure_audit_actor_clamped | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（初始化方式入部署文档属 G-DEPLOY/M4 缺口） |
| AUTH-03 | Infrastructure/Security/AuditLog.cs + audit_log（actor/时间/对象/原因/前后摘要/requestId/rule_version/env）；AuthEndpoints/AdminEndpoints 全留痕 | PermissionAuditTests.Audit_append_stores_before_after_and_actor | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（已实现动作；全业务动作审计随 M1–M4） |
| SRC-01 | Program.cs 连接串仅部署配置（不入 Git）；DbMigrator 只连目标库；DpapiProtector 凭据加密；源库只读适配器未实现 | DpapiProtectorTests（2 用例）；AT-001（计划） | G-ERP 未取证 | M0 部分完成（配置/工具约束）；只读同步 M1 未实现 |
| SRC-02 | source-mapping.md 候选组合键；0002 UQ_snapshot_key_version/UQ_line_key + ingest_exception；fixtures/ksoa/sale-missing-plh.json；键唯一性取证未做 | AT-002/003（计划） | G-ERP 未取证（NULL/重复/跨日复用查询待做） | M0 DDL+文档完成；接入验证 M1 未实现 |
| SRC-03 | source-mapping.md/adapter-facts.md（主档未给出，不猜表名）；product DF trace_classification=UNKNOWN 阻断 | AT-011（计划） | G-ERP 未取证 | 未实现（M1）；UNKNOWN 阻断 DDL 就绪 |
| SRC-04 | Domain/Codes/TraceCodeNormalizer.cs + CodeFormat.cs（raw+normalized，不截断、非 8 开头=Unsupported） | TraceCodeNormalizerTests（7 用例：Valid_8_prefix_20_digit_supported、Leading_zeros_preserved_as_string、Edge_whitespace_and_scanner_control_chars_removed、Overlong_code_not_truncated、Non_8_prefix_is_unsupported_not_absent、Internal_control_char_unsupported_not_silently_stripped、Empty_is_empty_not_exempt） | — | M0 完成（接入在 M1） |
| SRC-05 | Domain/Quantities/Qty.cs + UnitRatio.cs（decimal(18,6)、精确有向换算、超域抛错）；source_line expiry_precision CHECK DAY/MONTH/UNKNOWN | QtyTests（8 用例）、UnitRatioTests（5 用例） | — | M0 完成（值对象层；接入换算 M1/M2） |
| SYNC-01 | （M1 计划：重叠窗口/稳定排序/摘要去重/回扫）；source_snapshot.content_hash、sync_checkpoint DDL 就绪 | AT-004（计划） | G-ERP 未取证 | 未实现（M1） |
| SYNC-02 | （M1 计划：已提交读、两轮摘要一致+稳定期）；completeness_status 五态 CHECK 就绪 | AT-005（计划） | G-ERP 未取证 | 未实现（M1） |
| SYNC-03 | 0004 download_progress（列表/详情分页分离，bill_id=0 列表级）DDL 就绪；分页落库同事务逻辑未实现 | AT-006/022（计划） | G-TOP 未取证 | M0 DDL 完成；下载逻辑 M2 未实现 |
| SYNC-04 | （M1 计划：轮询/回扫窗口参数、持久检查点补拉）；sync_checkpoint DDL 就绪 | AT-007（计划） | G-ERP 未取证 | 未实现（M1） |
| SYNC-05 | （M1 计划：SOURCE_CHANGED 保留旧快照+异常+暂停依赖）；DDL 就绪 | AT-057（计划） | G-ERP 未取证 | 未实现（M1） |
| MD-01 | 0003 product trace_classification CHECK + classification_reason；分类变更/影响重算逻辑未实现；openapi TracePolicyRequest 契约已列 | AT-009/010/011（计划） | — | M0 DDL 完成；分类管理 M1 未实现 |
| MD-02 | 0003 platform_drug.source_kind/source_time（积累来源标记）；积累逻辑未实现 | — | G-TOP 未取证 | 未实现（M1/M2） |
| MD-03 | 0003 import_batch UQ_import_intent + import_row（行错误可追查）；暂存/预览/提交流程未实现 | — | — | M0 DDL 完成；导入 M1 未实现 |
| MATCH-01 | （M1 计划：五字段一致+双向唯一才 AUTO_CONFIRMED）；product_mapping DDL 就绪 | AT-012/014（计划） | — | 未实现（M1） |
| MATCH-02 | （M1 计划：条码仅辅助、单字段不自动绑定）；approval_no_raw/norm 双列就绪 | AT-013（计划） | — | 未实现（M1） |
| MATCH-03 | 0003 UX_pmap_active_local/UX_pmap_active_platform（双向有效唯一）；drug_prefix_index IX_drug_prefix 非唯一 | MasterConstraintTests.Two_active_mappings_to_same_platform_drug_rejected、Two_active_mappings_for_same_local_product_rejected；AT-015/018（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 DDL+约束集成完成；匹配算法 M1 未实现 |
| MATCH-04 | 0003 product_mapping/supplier_mapping 7 态 CHECK + is_locked + version；状态流转逻辑未实现 | AT-016/017（计划） | — | M0 DDL 完成；流转 M1 未实现 |
| MATCH-05 | （M1 计划：确认显示差异/证据/影响，强冲突禁忽略）；openapi ConfirmMappingRequest 契约已列 | AT-015（计划） | — | 未实现（M1） |
| ENT-01 | （M1 计划：证号精确匹配+审核枚举核对）；supplier_mapping DDL 就绪 | AT-019（计划） | G-TOP 未取证 | 未实现（M1） |
| ENT-02 | 0003 platform_enterprise_version UQ_ent_version(ref_ent_id, ent_id)（稳定身份+名称版本）；快照保护逻辑未实现 | AT-020/021（计划） | G-TOP 未取证 | M0 DDL 完成；逻辑 M1/M2 未实现 |
| ENT-03 | 代码无 saveent 调用能力；supplier_mapping 无默认供应商路径 | AT-019（计划） | G-TOP 未取证 | M0 完成（负约束）；匹配 M1 未实现 |
| UP-01 | 0004 upstream_bill UQ_upbill_identity（主体组合唯一）；listupout 下载未实现 | CodeConstraintTests.Same_bill_no_different_upstream_subjects_not_merged；AT-022/023（计划） | SQL 2019 集成（授权降级；2008 R2 待补验）；G-TOP 未取证 | M0 DDL+约束完成；下载 M2 未实现 |
| UP-02 | 0004 upstream_bill_line/upstream_bill_code（批次/数量/单位/码/包装层级/来源版本）；保存逻辑未实现 | AT-022（计划） | G-TOP 未取证 | M0 DDL 完成；M2 未实现 |
| IN-01 | （M2 计划：触发点+一键重排只入队）；openapi /inbounds/{id}/rematch 契约已列；outbox_job 入队能力就绪 | AT-024（计划） | — | 未实现（M2） |
| IN-02 | （M2 计划：外部单号优先+7 天窗口候选） | AT-024/025（计划） | — | 未实现（M2） |
| IN-03 | （M2 计划：AMBIGUOUS 多解阻断）；inbound_allocation DDL 就绪 | AT-026（计划） | — | 未实现（M2） |
| IN-04 | （M2 计划：多对多行级/码级分配、大码展开）；inbound_allocation/allocation_code DDL 就绪 | AT-027/028（计划） | — | 未实现（M2） |
| IN-05 | （M2 计划：全 EXEMPT=NOT_REQUIRED、UNKNOWN 阻断）；upload_task match_state 列就绪 | AT-030（计划） | — | 未实现（M2） |
| IN-06 | （M2 计划：ELIGIBLE 全条件资格检查）；Qty/UnitRatio 精确换算值对象就绪 | AT-024/029（计划）；QtyTests.Inexact_conversion_throws_not_rounds | — | 未实现（M2；换算基础 M0 完成） |
| IN-07 | （M2 计划：完全匹配自动建 102 任务、确认补配重验） | AT-031（计划） | G-TOP 未取证 | 未实现（M2） |
| IN-08 | Infrastructure/Jobs/JobQueue.cs EnqueueAsync（调用方事务内入队）；code_claim active 唯一；资格/冻结/快照同事务逻辑未实现 | JobQueueTests.Enqueue_inside_caller_transaction_rolls_back_together；CodeConstraintTests.Active_claim_unique_per_store_env_code_scope；AT-032/033（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 部分完成（事务入队+占用唯一）；完整业务事务 M2 |
| IN-09 | 0005 inbound_allocation CK_alloc_state（TENTATIVE/RESERVED/COMMITTED/CANCELLED）；取消/释放规则逻辑未实现 | AT-034（计划） | — | M0 DDL 完成；流转 M2 未实现 |
| SALE-01 | （M3 计划：REQUIRED shl=1 恰一码、plh 分行不聚合）；source_line CK_line_event 就绪 | AT-035/036（计划） | G-POS 未取证 | 未实现（M3） |
| SALE-02 | （M3 计划：逐码平台核验绑定摘要/规则版本）；verification 表+UX_verif_current 就绪 | AT-037（计划） | G-POS/G-TOP 未取证 | 未实现（M3） |
| SALE-03 | （M3 计划：任一码未通过整体阻断） | AT-038（计划） | G-POS 未取证 | 未实现（M3） |
| SALE-04 | （M3 计划：WAIT_DEPENDENCY+历史平台证据）；upload_task dependency_state 列就绪 | AT-039/040（计划） | G-POS/G-OPENING 未取证 | 未实现（M3） |
| SALE-05 | （M3 计划：核验通过自动建 321 任务）；upload_task RETAIL_321 类型 DDL 就绪 | AT-041（计划） | G-POS/G-TOP 未取证 | 未实现（M3） |
| RET-01 | 0002 CK_line_event + fixtures/ksoa/sale-mixed-return.json（正负分事件不抵消）；识别逻辑未实现 | AT-042（计划） | G-RETURN 未取证 | M0 DDL+fixture 完成；M3 未实现 |
| RET-02 | （M3 计划：人工首次确认+原销售候选唯一）；openapi ReturnConfirmRequest 契约已列 | AT-043/044（计划） | G-RETURN 未取证 | 未实现（M3） |
| RET-03 | adapter-facts.md 采购退货行（未接入）；PURCHASE_RETURN_202 仅 DDL 类型 | AT-045（计划） | G-RETURN 未取证 | 未实现（可选适配模块）；首次上报禁用 |
| RET-04 | （M3 计划：复售依平台实时核验流向）；code_event 多事件+前序依赖 DDL 就绪 | AT-046（计划）；CodeConstraintTests.Same_code_multiple_events_allowed_but_event_code_unique | G-RETURN/G-TOP 未取证 | 未实现（M3；事件链 DDL M0 完成） |
| TOP-01 | （M2/M3 计划：error_response 与业务双层解析、脱敏留证）；fixtures/top/error-response.json（_verified:false，Mock 形状非平台事实）；upload_attempt 留证列就绪 | AT-047（计划） | G-TOP 未取证 | 未实现（M2/M3） |
| TOP-02 | （M3 计划：result_type/sub_process_flag 枚举判定）；fixtures/top/result-type-1.json、partial-flag-52.json（Mock 形状）；UploadStateMachine 12 态就绪 | AT-048/049/050（计划）；UploadStateMachineTests（3 用例） | G-TOP 未取证 | 未实现（M3；状态机 M0 完成） |
| TOP-03 | （M3 计划：部分成功证据+失败码分页核对、UNKNOWN 保留）；code_result 三态 CHECK 就绪 | AT-049/051（计划）；UploadConstraintTests.Code_result_unknown_allowed | G-TOP 未取证 | 未实现（M3；三态 DDL M0 完成） |
| TOP-04 | （M3 计划：整单解析模式参数验证后开启）；生产通道禁用 | AT-049（计划） | G-TOP 未取证 | 未实现（M3） |
| IDEM-01 | 0006 upload_task UQ_task_event(org_id, env, event_key)；事件键生成/sourceHash 版本逻辑未实现 | UploadConstraintTests.Business_event_key_unique、Mock_task_with_same_event_key_isolated；AT-041（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（唯一约束层）；事件键逻辑 M2/M3 |
| IDEM-02 | Domain/Billing/BillCodeGenerator.cs + BillCodeContract.cs + upload_part.UQ_part_billcode | BillCodeGeneratorTests（9 用例，含 Base35 碰撞回归与 40 万码唯一性扫描）、UploadConstraintTests.Bill_code_unique_and_part_index_unique | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| IDEM-03 | 0006 upload_part（part_index/split_rule_version/code_set_hash/payload_hash）；确定性拆分逻辑未实现 | AT-056（计划） | G-TOP 未取证（限额待验证） | M0 DDL 完成；拆分 M3 未实现 |
| IDEM-04 | Infrastructure/Jobs/SendRecoveryService.cs + upload_attempt（DISPATCHING 持久尝试/fencing_token）；HTTP 发送动作未实现 | SendRecoveryTests.Expired_dispatch_becomes_acceptance_unknown_and_query_only、Valid_lease_dispatch_untouched、Recovery_idempotent_second_run_no_duplicate_query_job、Stale_receipt_preserved_as_evidence_without_state_change；AT-052/053（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（恢复路径）；上传动作 M3 |
| IDEM-05 | SendRecoveryService.cs（关死重传向量）+ UploadStateMachine RetryWait 边；未受理判定/观察期逻辑未实现 | SendRecoveryTests.Pending_dispatch_job_failed_on_recovery_closing_reupload_vector；UploadStateMachineTests.Legal_edge_transitions；AT-054/055（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 部分完成；受控重试判定 M3 未实现 |
| JOB-01 | Infrastructure/Jobs/JobQueue.cs + outbox_job（租约/fencingToken/nextRunAt）；Host/WorkerHostedService.cs | JobQueueTests（9 用例，含 Two_workers_claim_same_job_only_one_wins 双连接并发、Complete_with_correct_token_succeeds_stale_token_rejected、Expired_lease_reclaimed_token_increments_old_worker_rejected、Enqueue_same_intent_idempotent、Reschedule_keeps_token_guard、Claim_only_returns_jobs_of_requested_env、Fail_guards_owner_token_and_running_state）；AT-058（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| JOB-02 | Infrastructure/Jobs/SendRecoveryService.cs（失租→受理未知→只回查；业务占用与租约分离） | SendRecoveryTests（5 用例）、JobQueueTests.Recover_expired_leases_skips_upload_dispatch；AT-059（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（上传动作恢复在 M3） |
| JOB-03 | （M3 计划：AppKey/接口/主体共享调用预算、持久 nextAllowedAt） | AT-060（计划） | G-TOP 未取证 | 未实现（M3） |
| JOB-04 | （M3 计划：回查退避参数、24h 转人工）；outbox_job next_run_at/attempt_count/max_attempts DDL 就绪 | AT-061（计划） | G-TOP 未取证 | 未实现（M3） |
| JOB-05 | （M1/M3 计划：通道暂停/服务到期阻断）；app_setting DDL 就绪；Host env 配置大小写不敏感、启动规范化为 LIVE/MOCK（默认 MOCK；LIVE 须显式开启） | — | G-TOP/G-DEPLOY 未取证 | 未实现（M1/M3） |
| UI-01 | Host/Endpoints/DashboardEndpoints.cs + Web/src/pages/DashboardPage.tsx（真实 DB 计数、只读不触发平台请求） | ApiGateTests.Dashboard_requires_trace_read_and_returns_real_counts；AT-073（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（基础工作台）；深链/全待办 M4 |
| UI-02 | （M4 计划：商品资料与匹配页）；openapi /products 等契约已列 | — | — | 未实现（M4） |
| UI-03 | （M4 计划：供应商资料与匹配页） | — | — | 未实现（M4） |
| UI-04 | （M4 计划：上游出库单页） | — | — | 未实现（M4） |
| UI-05 | （M4 计划：入库关联与上传页） | — | — | 未实现（M4） |
| UI-06 | （M4 计划：POS 销售数据页） | — | G-POS 未取证 | 未实现（M4） |
| UI-07 | （M4 计划：退货数据页） | — | G-RETURN 未取证 | 未实现（M4） |
| UI-08 | （M4 计划：追溯码台账页）；code_event 事件链 DDL 就绪 | — | — | 未实现（M4） |
| UI-09 | （M4 计划：上报与对账中心页） | — | — | 未实现（M4） |
| UI-10 | （M4 计划：系统设置页）；openapi /settings/* 契约已列 | — | — | 未实现（M4） |
| UI-11 | （M2/M4 计划：补配逐行差异+版本确认 409 刷新）；openapi AllocationRequest/expectedVersion 契约已列 | AT-067（计划） | — | 未实现（M2/M4） |
| UI-12 | Web/src/pages/DashboardPage.tsx（10 秒轮询本系统，不直调 TOP）+ components/EnvBadge.tsx（LIVE/MOCK 标识）+ 状态由后台落库展示 | npm run build（tsc 0 错误+vite 产物）；AT-073（计划） | — | M0 完成（壳级）；部分成功标识/失败码入口 M3/M4 |
| UI-13 | （M4 计划：批量预览+导出后台任务与下载审计） | AT-074（计划） | — | 未实现（M4） |
| DB-01 | db/migrations/0001–0006（外部 ID/码 nvarchar + Latin1_General_BIN2；内部 bigint identity） | Sql2008CompatibilityTests、MasterConstraintTests、CodeConstraintTests | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| DB-02 | db/migrations（datetime2(3)/rowversion/decimal(18,6)）；Domain/Quantities（比例精确分子分母） | QtyTests、UnitRatioTests；Sql2008CompatibilityTests | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| DB-03 | db/migrations/*.sql + 内嵌 SQL（ROW_NUMBER 分页，无高版本语法） | Sql2008CompatibilityTests.No_high_version_syntax_in_any_migration_or_embedded_sql、No_high_version_syntax_in_migration_scripts_full_constraint_list、Row_number_pagination_works_on_2008r2；AT-070（2008 R2 实 instance 待补验） | 静态语法门禁通过；SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（语法层）；2008 R2 运行时门禁未满足 |
| DB-04 | 过滤唯一索引 UX_pmap_active_* / UX_smap_active_* / UX_claim_active / UX_alloc_pair_active / UX_verif_current；soft delete 不复用成功单号（UQ_part_billcode 无状态过滤） | MasterConstraintTests（2 用例）、CodeConstraintTests.Active_claim_unique_per_store_env_code_scope、Released_claim_allows_new_active_claim、UploadConstraintTests.Bill_code_unique_and_part_index_unique | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（DDL+约束集成）；替换事务逻辑 M1/M2 |
| DB-05 | 0005 code_event（事件链/direction/predecessor_event_id）+ code_claim（event_scope 隔离）；父子互斥事务逻辑未实现 | CodeConstraintTests.Same_code_multiple_events_allowed_but_event_code_unique、Active_claim_unique_per_store_env_code_scope；AT-046（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（DDL+占用唯一）；父子互斥事务 M2 |
| API-01 | Host/Security/ApiErrors.cs（统一 code/message/fieldErrors/retryable/requestId）；各端点 data/requestId 包络；外部 ID/数量/rowversion 字符串（openapi.yaml） | ApiGateTests（7 用例）；QtyTests（数量字符串精度基础） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（已实现端点；全量契约 M1+） |
| API-02 | Host/Security/IdempotencyExecutor.cs + Infrastructure/Security/IdempotencyStore.cs + idempotency_record（同事务） | IdempotencyStoreTests.Same_key_same_hash_replays_same_key_different_hash_conflicts、Rollback_removes_idempotency_record_with_business；ApiGateTests.Idempotent_user_create_same_key_same_body_replays_different_body_409（AT-066） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成 |
| API-03 | M0 仅 7 个端点（auth/admin/dashboard/health），无任意 SQL/平台代理/改结果/reset/换号强推/跳过核验后门；UploadStateMachine 无强制成功 | ApiGateTests（负向门禁）；UploadStateMachineTests.Illegal_edge_rejected；AT-068（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（负约束） |
| REC-01 | （M4 计划：日对账集合+摘要比对）；reconciliation_run/difference DDL 就绪 | AT-062/063（计划） | G-TOP 未取证 | 未实现（M4） |
| REC-02 | 0006 reconciliation_difference CK_diff_type（11 类全枚举）+ severity/owner_role/next_action/证据列；生成逻辑未实现 | AT-062（计划） | — | M0 DDL 完成；M4 未实现 |
| REC-03 | （M4 计划：INCOMPLETE 口径、查不到不立即重传）；reconciliation_run CK_recon_status 含 INCOMPLETE | AT-064（计划） | G-TOP 未取证 | 未实现（M4） |
| AUD-01 | Infrastructure/Security/AuditLog.cs + audit_log（不可变追加，无业务删除入口）；AuthEndpoints/AdminEndpoints/AdminInitializer 留痕 | PermissionAuditTests.Audit_append_stores_before_after_and_actor；AT-069（计划） | SQL 2019 集成（授权降级；2008 R2 待补验） | M0 完成（已实现动作；映射/分配/导出等审计随 M1–M4） |
| NFR-01 | docs/adr/ADR-0001-dotnet8-dapper-sql2008r2.md；Host/Program.cs UseWindowsService + --worker-only/--web-only；独立业务库 | dotnet build TraceFlow.sln -c Release（0 警告 0 错误）；Sql2008CompatibilityTests（语法层） | 服务器兼容证据：SQL 2019 降级运行（2008 R2 待补验）；G-DEPLOY 未取证 | M0 完成（ADR+宿主）；目标环境兼容实证待补 |
| NFR-02 | Web/src（React 18 + TS：独立请求、轮询清理、无组件内定义组件、交互在事件处理） | npm run build（tsc -b 0 错误 + vite 产物） | — | M0 完成（壳）；长表分页等随 M4 页面 |
| NFR-03 | （M4 计划：10000 商品/100000 销售行基准 p95≤2s） | AT-075（计划） | — | 未实现（M4）；M0 不声称性能 |
| NFR-04 | （M1 计划：ERP 查询页≤500 头、慢查询记录） | AT-075（计划） | G-ERP 未取证 | 未实现（M1） |
| NFR-05 | Infrastructure/Security/DpapiProtector.cs + app_setting CK_app_setting_secret（密文与明文互斥）；ApiErrors 不回显连接串/堆栈；HealthEndpoints 不回显连接细节 | DpapiProtectorTests.Roundtrip_under_current_user、Tampered_ciphertext_throws_no_fallback；AT-071（计划） | — | M0 完成 |
| NFR-06 | DashboardEndpoints 基础计数（积压/最近活动时间）；完整指标/站内告警未实现 | ApiGateTests.Dashboard_requires_trace_read_and_returns_real_counts | — | M0 部分完成；完整告警 M4 未实现 |
| NFR-07 | （M4/G-DEPLOY 计划：备份恢复/配置备份/密钥恢复文档与演练） | AT-072（计划） | G-DEPLOY 未取证 | 未实现（M4） |
| NFR-08 | （M4 计划：恢复后暂停上传→平台对账修复→再开放） | AT-072（计划） | G-DEPLOY 未取证 | 未实现（M4） |
| G-ERP | docs/contracts/adapter-facts.md + source-mapping.md + fixtures/ksoa/*（候选，未取证） | — | 未取证 | 真实 ERP 同步禁用（M1 通道） |
| G-TOP | docs/contracts/fixtures/top/*（_verified:false，Mock 形状非平台事实） | — | 未取证 | 真实平台发送禁用（M2/M3 通道） |
| G-POS | adapter-facts.md POS 采码行（SCOPE-09 冲突待样例裁决） | — | 未取证 | 销售生产自动上报禁用 |
| G-RETURN | adapter-facts.md 消费者退货/采购退货/负数退货行 | — | 未取证 | 未验证类型不生产上报 |
| G-OPENING | （启用日/历史回扫/期初码证据/平台流转对账未取证） | — | 未取证 | 期初歧义业务阻断，不虚构历史采购 |
| G-DEPLOY | （目标 OS/服务账号/HTTPS/备份恢复/主体服务期未核实）；Program.cs Cookie SecurePolicy 注释标注上线强制 HTTPS | — | 未取证 | 不启用生产发送 |

## 覆盖统计

- 需求行合计 **114**：SCOPE 10、DEF 7、AUTH 3、SRC 5、SYNC 5、MD 3、MATCH 5、ENT 3、UP 2、IN 9、SALE 5、RET 4、TOP 4、IDEM 5、JOB 5、UI 13、DB 5、API 3、REC 3、AUD 1、NFR 8、G-* 6。
- M0 已实现（含 DDL/值对象/壳层部分完成）行均给出实际文件与实际测试名；后续阶段行标注计划归属与"未实现"；门禁行全部"未取证"并写明对应真实通道禁用。
- 未声称任何未验证能力：TOP fixture 为 Mock 形状；2008 R2 运行时兼容未验证；性能/连续运行/备份恢复属 M4，M0 不声称。