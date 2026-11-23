import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api';
import EnvBadge from '../components/EnvBadge';

interface ListPage<T> { items: T[]; page: number; pageSize: number; total: number }

interface SaleItem {
  snapshotId: string; docKey: string; version: number; businessDatetimeUtc: string | null;
  completeness: string; lineCount: number; codeCount: number; env: string;
  verificationState: string | null; taskId: string | null; taskState: string | null;
}
interface SaleLine {
  lineId: string; sourceLineKey: string; productSourceId: string; quantity: string; unit: string | null;
  batchRaw: string | null; traceCodeNormalized: string | null; codeFormat: string; eventKind: string;
}
interface SaleCode {
  codeId: string; codeNormalized: string; sourceLineId: string; result: string;
  reason: string | null; checkedAt: string;
}
interface SaleTaskPart { partId: string; partIndex: number; billCode: string; state: string }
interface SaleDetail {
  snapshot: {
    snapshotId: string; docKey: string; version: number; businessDatetimeUtc: string | null;
    completeness: string; contentHash: string; observedAt: string;
  };
  lines: SaleLine[];
  codes: SaleCode[];
  verification: {
    id: string; state: string; ruleVersion: string; verifyReason: string | null; failCode: string | null;
    verifiedAt: string | null; isCurrent: boolean;
  } | null;
  task: {
    taskId: string; eventKey: string; taskType: string; state: string; stateReason: string | null;
    version: number; createdAt: string; parts: SaleTaskPart[];
  } | null;
}

const PAGE_SIZE = 20;
const ENV_OPTIONS = ['', 'MOCK', 'LIVE'];
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
// 后端 datetime2 出网不带偏移量，无偏移时按 UTC 解释，再转 Asia/Shanghai 显示（与既有页面一致）。
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

// 核验/逐码状态与原因的中文映射：未知代码原样展示（绝不把 UNKNOWN 当成功）。
const VERIFY_STATE_ZH: Record<string, string> = {
  PASSED: '核验通过', FAILED: '核验失败', UNRESOLVED: '核验未决', WAIT_RETRY: '等待重试',
};
const CODE_RESULT_ZH: Record<string, string> = {
  PASS: '通过', FAIL: '失败', UNKNOWN: '未知（UNKNOWN）',
};
const TASK_STATE_ZH: Record<string, string> = {
  Blocked: '阻断', Ready: '待上传', Dispatching: '发送中', Accepted: '已受理',
  AcceptanceUnknown: '受理未知', Processing: '平台处理中', RetryWait: '等待重试',
  PartialSuccess: '部分成功', ResultUnresolved: '结果未决', Succeeded: '成功',
  Failed: '失败', ManualAction: '需人工核对', Cancelled: '已取消',
};
const REASON_ZH: Record<string, string> = {
  PLATFORM_NOT_FOUND: '平台无此码', PLATFORM_REJECTED: '平台拒绝查询', PLATFORM_INSTORE: '平台在店',
  PLATFORM_UNAVAILABLE: '平台不可用', WAIT_DEPENDENCY: '等待采购入库依赖',
  DEPENDENCY_PURCHASE_IN_SUCCEEDED: '采购入库已成功', MISSING_CODE: '缺少追溯码',
  CODE_FORMAT: '码格式错误', DUPLICATE_CODE: '码重复', UNKNOWN_CLASSIFICATION: '商品分类未知',
  RETURN_CONFIRM_REQUIRED: '待首次人工确认', RETURN_CONFIRM_REJECTED: '人工确认已驳回',
  RETURN_CONFIRM_REJECTED_SUPERSEDED: '驳回后被新快照撤销', RETURN_CONFIRMED: '已确认',
  NO_UPLOADABLE_LINES: '无可上传行', VERIFY_CHANGED_AFTER_SEND: '发送后核验结果变更',
  VERIFY_CHANGED_WHILE_DISPATCHING: '发送中核验结果变更', SALE_LINES_REMOVED: '销售行已移除',
  RULE_VERSION_CHANGED: '规则版本变更', PREDECESSOR_MISSING: '缺少前序销售事件',
  PREDECESSOR_AMBIGUOUS: '前序销售事件不唯一', ALREADY_RETURNED: '该码已退货',
};
function reasonZh(reason: string | null): string {
  if (!reason) return '—';
  if (reason.startsWith('VERIFY_FAILED:')) return `核验失败：${reason.slice('VERIFY_FAILED:'.length)}`;
  return REASON_ZH[reason] ?? reason;
}
function stateZh(state: string | null, dict: Record<string, string>): string {
  if (!state) return '—';
  return dict[state] ?? state;
}

export default function SalesPage() {
  const [env, setEnv] = useState('');
  const [envFilter, setEnvFilter] = useState('');
  const [dateFrom, setDateFrom] = useState('');
  const [dateTo, setDateTo] = useState('');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<ListPage<SaleItem> | null>(null);
  const [error, setError] = useState('');
  // 钻取目标带上可见性环境：详情与列表同一 env 口径，否则 LIVE 行在 MOCK 宿主下会 404。
  const [detail, setDetail] = useState<{ id: string; env: string } | null>(null);
  const seqRef = useRef(0);
  const [searchParams] = useSearchParams();

  // 上传中心回链：/sales?snapshotId=xxx[&env=LIVE] 直接打开钻取对话框。
  useEffect(() => {
    const fromQuery = searchParams.get('snapshotId');
    if (fromQuery) setDetail({ id: fromQuery, env: searchParams.get('env') ?? '' });
    // 仅首次读入查询参数。
  }, []);

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
    if (envFilter) qs.set('env', envFilter);
    // 列表按 Asia/Shanghai 展示业务时间，筛选也必须按门店本地日界（含 +08:00 偏移），
    // 否则后端会把纯日期当作 UTC 日界，导致跨日边界单据筛错。
    if (dateFrom) qs.set('dateFrom', `${dateFrom}T00:00:00+08:00`);
    if (dateTo) qs.set('dateTo', `${dateTo}T23:59:59.999+08:00`);
    try {
      const res = await api<ListPage<SaleItem>>(`/api/v1/pos-sales?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [envFilter, dateFrom, dateTo, page]);

  // 筛选/页码变化：先清空陈旧行与旧错误（与 ReturnsPage 同一模式），让加载态重新出现再按新条件取数，
  // 绝不短暂显示上一筛选/上一页的数据或错误（load 的依赖只有筛选与页码）。
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
      <h1>POS 销售 <EnvBadge env={env} /></h1>
      <p>销售快照按单号取最新版本；核验失败/未决不视为成功，UNKNOWN 逐码单列展示。</p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap', alignItems: 'center' }}>
        <label>业务日期起 <input type="date" aria-label="业务日期起" value={dateFrom} onChange={(e) => { setDateFrom(e.target.value); setPage(1); }} /></label>
        <label>止 <input type="date" aria-label="业务日期止" value={dateTo} onChange={(e) => { setDateTo(e.target.value); setPage(1); }} /></label>
        <select aria-label="环境筛选" value={envFilter} onChange={(e) => { setEnvFilter(e.target.value); setPage(1); }}>
          {ENV_OPTIONS.map((v) => <option key={v} value={v}>{v === '' ? '当前环境' : v}</option>)}
        </select>
        <button onClick={() => { setData(null); setError(''); setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? (error ? null : <p>加载中…</p>) : data.items.length === 0 ? (
        <p>暂无销售快照（可调整日期/环境后重试）。</p>
      ) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr>
                <th>单号</th><th>业务时间</th><th>版本</th><th>行数</th><th>码数</th>
                <th>核验状态</th><th>任务状态</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.snapshotId} style={{ cursor: 'pointer' }}
                  onClick={() => setDetail({ id: r.snapshotId, env: envFilter || r.env })}>
                  <td>{r.docKey}</td>
                  <td>{fmtShanghai(r.businessDatetimeUtc)}</td>
                  <td>{r.version}</td>
                  <td>{r.lineCount}</td>
                  <td>{r.codeCount}</td>
                  <td>{stateZh(r.verificationState, VERIFY_STATE_ZH)}</td>
                  <td>
                    {r.taskId
                      ? <Link to={`/uploads?taskId=${r.taskId}`} onClick={(e) => e.stopPropagation()}>
                        {stateZh(r.taskState, TASK_STATE_ZH)}
                      </Link>
                      : '—'}
                  </td>
                  <td><button onClick={(e) => { e.stopPropagation(); setDetail({ id: r.snapshotId, env: envFilter || r.env }); }}>钻取</button></td>
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
      {detail !== null && (
        <SaleDetailDialog snapshotId={detail.id} env={detail.env} onClose={() => setDetail(null)} />
      )}
    </section>
  );
}

function SaleDetailDialog({ snapshotId, env, onClose }: {
  snapshotId: string; env: string; onClose: () => void;
}) {
  const [detail, setDetail] = useState<SaleDetail | null>(null);
  const [error, setError] = useState('');
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        // 详情与列表同一环境口径：LIVE 行必须在 ?env=LIVE 下钻取，否则在 MOCK 宿主下 404。
        const url = env
          ? `/api/v1/pos-sales/${snapshotId}?env=${encodeURIComponent(env)}`
          : `/api/v1/pos-sales/${snapshotId}`;
        const d = await api<SaleDetail>(url);
        if (active) { setDetail(d); setError(''); }
      } catch (e) {
        if (active) setError(errMsg(e));
      }
    })();
    return () => { active = false; };
  }, [snapshotId, env, reloadKey]);

  const pass = detail?.codes.filter((c) => c.result === 'PASS').length ?? 0;
  const fail = detail?.codes.filter((c) => c.result === 'FAIL').length ?? 0;
  const unknown = detail?.codes.filter((c) => c.result !== 'PASS' && c.result !== 'FAIL').length ?? 0;

  return (
    <div style={overlay}>
      <div style={panel} role="dialog" aria-modal="true"
        aria-label={`销售快照钻取 · ${detail?.snapshot.docKey ?? snapshotId}`}>
        <h3>销售快照 · {detail?.snapshot.docKey ?? snapshotId}</h3>
        {error && <p role="alert">{error}</p>}
        {detail === null ? (error ? null : <p>加载中…</p>) : (
          <>
            <p>
              版本 {detail.snapshot.version} · 业务时间 {fmtShanghai(detail.snapshot.businessDatetimeUtc)} ·
              完整性 {detail.snapshot.completeness} · 采集时间 {fmtShanghai(detail.snapshot.observedAt)}
            </p>
            {detail.verification ? (
              <p>
                核验结论：<strong>{stateZh(detail.verification.state, VERIFY_STATE_ZH)}</strong>
                {' · 规则版本 '}{detail.verification.ruleVersion}
                {detail.verification.verifyReason ? ` · 原因：${reasonZh(detail.verification.verifyReason)}` : ''}
                {detail.verification.failCode ? ` · 失败码：${detail.verification.failCode}` : ''}
                {' · 核验时间 '}{fmtShanghai(detail.verification.verifiedAt)}
              </p>
            ) : <p>尚无当前核验结论（等待核验作业，不视为成功）。</p>}
            {detail.verification && (
              <p>
                逐码统计：通过 {pass} · 失败 {fail} · UNKNOWN（未知，不计成功） {unknown}
              </p>
            )}
            {detail.task ? (
              <p>
                上传任务：<Link to={`/uploads?taskId=${detail.task.taskId}`}>#{detail.task.taskId}</Link>
                {' · '}{detail.task.taskType} · 状态 {stateZh(detail.task.state, TASK_STATE_ZH)}
                {detail.task.stateReason ? `（${reasonZh(detail.task.stateReason)}）` : ''}
              </p>
            ) : <p>尚无上传任务。</p>}
            <h4>单据行</h4>
            {detail.lines.length === 0 ? <p>无行记录。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>行号</th><th>商品 spid</th><th>追溯码</th><th>批号</th><th>数量</th><th>单位</th><th>码格式</th><th>事件</th></tr></thead>
                <tbody>
                  {detail.lines.map((l) => (
                    <tr key={l.lineId}>
                      <td>{l.sourceLineKey}</td><td>{l.productSourceId}</td>
                      <td style={{ fontFamily: 'monospace' }}>{l.traceCodeNormalized ?? '（缺码）'}</td>
                      <td>{l.batchRaw ?? '（缺）'}</td>
                      <td>{l.quantity}</td><td>{l.unit ?? '—'}</td><td>{l.codeFormat}</td><td>{l.eventKind}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <h4>逐码核验</h4>
            {detail.codes.length === 0 ? <p>尚无逐码结论。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>追溯码</th><th>结论</th><th>原因</th><th>来源行 id</th><th>检查时间</th></tr></thead>
                <tbody>
                  {detail.codes.map((c) => (
                    <tr key={c.codeId} style={{ background: c.result === 'UNKNOWN' ? '#fff3cd' : c.result === 'FAIL' ? '#f8d7da' : undefined }}>
                      <td style={{ fontFamily: 'monospace' }}>{c.codeNormalized}</td>
                      <td>{stateZh(c.result, CODE_RESULT_ZH)}</td>
                      <td>{reasonZh(c.reason)}</td>
                      <td>{c.sourceLineId}</td>
                      <td>{fmtShanghai(c.checkedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {detail.task && detail.task.parts.length > 0 && (
              <>
                <h4>任务分片</h4>
                <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                  <thead><tr><th>#</th><th>账单码</th><th>分片状态</th></tr></thead>
                  <tbody>
                    {detail.task.parts.map((p) => (
                      <tr key={p.partId}><td>{p.partIndex}</td><td>{p.billCode}</td><td>{stateZh(p.state, TASK_STATE_ZH)}</td></tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}
            <div style={{ marginTop: 12, display: 'flex', gap: 8 }}>
              <button onClick={() => setReloadKey(reloadKey + 1)}>刷新</button>
              <button onClick={onClose}>关闭</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 1040, width: '95%', maxHeight: '85vh', overflow: 'auto' };