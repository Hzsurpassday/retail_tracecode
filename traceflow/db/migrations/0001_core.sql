-- 0001 核心：门店/来源系统/设置/用户权限/审计/幂等
CREATE TABLE dbo.organization (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_organization PRIMARY KEY,
  org_code nvarchar(32) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(200) NOT NULL,
  ref_ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NULL,
  ent_id nvarchar(64) COLLATE Latin1_General_BIN2 NULL,
  time_zone nvarchar(64) NOT NULL CONSTRAINT DF_organization_tz DEFAULT N'Asia/Shanghai',
  is_active bit NOT NULL CONSTRAINT DF_organization_active DEFAULT 1,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_organization_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_organization_code UNIQUE (org_code)
);
CREATE UNIQUE INDEX UX_organization_single_active ON dbo.organization(is_active) WHERE is_active = 1;

CREATE TABLE dbo.source_system (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_source_system PRIMARY KEY,
  system_code nvarchar(32) COLLATE Latin1_General_BIN2 NOT NULL,
  name nvarchar(100) NOT NULL,
  adapter_contract_version nvarchar(32) NOT NULL,
  config_json nvarchar(max) NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_source_system_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_source_system_code UNIQUE (system_code)
);

CREATE TABLE dbo.app_setting (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_app_setting PRIMARY KEY,
  setting_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  value_json nvarchar(max) NULL,
  secret_protected varbinary(max) NULL,
  is_secret bit NOT NULL CONSTRAINT DF_app_setting_secret DEFAULT 0,
  protection_scope nvarchar(200) NULL,
  updated_by nvarchar(64) NOT NULL,
  updated_at datetime2(3) NOT NULL CONSTRAINT DF_app_setting_updated DEFAULT SYSUTCDATETIME(),
  reason nvarchar(500) NULL,
  ver rowversion NOT NULL,
  CONSTRAINT UQ_app_setting_key UNIQUE (setting_key),
  CONSTRAINT CK_app_setting_secret CHECK (is_secret = 0 OR (secret_protected IS NOT NULL AND value_json IS NULL))
);

CREATE TABLE dbo.app_user (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_app_user PRIMARY KEY,
  username nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  display_name nvarchar(100) NOT NULL,
  password_hash nvarchar(500) NOT NULL,
  is_active bit NOT NULL CONSTRAINT DF_app_user_active DEFAULT 1,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_app_user_created DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_app_user_username UNIQUE (username)
);

CREATE TABLE dbo.user_permission (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_user_permission PRIMARY KEY,
  user_id bigint NOT NULL CONSTRAINT FK_user_permission_user REFERENCES dbo.app_user(id),
  permission nvarchar(64) COLLATE Latin1_General_BIN2 NOT NULL,
  granted_by nvarchar(64) NOT NULL,
  granted_at datetime2(3) NOT NULL CONSTRAINT DF_user_permission_at DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_user_permission UNIQUE (user_id, permission)
);

CREATE TABLE dbo.audit_log (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_audit_log PRIMARY KEY,
  occurred_at datetime2(3) NOT NULL CONSTRAINT DF_audit_at DEFAULT SYSUTCDATETIME(),
  actor nvarchar(64) NOT NULL,
  actor_kind nvarchar(16) NOT NULL,
  action nvarchar(64) NOT NULL,
  object_type nvarchar(64) NOT NULL,
  object_id nvarchar(128) NULL,
  reason nvarchar(1000) NULL,
  before_digest nvarchar(max) NULL,
  after_digest nvarchar(max) NULL,
  request_id nvarchar(64) NULL,
  rule_version nvarchar(32) NULL,
  env char(4) NOT NULL CONSTRAINT DF_audit_env DEFAULT 'LIVE'
);
CREATE INDEX IX_audit_log_time ON dbo.audit_log(occurred_at);
CREATE INDEX IX_audit_log_object ON dbo.audit_log(object_type, object_id);

CREATE TABLE dbo.idempotency_record (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_idempotency PRIMARY KEY,
  idem_key nvarchar(128) COLLATE Latin1_General_BIN2 NOT NULL,
  request_hash char(64) COLLATE Latin1_General_BIN2 NOT NULL,
  user_id bigint NOT NULL,
  response_status int NOT NULL,
  response_body nvarchar(max) NOT NULL,
  created_at datetime2(3) NOT NULL CONSTRAINT DF_idempotency_created DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_idempotency_key UNIQUE (idem_key)
);
