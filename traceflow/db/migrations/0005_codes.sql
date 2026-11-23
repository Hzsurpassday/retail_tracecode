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
