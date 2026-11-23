import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError, type FieldError } from '../api';
import type { Page } from '../m1types';
import type {
  AllocationLineInput, AllocationSaveResult, ConfirmResult, InboundDetail, InboundListItem,
  UpstreamBill, UpstreamDetail,
} from '../m2types';

const MATCH_STATES = ['ALL', 'NOT_REQUIRED', 'UNMATCHED', 'PARTIAL', 'AMBIGUOUS', 'CONFLICT', 'COMPLETE'];
const PAGE_SIZE = 50;
const PRE_SEND = ['Blocked', 'Ready', 'Cancelled'];
function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
function fmt(d: string | null): string { return d ? d.replace('T', ' ').slice(0, 19) : '—'; }
// 仅用于差异高亮（R13a）：资格判定与数量合计全部由后端完成，此处不做业务判定
const round6 = (x: number): number => Math.round(x * 1e6) / 1e6;
const diffOf = (l: { quantity: string; allocatedQuantity: string }): number => round6(Number(l.quantity) - Number(l.allocatedQuantity));
const CONFLICT_HINT = '版本冲突或已冻结：数据可能已被他人修改，请刷新后重试';

export default function InboundsPage() {
  const [docKey, setDocKey] = useState('');
  const [matchState, setMatchState] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<InboundListItem> | null>(null);
  const [error, setError] = useState('');
  const [detailId, setDetailId] = useState<string | null>(null);
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (docKey.trim()) qs.set('docKey', docKey.trim());
    if (matchState !== 'ALL') qs.set('matchState', matchState);
    try {
      const res = await api<Page<InboundListItem>>(`/api/v1/inbounds?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [docKey, matchState, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>入库关联</h1>
      <p>自动匹配优先；差异走人工补配，确认后由系统复核资格并自动上传（无手工上传按钮）。</p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={docKey} onChange={(e) => { setDocKey(e.target.value); setPage(1); }} placeholder="单据编号 djbh（精确）" />
        <select value={matchState} onChange={(e) => { setMatchState(e.target.value); setPage(1); }}>
          {MATCH_STATES.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部匹配态' : s}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>单据编号</th><th>供应商 dwbh</th><th>业务日期</th><th>版本</th><th>完整性</th><th>匹配态</th><th>任务态</th><th>操作</th></tr></thead>
            <tbody>
              {data.records.map((r) => (
                <tr key={r.snapshotId}>
                  <td>{r.docKey}</td><td>{r.supplierDwbh ?? '—'}</td><td>{fmt(r.businessDatetimeUtc)}</td>
                  <td>{r.version}</td><td>{r.completeness}</td>
                  <td>{r.matchState ?? '（未匹配）'}</td><td>{r.taskState ?? '—'}</td>
                  <td><button onClick={() => setDetailId(r.snapshotId)}>详情/补配</button></td>
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
      {detailId !== null && <InboundDetailDialog snapshotId={detailId} onClose={() => { setDetailId(null); void load(); }} />}
    </section>
  );
}

function InboundDetailDialog({ snapshotId, onClose }: { snapshotId: string; onClose: () => void }) {
  const [detail, setDetail] = useState<InboundDetail | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [cancelTarget, setCancelTarget] = useState<{ allocationId: string | null } | null>(null);

  // R13d：409 后的自动重载不能顺手清掉冲突提示，否则用户只能看到一闪而过的错误
  const reload = useCallback(async (keepError = false) => {
    try { setDetail(await api<InboundDetail>(`/api/v1/inbounds/${snapshotId}`)); if (!keepError) setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, [snapshotId]);
  useEffect(() => { let active = true; (async () => { if (active) await reload(); })(); return () => { active = false; }; }, [reload]);

  const frozen = !!detail?.task && !PRE_SEND.includes(detail.task.state);
  const requiredLines = detail?.lines.filter((l) => l.classification === 'REQUIRED') ?? [];
  const allCovered = requiredLines.length > 0 && requiredLines.every((l) => diffOf(l) === 0);
  const notRequired = detail?.match?.matchState === 'NOT_REQUIRED';

  async function rematch() {
    setStatus(''); setError('');
    try {
      const r = await api<{ jobId: string }>(`/api/v1/inbounds/${snapshotId}/rematch`, { method: 'POST', headers: idem() });
      setStatus(`重排已排队（作业 ${r.jobId}），后台处理，稍后刷新查看结果。`);
    } catch (e) { setError(errMsg(e)); }
  }

  function onConflict(e: unknown) {
    if (e instanceof ApiError && e.status === 409) {
      setError(CONFLICT_HINT); setEditing(false); setConfirming(false); setCancelTarget(null); void reload(true);
    } else setError(errMsg(e));
  }

  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>入库单 · {detail?.snapshot.docKey ?? snapshotId}</h3>
        {error && <p role="alert">{error}</p>}
        {status && <p role="status">{status}</p>}
        {detail === null ? <p>加载中…</p> : (
          <>
            <p>
              匹配态：{detail.match?.matchState ?? '（未匹配）'}（版本 {detail.match?.version ?? '—'}）
              {detail.match?.reason ? ` · 原因：${detail.match.reason}` : ''}
            </p>
            {detail.task
              ? <p>上传任务 #{detail.task.taskId} · 状态 {detail.task.state} · 单据号 {detail.task.billCode ?? '（待生成）'}</p>
              : <p>尚无上传任务（确认补配或自动匹配合格后创建）。</p>}
            {frozen && <p role="alert" style={{ background: '#f8d7da', padding: 8 }}>已发起上传，分配冻结（只读）。发送后变更须走异常处理流程。</p>}
            {notRequired && <p>全部行豁免（EXEMPT），无需追溯上传。</p>}
            <h4>本地入库行</h4>
            <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
              <thead><tr><th>商品 spid</th><th>批号</th><th>数量</th><th>单位</th><th>分类</th><th>已配</th><th>差异</th></tr></thead>
              <tbody>
                {detail.lines.map((l) => {
                  const diff = diffOf(l);
                  const required = l.classification === 'REQUIRED';
                  return (
                    <tr key={l.lineId} style={{ background: required ? (diff === 0 ? '#d4edda' : '#f8d7da') : undefined }}>
                      <td>{l.productSourceId}</td><td>{l.batchRaw ?? '（缺）'}</td><td>{l.quantity}</td>
                      <td>{l.unit ?? '—'}</td><td>{l.classification ?? 'UNKNOWN'}</td>
                      <td>{l.allocatedQuantity}</td><td>{required ? diff : '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <h4>分配（RESERVED 暂存 / COMMITTED 已挂任务）</h4>
            {detail.allocations.length === 0 ? <p>暂无分配。</p> : (
              <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
                <thead><tr><th>本地行</th><th>上游行</th><th>数量</th><th>换算</th><th>状态</th><th>码</th><th>操作</th></tr></thead>
                <tbody>
                  {detail.allocations.map((a) => (
                    <tr key={a.allocationId}>
                      <td>{a.sourceLineId}</td><td>{a.upstreamLineId}</td><td>{a.quantity}</td>
                      <td>{a.unitRatio}</td><td>{a.state}</td>
                      <td title={a.codes.map((c) => c.codeNormalized).join('\n')}>{a.codes.length} 个</td>
                      <td>{!frozen && a.state === 'RESERVED' && <button onClick={() => setCancelTarget({ allocationId: a.allocationId })}>取消</button>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {!frozen && !notRequired && (
              <div style={{ marginTop: 12, display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                <button onClick={() => void rematch()}>一键重排</button>
                <button onClick={() => { setStatus(''); setEditing(true); }}>人工补配</button>
                <button disabled={!allCovered || !detail.match} onClick={() => { setStatus(''); setConfirming(true); }}>确认补配</button>
                <button disabled={detail.allocations.length === 0 && !detail.task} onClick={() => setCancelTarget({ allocationId: null })}>全部取消/回退</button>
                <button onClick={() => void reload()}>刷新</button>
              </div>
            )}
            {frozen && <div style={{ marginTop: 12 }}><button onClick={() => void reload()}>刷新</button></div>}
          </>
        )}
        <div style={{ marginTop: 8 }}><button onClick={onClose}>关闭</button></div>
      </div>
      {editing && detail?.match && (
        <AllocationEditor detail={detail} onClose={() => setEditing(false)}
          onSaved={(r) => { setEditing(false); setStatus(`已保存 ${r.allocationsSaved} 行分配（匹配版本 ${r.matchVersion}）。${r.allRequiredCovered ? '全部应追溯行已覆盖，可确认补配。' : '仍有差异，请继续补配。'}`); void reload(); }}
          onConflict={onConflict} />
      )}
      {confirming && detail?.match && (
        <ConfirmDialog detail={detail} onClose={() => setConfirming(false)}
          onDone={(r) => { setConfirming(false); setStatus(`已确认：任务 #${r.taskId} · 单据号 ${r.billCode}。${r.message}${r.reused ? '（复用既有单据号）' : ''}`); void reload(); }}
          onConflict={onConflict} />
      )}
      {cancelTarget && (
        <CancelDialog snapshotId={snapshotId} allocationId={cancelTarget.allocationId} onClose={() => setCancelTarget(null)}
          onDone={(n) => { setCancelTarget(null); setStatus(`已取消 ${n} 项。`); void reload(); }}
          onConflict={onConflict} />
      )}
    </div>
  );
}

interface EditRow {
  key: number; sourceLineId: string; upstreamLineId: string; quantity: string;
  unitRatio: string; evidence: string; codesText: string;
}

function AllocationEditor({ detail, onClose, onSaved, onConflict }: {
  detail: InboundDetail; onClose: () => void;
  onSaved: (r: AllocationSaveResult) => void; onConflict: (e: unknown) => void;
}) {
  const requiredLines = detail.lines.filter((l) => l.classification === 'REQUIRED');
  const [rows, setRows] = useState<EditRow[]>([]);
  const [nextKey, setNextKey] = useState(1);
  const [billNo, setBillNo] = useState('');
  const [bills, setBills] = useState<UpstreamBill[]>([]);
  const [upDetail, setUpDetail] = useState<UpstreamDetail | null>(null);
  const [error, setError] = useState('');
  const [fieldErrors, setFieldErrors] = useState<FieldError[]>([]);
  const [busy, setBusy] = useState(false);

  function addRow(sourceLineId: string) {
    setRows([...rows, { key: nextKey, sourceLineId, upstreamLineId: '', quantity: '', unitRatio: '1/1', evidence: '', codesText: '' }]);
    setNextKey(nextKey + 1);
  }
  function patch(key: number, p: Partial<EditRow>) { setRows(rows.map((r) => (r.key === key ? { ...r, ...p } : r))); }

  async function searchBills() {
    setError('');
    try {
      const qs = new URLSearchParams({ page: '1', pageSize: '20' });
      if (billNo.trim()) qs.set('billNo', billNo.trim());
      setBills((await api<Page<UpstreamBill>>(`/api/v1/upstream-bills?${qs.toString()}`)).records);
    } catch (e) { setError(errMsg(e)); }
  }
  async function pickBill(billId: string) {
    setError('');
    if (!billId) { setUpDetail(null); return; }
    try { setUpDetail(await api<UpstreamDetail>(`/api/v1/upstream-bills/${billId}`)); }
    catch (e) { setError(errMsg(e)); }
  }

  async function submit() {
    setError(''); setFieldErrors([]);
    if (rows.length === 0) { setError('请至少添加一行分配'); return; }
    const lines: AllocationLineInput[] = [];
    for (const r of rows) {
      const qty = Number(r.quantity);
      const codes = r.codesText.split(/[\s,;，；]+/).filter(Boolean);
      if (!r.upstreamLineId) { setError('每行必须选择上游明细行'); return; }
      if (!Number.isFinite(qty) || qty <= 0) { setError('数量必须为正数'); return; }
      if (codes.length === 0) { setError('每行必须登记至少一个追溯码'); return; }
      lines.push({ sourceLineId: Number(r.sourceLineId), upstreamLineId: Number(r.upstreamLineId), quantity: qty,
        unitRatio: r.unitRatio.trim(), conversionEvidence: r.evidence.trim() ? r.evidence.trim() : null, codes });
    }
    setBusy(true);
    try {
      const res = await api<AllocationSaveResult>(`/api/v1/inbounds/${detail.snapshot.snapshotId}/allocations`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ expectedMatchVersion: String(detail.match!.version), lines }),
      });
      onSaved(res);
    } catch (e) {
      setBusy(false);
      if (e instanceof ApiError && e.status === 409) { onConflict(e); return; }
      if (e instanceof ApiError && e.fieldErrors && e.fieldErrors.length > 0) setFieldErrors(e.fieldErrors);
      setError(errMsg(e));
    }
  }

  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>人工补配 · {detail.snapshot.docKey}（匹配版本 {detail.match!.version}）</h3>
        <p>强约束：批号一致、换算后数量=码数、码须属于所选上游行且为最小包装、码不得重复占用。保存为全量替换（原暂存分配先取消）。</p>
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 260 }}>
            <h4>本地应追溯行</h4>
            {requiredLines.map((l) => (
              <p key={l.lineId}>
                #{l.lineId} {l.productSourceId} · 批号 {l.batchRaw ?? '（缺）'} · {l.quantity}{l.unit ?? ''} · 已配 {l.allocatedQuantity}{' '}
                <button onClick={() => addRow(l.lineId)}>添加分配</button>
              </p>
            ))}
          </div>
          <div style={{ flex: 1, minWidth: 260 }}>
            <h4>上游明细来源</h4>
            <div style={{ display: 'flex', gap: 8 }}>
              <input value={billNo} onChange={(e) => setBillNo(e.target.value)} placeholder="上游单号" />
              <button onClick={() => void searchBills()}>搜索</button>
            </div>
            <select onChange={(e) => void pickBill(e.target.value)} defaultValue="" style={{ marginTop: 8, width: '100%' }}>
              <option value="">选择上游单…</option>
              {bills.map((b) => <option key={b.billId} value={b.billId}>{b.billNo ?? b.platformBillId}（{b.completeness}）</option>)}
            </select>
            {upDetail && upDetail.lines.map((l) => (
              <p key={l.lineId} style={{ margin: 4, fontSize: 13 }}>
                行 #{l.lineId} {l.platformDrugId ?? '?'} · 批号 {l.batchNo ?? '（缺）'} · 数量 {l.quantity ?? '?'} · 剩余 {l.remainingQuantity ?? '?'} · {l.codes.length} 码
              </p>
            ))}
          </div>
        </div>
        <h4>分配行</h4>
        {rows.map((r) => (
          <div key={r.key} style={{ border: '1px solid #ddd', padding: 8, marginBottom: 8 }}>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
              <select value={r.sourceLineId} onChange={(e) => patch(r.key, { sourceLineId: e.target.value })}>
                {requiredLines.map((l) => <option key={l.lineId} value={l.lineId}>本地行 #{l.lineId} {l.productSourceId}</option>)}
              </select>
              <select value={r.upstreamLineId} onChange={(e) => patch(r.key, { upstreamLineId: e.target.value })}>
                <option value="">上游行…</option>
                {(upDetail?.lines ?? []).map((l) => <option key={l.lineId} value={l.lineId}>#{l.lineId} {l.platformDrugId ?? '?'} / {l.batchNo ?? '?'} / 剩 {l.remainingQuantity ?? '?'}</option>)}
              </select>
              <input value={r.quantity} onChange={(e) => patch(r.key, { quantity: e.target.value })} placeholder="数量" style={{ width: 80 }} />
              <input value={r.unitRatio} onChange={(e) => patch(r.key, { unitRatio: e.target.value })} placeholder="换算 n/d" style={{ width: 80 }} />
              <input value={r.evidence} onChange={(e) => patch(r.key, { evidence: e.target.value })} placeholder="换算证据（比例≠1/1 必填）" style={{ flex: 1, minWidth: 160 }} />
              <button onClick={() => setRows(rows.filter((x) => x.key !== r.key))}>删除</button>
            </div>
            <textarea value={r.codesText} onChange={(e) => patch(r.key, { codesText: e.target.value })} rows={2}
              style={{ width: '100%', marginTop: 4, fontFamily: 'monospace' }} placeholder="追溯码（每行一个或空白分隔，须为该上游行的最小包装码）" />
          </div>
        ))}
        {fieldErrors.length > 0 && (
          <ul role="alert" style={{ color: '#a00' }}>
            {fieldErrors.map((f, i) => <li key={i}>行 {f.sourceLineId ?? '?'}{f.code ? `（${f.code}）` : ''}：{f.message ?? '校验失败'}</li>)}
          </ul>
        )}
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={() => void submit()}>保存分配（全量替换）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function ConfirmDialog({ detail, onClose, onDone, onConflict }: {
  detail: InboundDetail; onClose: () => void; onDone: (r: ConfirmResult) => void; onConflict: (e: unknown) => void;
}) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    setBusy(true); setError('');
    try {
      const r = await api<ConfirmResult>(`/api/v1/inbounds/${detail.snapshot.snapshotId}/confirm-allocation`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ expectedMatchVersion: String(detail.match!.version) }),
      });
      onDone(r);
    } catch (e) {
      setBusy(false);
      if (e instanceof ApiError && e.status === 409) { onConflict(e); return; }
      setError(errMsg(e));
    }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>确认补配 · {detail.snapshot.docKey}</h3>
        <p role="alert" style={{ background: '#fff3cd', padding: 8 }}>
          确认后将重新校验全部资格（批号/数量/码集合/占用/映射/供应商）。<strong>符合资格后将自动上传</strong>，无需手工上传；单据号由系统生成。
        </p>
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={() => void submit()}>确认（版本 {detail.match!.version}）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function CancelDialog({ snapshotId, allocationId, onClose, onDone, onConflict }: {
  snapshotId: string; allocationId: string | null; onClose: () => void;
  onDone: (cancelled: number) => void; onConflict: (e: unknown) => void;
}) {
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写取消理由'); return; }
    setBusy(true); setError('');
    try {
      const r = await api<{ cancelled: number }>(`/api/v1/inbounds/${snapshotId}/cancel-allocation`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ allocationId: allocationId === null ? null : Number(allocationId), reason }),
      });
      onDone(r.cancelled);
    } catch (e) {
      setBusy(false);
      if (e instanceof ApiError && e.status === 409) { onConflict(e); return; }
      setError(errMsg(e));
    }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>{allocationId === null ? '全部取消/回退' : `取消分配 #${allocationId}`}</h3>
        <p>{allocationId === null ? '取消全部暂存分配；若已挂发送前任务，任务与分配一并回退、码占用释放。已发送的单据将被拒绝（FROZEN）。' : '取消该暂存分配并释放码占用。'}</p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%' }} placeholder="取消理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={() => void submit()}>确认取消</button> <button onClick={onClose}>返回</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 960, width: '95%', maxHeight: '85vh', overflow: 'auto' };