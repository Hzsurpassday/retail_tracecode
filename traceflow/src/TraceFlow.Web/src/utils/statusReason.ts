// 状态原因中文映射的唯一来源（合并原 SalesPage / ReturnsPage / UploadCenterPage / CodesPage 四份副本）。
// 未知代码一律原样展示：绝不臆造中文含义，也绝不把 UNKNOWN 说成成功。
// 合并规则：并集保留所有键；同一键译文有差异时取 UploadCenterPage 现行文案（差异清单见 task-7 报告）。
export const REASON_ZH: Record<string, string> = {
  // 平台/逐码查询（SalesPage / CodesPage / UploadCenterPage 原样一致部分）
  PLATFORM_NOT_FOUND: '平台无此码', PLATFORM_REJECTED: '平台拒绝', PLATFORM_INSTORE: '平台在店',
  PLATFORM_UNAVAILABLE: '平台不可用',
  // 依赖/码分类（SalesPage 独有）
  WAIT_DEPENDENCY: '等待采购入库依赖', DEPENDENCY_PURCHASE_IN_SUCCEEDED: '采购入库已成功',
  MISSING_CODE: '缺少追溯码', CODE_FORMAT: '码格式错误', DUPLICATE_CODE: '码重复',
  UNKNOWN_CLASSIFICATION: '商品分类未知',
  // 退货确认（ReturnsPage / SalesPage / UploadCenterPage）
  RETURN_CONFIRM_REQUIRED: '待首次人工确认', RETURN_CONFIRM_REJECTED: '人工确认已驳回',
  RETURN_CONFIRM_REJECTED_SUPERSEDED: '驳回后被新快照撤销', RETURN_CONFIRMED: '已确认',
  PREDECESSOR_MISSING: '缺少前序销售事件', PREDECESSOR_AMBIGUOUS: '前序销售事件不唯一',
  PREDECESSOR_INVALID: '前序事件无效', ALREADY_RETURNED: '该码已退货',
  // 任务/发送侧（SalesPage / UploadCenterPage）
  NO_UPLOADABLE_LINES: '无可上传行', VERIFY_CHANGED_AFTER_SEND: '发送后核验变更',
  VERIFY_CHANGED_WHILE_DISPATCHING: '发送中核验变更', SALE_LINES_REMOVED: '销售行已移除',
  RETURN_LINES_REMOVED: '退货行已移除', RULE_VERSION_CHANGED: '规则版本变更',
  // 平台/投递侧状态原因（UploadCenterPage 独有）
  PARTIAL_ACCEPTED: '部分受理', DELIVERY_FAILED: '投递失败', MANUAL_REQUIRED: '需人工处置',
  PLATFORM_ACCEPTED: '平台已受理', ACCEPTANCE_UNKNOWN: '受理未知', RESULT_UNRESOLVED: '结果未决',
};

/** 空值 → `empty`（默认 `—`）；已知代码 → 中文；未知代码 → 原样返回。 */
export function reasonZh(reason: string | null, empty = '—'): string {
  if (!reason) return empty;
  return REASON_ZH[reason] ?? reason;
}