import { useState } from 'react';
import { api, ApiError } from '../api';
import type { ImportBatch, ImportDetail, JobStatus } from '../m1types';

const KINDS = [
  { value: 'PRODUCT', label: '商品档案（13 列）' },
  { value: 'SUPPLIER', label: '供应商档案（4 列）' },
  { value: 'PLATFORM_DRUG', label: '平台药品（7 列）' },
  { value: 'ENTERPRISE', label: '平台企业版本（6 列）' },
];

function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf)).map((b) => b.toString(16).padStart(2, '0')).join('');
}
function base64(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf); let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export default function ImportWizard() {
  const [kind, setKind] = useState('PRODUCT');
  const [file, setFile] = useState<{ name: string; hash: string; content: string } | null>(null);
  const [batch, setBatch] = useState<ImportBatch | null>(null);
  const [detail, setDetail] = useState<ImportDetail | null>(null);
  const [phase, setPhase] = useState<'select' | 'staging' | 'preview' | 'committed'>('select');
  const [error, setError] = useState('');
  const [applied, setApplied] = useState(0);

  async function onFile(f: File | undefined) {
    setError(''); setFile(null); setBatch(null); setDetail(null); setPhase('select');
    if (!f) return;
    try {
      const buf = await f.arrayBuffer();
      const hash = hex(await crypto.subtle.digest('SHA-256', buf));
      setFile({ name: f.name, hash, content: base64(buf) });
    } catch (e) { setError(errMsg(e)); }
  }

  async function upload() {
    if (!file) { setError('请先选择 CSV 文件'); return; }
    setPhase('staging'); setError('');
    try {
      const accepted = await api<{ jobId: string; batchId: string }>('/api/v1/imports', {
        method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() },
        body: JSON.stringify({ fileKind: kind, fileHash: file.hash, content: file.content, fileName: file.name }),
      });
      // 轮询暂存作业至终态（DONE/FAILED/CANCELLED），最多 60 次 × 1s
      let job: JobStatus | null = null;
      for (let i = 0; i < 60; i++) {
        job = await api<JobStatus>(`/api/v1/jobs/${accepted.jobId}`);
        if (job.jobState === 'DONE' || job.jobState === 'FAILED' || job.jobState === 'CANCELLED') break;
        await sleep(1000);
      }
      if (!job || job.jobState !== 'DONE') { setError(`暂存未完成：${job?.jobState ?? '超时'} ${job?.lastError ?? ''}`); setPhase('select'); return; }
      const b = await api<ImportBatch>(`/api/v1/imports?fileKind=${kind}&fileHash=${file.hash}`);
      const d = await api<ImportDetail>(`/api/v1/imports/${b.batchId}?page=1&pageSize=50`);
      setBatch(b); setDetail(d); setPhase('preview');
    } catch (e) { setError(errMsg(e)); setPhase('select'); }
  }

  async function commit() {
    if (!batch) return;
    setError('');
    try {
      const res = await api<{ appliedRows: number; status: string }>(`/api/v1/imports/${batch.batchId}/commit`, {
        method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() },
        body: JSON.stringify({ expectedVersion: batch.version, reason: '导入向导提交' }),
      });
      setApplied(res.appliedRows); setPhase('committed');
    } catch (e) { setError(errMsg(e)); }
  }

  return (
    <section>
      <h1>主数据导入</h1>
      <p>
        <select value={kind} onChange={(e) => { setKind(e.target.value); setFile(null); setBatch(null); setDetail(null); setPhase('select'); }} disabled={phase === 'staging'}>
          {KINDS.map((k) => <option key={k.value} value={k.value}>{k.label}</option>)}
        </select>{' '}
        <input type="file" accept=".csv,text/csv" onChange={(e) => void onFile(e.target.files?.[0])} disabled={phase === 'staging'} />
      </p>
      {file && <p>已选：{file.name} · SHA-256 {file.hash.slice(0, 16)}…</p>}
      {error && <p role="alert">{error}</p>}
      {phase === 'select' && <button disabled={!file} onClick={() => void upload()}>上传暂存</button>}
      {phase === 'staging' && <p>暂存校验中…（轮询作业状态）</p>}
      {phase === 'preview' && batch && detail && (
        <>
          <p>批次 {batch.batchId} · 状态 {batch.status} · 总 {batch.totalRows} 行 · 合格 {batch.successRows} · 错误 {batch.failedRows}</p>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead><tr><th>行号</th><th>状态</th><th>错误</th><th>原始</th></tr></thead>
            <tbody>
              {detail.rows.map((r) => (
                <tr key={r.rowNumber} style={{ background: r.status === 'ERROR' ? '#f8d7da' : undefined }}>
                  <td>{r.rowNumber}</td><td>{r.status}</td><td>{r.error ?? ''}</td>
                  <td style={{ maxWidth: 320, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.rawJson}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p style={{ color: '#666' }}>仅展示前 50 行；提交只应用 OK 行，ERROR 行跳过。</p>
          <button onClick={() => void commit()} disabled={batch.status !== 'STAGED'}>提交应用 OK 行</button>
        </>
      )}
      {phase === 'committed' && <p role="status">已提交，应用 {applied} 行。匹配作业已入队（master-match）。</p>}
    </section>
  );
}
