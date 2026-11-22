-- 0003 主数据：档案/平台身份/映射（双向有效唯一）/前缀索引/导入
CREATE TABLE dbo.product (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_product PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_product_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_product_sys REFERENCES dbo.source_system(id),
  spid nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  display_code nvarchar(64) NULL,
  barcode nvarchar(64) NULL,
  name nvarchar(200) NOT NULL,
  approval_no_raw nvarchar(100) NULL,
  approval_no_norm nvarchar(100) NULL,
  manufacturer_raw nvarchar(200) NULL,
  manufacturer_identity nvarchar(128) NULL,
  dosage_form nvarchar(64) NULL,
  strength nvarchar(100) NULL,
  package_spec nvarchar(100) NULL,
  unit nvarchar(32) NULL,
  trace_classification nvarchar(8) NOT NULL CONSTRAINT DF_product_class DEFAULT N'UNKNOWN',
  classification_reason nvarchar(500) NULL,
  raw_json nvarchar(max) NULL,
  updated_at datetime2(3) NOT NULL CONSTRAINT DF_product_updated DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_product_source UNIQUE (org_id, source_system_id, spid),
  CONSTRAINT CK_product_class CHECK (trace_classification IN (N'UNKNOWN',N'REQUIRED',N'EXEMPT'))
);

CREATE TABLE dbo.supplier (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_supplier PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_supplier_org REFERENCES dbo.organization(id),
  source_system_id bigint NOT NULL CONSTRAINT FK_supplier_sys REFERENCES dbo.source_system(id),
  dwbh nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(200) NOT NULL,
  credit_code nvarchar(64) NULL,
  role nvarchar(32) NULL,
  raw_json nvarchar(max) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_supplier_source UNIQUE (org_id, source_system_id, dwbh)
);

CREATE TABLE dbo.platform_drug (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_platform_drug PRIMARY KEY,
  platform_drug_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  approval_no_norm nvarchar(100) NULL,
  manufacturer_identity nvarchar(128) NULL,
  dosage_form nvarchar(64) NULL,
  strength nvarchar(100) NULL,
  package_spec nvarchar(100) NULL,
  identity_json nvarchar(max) NULL,
  source_kind nvarchar(32) NOT NULL,
  source_time datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_platform_drug UNIQUE (platform_drug_id)
);

CREATE TABLE dbo.platform_enterprise_version (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_platform_ent_ver PRIMARY KEY,
  ref_ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(200) NOT NULL,
  credit_code nvarchar(64) NULL,
  audit_status nvarchar(32) NULL,
  role nvarchar(32) NULL,
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_ent_ver_observed DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_ent_version UNIQUE (ref_ent_id, ent_id)
);

CREATE TABLE dbo.product_mapping (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_product_mapping PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_pmap_org REFERENCES dbo.organization(id),
  product_id bigint NOT NULL CONSTRAINT FK_pmap_product REFERENCES dbo.product(id),
  platform_drug_id bigint NOT NULL CONSTRAINT FK_pmap_drug REFERENCES dbo.platform_drug(id),
  state nvarchar(16) NOT NULL CONSTRAINT DF_pmap_state DEFAULT N'UNRESOLVED',
  is_locked bit NOT NULL CONSTRAINT DF_pmap_locked DEFAULT 0,
  rule_version nvarchar(32) NULL,
  evidence_json nvarchar(max) NULL,
  confirmed_by nvarchar(64) NULL,
  confirmed_at datetime2(3) NULL,
  version int NOT NULL CONSTRAINT DF_pmap_version DEFAULT 1,
  ver rowversion NOT NULL,
  CONSTRAINT CK_pmap_state CHECK (state IN (N'UNRESOLVED',N'CANDIDATE',N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED',N'CONFLICT',N'REVIEW_REQUIRED',N'DISABLED'))
);
CREATE UNIQUE INDEX UX_pmap_active_local ON dbo.product_mapping(product_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');
CREATE UNIQUE INDEX UX_pmap_active_platform ON dbo.product_mapping(platform_drug_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');

CREATE TABLE dbo.product_mapping_history (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_pmap_history PRIMARY KEY,
  mapping_id bigint NOT NULL,
  product_id bigint NOT NULL,
  platform_drug_id bigint NOT NULL,
  state nvarchar(16) NOT NULL,
  rule_version nvarchar(32) NULL,
  evidence_json nvarchar(max) NULL,
  changed_by nvarchar(64) NOT NULL,
  changed_at datetime2(3) NOT NULL CONSTRAINT DF_pmap_hist_at DEFAULT SYSUTCDATETIME(),
  reason nvarchar(500) NULL
);

CREATE TABLE dbo.supplier_mapping (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_supplier_mapping PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_smap_org REFERENCES dbo.organization(id),
  supplier_id bigint NOT NULL CONSTRAINT FK_smap_supplier REFERENCES dbo.supplier(id),
  enterprise_version_id bigint NOT NULL CONSTRAINT FK_smap_entver REFERENCES dbo.platform_enterprise_version(id),
  state nvarchar(16) NOT NULL CONSTRAINT DF_smap_state DEFAULT N'UNRESOLVED',
  is_locked bit NOT NULL CONSTRAINT DF_smap_locked DEFAULT 0,
  evidence_json nvarchar(max) NULL,
  version int NOT NULL CONSTRAINT DF_smap_version DEFAULT 1,
  ver rowversion NOT NULL,
  CONSTRAINT CK_smap_state CHECK (state IN (N'UNRESOLVED',N'CANDIDATE',N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED',N'CONFLICT',N'REVIEW_REQUIRED',N'DISABLED'))
);
CREATE UNIQUE INDEX UX_smap_active_local ON dbo.supplier_mapping(supplier_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');
CREATE UNIQUE INDEX UX_smap_active_platform ON dbo.supplier_mapping(org_id, enterprise_version_id) WHERE state IN (N'AUTO_CONFIRMED',N'MANUAL_CONFIRMED');

CREATE TABLE dbo.supplier_mapping_history (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_smap_history PRIMARY KEY,
  mapping_id bigint NOT NULL,
  supplier_id bigint NOT NULL,
  enterprise_version_id bigint NOT NULL,
  state nvarchar(16) NOT NULL,
  evidence_json nvarchar(max) NULL,
  changed_by nvarchar(64) NOT NULL,
  changed_at datetime2(3) NOT NULL CONSTRAINT DF_smap_hist_at DEFAULT SYSUTCDATETIME(),
  reason nvarchar(500) NULL
);

CREATE TABLE dbo.drug_prefix_index (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_drug_prefix PRIMARY KEY,
  prefix char(7) COLLATE Latin1_General_BIN2 NOT NULL,
  platform_drug_id bigint NOT NULL CONSTRAINT FK_prefix_drug REFERENCES dbo.platform_drug(id),
  evidence_json nvarchar(max) NULL,
  conflict_state nvarchar(16) NOT NULL CONSTRAINT DF_prefix_conflict DEFAULT N'OK',
  observed_at datetime2(3) NOT NULL CONSTRAINT DF_prefix_observed DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_drug_prefix ON dbo.drug_prefix_index(prefix); -- 非唯一：prefix 不是全局药品外键（MATCH-03）

CREATE TABLE dbo.import_batch (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_import_batch PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_import_org REFERENCES dbo.organization(id),
  file_kind nvarchar(32) NOT NULL,
  file_name nvarchar(260) NOT NULL,
  file_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  total_rows int NOT NULL,
  success_rows int NOT NULL CONSTRAINT DF_import_success DEFAULT 0,
  failed_rows int NOT NULL CONSTRAINT DF_import_failed DEFAULT 0,
  status nvarchar(16) NOT NULL CONSTRAINT DF_import_status DEFAULT N'STAGED',
  staged_by nvarchar(64) NOT NULL,
  staged_at datetime2(3) NOT NULL CONSTRAINT DF_import_staged DEFAULT SYSUTCDATETIME(),
  committed_by nvarchar(64) NULL,
  committed_at datetime2(3) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_import_intent UNIQUE (org_id, file_kind, file_hash),
  CONSTRAINT CK_import_status CHECK (status IN (N'STAGED',N'COMMITTED',N'FAILED'))
);

CREATE TABLE dbo.import_row (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_import_row PRIMARY KEY,
  batch_id bigint NOT NULL CONSTRAINT FK_import_row_batch REFERENCES dbo.import_batch(id),
  row_number int NOT NULL,
  raw_json nvarchar(max) NOT NULL,
  status nvarchar(16) NOT NULL CONSTRAINT DF_import_row_status DEFAULT N'OK',
  error nvarchar(1000) NULL,
  mapping_version int NULL,
  CONSTRAINT UQ_import_row UNIQUE (batch_id, row_number),
  CONSTRAINT CK_import_row_status CHECK (status IN (N'OK',N'ERROR',N'APPLIED'))
);