# 数据字典（PRD §11.2 逻辑实体 → 物理表映射）

物理表名与关键约束已逐一核对实际迁移脚本 `traceflow/db/migrations/0001_core.sql` … `0006_upload.sql` + M1 增量 `0007_m1_source_master.sql` + M2 增量 `0008_m2_upstream_allocation.sql` + M3 增量 `0009_m3_sale_upload.sql`（HEAD 以实际 M3 末提交为准）。
**核对结论：任务简报映射表中的全部物理表名与实际 DDL 一致，无需更正**（含 `sync_checkpoint` 与 `download_progress`，两者均真实存在：`sync_checkpoint` 在 0002，`download_progress` 在 0004）。
关键约束列使用实际约束/索引名。通用规则（DB-01/02）：外部 ID/单号/码为 nvarchar + `Latin1_General_BIN2` 排序规则；内部 UTC `datetime2(3)`；并发版本 `rowversion`（序列化为字符串）；数量 `decimal(18,6)`；禁止 float 计量。

| PRD 实体 | 物理表 | 关键约束（实际 DDL） |
| --- | --- | --- |
| Organization/SourceSystem | organization / source_system | 单 active 门店过滤唯一索引 `UX_organization_single_active`（WHERE is_active=1）；`UQ_organization_code`、`UQ_source_system_code` |
| SourceSnapshot/SourceLine | source_snapshot / source_line | 键+版本唯一 `UQ_snapshot_key_version`(source_system_id, source_document_key, version)；行键唯一 `UQ_line_key`(snapshot_id, source_line_key)；历史不可改（不可变版本快照，SYNC-05）；完整性/效期精度/事件类型 CHECK |
| Product/Supplier | product / supplier | 门店+来源+稳定 ID 唯一 `UQ_product_source`(org_id, source_system_id, spid)、`UQ_supplier_source`(org_id, source_system_id, dwbh)；分类 CHECK UNKNOWN/REQUIRED/EXEMPT |
| PlatformDrug/PlatformEnterpriseVersion | platform_drug / platform_enterprise_version | 平台身份(+版本)唯一 `UQ_platform_drug`(platform_drug_id)、`UQ_ent_version`(ref_ent_id, ent_id) |
| ActiveProductMapping/MappingHistory | product_mapping / product_mapping_history | 双向有效唯一（两个过滤索引）`UX_pmap_active_local`(product_id)、`UX_pmap_active_platform`(platform_drug_id)，均 WHERE state IN (AUTO_CONFIRMED, MANUAL_CONFIRMED)；历史独立追加表；7 态 CHECK（MATCH-04） |
| ActiveSupplierMapping/MappingHistory | supplier_mapping / supplier_mapping_history | 同上：`UX_smap_active_local`(supplier_id)、`UX_smap_active_platform`(org_id, enterprise_version_id)，过滤条件同商品映射；历史独立追加表 |
| DrugPrefixIndex | drug_prefix_index | prefix 非唯一（普通索引 `IX_drug_prefix`，MATCH-03：prefix 不是全局药品外键）；conflict_state 记录前缀冲突 |
| UpstreamBill/Line/Code | upstream_bill / upstream_bill_line / upstream_bill_code | 主体组合唯一 `UQ_upbill_identity`(org_id, env, upstream_ref_ent_id, platform_bill_id)；`UQ_upline`(bill_id, platform_line_id)；`UQ_upcode`(bill_id, code_normalized)；完整性/env CHECK |
| Allocation/AllocationCode | inbound_allocation / allocation_code | 有效对唯一 `UX_alloc_pair_active`(source_line_id, upstream_line_id) WHERE state IN (TENTATIVE, RESERVED, COMMITTED)；`UQ_alloc_code`(allocation_id, code_id)；码占用经 code_claim（event_scope='PURCHASE_IN' 的 active 唯一索引，DB-05）；精确分子/分母换算列 |
| TraceCode/CodeRelation/CodeEvent | trace_code / code_relation / code_event | 码身份唯一 `UQ_trace_code`(code_normalized)；真实父子边唯一 `UQ_code_relation`(parent_code_id, child_code_id, relation_source)；事件+码唯一 `UQ_event_code`(org_id, env, code_id, event_type, source_line_id)，事件非全历史唯一；前序依赖 predecessor_event_id 自引用 |
| CodeClaim | code_claim | active 唯一（含 env 隔离）`UX_claim_active`(org_id, env, code_id, event_scope) WHERE claim_state='ACTIVE'；释放留 released_by/release_evidence |
| Verification | verification | 当前有效摘要唯一（is_current 过滤）`UX_verif_current`(org_id, env, event_key) WHERE is_current=1；source_hash/code_set_hash/rule_version 绑定；7 态 CHECK |
| UploadTask/UploadPart | upload_task / upload_part | 事件键唯一 `UQ_task_event`(org_id, env, event_key)；平台号唯一 `UQ_part_billcode`(org_id, env, bill_code)；part index 唯一 `UQ_part_index`(task_id, part_index)；12 态 PascalCase CHECK 与 UploadState 枚举一致；四维正交状态列（source_integrity/match_state/verification_state/dependency_state） |
| UploadAttempt/CodeResult | upload_attempt / code_result | 追加保存 `UQ_attempt`(task_id, attempt_kind, attempt_no)、`UQ_code_result`(part_id, code_id, attempt_id)；三态含 UNKNOWN（CHECK result IN SUCCESS/FAILED/UNKNOWN）；fencing_token、is_stale_evidence 迟到回执留证；raw_response 脱敏后保存 |
| OutboxJob/SyncCheckpoint | outbox_job / sync_checkpoint（上游分页进度另见 download_progress） | 意图幂等 `UQ_job_intent`(org_id, env, job_kind, intent_key)；检查点不领先数据 `UQ_checkpoint`(source_system_id, entity, checkpoint_kind)；download_progress `UQ_download`(org_id, env, job_kind, bill_id)（bill_id=0 为列表级进度，列表/详情分页分离，SYNC-03）；租约/fencing_token 列（JOB-01） |
| ReconciliationRun/Difference | reconciliation_run / reconciliation_difference | 快照不可静默覆盖（每次运行独立行，local_summary/platform_summary 保存）；差异 11 类 CHECK（REC-02 全枚举）；解决留证据 resolution_evidence |
| ImportBatch/ImportRow | import_batch / import_row | 导入意图幂等 `UQ_import_intent`(org_id, file_kind, file_hash)；行唯一 `UQ_import_row`(batch_id, row_number)；行状态 OK/ERROR/APPLIED 可追查（MD-03） |
| User/Role/Permission/AuditLog | app_user / user_permission / audit_log（角色为权限模板，见 PermissionCatalog 与 PRD §3） | 权限唯一 `UQ_user_permission`(user_id, permission)；不可逆哈希（password_hash，PasswordHasher）；审计不可变（audit_log 无 ver/无更新路径，仅追加；actor_kind 支持 SYSTEM/Worker）；用户名唯一 `UQ_app_user_username` |

## 补充：映射表之外的实际物理表（同批迁移交付）

| 物理表 | 迁移 | 用途与关键约束 |
| --- | --- | --- |
| app_setting | 0001 | 设置与加密凭据：`UQ_app_setting_key`；`CK_app_setting_secret`（is_secret=1 时只存 DPAPI 密文 secret_protected，不存明文 value_json，NFR-05） |
| idempotency_record | 0001（0007 变更） | API 幂等（API-02）：`UQ_idempotency_user_key`(idem_key, user_id)（0007 由 `UQ_idempotency_key`(idem_key) 改为用户作用域）；request_hash 绑定规范化请求（含查询串）；与业务同事务提交/回滚 |
| ingest_exception | 0002 | 接入异常（SRC-02 plh 空等）：status CHECK OPEN/RESOLVED；`IX_ingest_exception_open`；解决留证据 |
| schema_version | DbMigrator 运行时创建 | 迁移账本：version 主键、name、checksum(SHA-256 char(64))、applied_at、success；校验摘要不一致即停止（MigratorTests） |

## 0007 迁移增量（M1，`0007_m1_source_master.sql`）

不修改 0001–0006 任何字节（MigrationRunner 校验摘要）；0007 以 ALTER/CREATE 增量演进：

| 对象 | 变更 | 用途/约束 |
| --- | --- | --- |
| source_line.code_format | nvarchar(16)→nvarchar(32) NOT NULL DEFAULT N'EMPTY' | CodeFormat 持久化 token（EMPTY/S8P20/UNSUP），M0 携带项 1 |
| trace_code.format | nvarchar(16)→nvarchar(32) NOT NULL | 同上（无默认约束，直接 ALTER COLUMN） |
| source_snapshot.stabilize_rounds | int NOT NULL DEFAULT 0 | 稳定期轮次计数（SYNC-02） |
| source_snapshot.last_round_at | datetime2(3) NULL | 上一轮观测时间（轮间隔≥5s 判定） |
| idempotency_record | UQ_idempotency_key→UQ_idempotency_user_key(idem_key, user_id) | 幂等键用户作用域（不同用户同键独立） |
| product_mapping | UX_pmap_pair UNIQUE(product_id, platform_drug_id) | 映射对唯一（M1 每商品至多一条映射行，防多行竞态） |
| supplier_mapping | UX_smap_pair UNIQUE(supplier_id, enterprise_version_id) | 同上 |
| import_batch.staged_content | nvarchar(max) NULL | 暂存 CSV 原文，供 import-stage 作业重解析（MD-03） |

CodeFormat token 映射由 `TraceFlow.Domain.Codes.CodeFormatStorage` 统一（禁 enum.ToString 落库）；rowversion 经 `TraceFlow.Domain.Versioning.RowVer.ToHex/FromHex`（16 位大写 hex）。

## 0008 迁移增量（M2，`0008_m2_upstream_allocation.sql`）

不修改 0001–0007 任何字节；0008 以 CREATE/ALTER 增量演进：

| 对象 | 变更 | 用途/约束 |
| --- | --- | --- |
| inbound_match | 新表 | 入库单匹配维度（IN-01..09）：`UQ_inbound_match`(org_id,env,source_snapshot_id)；match_state CHECK NOT_REQUIRED/UNMATCHED/PARTIAL/AMBIGUOUS/CONFLICT/COMPLETE；version int 乐观锁（AT-067）；supplier_dwbh BIN2 NULL；evidence_json 留证 |
| top_rate_budget | 新表 | 持久限流预算（JOB-03/05）：`UQ_rate_budget`(env,budget_key BIN2)；日/分/秒计数+窗口起点+next_allowed_at；limit NULL=本地不设帽（G-TOP 取证前不猜平台限值）；重启不清零 |
| top_channel_pause | 新表 | 授权范围暂停（FAIL_BIZ_AUTH_ERROR→按接口/主体/凭证暂停）：`UX_pause_active`(env,scope_key) WHERE is_active=1；恢复须人工动作留 resumed_by |
| download_progress.window_start/window_end | datetime2(3) NULL | 上游下载按窗口记录列表/详情分页进度（SYNC-03/04；详情未全部成功不得 COMPLETE） |
| upload_task.CK_task_state / upload_part.CK_part_state | CHECK 增加 N'Cancelled' | 行政终态（IN-09）：仅 (Blocked|Ready, AllocationCancelled)→Cancelled；任何 UPLOAD 尝试后不可达；不计入平台成功/失败口径 |

Cancelled 为纯本地行政终态，非平台状态；`UploadStateMachine` 仅增两条边（UploadStateMachineTests 全边覆盖含新边）。

## 0009 迁移增量（M3，`0009_m3_sale_upload.sql`）

不修改 0001–0008 任何字节；0009 以 ALTER/CREATE 增量演进。**注意：`upload_part` / `upload_attempt` / `code_result` 均为 0006 既有表，0009 只改 `upload_part` 三列，不新建任何 `upload_task_part`/`upload_attempt`/`code_result` 变体。**

| 对象 | 变更 | 列/约束明细 | 用途 |
| --- | --- | --- | --- |
| upload_task | 新列 + FK + 筛选索引 | `predecessor_task_id bigint NULL`，`FK_task_predecessor REFERENCES upload_task(id)`；`IX_task_predecessor(predecessor_task_id) WHERE predecessor_task_id IS NOT NULL` | 116 退货任务关联原销售任务（RET-04）；仅非空行入索引，2008 R2 支持 |
| upload_part | 三列 + 索引 | `next_query_at datetime2(3) NULL`、`query_count int NOT NULL CONSTRAINT DF_part_qcount DEFAULT 0`、`first_sent_at datetime2(3) NULL`；`IX_part_query(state, next_query_at)` | billstatus 退避回查调度（TOP-02/03/04）；到期同状态分片不回表扫描 |
| verification_code | 新表 | `id bigint IDENTITY PK`；`verification_id bigint NOT NULL FK → verification(id)`；`org_id bigint NOT NULL`；`env char(4) NOT NULL`；`code_id bigint NOT NULL FK → trace_code(id)`；`source_line_id bigint NOT NULL`（无 FK）；`result nvarchar(8) NOT NULL`，`CK_vcode_result CHECK (result IN ('PASS','FAIL','UNKNOWN'))`；`reason nvarchar(500) NULL`；`evidence_json nvarchar(max) NULL`；`checked_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()`（`DF_vcode_at`）；`UQ_vcode UNIQUE(verification_id, code_id)`；`IX_vcode_code(code_id)` | 零售校验逐码三态结论与证据（SALE-02/TOP-04）；同一核验内每码仅一行，按码反查走 IX_vcode_code |
| verification | 新索引 | `IX_verif_snapshot(source_snapshot_id, is_current, state)` | 源同步/核验兜底按快照判重，避免全表扫描 |
| return_confirmation | 新表 | `id bigint IDENTITY PK`；`org_id bigint NOT NULL FK → organization(id)`；`env char(4) NOT NULL`；`task_id bigint NOT NULL FK → upload_task(id)` + `UQ_retconf_task UNIQUE(task_id)`（一任务一确认）；`decision nvarchar(16) NOT NULL`，`CK_retconf_decision CHECK (decision IN ('CONFIRMED','REJECTED'))`；`relation_kind nvarchar(16) NOT NULL`，`CK_retconf_relation CHECK (relation_kind IN ('SALE','PURCHASE_IN'))`；`predecessor_task_id bigint NULL`（无 FK）；`evidence_json nvarchar(max) NOT NULL`；`confirmed_by nvarchar(64) NOT NULL`；`confirmed_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()`（`DF_retconf_at`）；`ver rowversion NOT NULL` | 116 退货候选人工确认/拒绝留证（RET-01/02）；`ver` 供确认接口 `expectedVersion` 乐观锁 |

`verification_code.env` 与 `return_confirmation.env` 沿用 char(4) LIVE/MOCK 隔离（无独立 CHECK，与既有表一致）；`return_confirmation` 的 `predecessor_task_id` 是留证快照而非外键，避免平台/本地任务历史级联约束。除 `DF_part_qcount` 外，新增列均可空或带默认，0009 对既有行无数据迁移。

## 环境隔离（Live/Mock）

`upstream_bill`、`download_progress`、`code_event`、`code_claim`、`inbound_allocation`、`verification`、`verification_code`、`upload_task`、`upload_part`、`upload_attempt`、`outbox_job`、`reconciliation_run`、`return_confirmation`、`audit_log` 均含 `env` 列（LIVE/MOCK，多数有 CHECK；`verification_code`/`return_confirmation` 为 char(4) 无独立 CHECK）；Mock 与 Live 数据互不串扰（CodeConstraintTests.Mock_and_live_claims_isolated、UploadConstraintTests.Mock_task_with_same_event_key_isolated、JobQueueTests.Claim_only_returns_jobs_of_requested_env）。宿主配置 `TraceFlow:Env` 大小写不敏感、启动规范化为 LIVE/MOCK（默认 MOCK；非法值启动失败），LIVE 须门禁后显式开启。

对账口径（M4，REC-01/02/03）：`reconciliation_run` 只含 `env`，按宿主 env + active organization 定界；`source_snapshot` 无 `env` 列：SALE 文档集合仅按 `document_type=N'SALE'` 与 `business_datetime` 窗口选取（与 env 无关，窗口内最新版本），env 只作用于其上的 verification/upload_task 证据，因此“该环境无证据”的快照在 SALE 口径下仍计入文档集合（可表现为 SOURCE_MISSING/WAIT_VERIFY），不会因本环境无证据被排除；RETURN 文档集合以同 org+env 退货任务锚定的 `source_snapshot_id` 为准（不做版本过滤，任务锚定哪个快照就核对该快照）。`business_type` 取值 SALE/RETURN，窗口按 `source_snapshot.business_datetime` UTC 半开区间 [window_start, window_end)。`local_summary`/`platform_summary` 为运行结果快照；平台侧仅由持久化 `upload_attempt`/`code_result` 证据推导（对账作业零 TOP 调用），存在非终态分片时 run=`INCOMPLETE` 且 `platform_summary.unavailable=true`、`unavailableReason=NON_TERMINAL_PARTS`，不得解读为"平台无记录"。差异 `evidence_json` 只存请求号/HTTP 状态/计数等，不存原始平台响应或凭据；`reconciliation_run.ver` 为 rowversion，但当前对账端点不暴露乐观锁参数（运行行由作业单写，处置走差异行状态机）。

码台账读取口径（M4，UI-08，只读）：`GET /codes` 只列本 org+env 中存在 `code_event` 的码（无事件的码不入列表，也不臆造状态）；行内 `firstEventAt`/`lastEventAt`/`eventCount`/`latestEventType` 基于**筛选后**的事件集合；`claimState`=存在激活占用 `ACTIVE`，否则曾释放 `RELEASED`，否则 `NONE`；`billCode` 仅取激活占用任务的分片（已释放占用不回单号）。单码详情 `events` ≤500（升序 `occurred_at,id`，`source_line_id=0` 表示外部证据事件）、`claims` ≤200、`results` ≤200（只取 `result`/`reason`/`recorded_at` 与同 task+env 的 `bill_code`/分片状态/任务状态/`request_id`/`http_status`）、`confirmations` ≤50、父子关系各 ≤100；`platform_evidence`/`raw_response`/`evidence_json` 一律不入响应。码身份全局唯一（`trace_code` 无 org 列），可见性由本 org+env 的事件存在性定界（只在 MOCK 有事件的码在 LIVE 视图 404）。
