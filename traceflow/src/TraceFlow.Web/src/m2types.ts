// M2 类型（T14 复用）。M2 契约：实体 id 与数量以 JSON 字符串出网（API-01），
// 故 id/quantity 字段一律 string；请求体（AllocationLineInput）为数字，与后端 DTO 一致。

export interface UpstreamBill {
  billId: string; platformBillId: string; billNo: string | null; billDate: string | null;
  upstreamRefEntId: string; toEntId: string | null; completeness: string;
  sourceVersion: string | null; fetchedAtUtc: string;
}
export interface UpstreamCode { codeNormalized: string; packageLevel: string | null; }
export interface UpstreamLine {
  lineId: string; platformLineId: string; platformDrugId: string | null; batchNo: string | null;
  quantity: string | null; unit: string | null; allocatedQuantity: string; remainingQuantity: string | null;
  codes: UpstreamCode[];
}
export interface UpstreamDetail { bill: UpstreamBill; lines: UpstreamLine[]; }

export interface InboundListItem {
  snapshotId: string; docKey: string; version: number; businessDatetimeUtc: string | null;
  completeness: string; matchState: string | null; matchVersion: number | null; reason: string | null;
  supplierDwbh: string | null; taskId: string | null; taskState: string | null;
}
export interface InboundLine {
  lineId: string; sourceLineKey: string; productSourceId: string; quantity: string; unit: string | null;
  batchRaw: string | null; classification: string | null; allocatedQuantity: string;
}
export interface InboundAllocCode { codeId: string; codeNormalized: string; }
export interface InboundAllocation {
  allocationId: string; sourceLineId: string; upstreamLineId: string; quantity: string;
  unitRatio: string; state: string; codes: InboundAllocCode[];
}
export interface InboundDetail {
  snapshot: {
    snapshotId: string; docKey: string; version: number; businessDatetimeUtc: string | null;
    completeness: string; contentHash: string;
  };
  lines: InboundLine[];
  match: { matchState: string; version: number; reason: string | null } | null;
  allocations: InboundAllocation[];
  task: { taskId: string; state: string; billCode: string | null } | null;
}

export interface AllocationLineInput {
  sourceLineId: number; upstreamLineId: number; quantity: number; unitRatio: string;
  conversionEvidence: string | null; codes: string[];
}
export interface AllocationSaveResult { matchVersion: number; allocationsSaved: number; allRequiredCovered: boolean; }
export interface ConfirmResult { taskId: string; billCode: string; matchVersion: number; reused: boolean; message: string; }

export interface ServiceAccount { serviceAccount: string; machineName: string; }
export interface TopBudget {
  budgetKey: string; dayCount: number; dayWindowStart: string | null; dayLimit: number | null;
  minuteCount: number; minuteWindowStart: string | null; minuteLimit: number | null;
  secondCount: number; secondWindowStart: string | null; secondLimit: number | null;
  nextAllowedAtUtc: string | null;
}
export interface ChannelPause {
  id: string; scopeKey: string; reason: string; isActive: boolean; pausedAtUtc: string;
  pausedBy: string; resumedAtUtc: string | null; resumedBy: string | null;
}
export interface TopBudgetView { budgets: TopBudget[]; pauses: ChannelPause[]; }