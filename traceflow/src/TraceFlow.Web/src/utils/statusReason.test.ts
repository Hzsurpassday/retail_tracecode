import { describe, expect, it } from 'vitest';
import { REASON_ZH, reasonZh } from './statusReason';

describe('reasonZh', () => {
  it('已知键返回中文', () => {
    expect(reasonZh('PLATFORM_NOT_FOUND')).toBe('平台无此码');
    expect(reasonZh('RETURN_CONFIRM_REQUIRED')).toBe('待首次人工确认');
    expect(reasonZh('PARTIAL_ACCEPTED')).toBe('部分受理');
    expect(reasonZh('PLATFORM_INSTORE')).toBe('平台在店');
  });

  it('未知键原样返回（不臆造中文）', () => {
    expect(reasonZh('SOME_NEW_REASON')).toBe('SOME_NEW_REASON');
  });

  it('null / 空串 → —', () => {
    expect(reasonZh(null)).toBe('—');
    expect(reasonZh('')).toBe('—');
  });

  it('自定义 empty 参数', () => {
    expect(reasonZh(null, '唯一前序（可确认）')).toBe('唯一前序（可确认）');
    expect(reasonZh('', '无')).toBe('无');
    // 未知值不受 empty 影响，仍原样返回
    expect(reasonZh('UNKNOWN_KEY', '无')).toBe('UNKNOWN_KEY');
  });

  it('合并后保留三页原有全部键（防合并丢失）', () => {
    // SalesPage 原有关键键
    const salesKeys = [
      'PLATFORM_NOT_FOUND', 'PLATFORM_REJECTED', 'PLATFORM_INSTORE', 'PLATFORM_UNAVAILABLE',
      'WAIT_DEPENDENCY', 'DEPENDENCY_PURCHASE_IN_SUCCEEDED', 'MISSING_CODE', 'CODE_FORMAT',
      'DUPLICATE_CODE', 'UNKNOWN_CLASSIFICATION', 'RETURN_CONFIRM_REQUIRED', 'RETURN_CONFIRM_REJECTED',
      'RETURN_CONFIRM_REJECTED_SUPERSEDED', 'RETURN_CONFIRMED', 'NO_UPLOADABLE_LINES',
      'VERIFY_CHANGED_AFTER_SEND', 'VERIFY_CHANGED_WHILE_DISPATCHING', 'SALE_LINES_REMOVED',
      'RULE_VERSION_CHANGED', 'PREDECESSOR_MISSING', 'PREDECESSOR_AMBIGUOUS', 'ALREADY_RETURNED',
    ];
    // ReturnsPage 原有关键键
    const returnsKeys = ['PREDECESSOR_INVALID'];
    // UploadCenterPage 原有关键键
    const uploadKeys = [
      'RETURN_LINES_REMOVED', 'PARTIAL_ACCEPTED', 'DELIVERY_FAILED', 'MANUAL_REQUIRED',
      'PLATFORM_ACCEPTED', 'ACCEPTANCE_UNKNOWN', 'RESULT_UNRESOLVED',
    ];
    for (const key of [...salesKeys, ...returnsKeys, ...uploadKeys]) {
      expect(REASON_ZH, `缺少键 ${key}`).toHaveProperty(key);
      expect(REASON_ZH[key].length, `键 ${key} 译文为空`).toBeGreaterThan(0);
    }
  });

  it('译文冲突按 UploadCenterPage 现行文案收敛', () => {
    expect(REASON_ZH.PLATFORM_REJECTED).toBe('平台拒绝');
    expect(REASON_ZH.VERIFY_CHANGED_AFTER_SEND).toBe('发送后核验变更');
    expect(REASON_ZH.VERIFY_CHANGED_WHILE_DISPATCHING).toBe('发送中核验变更');
    expect(REASON_ZH.RETURN_CONFIRMED).toBe('已确认');
  });
});