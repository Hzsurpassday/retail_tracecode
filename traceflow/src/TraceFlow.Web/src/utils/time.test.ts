import { describe, expect, it } from 'vitest';
import { fmtShanghai } from './time';

describe('fmtShanghai', () => {
  it('null → —', () => {
    expect(fmtShanghai(null)).toBe('—');
  });

  it('空串 → —', () => {
    expect(fmtShanghai('')).toBe('—');
  });

  it('带 Z 按 UTC 解析后转上海时间', () => {
    expect(fmtShanghai('2026-11-20T12:00:00Z')).toBe('2026-11-20 20:00:00');
  });

  it('带 +08:00 偏移原样按其时区换算', () => {
    expect(fmtShanghai('2026-11-20T12:00:00+08:00')).toBe('2026-11-20 12:00:00');
  });

  it('无偏移量按 UTC 解释（跨日）', () => {
    expect(fmtShanghai('2026-11-20T16:30:00')).toBe('2026-11-21 00:30:00');
  });

  it('纯日期补 T00:00:00Z，显示为上海当日 08:00:00', () => {
    expect(fmtShanghai('2026-11-20')).toBe('2026-11-20 08:00:00');
  });

  it('午夜用 h23（00 而非 24）', () => {
    expect(fmtShanghai('2026-11-19T16:00:00Z')).toBe('2026-11-20 00:00:00');
  });

  it('非法日期回退为去 T 的原文', () => {
    expect(fmtShanghai('not-a-date')).toBe('not-a-date');
    expect(fmtShanghai('2026-13-45T99:99:99')).toBe('2026-13-45 99:99:99');
  });

  it('带毫秒的 UTC 值截断到秒', () => {
    expect(fmtShanghai('2026-11-20T12:00:00.987Z')).toBe('2026-11-20 20:00:00');
  });
});