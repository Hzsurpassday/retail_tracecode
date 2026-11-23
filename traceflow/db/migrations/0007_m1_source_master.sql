-- 0007 M1：format 加宽（M0 携带项 1）/稳定期列/幂等键用户作用域/映射对唯一
ALTER TABLE dbo.source_line DROP CONSTRAINT DF_line_codefmt;
ALTER TABLE dbo.source_line ALTER COLUMN code_format nvarchar(32) NOT NULL;
UPDATE dbo.source_line SET code_format = N'EMPTY' WHERE code_format = N'Empty';
ALTER TABLE dbo.source_line ADD CONSTRAINT DF_line_codefmt DEFAULT N'EMPTY' FOR code_format;
GO
ALTER TABLE dbo.trace_code ALTER COLUMN format nvarchar(32) NOT NULL;
GO
ALTER TABLE dbo.source_snapshot ADD stabilize_rounds int NOT NULL CONSTRAINT DF_snapshot_rounds DEFAULT 0;
ALTER TABLE dbo.source_snapshot ADD last_round_at datetime2(3) NULL;
GO
ALTER TABLE dbo.idempotency_record DROP CONSTRAINT UQ_idempotency_key;
ALTER TABLE dbo.idempotency_record ADD CONSTRAINT UQ_idempotency_user_key UNIQUE (idem_key, user_id);
GO
CREATE UNIQUE INDEX UX_pmap_pair ON dbo.product_mapping(product_id, platform_drug_id);
CREATE UNIQUE INDEX UX_smap_pair ON dbo.supplier_mapping(supplier_id, enterprise_version_id);
GO
ALTER TABLE dbo.import_batch ADD staged_content nvarchar(max) NULL;
