import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { Page, Product } from '../m1types';

const CLASSIFICATIONS = ['ALL', 'UNKNOWN', 'REQUIRED', 'EXEMPT'];
const MAPPING_STATES = ['ALL', 'NONE', 'UNRESOLVED', 'CANDIDATE', 'AUTO_CONFIRMED', 'MANUAL_CONFIRMED', 'CONFLICT', 'REVIEW_REQUIRED', 'DISABLED'];
const PAGE_SIZE = 50;

function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function ProductsPage() {
  const [keyword, setKeyword] = useState('');
  const [classification, setClassification] = useState('ALL');
  const [mappingState, setMappingState] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<Product> | null>(null);
  const [error, setError] = useState('');
  const [classifyTarget, setClassifyTarget] = useState<Product | null>(null);
  const [confirmTarget, setConfirmTarget] = useState<Product | null>(null);
  const [disableTarget, setDisableTarget] = useState<Product | null>(null);
  const [resolveTarget, setResolveTarget] = useState<Product | null>(null);
  // 请求序号守卫：每次 load 递增；await 后若已有更新的请求则丢弃本次结果，防止旧响应覆盖新数据
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (keyword.trim()) qs.set('keyword', keyword.trim());
    if (classification !== 'ALL') qs.set('classification', classification);
    if (mappingState !== 'ALL') qs.set('mappingState', mappingState);
    try {
      const res = await api<Page<Product>>(`/api/v1/products?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [keyword, classification, mappingState, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <section>
      <h1>商品映射</h1>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={keyword} onChange={(e) => { setKeyword(e.target.value); setPage(1); }} placeholder="名称/编码/条码" />
        <select value={classification} onChange={(e) => { setClassification(e.target.value); setPage(1); }}>
          {CLASSIFICATIONS.map((c) => <option key={c} value={c}>{c === 'ALL' ? '全部分类' : c}</option>)}
        </select>
        <select value={mappingState} onChange={(e) => { setMappingState(e.target.value); setPage(1); }}>
          {MAPPING_STATES.map((s) => <option key={s} value={s}>{s === 'ALL' ? '全部映射态' : s}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr><th>SPID</th><th>名称</th><th>批准文号</th><th>分类</th><th>映射态</th><th>平台药</th><th>操作</th></tr>
            </thead>
            <tbody>
              {data.records.map((p) => (
                <tr key={p.productId}>
                  <td>{p.spid}</td><td>{p.name}</td><td>{p.approvalNoNorm ?? '（缺）'}</td>
                  <td>{p.traceClassification}</td><td>{p.mappingState}</td>
                  <td>{p.mapping?.platformDrugId ?? '—'}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    <button onClick={() => setClassifyTarget(p)}>分类</button>{' '}
                    {p.mapping && p.mappingState !== 'DISABLED' && <button onClick={() => setConfirmTarget(p)}>确认映射</button>}{' '}
                    {p.mapping && ['CONFLICT', 'REVIEW_REQUIRED', 'CANDIDATE', 'UNRESOLVED'].includes(p.mappingState) &&
                      <button onClick={() => setResolveTarget(p)}>改绑</button>}{' '}
                    {p.mapping && (p.mappingState === 'AUTO_CONFIRMED' || p.mappingState === 'MANUAL_CONFIRMED') &&
                      <button onClick={() => setDisableTarget(p)}>解除</button>}
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
      {classifyTarget && <ClassifyDialog product={classifyTarget} onClose={() => setClassifyTarget(null)} onDone={() => { setClassifyTarget(null); void load(); }} />}
      {confirmTarget && confirmTarget.mapping && <ConfirmDialog product={confirmTarget} onClose={() => setConfirmTarget(null)} onDone={() => { setConfirmTarget(null); void load(); }} />}
      {disableTarget && disableTarget.mapping && <DisableDialog product={disableTarget} onClose={() => setDisableTarget(null)} onDone={() => { setDisableTarget(null); void load(); }} />}
      {resolveTarget && resolveTarget.mapping && <ResolveDialog product={resolveTarget} onClose={() => setResolveTarget(null)} onDone={() => { setResolveTarget(null); void load(); }} />}
    </section>
  );
}

function ClassifyDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const [cls, setCls] = useState(product.traceClassification === 'EXEMPT' ? 'EXEMPT' : 'REQUIRED');
  const [reason, setReason] = useState(product.classificationReason ?? '');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写分类理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/products/${product.productId}/trace-policy`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ classification: cls, reason, expectedVersion: product.version }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>追溯分类 · {product.name}</h3>
        <p>
          <select value={cls} onChange={(e) => setCls(e.target.value)}>
            <option value="REQUIRED">REQUIRED 需追溯</option>
            <option value="EXEMPT">EXEMPT 豁免</option>
          </select>
        </p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={3} style={{ width: '100%' }} placeholder="分类理由（必填，≤500 字）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>保存</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

type FiveKey = 'approvalNoNorm' | 'manufacturerIdentity' | 'dosageForm' | 'strength' | 'packageSpec';
const FIVE: { key: FiveKey; label: string }[] = [
  { key: 'approvalNoNorm', label: '批准文号' }, { key: 'manufacturerIdentity', label: '厂家身份' },
  { key: 'dosageForm', label: '剂型' }, { key: 'strength', label: '规格' }, { key: 'packageSpec', label: '包装' },
];

function ConfirmDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const m = product.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写确认理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/product-mappings/${m.mappingId}/confirm`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: m.platformDrugId, expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>确认映射 · {product.name}</h3>
        <p>平台药：{m.platformDrugId}（确认不改绑；强冲突须先解决）</p>
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>字段</th><th>本地</th><th>平台</th><th>比对</th></tr></thead>
          <tbody>
            {FIVE.map(({ key, label }) => {
              const local = product[key] ?? '';
              const platform = m[key] ?? '';
              const same = local !== '' && platform !== '' && local === platform;
              const missing = local === '' || platform === '';
              return (
                <tr key={key} style={{ background: missing ? '#fff3cd' : same ? '#d4edda' : '#f8d7da' }}>
                  <td>{label}</td><td>{local || '（缺）'}</td><td>{platform || '（缺）'}</td>
                  <td>{missing ? '缺字段' : same ? '一致' : '冲突'}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="确认理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>确认（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function ResolveDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const m = product.mapping!;
  const [targetId, setTargetId] = useState(m.platformDrugId);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!targetId.trim()) { setError('请填写目标平台药 ID'); return; }
    if (!reason.trim()) { setError('请填写改绑理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/product-mappings/${m.mappingId}/resolve`, {
        method: 'POST', headers: idem(),
        body: JSON.stringify({ targetId: targetId.trim(), expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>改绑映射 · {product.name}</h3>
        <p>当前状态 {m.state} · 当前平台药 {m.platformDrugId}。改绑成功后锁定为 MANUAL_CONFIRMED；目标已被其他有效映射占用时返回 409（双向唯一）。</p>
        <input value={targetId} onChange={(e) => setTargetId(e.target.value)} style={{ width: '100%' }} placeholder="目标平台药 platform_drug_id" />
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="改绑理由（必填，留审计）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>改绑（锁定）</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

function DisableDialog({ product, onClose, onDone }: { product: Product; onClose: () => void; onDone: () => void }) {
  const m = product.mapping!;
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写解除理由'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/product-mappings/${m.mappingId}/disable`, {
        method: 'POST', headers: idem(), body: JSON.stringify({ expectedVersion: m.version, reason }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>解除映射 · {product.name}</h3>
        <p>存在非终态上传任务时将被阻断（TASKS_IN_FLIGHT）。</p>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%' }} placeholder="解除理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>解除</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 640, width: '90%', maxHeight: '85vh', overflow: 'auto' };
