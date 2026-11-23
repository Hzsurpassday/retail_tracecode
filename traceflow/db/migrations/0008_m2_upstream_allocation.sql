-- 0008 M2：入库匹配维度/持久限流预算/授权范围暂停/下载窗口列/Cancelled 行政终态（IN-09）
CREATE TABLE dbo.inbound_match (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_inbound_match PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_imatch_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  source_snapshot_id bigint NOT NULL CONSTRAINT FK_imatch_snapshot REFERENCES dbo.source_snapshot(id),
  supplier_dwbh nvarchar(64) COLLATE Latin1_General_BIN2 NULL,
  match_state nvarchar(16) NOT NULL CONSTRAINT DF_imatch_state DEFAULT N'UNMATCHED',
  reason nvarchar(500) NULL,
  evidence_json nvarchar(max) NULL,
  version int NOT NULL CONSTRAINT DF_imatch_version DEFAULT 1,
  matched_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_inbound_match UNIQUE (org_id, env, source_snapshot_id),
  CONSTRAINT CK_imatch_state CHECK (match_state IN (N'NOT_REQUIRED',N'UNMATCHED',N'PARTIAL',N'AMBIGUOUS',N'CONFLICT',N'COMPLETE')),
  CONSTRAINT CK_imatch_env CHECK (env IN (N'LIVE',N'MOCK'))
);
GO
CREATE TABLE dbo.top_rate_budget (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_rate_budget PRIMARY KEY,
  env char(4) NOT NULL,
  budget_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  next_allowed_at datetime2(3) NULL,
  day_count int NOT NULL CONSTRAINT DF_budget_day DEFAULT 0,
  day_window_start datetime2(3) NULL,
  minute_count int NOT NULL CONSTRAINT DF_budget_min DEFAULT 0,
  minute_window_start datetime2(3) NULL,
  second_count int NOT NULL CONSTRAINT DF_budget_sec DEFAULT 0,
  second_window_start datetime2(3) NULL,
  day_limit int NULL,
  minute_limit int NULL,
  second_limit int NULL,
  updated_at datetime2(3) NOT NULL CONSTRAINT DF_budget_at DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_rate_budget UNIQUE (env, budget_key),
  CONSTRAINT CK_budget_env CHECK (env IN (N'LIVE',N'MOCK'))
);
GO
CREATE TABLE dbo.top_channel_pause (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_channel_pause PRIMARY KEY,
  env char(4) NOT NULL,
  scope_key nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  reason nvarchar(500) NOT NULL,
  is_active bit NOT NULL CONSTRAINT DF_pause_active DEFAULT 1,
  paused_at datetime2(3) NOT NULL CONSTRAINT DF_pause_at DEFAULT SYSUTCDATETIME(),
  paused_by nvarchar(64) NOT NULL,
  resumed_at datetime2(3) NULL,
  resumed_by nvarchar(64) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT CK_pause_env CHECK (env IN (N'LIVE',N'MOCK'))
);
CREATE UNIQUE INDEX UX_pause_active ON dbo.top_channel_pause(env, scope_key) WHERE is_active = 1;
GO
ALTER TABLE dbo.download_progress ADD window_start datetime2(3) NULL;
ALTER TABLE dbo.download_progress ADD window_end datetime2(3) NULL;
GO
ALTER TABLE dbo.upload_task DROP CONSTRAINT CK_task_state;
ALTER TABLE dbo.upload_task ADD CONSTRAINT CK_task_state CHECK (state IN (N'Blocked',N'Ready',N'Dispatching',N'Accepted',N'AcceptanceUnknown',N'Processing',N'RetryWait',N'PartialSuccess',N'ResultUnresolved',N'Succeeded',N'Failed',N'ManualAction',N'Cancelled'));
GO
ALTER TABLE dbo.upload_part DROP CONSTRAINT CK_part_state;
ALTER TABLE dbo.upload_part ADD CONSTRAINT CK_part_state CHECK (state IN (N'Blocked',N'Ready',N'Dispatching',N'Accepted',N'AcceptanceUnknown',N'Processing',N'RetryWait',N'PartialSuccess',N'ResultUnresolved',N'Succeeded',N'Failed',N'ManualAction',N'Cancelled'));
