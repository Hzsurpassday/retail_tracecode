import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import EnvBadge from '../components/EnvBadge';

interface ListPage<T> { items: T[]; page: number; pageSize: number; total: number; env: string }

interface CodeItem {
  code: string; codeRaw: string; format: string;
  firstSeenAt: string; firstEventAt: string; lastEventAt: string; eventCount: number;
  latestEventType: string | null; claimState: string; claimScope: string | null; billCode: string | null;
}
interface CodeEvent {
  eventId: string; eventType: string; direction: string; occurredAt: string;
  businessDatetime: string | null; sourceSnapshotId: number | null; sourceLineId: number;
  uploadTaskId: number | null; predecessorEventId: string | null;
}
interface CodeClaim {
  claimState: string; eventScope: string; uploadTaskId: number | null;
  claimedAt: string; releasedAt: string | null; releasedBy: string | null;
}
interface CodeResult {
  taskId: string; result: string; reason: string | null; recordedAt: string;
  billCode: string; partState: string; taskState: string; requestId: string | null; httpStatus: number | null;
}
interface CodeConfirmation {
  taskId: string; decision: string; relationKind: string;
  predecessorTaskId: string | null; confirmedBy: string; confirmedAt: string;
}
interface CodeRelation { code: string; relationSource: string; observedAt: string }
interface CodeDetail {
  code: { code: string; codeRaw: string; format: string; firstSeenAt: string; env: string };
  events: CodeEvent[];
  claims: CodeClaim[];
  results: CodeResult[];
  confirmations: CodeConfirmation[];
  relations: { parents: CodeRelation[]; children: CodeRelation[] };
}

const PAGE_SIZE = 50;
const MIN_PREFIX = 4;
const EVENT_TYPE_OPTIONS: [string, string][] = [
  ['', '全部事件类型'], ['PURCHASE_IN', '入库'], ['SALE', '销售'],
  ['CONSUMER_RETURN', '消费者退货'], ['PURCHASE_RETURN', '采购退货'],
];
const CLAIM_STATE_OPTIONS: [string, string][] = [
  ['', '全部占用状态'], ['ACTIVE', '占用中'], ['RELEASED', '已释放'], ['NONE', '未占用'],
];
const ENV_OPTIONS = ['', 'MOCK', 'LIVE'];
const EVENT_TYPE_ZH: Record<string, string> = {
  PURCHASE_IN: '入库', SALE: '销售', CONSUMER_RETURN: '消费者退货', PURCHASE_RETURN: '采购退货',
};
const CLAIM_STATE_ZH: Record<string, string> = { ACTIVE: '占用中', RELEASED: '已释放', NONE: '未占用' };
const SCOPE_ZH: Record<string, string> = { PURCHASE_IN: '采购入库', SALE: '销售' };
const CODE_RESULT_ZH: Record<string, string> = {
  SUCCESS: '成功', FAILED: '失败', UNKNOWN: 'UNKNOWN（未知，不计成功）',
};
const TASK_STATE_ZH: Record<string, string> = {
  Blocked: '阻断', Ready: '待上传', Dispatching: '发送中', Accepted: '已受理',
  AcceptanceUnknown: '受理未知', Processing: '平台处理中', RetryWait: '等待重试',
  PartialSuccess: '部分成功', ResultUnresolved: '结果未决', Succeeded: '成功',
  Failed: '失败', ManualAction: '需人工核对', Cancelled: '已取消',
};
const DECISION_ZH: Record<string, string> = { CONFIRMED: '已确认', REJECTED: '已驳回' };
const RELATION_KIND_ZH: Record<string, string> = { SALE: '销售（原销售单）', PURCHASE_IN: '采购入库' };
const REASON_ZH: Record<string, string> = {
  PLATFORM_NOT_FOUND: '平台无此码', PLATFORM_REJECTED: '平台拒绝查询', PLATFORM_INSTORE: '平台在店',
  PLATFORM_UNAVAILABLE: '平台不可用',
};
function reasonZh(reason: string | null): string {
  if (!reason) return '—';
  return REASON_ZH[reason] ?? reason;
}
function stateZh(value: string | null, dict: Record<string, string>): string {
  if (!value) return '—';
  return dict[value] ?? value;
}
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
// 后端 datetime2 出网不带偏移量，无偏移时按 UTC 解释，再转 Asia/Shanghai 显示（与 SalesPage 一致）。
const SHANGHAI = new Intl.DateTimeFormat('en-US', {
  timeZone: 'Asia/Shanghai', hourCycle: 'h23',
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
});
function fmtShanghai(d: string | null): string {
  if (!d) return '—';
  const iso = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(d) ? d : d.length === 10 ? `${d}T00:00:00Z` : `${d}Z`;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return d.replace('T', ' ').slice(0, 19);
  const parts = SHANGHAI.formatToParts(new Date(t));
  const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((p) => p.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')} ${part('hour')}:${part('minute')}:${part('second')}`;
}

export default function CodesPage() {
  const [env, setEnv] = useState('');
  const [envFilter, setEnvFilter] = useState('');
  const [prefixInput, setPrefixInput] = useState('');
  const [prefix, setPrefix] = useState('');
  const [eventType, setEventType] = useState('');
  const [claimState, setClaimState] = useState('');
  const [dateFrom, setDateFrom] = useState('');
  const [dateTo, setDateTo] = useState('');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<ListPage<CodeItem> | null>(null);
  const [error, setError] = useState('');
  const [exporting, setExporting] = useState(false);
  const [openCode, setOpenCode] = useState<string | null>(null);
  const seqRef = useRef(0);

  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);

  // 码前缀 ≥4 位才提交（后端同样 400）：过短前缀命中过宽，前端先给出可读提示。
  function applyPrefix() {
    const next = prefixInput.trim();
    if (next.length > 0 && next.length < MIN_PREFIX) {
      setError(`码前缀至少 ${MIN_PREFIX} 位（当前 ${next.length} 位）`);
      return;
    }
    setPage(1);
    setPrefix(next);
  }

  function reset() {
    setPrefixInput(''); setPrefix('');
    setEventType(''); setClaimState('');
    setDateFrom(''); setDateTo(''); setEnvFilter('');
    setPage(1); setError('');
  }

  // CSV 导出（AT-074）：携带当前筛选（含 env），blob 下载；文件名取响应头 Content-Disposition，缺失则本地时间戳。
  // 导出中禁用按钮；失败统一展示 {code}: {message}（如 EXPORT_TOO_LARGE 提示缩小筛选）。
  async function exportCsv() {
    if (exporting) return;
    const qs = new URLSearchParams();
    if (prefix) qs.set('q', prefix);
    if (eventType) qs.set('eventType', eventType);
    if (claimState) qs.set('state', claimState);
    if (envFilter) qs.set('env', envFilter);
    if (dateFrom) qs.set('from', `${dateFrom}T00:00:00+08:00`);
    if (dateTo) qs.set('to', `${dateTo}T23:59:59.999+08:00`);
    setExporting(true);
    try {
      const resp = await fetch(`/api/v1/exports/codes.csv?${qs.toString()}`, { credentials: 'include' });
      if (!resp.ok) {
        const json = await resp.json().catch(() => null) as { code?: string; message?: string } | null;
        throw new ApiError(resp.status, json?.code ?? `HTTP_${resp.status}`, json?.message ?? '导出失败', false);
      }
      const blob = await resp.blob();
      const disposition = resp.headers.get('Content-Disposition') ?? '';
      const matched = /filename="?([^";]+)"?/i.exec(disposition);
      const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = matched ? matched[1] : `codes-${stamp}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      setError('');
    } catch (e) {
      setError(errMsg(e));
    } finally {
      setExporting(false);
    }
  }

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (prefix) qs.set('q', prefix);
    if (eventType) qs.set('eventType', eventType);
    if (claimState) qs.set('state', claimState);
    if (envFilter) qs.set('env', envFilter);
    // 日期按门店本地日界（含 +08:00）过滤 code_event.occurred_at，避免纯日期被当作 UTC 日界。
    if (dateFrom) qs.set('from', `${dateFrom}T00:00:00+08:00`);
    if (dateTo) qs.set('to', `${dateTo}T23:59:59.999+08:00`);
    try {
      const res = await api<ListPage<CodeItem>>(`/api/v1/codes?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e)); setData(null);
    }
  }, [prefix, eventType, claimState, envFilter, dateFrom, dateTo, page]);

  // 筛选/页码变化：先清空陈旧行与旧错误（M3 Low 修复模式），加载中与错误互斥渲染。
  useEffect(() => {
    setData(null);
    setError('');
    let active = true;
    (async () => { if (active) await load(); })();
    return () => { active = false; };
  }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>码台账 <EnvBadge env={env} /></h1>
      <p>
        只读视图：每个码的当前占用、最近事件与不可变事件流水；按码前缀、事件类型、时间窗与占用状态筛选。
        码无事件时不出现在台账（无证据不臆造）；占用释放属独立能力，本页不提供任何修改入口。
      </p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap', alignItems: 'center' }}>
        <input aria-label="码前缀" placeholder={`码前缀（≥${MIN_PREFIX} 位，回车搜索）`}
          value={prefixInput} style={{ minWidth: 260, fontFamily: 'monospace' }}
          onChange={(e) => setPrefixInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') applyPrefix(); }} />
        <button onClick={applyPrefix}>搜索</button>
        <select aria-label="事件类型" value={eventType}
          onChange={(e) => { setEventType(e.target.value); setPage(1); }}>
          {EVENT_TYPE_OPTIONS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
        </select>
        <select aria-label="占用状态" value={claimState}
          onChange={(e) => { setClaimState(e.target.value); setPage(1); }}>
          {CLAIM_STATE_OPTIONS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
        </select>
        <label>事件日期起 <input type="date" aria-label="事件日期起" value={dateFrom}
          onChange={(e) => { setDateFrom(e.target.value); setPage(1); }} /></label>
        <label>止 <input type="date" aria-label="事件日期止" value={dateTo}
          onChange={(e) => { setDateTo(e.target.value); setPage(1); }} /></label>
        <select aria-label="环境筛选" value={envFilter}
          onChange={(e) => { setEnvFilter(e.target.value); setPage(1); }}>
          {ENV_OPTIONS.map((v) => <option key={v} value={v}>{v === '' ? '当前环境' : v}</option>)}
        </select>
        <button onClick={reset}>重置</button>
        <button onClick={() => void exportCsv()} disabled={exporting}>
          {exporting ? '导出中…' : '导出 CSV'}
        </button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? (error ? null : <p>加载中…</p>) : data.items.length === 0 ? (
        <p>暂无匹配的追溯码（可调整前缀/事件类型/日期/占用状态后重试）。</p>
      ) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr>
                <th>码</th><th>格式</th><th>最近事件类型</th><th>事件数</th><th>最近事件时间</th>
                <th>占用状态</th><th>占用范围</th><th>单号</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.code} style={{ cursor: 'pointer' }} onClick={() => setOpenCode(r.code)}>
                  <td style={{ fontFamily: 'monospace' }}>{r.code}</td>
                  <td>{r.format}</td>
                  <td>{stateZh(r.latestEventType, EVENT_TYPE_ZH)}</td>
                  <td>{r.eventCount}</td>
                  <td>{fmtShanghai(r.lastEventAt)}</td>
                  <td style={{ color: r.claimState === 'ACTIVE' ? '#a00' : undefined }}>
                    {stateZh(r.claimState, CLAIM_STATE_ZH)}
                  </td>
                  <td>{stateZh(r.claimScope, SCOPE_ZH)}</td>
                  <td style={{ fontFamily: 'monospace' }}>{r.billCode ?? '—'}</td>
                  <td>
                    <button onClick={(e) => { e.stopPropagation(); setOpenCode(r.code); }}>详情</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center' }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
            <span>第 {data.page} / {totalPages} 页 · 共 {data.total} 条</span>
            <button disabled={page >= totalPages} onClick={() => setPage(page + 1)}>下一页</button>
          </div>
        </>
      )}
      {openCode !== null && (
        <CodeDrawer code={openCode} envFilter={envFilter} onClose={() => setOpenCode(null)} />
      )}
    </section>
  );
}

function CodeDrawer({ code, envFilter, onClose }: {
  code: string; envFilter: string; onClose: () => void;
}) {
  const [detail, setDetail] = useState<CodeDetail | null>(null);
  const [error, setError] = useState('');
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        // 详情与列表同一环境口径：设置环境筛选时必须带 env，否则跨环境码会 404。
        const url = envFilter
          ? `/api/v1/codes/${encodeURIComponent(code)}?env=${encodeURIComponent(envFilter)}`
          : `/api/v1/codes/${encodeURIComponent(code)}`;
        const d = await api<CodeDetail>(url);
        if (active) { setDetail(d); setError(''); }
      } catch (e) {
        if (active) { setError(errMsg(e)); setDetail(null); }
      }
    })();
    return () => { active = false; };
  }, [code, envFilter, reloadKey]);

  return (
    <div style={overlay}>
      <div style={drawer} role="dialog" aria-modal="true" aria-label={`追溯码 ${code} 台账详情`}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <h3 style={{ margin: 0 }}>追溯码台账</h3>
          <div style={{ display: 'flex', gap: 8 }}>
            {/* 只读页：仅刷新/关闭（不提供改结果或释放占用的任何写入口）。 */}
            <button onClick={() => setReloadKey(reloadKey + 1)}>刷新</button>
            <button onClick={onClose}>关闭</button>
          </div>
        </div>
        {error && <p role="alert">{error}</p>}
        {detail === null ? (error ? null : <p>加载中…</p>) : (
          <>
            <p>
              码 <span style={{ fontFamily: 'monospace' }}>{detail.code.code}</span>
              {detail.code.codeRaw !== detail.code.code ? `（原文 ${detail.code.codeRaw}）` : ''}<br />
              格式 {detail.code.format} · 首次出现 {fmtShanghai(detail.code.firstSeenAt)} · 环境 {detail.code.env}
            </p>

            <h4>事件流水（按时间升序，共 {detail.events.length} 条）</h4>
            {detail.events.length === 0 ? <p>无事件记录。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead>
                  <tr><th>事件 id</th><th>类型</th><th>方向</th><th>发生时间</th><th>业务时间</th>
                    <th>前序事件</th><th>来源行</th><th>任务</th></tr>
                </thead>
                <tbody>
                  {detail.events.map((e) => (
                    <tr key={e.eventId}>
                      <td>{e.eventId}</td>
                      <td>{stateZh(e.eventType, EVENT_TYPE_ZH)}</td>
                      <td>{e.direction}</td>
                      <td>{fmtShanghai(e.occurredAt)}</td>
                      <td>{fmtShanghai(e.businessDatetime)}</td>
                      {/* 前序事件只标注 id（事件无独立详情页，不做伪链接）。 */}
                      <td>{e.predecessorEventId ?? '—'}</td>
                      {/* source_line_id=0 表示外部证据事件（无本地来源行）。 */}
                      <td>{e.sourceLineId === 0 ? '—' : e.sourceLineId}</td>
                      <td>{e.uploadTaskId ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            <h4>占用记录（共 {detail.claims.length} 条）</h4>
            {detail.claims.length === 0 ? <p>无占用记录。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>状态</th><th>范围</th><th>任务</th><th>占用时间</th><th>释放时间</th><th>释放人</th></tr></thead>
                <tbody>
                  {detail.claims.map((c, i) => (
                    <tr key={i}>
                      <td>{stateZh(c.claimState, CLAIM_STATE_ZH)}</td>
                      <td>{stateZh(c.eventScope, SCOPE_ZH)}</td>
                      <td>{c.uploadTaskId ?? '—'}</td>
                      <td>{fmtShanghai(c.claimedAt)}</td>
                      <td>{fmtShanghai(c.releasedAt)}</td>
                      <td>{c.releasedBy ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            <h4>逐码平台结果（UNKNOWN 单列，不并入成功；不回显平台原始响应）</h4>
            {detail.results.length === 0 ? <p>无平台结果记录。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead>
                  <tr><th>结果</th><th>原因</th><th>单号</th><th>分片状态</th><th>任务状态</th>
                    <th>请求号</th><th>HTTP</th><th>记录时间</th></tr>
                </thead>
                <tbody>
                  {detail.results.map((r, i) => (
                    <tr key={i} style={{
                      background: r.result === 'UNKNOWN' ? '#fff3cd' : r.result === 'FAILED' ? '#f8d7da' : undefined,
                    }}>
                      <td>{stateZh(r.result, CODE_RESULT_ZH)}</td>
                      <td>{reasonZh(r.reason)}</td>
                      <td style={{ fontFamily: 'monospace' }}>{r.billCode}</td>
                      <td>{stateZh(r.partState, TASK_STATE_ZH)}</td>
                      <td>{stateZh(r.taskState, TASK_STATE_ZH)}</td>
                      <td>{r.requestId ?? '—'}</td>
                      <td>{r.httpStatus ?? '—'}</td>
                      <td>{fmtShanghai(r.recordedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            <h4>退货确认记录（共 {detail.confirmations.length} 条）</h4>
            {detail.confirmations.length === 0 ? <p>无退货确认记录。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead>
                  <tr><th>任务</th><th>决定</th><th>关联类型</th><th>前序任务</th><th>确认人</th><th>确认时间</th></tr>
                </thead>
                <tbody>
                  {detail.confirmations.map((c) => (
                    <tr key={c.taskId}>
                      <td>{c.taskId}</td>
                      <td>{stateZh(c.decision, DECISION_ZH)}</td>
                      <td>{stateZh(c.relationKind, RELATION_KIND_ZH)}</td>
                      <td>{c.predecessorTaskId ?? '—'}</td>
                      <td>{c.confirmedBy}</td>
                      <td>{fmtShanghai(c.confirmedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            <h4>父子关系</h4>
            <p style={{ margin: '4px 0' }}>父码（本码为子，共 {detail.relations.parents.length} 条）</p>
            {detail.relations.parents.length === 0 ? <p>无父码关系。</p> : (
              <ul>
                {detail.relations.parents.map((r, i) => (
                  <li key={i}><span style={{ fontFamily: 'monospace' }}>{r.code}</span>
                    {' · '}{r.relationSource} · {fmtShanghai(r.observedAt)}</li>
                ))}
              </ul>
            )}
            <p style={{ margin: '4px 0' }}>子码（本码为父，共 {detail.relations.children.length} 条）</p>
            {detail.relations.children.length === 0 ? <p>无子码关系。</p> : (
              <ul>
                {detail.relations.children.map((r, i) => (
                  <li key={i}><span style={{ fontFamily: 'monospace' }}>{r.code}</span>
                    {' · '}{r.relationSource} · {fmtShanghai(r.observedAt)}</li>
                ))}
              </ul>
            )}
          </>
        )}
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', justifyContent: 'flex-end', zIndex: 100 };
const drawer: CSSProperties = { background: '#fff', padding: 20, width: 900, maxWidth: '96%', height: '100%', overflow: 'auto', boxSizing: 'border-box', boxShadow: '-4px 0 12px rgba(0,0,0,0.2)' };