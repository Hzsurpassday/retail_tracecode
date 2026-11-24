import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api';
import EnvBadge from '../components/EnvBadge';
import { useEscapeClose } from '../hooks/useEscapeClose';
import { fmtShanghai } from '../utils/time';

// 作业状态页（/ops，M4 Task 5）：管道健康指标、限流预算、暂停列表、站内告警与作业表。
// 全部为只读观测：数据来自本系统 DB，不直接调用 TOP/ERP（UI-12）；env 恒为宿主 env。

interface Metrics {
  env: string;
  jobs: {
    pending: number; running: number; done: number; failed: number; cancelled: number;
    backlog: number; oldestPendingAgeSeconds: number; failureRate24h: number | null;
  };
  attempts24h: { upload: number; query: number };
  uploadsToday: { total: number; succeeded: number; failed: number; successRate: number | null };
  perJobKind: { jobKind: string; backlog: number; lastDoneAtUtc: string | null }[];
  rateBudget: {
    budgetKey: string; dayCount: number; dayLimit: number | null; minuteCount: number; minuteLimit: number | null;
    secondCount: number; secondLimit: number | null; nextAllowedAtUtc: string | null;
  }[];
  pauses: { scopeKey: string; reason: string; pausedAtUtc: string; pausedBy: string }[];
  leaseRecoveries24h: number;
  recon: { incomplete24h: number; openDifferences: number };
}
interface AlertItem {
  code: string; severity: 'HIGH' | 'MEDIUM'; title: string; detail: string; count: number; deepLink: string;
}
interface AlertData { env: string; alerts: AlertItem[] }
interface JobRow {
  jobId: string; jobKind: string; jobState: string; attemptCount: number; maxAttempts: number;
  nextRunAtUtc: string; lastError: string | null; createdAtUtc: string; finishedAtUtc: string | null;
}
interface JobsData { items: JobRow[]; page: number; pageSize: number; total: number; env: string }
interface JobDetail {
  jobId: string; jobKind: string; intentKey: string; jobState: string; attemptCount: number; maxAttempts: number;
  nextRunAtUtc: string; lastError: string | null; leaseOwner: string | null; fencingToken: number;
  env: string; createdAtUtc: string; finishedAtUtc: string | null;
}

const PAGE_SIZE = 50;
const STATE_OPTIONS = ['', 'PENDING', 'RUNNING', 'DONE', 'FAILED', 'CANCELLED'];
const STATE_ZH: Record<string, string> = {
  PENDING: '待处理', RUNNING: '运行中', DONE: '已完成', FAILED: '失败', CANCELLED: '已取消',
};
const ALERT_ZH: Record<string, string> = {
  CHANNEL_PAUSED: '通道暂停', LEASE_RECOVERED_24H: '租约回收', JOB_BACKLOG_HIGH: '作业积压',
  JOBS_FAILED_24H: '作业失败', RECON_INCOMPLETE_24H: '对账未完成', RECON_DIFFERENCES_OPEN: '对账差异',
  RATE_BUDGET_WAITING: '限流等待',
};
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
function pct(ratio: number | null): string {
  return ratio === null ? '—' : `${(ratio * 100).toFixed(2)}%`;
}
function limitText(limit: number | null): string { return limit === null ? '未取证' : String(limit); }

export default function OpsPage() {
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const stateParam = searchParams.get('state') ?? '';
  // 仅接受已知状态值（与下拉框同一白名单），非法值按“全部”处理。
  const stateFromUrl = STATE_OPTIONS.indexOf(stateParam) > 0 ? stateParam : '';
  const [env, setEnv] = useState('');
  const [metrics, setMetrics] = useState<Metrics | null>(null);
  const [alerts, setAlerts] = useState<AlertItem[] | null>(null);
  const [error, setError] = useState('');
  const [state, setState] = useState(stateFromUrl);
  const [kindDraft, setKindDraft] = useState('');
  const [kind, setKind] = useState('');
  const [page, setPage] = useState(1);
  const [jobsData, setJobsData] = useState<JobsData | null>(null);
  const [jobsError, setJobsError] = useState('');
  const [openJobId, setOpenJobId] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const seqRef = useRef(0);
  // 已应用到 state 的 URL 标识（location.key + 参数）：初值即挂载时的值，故挂载不会二次取数。
  // 用 location.key 而非仅参数：已在 /ops 时再次点击同一 /ops?state=X 也视为新的一次导航，
  // 即使 URL 文本未变（用户可能已手动改过下拉框），也要重新应用 URL 状态。
  const appliedUrlRef = useRef(`${location.key}|${stateParam}`);

  // 深链（如告警里的 /ops?state=FAILED）在已在 /ops 时点链接也要生效：
  // 导航标识变化 → 同步筛选状态；取数由 loadJobs 的依赖变化驱动，这里不额外请求。
  useEffect(() => {
    const key = `${location.key}|${stateParam}`;
    if (appliedUrlRef.current === key) return;
    appliedUrlRef.current = key;
    setState(stateFromUrl);
    setPage(1);
  }, [location.key, stateParam, stateFromUrl]);

  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const [m, a] = await Promise.all([
          api<Metrics>('/api/v1/ops/metrics'),
          api<AlertData>('/api/v1/ops/alerts'),
        ]);
        if (active) { setMetrics(m); setAlerts(a.alerts); setError(''); }
      } catch (e) {
        if (active) { setMetrics(null); setAlerts(null); setError(errMsg(e)); }
      }
    })();
    return () => { active = false; };
  }, [refreshKey]);

  const loadJobs = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (state) qs.set('state', state);
    if (kind) qs.set('kind', kind);
    try {
      const res = await api<JobsData>(`/api/v1/ops/jobs?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setJobsData(res);
      setJobsError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setJobsError(errMsg(e));
    }
  }, [page, state, kind, refreshKey]);

  // 筛选/翻页变化：先清空旧行与旧错误，再按新条件取数。
  useEffect(() => {
    setJobsData(null);
    setJobsError('');
    void loadJobs();
  }, [loadJobs]);

  const totalPages = jobsData ? Math.max(1, Math.ceil(jobsData.total / PAGE_SIZE)) : 1;

  if (error) return <p role="alert">{error}</p>;
  if (metrics === null || alerts === null) return <p>加载中…</p>;

  function applyKind() {
    setKind(kindDraft.trim());
    setPage(1);
  }

  return (
    <section>
      <h1>作业状态 <EnvBadge env={env || metrics.env} /></h1>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 8 }}>
        <button onClick={() => { setRefreshKey(refreshKey + 1); }}>刷新</button>
        <span style={{ color: '#666' }}>只读观测：数据来自本系统数据库，不触发平台请求。</span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 12, marginBottom: 16 }}>
        <MetricCard label="待处理积压" value={String(metrics.jobs.backlog)}
          hint={`最老待处理 ${metrics.jobs.oldestPendingAgeSeconds} 秒`} />
        <MetricCard label="24h 失败率" value={pct(metrics.jobs.failureRate24h)}
          hint={`DONE ${metrics.jobs.done} / FAILED ${metrics.jobs.failed}（含历史状态计数）`} />
        <MetricCard label="今日上传成功率" value={pct(metrics.uploadsToday.successRate)}
          hint={`今日 ${metrics.uploadsToday.total} 个任务；成功 ${metrics.uploadsToday.succeeded} / 失败 ${metrics.uploadsToday.failed}`} />
        <MetricCard label="24h 租约恢复" value={String(metrics.leaseRecoveries24h)} hint="job.lease.recovered 审计条数" />
        <MetricCard label="激活暂停" value={String(metrics.pauses.length)} hint="top_channel_pause is_active=1" />
        <MetricCard label="对账未完成（24h）" value={String(metrics.recon.incomplete24h)}
          hint={`待处理差异 ${metrics.recon.openDifferences} 条`} />
      </div>

      <h2>站内告警</h2>
      {alerts.length === 0 ? <p style={{ color: '#666' }}>当前无站内告警。</p> : (
        <ul style={{ listStyle: 'none', padding: 0 }}>
          {alerts.map((a, i) => (
            <li key={`${a.code}:${i}`} style={a.severity === 'HIGH' ? alertHigh : alertMedium}>
              <strong>{ALERT_ZH[a.code] ?? a.code}</strong>
              <span style={severityTag(a.severity)}>{a.severity}</span>
              <span> {a.title}（{a.count}）</span>
              <div style={{ fontSize: 13, marginTop: 2 }}>{a.detail}</div>
              <div style={{ fontSize: 13, marginTop: 2 }}>
                处置入口 <Link to={a.deepLink}>{a.deepLink}</Link>
              </div>
            </li>
          ))}
        </ul>
      )}

      <h2>限流预算</h2>
      {metrics.rateBudget.length === 0 ? <p style={{ color: '#666' }}>暂无预算记录（尚未发起平台调用）。</p> : (
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%', marginBottom: 16 }}>
          <thead>
            <tr>
              <th>预算键</th><th>日计数 / 限值</th><th>分计数 / 限值</th><th>秒计数 / 限值</th><th>下次允许</th>
            </tr>
          </thead>
          <tbody>
            {metrics.rateBudget.map((b) => (
              <tr key={b.budgetKey}>
                <td style={{ fontFamily: 'monospace' }}>{b.budgetKey}</td>
                <td>{b.dayCount} / {limitText(b.dayLimit)}</td>
                <td>{b.minuteCount} / {limitText(b.minuteLimit)}</td>
                <td>{b.secondCount} / {limitText(b.secondLimit)}</td>
                <td>{fmtShanghai(b.nextAllowedAtUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p style={{ color: '#666', fontSize: 13 }}>
        限值为空表示本地未设帽（"未取证"）：本页不计算、不虚构平台限值，G-TOP 取证后由配置填实。
      </p>

      <h2>通道暂停</h2>
      {metrics.pauses.length === 0 ? <p style={{ color: '#666' }}>当前无激活暂停。</p> : (
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%', marginBottom: 16 }}>
          <thead><tr><th>范围</th><th>原因</th><th>暂停时间</th><th>操作人</th></tr></thead>
          <tbody>
            {metrics.pauses.map((p) => (
              <tr key={p.scopeKey}>
                <td style={{ fontFamily: 'monospace' }}>{p.scopeKey}</td>
                <td>{p.reason}</td>
                <td>{fmtShanghai(p.pausedAtUtc)}</td>
                <td>{p.pausedBy}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h2>作业列表</h2>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginBottom: 8, flexWrap: 'wrap' }}>
        <label>状态
          <select aria-label="作业状态筛选" value={state}
            onChange={(e) => { setState(e.target.value); setPage(1); }}>
            {STATE_OPTIONS.map((s) => (
              <option key={s || 'ALL'} value={s}>{s ? `${s}（${STATE_ZH[s] ?? s}）` : '全部'}</option>
            ))}
          </select>
        </label>
        <label>作业类型
          <input aria-label="作业类型筛选" value={kindDraft} placeholder="精确等值，如 source-sync"
            onChange={(e) => setKindDraft(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') applyKind(); }} />
        </label>
        <button onClick={applyKind}>应用</button>
        {jobsData && <span style={{ color: '#666' }}>环境 {jobsData.env} · 共 {jobsData.total} 条</span>}
      </div>
      {jobsError && <p role="alert">{jobsError}</p>}
      {jobsData === null ? (jobsError ? null : <p>加载中…</p>) : jobsData.items.length === 0 ? (
        <p>暂无符合条件的作业。</p>
      ) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr>
                <th>作业</th><th>类型</th><th>状态</th><th>尝试</th><th>下次运行</th><th>失败原因</th><th>创建时间</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {jobsData.items.map((j) => (
                <tr key={j.jobId}>
                  <td style={{ fontFamily: 'monospace' }}>{j.jobId}</td>
                  <td>{j.jobKind}</td>
                  <td title={j.jobState}>{STATE_ZH[j.jobState] ?? j.jobState}</td>
                  <td>{j.attemptCount} / {j.maxAttempts}</td>
                  <td>{fmtShanghai(j.nextRunAtUtc)}</td>
                  <td title={j.lastError ?? ''}>{j.lastError ?? '—'}</td>
                  <td>{fmtShanghai(j.createdAtUtc)}</td>
                  <td><button onClick={() => setOpenJobId(j.jobId)}>展开</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center' }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
            <span>第 {jobsData.page} / {totalPages} 页 · 每页 {jobsData.pageSize} 条</span>
            <button disabled={page >= totalPages} onClick={() => setPage(page + 1)}>下一页</button>
          </div>
        </>
      )}
      {openJobId !== null && <JobDialog jobId={openJobId} onClose={() => setOpenJobId(null)} />}
    </section>
  );
}

function MetricCard({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div style={{ border: '1px solid #ddd', borderRadius: 6, padding: 12 }}>
      <div style={{ color: '#666', fontSize: 13 }}>{label}</div>
      <div style={{ fontSize: 24, fontWeight: 700 }}>{value}</div>
      <div style={{ color: '#888', fontSize: 12 }}>{hint}</div>
    </div>
  );
}

function JobDialog({ jobId, onClose }: { jobId: string; onClose: () => void }) {
  const [job, setJob] = useState<JobDetail | null>(null);
  const [error, setError] = useState('');
  // 弹窗仅在打开时挂载：Esc 复用同一 onClose（父级只清 openJobId），不另造关闭路径。
  useEscapeClose(true, onClose);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const d = await api<JobDetail>(`/api/v1/jobs/${encodeURIComponent(jobId)}`);
        if (active) { setJob(d); setError(''); }
      } catch (e) {
        if (active) setError(errMsg(e));
      }
    })();
    return () => { active = false; };
  }, [jobId]);

  return (
    <div style={overlay}>
      <div style={panel} role="dialog" aria-modal="true" aria-label={`作业 ${jobId}`}>
        <h3>作业 {jobId}</h3>
        {error && <p role="alert">{error}</p>}
        {job === null ? (error ? null : <p>加载中…</p>) : (
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <tbody>
              <tr><th>类型</th><td>{job.jobKind}</td></tr>
              <tr><th>状态</th><td>{STATE_ZH[job.jobState] ?? job.jobState}（{job.jobState}）</td></tr>
              <tr><th>环境</th><td>{job.env}</td></tr>
              <tr><th>意图键</th><td style={{ fontFamily: 'monospace', wordBreak: 'break-all' }}>{job.intentKey}</td></tr>
              <tr><th>尝试</th><td>{job.attemptCount} / {job.maxAttempts}</td></tr>
              <tr><th>下次运行</th><td>{fmtShanghai(job.nextRunAtUtc)}</td></tr>
              <tr><th>创建时间</th><td>{fmtShanghai(job.createdAtUtc)}</td></tr>
              <tr><th>结束时间</th><td>{fmtShanghai(job.finishedAtUtc)}</td></tr>
              <tr><th>租约持有</th><td>{job.leaseOwner ?? '—'}</td></tr>
              <tr><th>Fencing token</th><td>{job.fencingToken}</td></tr>
              <tr><th>失败原因</th><td style={{ wordBreak: 'break-all' }}>{job.lastError ?? '—'}</td></tr>
            </tbody>
          </table>
        )}
        <div style={{ marginTop: 12 }}><button onClick={onClose}>关闭窗口</button></div>
      </div>
    </div>
  );
}

function severityTag(severity: 'HIGH' | 'MEDIUM'): CSSProperties {
  return {
    marginLeft: 8, padding: '1px 6px', borderRadius: 3, color: '#fff', fontSize: 12,
    background: severity === 'HIGH' ? '#c0392b' : '#e67e22',
  };
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 900, width: '92%', maxHeight: '85vh', overflow: 'auto' };
const alertHigh: CSSProperties = { borderLeft: '4px solid #c0392b', background: '#fdf0ef', padding: '8px 10px', marginBottom: 6 };
const alertMedium: CSSProperties = { borderLeft: '4px solid #e67e22', background: '#fdf6ec', padding: '8px 10px', marginBottom: 6 };