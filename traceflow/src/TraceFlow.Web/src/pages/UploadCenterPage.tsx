import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api';
import EnvBadge from '../components/EnvBadge';

interface ListPage<T> { items: T[]; page: number; pageSize: number; total: number }

interface UploadSummary {
  total: number; succeeded: number; failed: number; settled: number; partialSuccess: number;
  processing: number; acceptanceUnknown: number; resultUnresolved: number; manualAction: number;
  finalSuccessRate: number | null;
}
interface UploadItem {
  id: string; eventKey: string; taskType: string; state: string; stateReason: string | null; env: string;
  verificationState: string; partCount: number; codeCount: number; succeededCodes: number;
  failedCodes: number; unknownCodes: number; succeededParts: number; failedParts: number;
  partialSuccessParts: number; processingParts: number; acceptanceUnknownParts: number;
  resultUnresolvedParts: number; manualActionParts: number; createdAt: string;
}
interface UploadDetail {
  retryPathCertified: boolean;
  task: {
    taskId: string; eventKey: string; taskType: string; env: string; sourceSnapshotId: string | null;
    predecessorTaskId: string | null; state: string; stateReason: string | null; version: number;
    createdAt: string; verificationState: string; dependencyState: string; matchState: string;
    sourceIntegrity: string;
  };
  parts: {
    partId: string; partIndex: number; billCode: string; state: string; queryCount: number;
    nextQueryAt: string | null; firstSentAt: string | null; resultRows: number;
  }[];
  attempts: {
    attemptId: string; partId: string | null; attemptNo: number; attemptKind: string; startedAt: string;
    finishedAt: string | null; httpStatus: number | null; requestId: string | null;
    platformTaskId: string | null; errorKind: string | null; isStaleEvidence: boolean;
  }[];
  codeResults: {
    resultId: string; partId: string; codeId: string; codeNormalized: string; result: string;
    reason: string | null; attemptId: string; recordedAt: string;
  }[];
  timeline: { at: string; kind: string; detail: string | null; actor: string | null }[];
}

const PAGE_SIZE = 20;
const STATE_OPTIONS = ['ALL', 'Blocked', 'Ready', 'Dispatching', 'Accepted', 'AcceptanceUnknown', 'Processing',
  'RetryWait', 'PartialSuccess', 'ResultUnresolved', 'Succeeded', 'Failed', 'ManualAction', 'Cancelled'];
const TYPE_OPTIONS = ['ALL', 'RETAIL_321', 'PURCHASE_IN_102', 'CONSUMER_RETURN_116', 'PURCHASE_RETURN_202'];
const STATE_ZH: Record<string, string> = {
  Blocked: '阻断', Ready: '待上传', Dispatching: '发送中', Accepted: '已受理', AcceptanceUnknown: '受理未知',
  Processing: '平台处理中', RetryWait: '等待重试', PartialSuccess: '部分成功', ResultUnresolved: '结果未决',
  Succeeded: '成功', Failed: '失败', ManualAction: '需人工核对', Cancelled: '已取消',
};
const TYPE_ZH: Record<string, string> = {
  RETAIL_321: '零售销售 321', PURCHASE_IN_102: '采购入库 102',
  CONSUMER_RETURN_116: '消费者退货 116', PURCHASE_RETURN_202: '采购退货 202',
};
const CODE_RESULT_ZH: Record<string, string> = { SUCCESS: '成功', FAILED: '失败', UNKNOWN: 'UNKNOWN（未知，不计成功）' };
const VERIFY_STATE_ZH: Record<string, string> = {
  PASSED: '核验通过', FAILED: '核验失败', UNRESOLVED: '核验未决', WAIT_RETRY: '等待重试',
};
const REASON_ZH: Record<string, string> = {
  PLATFORM_NOT_FOUND: '平台无此码', PLATFORM_REJECTED: '平台拒绝', VERIFY_CHANGED_AFTER_SEND: '发送后核验变更',
  VERIFY_CHANGED_WHILE_DISPATCHING: '发送中核验变更', NO_UPLOADABLE_LINES: '无可上传行',
  RETURN_CONFIRM_REQUIRED: '待首次人工确认', RETURN_CONFIRM_REJECTED: '人工确认已驳回',
  RETURN_CONFIRM_REJECTED_SUPERSEDED: '驳回后被新快照撤销', RETURN_CONFIRMED: '已确认',
  SALE_LINES_REMOVED: '销售行已移除', RETURN_LINES_REMOVED: '退货行已移除',
  RULE_VERSION_CHANGED: '规则版本变更', WAIT_DEPENDENCY: '等待采购入库依赖',
  PREDECESSOR_MISSING: '缺少前序销售事件', PREDECESSOR_AMBIGUOUS: '前序销售事件不唯一',
  ALREADY_RETURNED: '该码已退货',
  // 平台/投递侧状态原因：未知值保留原文（绝不臆造中文含义）。
  PARTIAL_ACCEPTED: '部分受理', DELIVERY_FAILED: '投递失败', MANUAL_REQUIRED: '需人工处置',
  PLATFORM_ACCEPTED: '平台已受理', ACCEPTANCE_UNKNOWN: '受理未知', RESULT_UNRESOLVED: '结果未决',
};
function reasonZh(reason: string | null): string {
  if (!reason) return '—';
  return REASON_ZH[reason] ?? reason;
}
const RETRY_BLOCK_REASON = '受控重试通道未取证（top.retryPathCertified=false），禁止重发；请等待回查或联系平台';
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

export default function UploadCenterPage() {
  const [env, setEnv] = useState('');
  const [envFilter, setEnvFilter] = useState('');
  const [state, setState] = useState('ALL');
  const [taskType, setTaskType] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<ListPage<UploadItem> | null>(null);
  const [summary, setSummary] = useState<UploadSummary | null>(null);
  const [error, setError] = useState('');
  const [openId, setOpenId] = useState<string | null>(null);
  const seqRef = useRef(0);
  const [searchParams, setSearchParams] = useSearchParams();

  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);

  // 销售页“任务链接”钻取：/uploads?taskId=xxx 直接打开抽屉。
  useEffect(() => {
    const fromQuery = searchParams.get('taskId');
    if (fromQuery) setOpenId(fromQuery);
    // 仅首次读入查询参数；后续由本地状态控制。
  }, []);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (envFilter) qs.set('env', envFilter);
    if (state !== 'ALL') qs.set('state', state);
    if (taskType !== 'ALL') qs.set('taskType', taskType);
    try {
      const res = await api<ListPage<UploadItem> & { summary: UploadSummary }>(`/api/v1/upload-tasks?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData({ items: res.items, page: res.page, pageSize: res.pageSize, total: res.total });
      setSummary(res.summary);
      setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [envFilter, state, taskType, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  function closeDrawer() {
    setOpenId(null);
    if (searchParams.get('taskId')) setSearchParams({}, { replace: true });
  }

  return (
    <section>
      <h1>上传中心 <EnvBadge env={env} /></h1>
      <p>
        成功率口径 = 最终全部成功（Succeeded ÷（Succeeded + Failed））；部分成功/处理中/受理未知/结果未决/需人工核对单列，
        UNKNOWN 码不计入成功。
      </p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap', alignItems: 'center' }}>
        <select value={state} onChange={(e) => { setState(e.target.value); setPage(1); }}>
          {STATE_OPTIONS.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部状态' : `${STATE_ZH[s] ?? s}（${s}）`}</option>)}
        </select>
        <select value={taskType} onChange={(e) => { setTaskType(e.target.value); setPage(1); }}>
          {TYPE_OPTIONS.map((t) => <option key={t} value={t}>{t === 'ALL' ? '全部类型' : TYPE_ZH[t] ?? t}</option>)}
        </select>
        <select value={envFilter} onChange={(e) => { setEnvFilter(e.target.value); setPage(1); }}>
          <option value="">当前环境</option><option value="MOCK">MOCK</option><option value="LIVE">LIVE</option>
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {summary && (
        <p style={{ background: '#f5f5f5', padding: 8 }}>
          任务汇总（当前状态/类型/环境筛选后） {summary.total} · 成功 {summary.succeeded} · 失败 {summary.failed} · 已结算 {summary.settled} ·
          部分成功 {summary.partialSuccess} · 处理中 {summary.processing} · 受理未知 {summary.acceptanceUnknown} ·
          结果未决 {summary.resultUnresolved} · 需人工核对 {summary.manualAction} ·
          最终成功率 {summary.finalSuccessRate === null ? '—（尚无结算任务）' : `${(summary.finalSuccessRate * 100).toFixed(2)}%`}
        </p>
      )}
      {data === null ? (error ? null : <p>加载中…</p>) : data.items.length === 0 ? (
        <p>暂无上传任务（可调整状态/类型/环境后重试）。</p>
      ) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr>
                <th>任务</th><th>类型</th><th>状态</th><th>分片进度</th><th>码数</th>
                <th>成功码</th><th>失败码</th><th>UNKNOWN 码</th><th>创建时间</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.id}>
                  <td title={r.eventKey}>#{r.id}</td>
                  <td>{TYPE_ZH[r.taskType] ?? r.taskType}</td>
                  <td>
                    {STATE_ZH[r.state] ?? r.state}
                    {r.stateReason ? <div style={{ fontSize: 12, color: '#666' }}>{reasonZh(r.stateReason)}</div> : null}
                  </td>
                  <td title={`成功 ${r.succeededParts} / 失败 ${r.failedParts} / 部分 ${r.partialSuccessParts} / 处理中 ${r.processingParts} / 受理未知 ${r.acceptanceUnknownParts} / 结果未决 ${r.resultUnresolvedParts} / 人工 ${r.manualActionParts}`}>
                    {r.succeededParts}/{r.partCount} 成功
                    {r.failedParts > 0 ? ` · 失败 ${r.failedParts}` : ''}
                    {r.partialSuccessParts > 0 ? ` · 部分 ${r.partialSuccessParts}` : ''}
                    {r.manualActionParts > 0 ? ` · 人工 ${r.manualActionParts}` : ''}
                  </td>
                  <td>{r.codeCount}</td>
                  <td>{r.succeededCodes}</td>
                  <td>{r.failedCodes}</td>
                  <td style={{ background: r.unknownCodes > 0 ? '#fff3cd' : undefined }}>{r.unknownCodes}</td>
                  <td>{fmtShanghai(r.createdAt)}</td>
                  <td>
                    <button onClick={() => setOpenId(r.id)}>详情</button>
                    {r.state === 'ManualAction' && <button onClick={() => setOpenId(r.id)} style={{ marginLeft: 4 }}>人工核对</button>}
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
      {openId !== null && (
        <UploadDrawer taskId={openId} envFilter={envFilter} onClose={closeDrawer} onChanged={() => void load()} />
      )}
    </section>
  );
}

function UploadDrawer({ taskId, envFilter, onClose, onChanged }: {
  taskId: string; envFilter: string; onClose: () => void; onChanged: () => void;
}) {
  const [detail, setDetail] = useState<UploadDetail | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [reloadKey, setReloadKey] = useState(0);
  const [retryBlocked, setRetryBlocked] = useState<string>('');
  const [busy, setBusy] = useState(false);
  // 幂等键在抽屉打开时生成一次：同一抽屉内的重试提交复用同一 key，关闭重开才换新 key。
  const [retryKey] = useState(() => crypto.randomUUID());

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        // 详情与列表同一环境口径：设置环境筛选时必须带 env，否则跨环境 id 会 404（或读错环境）。
        const detailUrl = envFilter
          ? `/api/v1/upload-tasks/${taskId}?env=${encodeURIComponent(envFilter)}`
          : `/api/v1/upload-tasks/${taskId}`;
        const d = await api<UploadDetail>(detailUrl);
        if (active) {
          setDetail(d); setError('');
          setRetryBlocked(d.retryPathCertified ? '' : RETRY_BLOCK_REASON);
        }
      } catch (e) {
        if (active) setError(errMsg(e));
      }
    })();
    return () => { active = false; };
  }, [taskId, envFilter, reloadKey]);

  const task = detail?.task ?? null;
  const successCodes = detail?.codeResults.filter((c) => c.result === 'SUCCESS').length ?? 0;
  const failedCodes = detail?.codeResults.filter((c) => c.result === 'FAILED').length ?? 0;
  const unknownCodes = detail?.codeResults.filter((c) => c.result !== 'SUCCESS' && c.result !== 'FAILED').length ?? 0;

  async function retry() {
    if (!task) return;
    setBusy(true); setError(''); setStatus('');
    try {
      const r = await api<{ state: string; version: number }>(`/api/v1/upload-tasks/${taskId}/controlled-retry`, {
        method: 'POST', headers: { 'Idempotency-Key': retryKey },
        body: JSON.stringify({ expectedVersion: task.version }),
      });
      setStatus(`受控重试已接受（任务状态 ${r.state}，版本 ${r.version}），后台执行中。`);
      setReloadKey(reloadKey + 1);
      onChanged();
    } catch (e) {
      if (e instanceof ApiError && e.code === 'RETRY_PATH_NOT_CERTIFIED') setRetryBlocked(RETRY_BLOCK_REASON);
      else setError(errMsg(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={overlay}>
      <div style={drawer} role="dialog" aria-modal="true" aria-label={`上传任务 #${taskId} 详情`}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <h3 style={{ margin: 0 }}>上传任务 #{taskId}</h3>
          <button onClick={onClose}>关闭</button>
        </div>
        {error && <p role="alert">{error}</p>}
        {status && <p role="status">{status}</p>}
        {detail === null ? (error ? null : <p>加载中…</p>) : (
          <>
            <p>
              事件键 {detail.task.eventKey} · 类型 {TYPE_ZH[detail.task.taskType] ?? detail.task.taskType} · 环境 {detail.task.env}<br />
              状态 {STATE_ZH[detail.task.state] ?? detail.task.state}
              {detail.task.stateReason ? `（${reasonZh(detail.task.stateReason)}）` : ''} · 版本 {detail.task.version}<br />
              核验 {VERIFY_STATE_ZH[detail.task.verificationState] ?? detail.task.verificationState} · 依赖 {detail.task.dependencyState} · 匹配 {detail.task.matchState} ·
              来源完整性 {detail.task.sourceIntegrity} · 创建 {fmtShanghai(detail.task.createdAt)}
            </p>
            {detail.task.sourceSnapshotId && (
              <p>来源销售快照：<Link to={`/sales?snapshotId=${detail.task.sourceSnapshotId}&env=${encodeURIComponent(detail.task.env)}`}>#{detail.task.sourceSnapshotId}</Link></p>
            )}
            <h4>分片</h4>
            {detail.parts.length === 0 ? <p>无分片。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>#</th><th>账单码</th><th>状态</th><th>回查次数</th><th>下次回查</th><th>首次发送</th><th>结果行</th></tr></thead>
                <tbody>
                  {detail.parts.map((p) => (
                    <tr key={p.partId}>
                      <td>{p.partIndex}</td><td style={{ fontFamily: 'monospace' }}>{p.billCode}</td>
                      <td>{STATE_ZH[p.state] ?? p.state}</td><td>{p.queryCount}</td>
                      <td>{fmtShanghai(p.nextQueryAt)}</td><td>{fmtShanghai(p.firstSentAt)}</td><td>{p.resultRows}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <h4>发送尝试（不回显平台原始响应）</h4>
            {detail.attempts.length === 0 ? <p>无发送尝试。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>尝试</th><th>类型</th><th>开始</th><th>结束</th><th>HTTP</th><th>错误种类</th><th>平台任务号</th><th>证据是否过期</th></tr></thead>
                <tbody>
                  {detail.attempts.map((a) => (
                    <tr key={a.attemptId}>
                      <td>{a.attemptNo}</td><td>{a.attemptKind}</td><td>{fmtShanghai(a.startedAt)}</td>
                      <td>{fmtShanghai(a.finishedAt)}</td><td>{a.httpStatus ?? '—'}</td>
                      <td>{a.errorKind ?? '—'}</td><td>{a.platformTaskId ?? '—'}</td>
                      <td>{a.isStaleEvidence ? '是' : '否'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <h4>逐码结果（UNKNOWN 单列，不并入成功）</h4>
            <p>成功 {successCodes} · 失败 {failedCodes} · UNKNOWN（未知，不计成功） {unknownCodes}</p>
            {detail.codeResults.length === 0 ? <p>无逐码结果。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>追溯码</th><th>结果</th><th>原因</th><th>记录时间</th></tr></thead>
                <tbody>
                  {detail.codeResults.map((c) => (
                    <tr key={c.resultId} style={{ background: c.result === 'UNKNOWN' ? '#fff3cd' : c.result === 'FAILED' ? '#f8d7da' : undefined }}>
                      <td style={{ fontFamily: 'monospace' }}>{c.codeNormalized}</td>
                      <td>{CODE_RESULT_ZH[c.result] ?? c.result}</td>
                      <td>{reasonZh(c.reason)}</td>
                      <td>{fmtShanghai(c.recordedAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <h4>时间线</h4>
            {detail.timeline.length === 0 ? <p>无时间线记录。</p> : (
              <ul>
                {detail.timeline.map((t, i) => (
                  <li key={i}>{fmtShanghai(t.at)} · {t.kind}{t.detail ? ` · ${t.detail}` : ''}{t.actor ? ` · ${t.actor}` : ''}</li>
                ))}
              </ul>
            )}
            <h4>人工处置</h4>
            <p>受控重试仅在平台通道已取证（top.retryPathCertified=true）时可用；未取证会被拒绝并在此禁用按钮。</p>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <button disabled={busy || retryBlocked !== '' || detail.task.state !== 'Failed'}
                aria-describedby="retry-gate-reason"
                title={retryBlocked || (detail.task.state !== 'Failed' ? '当前状态不可重试' : undefined)}
                onClick={() => void retry()}>受控重试</button>
              <span id="retry-gate-reason" role={retryBlocked ? 'alert' : undefined}
                style={{ fontSize: 12, color: retryBlocked ? '#a00' : '#666' }}>
                {retryBlocked || (detail.task.state !== 'Failed'
                  ? '仅 Failed 任务可分片受控重试。'
                  : '通道取证状态由服务端门禁判定：未取证时会被拒绝并在此禁用。')}
              </span>
              <button onClick={() => setReloadKey(reloadKey + 1)}>刷新</button>
            </div>
            {detail.task.state === 'ManualAction' && (
              <ManualVerifyForm detail={detail}
                onDone={(msg) => { setStatus(msg); setError(''); setReloadKey(reloadKey + 1); onChanged(); }} />
            )}
          </>
        )}
      </div>
    </div>
  );
}

function ManualVerifyForm({ detail, onDone }: { detail: UploadDetail; onDone: (msg: string) => void }) {
  const [decision, setDecision] = useState<'ACCEPTED' | 'PROCESSING' | 'NOT_ACCEPTED'>('ACCEPTED');
  // 默认不预选账单码：必须由操作员显式选择，避免把第一片静默当成平台受理单号。
  const [billCode, setBillCode] = useState('');
  const [contract, setContract] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  // 幂等键在表单打开时生成一次：同一表单内的重试复用同一 key，关闭重开才换新 key。
  const [idemKey] = useState(() => crypto.randomUUID());

  async function submit() {
    setError('');
    if (decision !== 'NOT_ACCEPTED' && !billCode.trim()) { setError('ACCEPTED/PROCESSING 必须填写平台受理单号（须等于某分片账单码）'); return; }
    if (decision === 'NOT_ACCEPTED' && !contract.trim()) { setError('NOT_ACCEPTED 必须填写合同/平台协作证据'); return; }
    setBusy(true);
    try {
      const r = await api<{ state: string; version: number; decision: string }>(`/api/v1/upload-tasks/${detail.task.taskId}/manual-verify`, {
        method: 'POST', headers: { 'Idempotency-Key': idemKey },
        body: JSON.stringify({
          decision,
          evidence: {
            platformBillCode: decision === 'NOT_ACCEPTED' ? null : billCode.trim(),
            contractEvidence: contract.trim() ? contract.trim() : null,
          },
          expectedVersion: detail.task.version,
        }),
      });
      onDone(`人工核对已提交（${r.decision}）：任务状态 ${r.state}，版本 ${r.version}。`);
    } catch (e) {
      setError(errMsg(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ border: '1px solid #ccc', padding: 12, marginTop: 8, background: '#fafafa' }}>
      <h4 style={{ marginTop: 0 }}>人工核对（受理未知）</h4>
      <p style={{ fontSize: 12 }}>
        ACCEPTED / PROCESSING 需平台受理单号且必须等于本任务某分片账单码；NOT_ACCEPTED 需一致性窗口已过并提供合同证据。
      </p>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginBottom: 8 }}>
        <label><input type="radio" checked={decision === 'ACCEPTED'} onChange={() => setDecision('ACCEPTED')} /> 平台已受理（ACCEPTED）</label>
        <label><input type="radio" checked={decision === 'PROCESSING'} onChange={() => setDecision('PROCESSING')} /> 平台处理中（PROCESSING）</label>
        <label><input type="radio" checked={decision === 'NOT_ACCEPTED'} onChange={() => setDecision('NOT_ACCEPTED')} /> 平台未受理（NOT_ACCEPTED）</label>
      </div>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: 8 }}>
        <select value={detail.parts.some((p) => p.billCode === billCode) ? billCode : ''}
          onChange={(e) => setBillCode(e.target.value)} disabled={decision === 'NOT_ACCEPTED'}>
          <option value="">选择分片账单码…</option>
          {detail.parts.map((p) => <option key={p.partId} value={p.billCode}>#{p.partIndex} {p.billCode}</option>)}
        </select>
        <input value={billCode} onChange={(e) => setBillCode(e.target.value)} placeholder="平台受理单号（= 分片账单码）"
          style={{ minWidth: 280, fontFamily: 'monospace' }} disabled={decision === 'NOT_ACCEPTED'} />
      </div>
      <textarea value={contract} onChange={(e) => setContract(e.target.value)} rows={2} maxLength={500}
        style={{ width: '100%' }} placeholder={decision === 'NOT_ACCEPTED' ? '合同/平台协作证据（必填）' : '可选补充证据（合同/协作记录）'} />
      {error && <p role="alert">{error}</p>}
      <div style={{ marginTop: 8 }}>
        <button disabled={busy} onClick={() => void submit()}>提交人工核对（版本 {detail.task.version}）</button>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', justifyContent: 'flex-end', zIndex: 100 };
const drawer: CSSProperties = { background: '#fff', padding: 20, width: 720, maxWidth: '96%', height: '100%', overflow: 'auto', boxShadow: '-4px 0 12px rgba(0,0,0,0.2)' };