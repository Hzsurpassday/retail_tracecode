-- 0002 来源镜像：不可变版本 + 摘要 + 完整性 + 检查点 + 接入异常
CREATE TABLE dbo.source_snapshot (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_source_snapshot PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_snapshot_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_snapshot_sys REFERENCES dbo.source_system(id),
  source_document_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  document_type nvarchar(16) NOT NULL,
  version int NOT NULL,
  business_datetime datetime2(3) NULL,
  source_time_raw nvarchar(64) NULL,
  source_time_zone nvarchar(64) NULL,
  completeness_status nvarchar(16) NOT NULL CONSTRAINT DF_snapshot_completeness DEFAULT N'DISCOVERED',
  content_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  raw_header nvarchar(max) NULL,
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_snapshot_observed DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_snapshot_key_version UNIQUE (source_system_id, source_document_key, version),
  CONSTRAINT CK_snapshot_completeness CHECK (completeness_status IN (N'DISCOVERED',N'STABILIZING',N'COMPLETE',N'SOURCE_CHANGED',N'INVALID'))
);

CREATE TABLE dbo.source_line (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_source_line PRIMARY KEY,
  snapshot_id bigint NOT NULL CONSTRAINT FK_line_snapshot REFERENCES dbo.source_snapshot(id),
  source_line_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  product_source_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  quantity_original decimal(18,6) NOT NULL,
  unit_original nvarchar(32) NULL,
  batch_raw nvarchar(100) NULL,
  expiry_raw nvarchar(32) NULL,
  expiry_precision nvarchar(8) NOT NULL CONSTRAINT DF_line_expiry DEFAULT N'UNKNOWN',
  trace_code_raw nvarchar(200) NULL,
  trace_code_normalized nvarchar(200) COLLATE Latin1_General_BIN2 NULL,
  code_format nvarchar(16) NOT NULL CONSTRAINT DF_line_codefmt DEFAULT N'Empty',
  line_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  event_kind nvarchar(16) NOT NULL,
  raw_line nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_line_key UNIQUE (snapshot_id, source_line_key),
  CONSTRAINT CK_line_expiry CHECK (expiry_precision IN (N'DAY',N'MONTH',N'UNKNOWN')),
  CONSTRAINT CK_line_event CHECK (event_kind IN (N'SALE',N'CONSUMER_RETURN',N'NO_EVENT',N'PURCHASE_IN'))
);

CREATE TABLE dbo.sync_checkpoint (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_sync_checkpoint PRIMARY KEY,
  source_system_id bigint NOT NULL CONSTRAINT FK_checkpoint_sys REFERENCES dbo.source_system(id),
  entity nvarchar(32) NOT NULL,
  checkpoint_kind nvarchar(16) NOT NULL,
  cursor_value nvarchar(200) NULL,
  window_start datetime2(3) NULL,
  window_end datetime2(3) NULL,
  last_success_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_checkpoint UNIQUE (source_system_id, entity, checkpoint_kind)
);

CREATE TABLE dbo.ingest_exception (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ingest_exception PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_ingest_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_ingest_sys REFERENCES dbo.source_system(id),
  source_document_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  source_line_key nvarchar(128) COLLATE Latin1_General_BIN2 NULL,
  exception_type nvarchar(32) NOT NULL,
  detail nvarchar(max) NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_ingest_status DEFAULT N'OPEN',
  resolved_by nvarchar(64) NULL,
  resolved_at datetime2(3) NULL,
  resolution_evidence nvarchar(max) NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_ingest_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT CK_ingest_status CHECK (status IN (N'OPEN',N'RESOLVED'))
);
CREATE INDEX IX_ingest_exception_open ON dbo.ingest_exception(status, created_at);
