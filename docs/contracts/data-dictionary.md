# 数据字典（PRD §11.2 逻辑实体 → 物理表映射）

物理表名与关键约束已逐一核对实际迁移脚本 `traceflow/db/migrations/0001_core.sql` … `0006_upload.sql` + M1 增量 `0007_m1_source_master.sql`（HEAD 以实际 M1 末提交为准）。
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

## 环境隔离（Live/Mock）

`upstream_bill`、`download_progress`、`code_event`、`code_claim`、`inbound_allocation`、`verification`、`upload_task`、`upload_part`、`upload_attempt`、`outbox_job`、`reconciliation_run`、`audit_log` 均含 `env` 列（LIVE/MOCK，多数有 CHECK）；Mock 与 Live 数据互不串扰（CodeConstraintTests.Mock_and_live_claims_isolated、UploadConstraintTests.Mock_task_with_same_event_key_isolated、JobQueueTests.Claim_only_returns_jobs_of_requested_env）。宿主配置 `TraceFlow:Env` 大小写不敏感、启动规范化为 LIVE/MOCK（默认 MOCK；非法值启动失败），LIVE 须门禁后显式开启。
