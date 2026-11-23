import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import EnvBadge from '../components/EnvBadge';

interface ListPage<T> { items: T[]; page: number; pageSize: number; total: number }

interface ReturnCandidate {
  codeId: string | null; sourceLineId: string | null; predecessorEventId: string | null; error: string | null;
  /** 前序销售单据键（来源快照单号）；缺行时为 null。 */
  docKey: string | null;
  /** 前序销售事件发生时间（ISO）；缺行时为 null。 */
  occurredAt: string | null;
  /** 退货码（规范化）；缺行时为 null。 */
  code: string | null;
}
interface ReturnItem {
  id: string; eventKey: string; taskType: string; state: string; stateReason: string | null; version: number;
  predecessorTaskId: string | null; partCount: number; codeCount: number; createdAt: string;
  predecessorCandidates: ReturnCandidate[];
}
interface ConfirmResult { taskId: string; state: string; idempotent: boolean }

const PAGE_SIZE = 20;
type Tab = 'pending' | 'confirmed' | 'rejected';
const TABS: { key: Tab; label: string }[] = [
  { key: 'pending', label: '待确认' },
  { key: 'confirmed', label: '已确认' },
  { key: 'rejected', label: '已驳回' },
];
// 页签即服务端过滤条件（不再对「当前 20 行」做客户端筛选，待确认行不会藏在别的页里）。
// 已确认页签只按原因收窄，不带 state=Blocked（确认后任务已离开 Blocked）。
const TAB_FILTER: Record<Tab, { state?: string; stateReason: string }> = {
  pending: { state: 'Blocked', stateReason: 'RETURN_CONFIRM_REQUIRED' },
  rejected: { state: 'Blocked', stateReason: 'RETURN_CONFIRM_REJECTED,RETURN_CONFIRM_REJECTED_SUPERSEDED' },
  confirmed: { stateReason: 'RETURN_CONFIRMED' },
};
const TASK_TYPE_ZH: Record<string, string> = {
  CONSUMER_RETURN_116: '消费者退货 116（原码退回）',
  PURCHASE_RETURN_202: '采购退货 202',
};
const STATE_ZH: Record<string, string> = {
  Blocked: '阻断', Ready: '待上传', Dispatching: '发送中', Accepted: '已受理', AcceptanceUnknown: '受理未知',
  Processing: '平台处理中', RetryWait: '等待重试', PartialSuccess: '部分成功', ResultUnresolved: '结果未决',
  Succeeded: '成功', Failed: '失败', ManualAction: '需人工核对', Cancelled: '已取消',
};
const REASON_ZH: Record<string, string> = {
  RETURN_CONFIRM_REQUIRED: '待首次人工确认', RETURN_CONFIRM_REJECTED: '人工确认已驳回',
  RETURN_CONFIRM_REJECTED_SUPERSEDED: '驳回后被新快照撤销', RETURN_CONFIRMED: '已确认（符合资格自动上传）',
  PREDECESSOR_MISSING: '缺少前序销售事件', PREDECESSOR_AMBIGUOUS: '前序销售事件不唯一',
  PREDECESSOR_INVALID: '前序事件无效', ALREADY_RETURNED: '该码已退货',
};
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
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
function isPending(r: ReturnItem): boolean {
  return r.state === 'Blocked' && r.stateReason === 'RETURN_CONFIRM_REQUIRED';
}
function candidateErrorZh(error: string | null): string {
  if (!error) return '唯一前序（可确认）';
  return REASON_ZH[error] ?? error;
}

export default function ReturnsPage() {
  const [env, setEnv] = useState('');
  const [tab, setTab] = useState<Tab>('pending');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<ListPage<ReturnItem> | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [confirmTarget, setConfirmTarget] = useState<ReturnItem | null>(null);
  const seqRef = useRef(0);

  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    // 页签在服务端过滤：待确认/已驳回只见 Blocked，已确认只按 RETURN_CONFIRMED 收窄。
    const filter = TAB_FILTER[tab];
    if (filter.state) qs.set('state', filter.state);
    qs.set('stateReason', filter.stateReason);
    try {
      const res = await api<ListPage<ReturnItem>>(`/api/v1/returns?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [page, tab]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;
  const rows = data?.items ?? [];

  return (
    <section>
      <h1>退货 <EnvBadge env={env} /></h1>
      <p>
        消费者退货 116 必须先由人工完成首次确认：选择前序销售事件并勾选首次上报声明后，符合资格将由系统自动上传。
        采购退货 202 采集未接入（G-ERP）。
      </p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        {TABS.map((t) => (
          <button key={t.key} onClick={() => { setData(null); setTab(t.key); setPage(1); setStatus(''); }} disabled={tab === t.key}>
            {t.label}{tab === t.key && data ? `（共 ${data.total}）` : ''}
          </button>
        ))}
        <button onClick={() => { void load(); }}>刷新</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {status && <p role="status">{status}</p>}
      {data === null ? (error ? null : <p>加载中…</p>) : rows.length === 0 ? (
        <p>暂无“{TABS.find((t) => t.key === tab)?.label}”状态的退货任务。</p>
      ) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr>
                <th>任务</th><th>类型</th><th>状态</th><th>原因</th><th>前序候选</th>
                <th>前序任务</th><th>分片</th><th>码数</th><th>创建时间</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td title={r.eventKey}>#{r.id}</td>
                  <td>
                    {TASK_TYPE_ZH[r.taskType] ?? r.taskType}
                    {r.taskType === 'PURCHASE_RETURN_202' && <div style={{ color: '#8a6d3b', fontSize: 12 }}>采购退货采集未接入（G-ERP）</div>}
                  </td>
                  <td>{STATE_ZH[r.state] ?? r.state}</td>
                  <td>{r.stateReason ? (REASON_ZH[r.stateReason] ?? r.stateReason) : '—'}</td>
                  <td>
                    {r.predecessorCandidates.length === 0 ? '—' : (
                      <span title={r.predecessorCandidates.map((c) =>
                        `码 ${c.code ?? c.codeId ?? '?'}：${c.predecessorEventId ? `前序事件 ${c.predecessorEventId}` : candidateErrorZh(c.error)}`).join('\n')}>
                        {r.predecessorCandidates.length} 个码
                        {r.predecessorCandidates.some((c) => c.error)
                          ? `（${r.predecessorCandidates.filter((c) => c.error).length} 个有异常）` : ''}
                      </span>
                    )}
                  </td>
                  <td>{r.predecessorTaskId ? `#${r.predecessorTaskId}` : '—'}</td>
                  <td>{r.partCount}</td>
                  <td>{r.codeCount}</td>
                  <td>{fmtShanghai(r.createdAt)}</td>
                  <td>
                    {isPending(r) && <button onClick={() => { setStatus(''); setConfirmTarget(r); }}>确认/驳回</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {rows.some(isPending) && (
            <div style={{ marginTop: 12 }}>
              <h4>待确认前序候选</h4>
              {rows.filter(isPending).map((r) => (
                <div key={r.id} style={{ border: '1px solid #ddd', padding: 8, marginBottom: 8 }}>
                  <div>任务 #{r.id} · {TASK_TYPE_ZH[r.taskType] ?? r.taskType} · 版本 {r.version}</div>
                  {r.predecessorCandidates.length === 0 ? <p>无前序候选。</p> : (
                    <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                      <thead><tr><th>单号</th><th>时间</th><th>码</th><th>错误</th></tr></thead>
                      <tbody>
                        {r.predecessorCandidates.map((c, i) => (
                          <tr key={i} title={`前序事件 ${c.predecessorEventId ?? '—'} · 来源行 ${c.sourceLineId ?? '—'}`}>
                            <td>{c.docKey ?? '—'}</td>
                            <td>{fmtShanghai(c.occurredAt)}</td>
                            <td style={{ fontFamily: 'monospace' }}>{c.code ?? c.codeId ?? '—'}</td>
                            <td style={{ color: c.error ? '#a00' : undefined }}>{candidateErrorZh(c.error)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                </div>
              ))}
            </div>
          )}
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center' }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
            <span>第 {data.page} / {totalPages} 页 · 共 {data.total} 条</span>
            <button disabled={page >= totalPages} onClick={() => setPage(page + 1)}>下一页</button>
          </div>
        </>
      )}
      {confirmTarget && (
        <ReturnConfirmDialog item={confirmTarget} onClose={() => setConfirmTarget(null)}
          onDone={(r) => {
            setConfirmTarget(null);
            setStatus(`退货任务 #${r.taskId} 已提交，当前状态 ${r.state}${r.idempotent ? '（幂等重放）' : ''}。`);
            void load();
          }} />
      )}
    </section>
  );
}

function ReturnConfirmDialog({ item, onClose, onDone }: {
  item: ReturnItem; onClose: () => void; onDone: (r: ConfirmResult) => void;
}) {
  const [decision, setDecision] = useState<'CONFIRMED' | 'REJECTED'>('CONFIRMED');
  const [picked, setPicked] = useState<string[]>([]);
  const [firstReport, setFirstReport] = useState(false);
  const [evidence, setEvidence] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  // 同一个对话框内重试沿用同一幂等键：网络重试不会造成第二次确认。
  const [idemKey] = useState(() => crypto.randomUUID());

  // 候选里的前序事件去重（每码唯一前序由服务端复核，前端只做勾选回显）。
  const events: { id: string; codeId: string | null }[] = [];
  for (const c of item.predecessorCandidates) {
    if (c.predecessorEventId && !events.some((e) => e.id === c.predecessorEventId))
      events.push({ id: c.predecessorEventId, codeId: c.codeId });
  }
  const failedCandidates = item.predecessorCandidates.filter((c) => c.error);
  const confirmBlocked = events.length === 0 || failedCandidates.length > 0;
  const is202 = item.taskType === 'PURCHASE_RETURN_202';

  function toggle(id: string) {
    setPicked(picked.includes(id) ? picked.filter((x) => x !== id) : [...picked, id]);
  }

  async function submit() {
    setError('');
    if (!evidence.trim()) { setError(decision === 'REJECTED' ? '驳回必须填写证据' : '确认必须填写确认证据'); return; }
    if (decision === 'CONFIRMED') {
      if (events.length === 0) { setError('没有可用的前序销售事件，无法确认退货'); return; }
      if (failedCandidates.length > 0) { setError('存在无唯一前序的退货码，服务端会拒绝确认（可先驳回）'); return; }
      if (picked.length === 0) { setError('请至少选择一个前序销售事件'); return; }
      if (!firstReport) { setError('请勾选“首次上报，确认后符合资格将自动上传”'); return; }
    }
    setBusy(true);
    try {
      const r = await api<ConfirmResult>(`/api/v1/returns/${item.id}/confirm`, {
        method: 'POST',
        body: JSON.stringify({
          expectedVersion: item.version,
          decision,
          predecessorEventIds: picked.map((p) => Number(p)),
          evidence: { reason: evidence.trim() },
          idemKey,
        }),
      });
      onDone(r);
    } catch (e) {
      setBusy(false);
      setError(errMsg(e));
    }
  }

  return (
    <div style={overlay}>
      <div style={panel} role="dialog" aria-modal="true"
        aria-label={`退货首次人工确认 · 任务 #${item.id}`}>
        <h3>退货首次人工确认 · 任务 #{item.id}</h3>
        <p>
          {TASK_TYPE_ZH[item.taskType] ?? item.taskType} · 当前版本 {item.version} · 状态 {STATE_ZH[item.state] ?? item.state}
          （{item.stateReason ? REASON_ZH[item.stateReason] ?? item.stateReason : '—'}）
        </p>
        {is202 && <p role="alert" style={{ background: '#fff3cd', padding: 8 }}>采购退货采集未接入（G-ERP）：仅支持服务/测试数据的确认，无真实采集入口。</p>}
        <div style={{ display: 'flex', gap: 16, marginBottom: 8 }}>
          <label><input type="radio" checked={decision === 'CONFIRMED'} onChange={() => setDecision('CONFIRMED')} /> 确认退货</label>
          <label><input type="radio" checked={decision === 'REJECTED'} onChange={() => setDecision('REJECTED')} /> 驳回</label>
        </div>
        {decision === 'CONFIRMED' && (
          <>
            <h4>选择前序销售事件</h4>
            {events.length === 0 ? <p role="alert">无可用前序销售候选，不能确认（可先驳回）。</p> : (
              <div>
                {events.map((e) => (
                  <label key={e.id} style={{ display: 'block' }}>
                    <input type="checkbox" checked={picked.includes(e.id)} onChange={() => toggle(e.id)} />
                    {' '}前序事件 #{e.id}{e.codeId ? `（退货码 id ${e.codeId}）` : ''}
                  </label>
                ))}
              </div>
            )}
            {failedCandidates.length > 0 && (
              <div role="alert" style={{ color: '#a00', marginTop: 4 }}>
                <div>以下退货码无唯一前序，服务端会拒绝确认（请先驳回或核对前序销售）：</div>
                <ul style={{ margin: 0, paddingLeft: 18, fontSize: 13 }}>
                  {failedCandidates.map((c, i) => (
                    <li key={i}>退货码 id {c.codeId ?? '—'}（来源行 {c.sourceLineId ?? '—'}）：{candidateErrorZh(c.error)}</li>
                  ))}
                </ul>
              </div>
            )}
            <label style={{ display: 'block', marginTop: 8 }}>
              <input type="checkbox" checked={firstReport} onChange={(e) => setFirstReport(e.target.checked)} />
              {' '}首次上报，确认后符合资格将自动上传
            </label>
          </>
        )}
        <h4>{decision === 'REJECTED' ? '驳回证据（必填）' : '确认证据（必填）'}</h4>
        <textarea value={evidence} onChange={(e) => setEvidence(e.target.value)} rows={3} maxLength={500}
          style={{ width: '100%' }} placeholder="例如：顾客持原包装商品退回，码完好" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}>
          <button disabled={busy || (decision === 'CONFIRMED' && confirmBlocked)} onClick={() => void submit()}
            title={decision === 'CONFIRMED' && confirmBlocked ? '前序候选缺失或存在异常码，不能确认' : undefined}>
            {decision === 'REJECTED' ? '提交驳回' : `提交确认（版本 ${item.version}）`}
          </button>{' '}
          <button onClick={onClose}>取消</button>
        </div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 820, width: '95%', maxHeight: '85vh', overflow: 'auto' };