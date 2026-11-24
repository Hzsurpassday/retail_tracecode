import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { Page } from '../m1types';
import type { UpstreamBill, UpstreamDetail } from '../m2types';
import { fmtShanghai } from '../utils/time';

const COMPLETENESS = ['ALL', 'COMPLETE', 'INCOMPLETE'];
const PAGE_SIZE = 50;
function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function UpstreamBillsPage() {
  const [billNo, setBillNo] = useState('');
  const [completeness, setCompleteness] = useState('ALL');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<UpstreamBill> | null>(null);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const [detailId, setDetailId] = useState<string | null>(null);
  const seqRef = useRef(0);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (billNo.trim()) qs.set('billNo', billNo.trim());
    if (completeness !== 'ALL') qs.set('completeness', completeness);
    try {
      const res = await api<Page<UpstreamBill>>(`/api/v1/upstream-bills?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res); setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [billNo, completeness, page]);

  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  async function redownload(billId: string) {
    setStatus(''); setError('');
    try {
      const r = await api<{ jobId: string }>(`/api/v1/upstream-bills/${billId}/redownload`, { method: 'POST', headers: idem() });
      setStatus(`补拉已排队（作业 ${r.jobId}），后台处理，稍后刷新查看结果。`);
    } catch (e) { setError(errMsg(e)); }
  }

  return (
    <section>
      <h1>上游出库单</h1>
      <p>完整性 INCOMPLETE 的单据不可供完整入库上传；列表不提供"视为已入库"。</p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <input value={billNo} onChange={(e) => { setBillNo(e.target.value); setPage(1); }} placeholder="单号（精确）" />
        <select value={completeness} onChange={(e) => { setCompleteness(e.target.value); setPage(1); }}>
          {COMPLETENESS.map((c) => <option key={c} value={c}>{c === 'ALL' ? '全部完整性' : c}</option>)}
        </select>
        <button onClick={() => { setPage(1); void load(); }}>查询</button>
      </div>
      {error && <p role="alert">{error}</p>}
      {status && <p role="status">{status}</p>}
      {data === null ? <p>加载中…</p> : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>单号</th><th>平台单 ID</th><th>上游主体</th><th>单据日期</th><th>完整性</th><th>抓取时间</th><th>操作</th></tr></thead>
            <tbody>
              {data.records.map((b) => (
                <tr key={b.billId}>
                  <td>{b.billNo ?? '（缺）'}</td><td>{b.platformBillId}</td><td>{b.upstreamRefEntId}</td>
                  <td>{fmtShanghai(b.billDate)}</td>
                  <td style={{ background: b.completeness === 'COMPLETE' ? '#d4edda' : '#f8d7da' }}>{b.completeness}</td>
                  <td>{fmtShanghai(b.fetchedAtUtc)}</td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    <button onClick={() => setDetailId(b.billId)}>详情</button>{' '}
                    <button onClick={() => void redownload(b.billId)}>补拉</button>
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
      {detailId !== null && <BillDetailDialog billId={detailId} onClose={() => setDetailId(null)} />}
    </section>
  );
}

function BillDetailDialog({ billId, onClose }: { billId: string; onClose: () => void }) {
  const [detail, setDetail] = useState<UpstreamDetail | null>(null);
  const [error, setError] = useState('');
  const [openLine, setOpenLine] = useState<string | null>(null);
  useEffect(() => {
    let active = true;
    api<UpstreamDetail>(`/api/v1/upstream-bills/${billId}`)
      .then((d) => { if (active) setDetail(d); })
      .catch((e) => { if (active) setError(errMsg(e)); });
    return () => { active = false; };
  }, [billId]);
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>上游单详情 · {detail?.bill.billNo ?? billId}</h3>
        {error && <p role="alert">{error}</p>}
        {detail === null ? <p>加载中…</p> : (
          <>
            <p>平台单 ID：{detail.bill.platformBillId} · 上游主体：{detail.bill.upstreamRefEntId} · 收货主体：{detail.bill.toEntId ?? '—'} · 完整性：{detail.bill.completeness}</p>
            <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
              <thead><tr><th>平台药</th><th>批号</th><th>数量</th><th>单位</th><th>已配</th><th>剩余</th><th>码</th></tr></thead>
              <tbody>
                {detail.lines.map((l) => (
                  <tr key={l.lineId}>
                    <td>{l.platformDrugId ?? '（缺）'}</td><td>{l.batchNo ?? '（缺）'}</td>
                    <td>{l.quantity ?? '—'}</td><td>{l.unit ?? '—'}</td>
                    <td>{l.allocatedQuantity}</td><td>{l.remainingQuantity ?? '—'}</td>
                    <td><button onClick={() => setOpenLine(openLine === l.lineId ? null : l.lineId)}>{l.codes.length} 个码{l.codes.length > 0 ? '（展开）' : ''}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            {openLine !== null && (
              <div style={{ marginTop: 8, maxHeight: 200, overflow: 'auto', border: '1px solid #ddd', padding: 8 }}>
                {detail.lines.find((l) => l.lineId === openLine)?.codes.map((c) => (
                  <p key={c.codeNormalized} style={{ margin: 2, fontFamily: 'monospace' }}>{c.codeNormalized}（{c.packageLevel ?? '?'}）</p>
                ))}
              </div>
            )}
          </>
        )}
        <div style={{ marginTop: 8 }}><button onClick={onClose}>关闭</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 860, width: '95%', maxHeight: '85vh', overflow: 'auto' };
