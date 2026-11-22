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
