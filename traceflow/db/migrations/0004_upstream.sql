-- 0004 上游出库单：主体组合唯一 + 列表/详情分页进度分离
CREATE TABLE dbo.upstream_bill (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upstream_bill PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_upbill_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  upstream_ref_ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  platform_bill_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  bill_no nvarchar(64) NULL,
  bill_date datetime2(3) NULL,
  from_ent_id nvarchar(64) NULL,
  to_ent_id nvarchar(64) NULL,
  owner_ent_id nvarchar(64) NULL,
  deliver_ent_id nvarchar(64) NULL,
  completeness nvarchar(16) NOT NULL CONSTRAINT DF_upbill_complete DEFAULT N'LIST_ONLY',
  list_raw nvarchar(max) NULL,
  source_version nvarchar(64) NULL,
  fetched_at datetime2(3) NOT NULL CONSTRAINT DF_upbill_fetched DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_upbill_identity UNIQUE (org_id, env, upstream_ref_ent_id, platform_bill_id),
  CONSTRAINT CK_upbill_complete CHECK (completeness IN (N'LIST_ONLY',N'DETAIL_PARTIAL',N'COMPLETE',N'INCOMPLETE')),
  CONSTRAINT CK_upbill_env CHECK (env IN (N'LIVE',N'MOCK'))
);

CREATE TABLE dbo.upstream_bill_line (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upstream_line PRIMARY KEY,
  bill_id bigint NOT NULL CONSTRAINT FK_upline_bill REFERENCES dbo.upstream_bill(id),
  platform_line_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  platform_drug_id nvarchar(64) NULL,
  batch_no nvarchar(100) NULL,
  quantity decimal(18,6) NULL,
  unit nvarchar(32) NULL,
  package_level nvarchar(16) NULL,
  raw_json nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_upline UNIQUE (bill_id, platform_line_id)
);

CREATE TABLE dbo.upstream_bill_code (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_upstream_code PRIMARY KEY,
  bill_id bigint NOT NULL CONSTRAINT FK_upcode_bill REFERENCES dbo.upstream_bill(id),
  line_id bigint NOT NULL CONSTRAINT FK_upcode_line REFERENCES dbo.upstream_bill_line(id),
  code_normalized nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
  code_raw nvarchar(200) NOT NULL,
  package_level nvarchar(16) NULL,
  parent_code nvarchar(200) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_upcode UNIQUE (bill_id, code_normalized)
);

CREATE TABLE dbo.download_progress (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_download_progress PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_download_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  job_kind nvarchar(32) NOT NULL,
  bill_id bigint NOT NULL CONSTRAINT DF_download_bill DEFAULT 0, -- 0=列表级进度
  page_cursor nvarchar(200) NULL,
  pages_done int NOT NULL CONSTRAINT DF_download_pages DEFAULT 0,
  total_pages int NULL,
  total_count_expected int NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_download_status DEFAULT N'RUNNING',
  last_success_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_download UNIQUE (org_id, env, job_kind, bill_id),
  CONSTRAINT CK_download_status CHECK (status IN (N'RUNNING',N'COMPLETE',N'FAILED'))
);