// 时间显示唯一实现（NFR-02：时间显示 Asia/Shanghai）。
// 后端 datetime2 出网不带偏移量（"YYYY-MM-DDTHH:mm:ss"），故无偏移时显式按 UTC 解释，再转 Asia/Shanghai 展示。
export const SHANGHAI = new Intl.DateTimeFormat('en-US', {
  timeZone: 'Asia/Shanghai', hourCycle: 'h23',
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
});

/** 空值 → `—`；无偏移按 UTC；带 `Z`/`±hh:mm` 原样解析；非法日期回退为去掉 `T` 的原文（截 19 位）。 */
export function fmtShanghai(d: string | null): string {
  if (!d) return '—';
  const iso = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(d) ? d : d.length === 10 ? `${d}T00:00:00Z` : `${d}Z`;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return d.replace('T', ' ').slice(0, 19);
  const parts = SHANGHAI.formatToParts(new Date(t));
  const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((p) => p.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')} ${part('hour')}:${part('minute')}:${part('second')}`;
}