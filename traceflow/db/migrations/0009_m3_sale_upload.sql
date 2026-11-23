-- 0009 M3：销售核验逐码结论/退货确认/回查调度/前置任务
ALTER TABLE dbo.upload_task ADD predecessor_task_id bigint NULL
  CONSTRAINT FK_task_predecessor REFERENCES dbo.upload_task(id);
GO
ALTER TABLE dbo.upload_part ADD
  next_query_at datetime2(3) NULL,
  query_count int NOT NULL CONSTRAINT DF_part_qcount DEFAULT 0,
  first_sent_at datetime2(3) NULL;
GO
CREATE TABLE dbo.verification_code (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_verification_code PRIMARY KEY,
  verification_id bigint NOT NULL CONSTRAINT FK_vcode_verif REFERENCES dbo.verification(id),
  org_id bigint NOT NULL,
  env char(4) NOT NULL,
  code_id bigint NOT NULL CONSTRAINT FK_vcode_code REFERENCES dbo.trace_code(id),
  source_line_id bigint NOT NULL,
  result nvarchar(8) NOT NULL,
  reason nvarchar(500) NULL,
  evidence_json nvarchar(max) NULL,
  checked_at datetime2(3) NOT NULL CONSTRAINT DF_vcode_at DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_vcode UNIQUE (verification_id, code_id),
  CONSTRAINT CK_vcode_result CHECK (result IN (N'PASS',N'FAIL',N'UNKNOWN'))
);
GO
CREATE TABLE dbo.return_confirmation (
  id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_return_confirmation PRIMARY KEY,
  org_id bigint NOT NULL CONSTRAINT FK_retconf_org REFERENCES dbo.organization(id),
  env char(4) NOT NULL,
  task_id bigint NOT NULL CONSTRAINT FK_retconf_task REFERENCES dbo.upload_task(id),
  decision nvarchar(16) NOT NULL,
  relation_kind nvarchar(16) NOT NULL,
  predecessor_task_id bigint NULL,
  evidence_json nvarchar(max) NOT NULL,
  confirmed_by nvarchar(64) NOT NULL,
  confirmed_at datetime2(3) NOT NULL CONSTRAINT DF_retconf_at DEFAULT SYSUTCDATETIME(),
  ver rowversion NOT NULL,
  CONSTRAINT UQ_retconf_task UNIQUE (task_id),
  CONSTRAINT CK_retconf_decision CHECK (decision IN (N'CONFIRMED',N'REJECTED')),
  CONSTRAINT CK_retconf_relation CHECK (relation_kind IN (N'SALE',N'PURCHASE_IN'))
);
