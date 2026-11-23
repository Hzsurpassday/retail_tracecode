export interface ProductMapping {
  mappingId: string; state: string; isLocked: boolean; mappingVersion: number; version: string;
  platformDrugId: string; approvalNoNorm: string | null; manufacturerIdentity: string | null;
  dosageForm: string | null; strength: string | null; packageSpec: string | null;
}
export interface Product {
  productId: string; spid: string; displayCode: string | null; barcode: string | null; name: string;
  approvalNoRaw: string | null; approvalNoNorm: string | null; manufacturerRaw: string | null;
  manufacturerIdentity: string | null; dosageForm: string | null; strength: string | null;
  packageSpec: string | null; unit: string | null; traceClassification: string; classificationReason: string | null;
  updatedAt: string; version: string; mappingState: string; mapping: ProductMapping | null;
}
export interface SupplierMapping {
  mappingId: string; state: string; isLocked: boolean; mappingVersion: number; version: string;
  refEntId: string; entId: string; platformName: string; creditCode: string | null;
  auditStatus: string | null; role: string | null;
}
export interface Supplier {
  supplierId: string; dwbh: string; name: string; creditCode: string | null; role: string | null;
  version: string; mappingState: string; mapping: SupplierMapping | null;
}
export interface Page<T> { records: T[]; total: number; page: number; pageSize: number; }
export interface ImportBatch {
  batchId: string; fileKind: string; fileName: string; fileHash: string; totalRows: number;
  successRows: number; failedRows: number; status: string; version: string;
  stagedBy: string; stagedAt: string; committedBy: string | null; committedAt: string | null;
}
export interface ImportRow { rowNumber: number; status: string; error: string | null; rawJson: string; }
export interface ImportDetail { batch: ImportBatch; rows: ImportRow[]; total: number; page: number; pageSize: number; }
export interface JobStatus {
  jobId: string; jobKind: string; intentKey: string; jobState: string; attemptCount: number;
  nextRunAtUtc: string; lastError: string | null; leaseOwner: string | null; fencingToken: number;
}
export interface SettingView {
  id: number; key: string; valueJson: string | null; isSecret: boolean; hasSecretValue: boolean;
  version: string; updatedBy: string | null; updatedAtUtc: string | null; reason: string | null;
}
export interface ConnectionTestResult { ok: boolean; message: string; }
