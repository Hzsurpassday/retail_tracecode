import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { Page, Supplier } from '../m1types';

const MAPPING_STATES = ['ALL', 'NONE', 'UNRESOLVED', 'CANDIDATE', 'AUTO_CONFIRMED', 'MANUAL_CONFIRMED', 'CONFLICT', 'REVIEW_REQUIRED', 'DISABLED'];
const PAGE_SIZE = 50;
function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function SuppliersPage() {
  const [keyword, setKeyword] = useState('');
  const [mappingState, setMappingState] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<Supplier> | null>(null);
  const [error, setError] = useState('');
  const [confirmTarget, setConfirmTarget] = useState<Supplier | null>(null);
  const [disableTarget, setDisableTarget] = useState<Supplier | null>(null);
  const [resolveTarget, setResolveTarget] = useState<Supplier | null>(null);
  // 请求序号守卫：每次 load 递增；await 后若已有更新的请求则丢弃本次结果，防止旧响应覆盖新数据
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (keyword.trim()) qs.set('keyword', keyword.trim());
    if (mappingState !== 'ALL') qs.set('mappingState', mappingState);
    try {
      const res = await api<Page<Supplier>>(`/api/v1/suppliers?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [keyword, mappingState, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>供应商映射</h1>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={keyword} onChange={(e) => { setKeyword(e.target.value); setPage(1); }} placeholder="名称/单位编号/信用代码" />
        <select value={mappingState} onChange={(e) => { setMappingState(e.target.value); setPage(1); }}>
          {MAPPING_STATES.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部映射态' : s}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>单位编号</th><th>名称</th><th>信用代码</th><th>映射态</th><th>平台企业</th><th>审核</th><th>操作</th></tr></thead>
            <tbody>
              {data.records.map((s) => (
                <tr key={s.supplierId}>
                  <td>{s.dwbh}</td><td>{s.name}</td><td>{s.creditCode ?? '（缺）'}</td>
                  <td>{s.mappingState}</td><td>{s.mapping?.platformName ?? '—'}</td>
                  <td>{s.mapping?.auditStatus ?? '—'}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    {s.mapping && s.mappingState !== 'DISABLED' && <button onClick={() => setConfirmTarget(s)}>确认映射</button>}{' '}
                    {s.mapping && ['CONFLICT', 'REVIEW_REQUIRED', 'CANDIDATE', 'UNRESOLVED'].includes(s.mappingState) &&
                      <button onClick={() => setResolveTarget(s)}>改绑</button>}{' '}
                    {s.mapping && (s.mappingState === 'AUTO_CONFIRMED' || s.mappingState === 'MANUAL_CONFIRMED') &&
                      <button onClick={() => setDisableTarget(s)}>解除</button>}
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
      {confirmTarget && confirmTarget.mapping && <ConfirmDialog supplier={confirmTarget} onClose={() => setConfirmTarget(null)} onDone={() => { setConfirmTarget(null); void load(); }} />}
      {disableTarget && disableTarget.mapping && <DisableDialog supplier={disableTarget} onClose={() => setDisableTarget(null)} onDone={() => { setDisableTarget(null); void load(); }} />}
      {resolveTarget && resolveTarget.mapping && <ResolveDialog supplier={resolveTarget} onClose={() => setResolveTarget(null)} onDone={() => { setResolveTarget(null); void load(); }} />}
    </section>
  );
}

function ConfirmDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: () => void }) {
  const m = supplier.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const creditSame = !!supplier.creditCode && !!m.creditCode && supplier.creditCode === m.creditCode;
  async function submit() {
    if (!reason.trim()) { setError('请填写确认理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/supplier-mappings/${m.mappingId}/confirm`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: m.entId, expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>确认供应商映射 · {supplier.name}</h3>
        <p>平台企业 ent_id：{m.entId}（确认不改绑）· 审核状态：{m.auditStatus ?? '（未知）'}</p>
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>字段</th><th>本地</th><th>平台</th><th>比对</th></tr></thead>
          <tbody>
            <tr style={{ background: creditSame ? '#d4edda' : '#f8d7da' }}>
              <td>信用代码（精确）</td><td>{supplier.creditCode ?? '（缺）'}</td><td>{m.creditCode ?? '（缺）'}</td>
              <td>{creditSame ? '一致' : '不一致/缺'}</td>
            </tr>
            <tr><td>名称（仅候选）</td><td>{supplier.name}</td><td>{m.platformName}</td><td>—</td></tr>
            <tr><td>角色</td><td>{supplier.role ?? '（缺）'}</td><td>{m.role ?? '（缺）'}</td><td>独立</td></tr>
          </tbody>
        </table>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="确认理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>确认（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function ResolveDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: () => void }) {
  const m = supplier.mapping!;
  const [targetId, setTargetId] = useState(m.refEntId);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!targetId.trim()) { setError('请填写目标 ref_ent_id'); return; }
    if (!reason.trim()) { setError('请填写改绑理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/supplier-mappings/${m.mappingId}/resolve`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: targetId.trim(), expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>改绑供应商映射 · {supplier.name}</h3>
        <p>当前状态 {m.state} · 当前 ref_ent_id {m.refEntId}。改绑目标为同一 ref_ent_id 的最新企业版本；<strong>统一社会信用代码必须精确一致</strong>（否则 422 CREDIT_MISMATCH）；成功后锁定 MANUAL_CONFIRMED。</p>
        <input value={targetId} onChange={(e) => setTargetId(e.target.value)} style={{ width: '100%' }} placeholder="目标 ref_ent_id（稳定主体键）" />
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="改绑理由（必填，留审计）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>改绑（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function DisableDialog({ supplier, onClose, onDone }: { supplier: Supplier; onClose: () => void; onDone: () => void }) {
  const m = supplier.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写解除理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/supplier-mappings/${m.mappingId}/disable`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>解除供应商映射 · {supplier.name}</h3>
        <p>存在非终态采购任务时将被阻断（TASKS_IN_FLIGHT）。</p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%' }} placeholder="解除理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>解除</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 640, width: '90%', maxHeight: '85vh', overflow: 'auto' };
