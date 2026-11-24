import { useEffect, useState, type CSSProperties } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api';

interface Dashboard {
  env: string;
  openIngestExceptions: number;
  pendingJobs: number;
  uploadTasksByState: Record<string, number>;
  lastActivityUtc: string | null;
  queue: { ingestOpen: number; reconOpen: number; taskOpen: number; mappingOpen: number };
  openDifferences: number;
  failedJobs24h: number;
  activePauses: number;
  alertsCount: number;
  todayUploads: { total: number; succeeded: number; successRate: number | null };
}
interface AlertItem {
  code: string; severity: 'HIGH' | 'MEDIUM'; title: string; detail: string; count: number; deepLink: string;
}
interface AlertData { env: string; alerts: AlertItem[] }

const ALERT_ZH: Record<string, string> = {
  CHANNEL_PAUSED: '通道暂停', LEASE_RECOVERED_24H: '租约回收', JOB_BACKLOG_HIGH: '作业积压',
  JOBS_FAILED_24H: '作业失败', RECON_INCOMPLETE_24H: '对账未完成', RECON_DIFFERENCES_OPEN: '对账差异',
  RATE_BUDGET_WAITING: '限流等待',
};
function pct(ratio: number | null): string {
  return ratio === null ? '—' : `${(ratio * 100).toFixed(2)}%`;
}

export default function DashboardPage() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [alerts, setAlerts] = useState<AlertItem[] | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const [d, a] = await Promise.all([
          api<Dashboard>('/api/v1/dashboard'),
          api<AlertData>('/api/v1/ops/alerts'),
        ]);
        if (active) { setData(d); setAlerts(a.alerts); setError(''); }
      } catch {
        if (active) setError('加载失败，10 秒后重试');
      }
    }
    load();
    const timer = setInterval(load, 10000); // 轮询本系统，不直接调用 TOP（UI-12）
    return () => { active = false; clearInterval(timer); };
  }, []);

  if (error) return <p role="alert">{error}</p>;
  if (data === null || alerts === null) return <p>加载中…</p>;
  const succeeded = data.uploadTasksByState['Succeeded'] ?? 0;
  const total = Object.values(data.uploadTasksByState).reduce((a, b) => a + b, 0);
  return (
    <section>
      <h1>工作台</h1>
      {alerts.length > 0 ? (
        <div style={{ marginBottom: 12 }}>
          <strong>站内告警（{data.alertsCount}）</strong>
          <ul style={{ listStyle: 'none', padding: 0, marginTop: 6 }}>
            {alerts.map((a, i) => (
              <li key={`${a.code}:${i}`} style={a.severity === 'HIGH' ? alertHigh : alertMedium}>
                <span style={severityTag(a.severity)}>{a.severity}</span>
                <strong> {ALERT_ZH[a.code] ?? a.code}</strong> · {a.title}（{a.count}）
                <span style={{ marginLeft: 8 }}><Link to={a.deepLink}>处理</Link></span>
                <div style={{ fontSize: 13 }}>{a.detail}</div>
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <p style={{ color: '#666' }}>当前无站内告警。</p>
      )}
      <h2>待办</h2>
      <ul>
        <li><Link to="/exceptions?category=INGEST">接入异常（待处理）：{data.queue.ingestOpen}</Link></li>
        <li><Link to="/exceptions?category=RECON">对账差异（待处理）：{data.queue.reconOpen}</Link>
          {' '}<span style={{ color: '#666' }}>（开放差异 {data.openDifferences}）</span></li>
        <li><Link to="/exceptions?category=TASK">上传任务（待人工）：{data.queue.taskOpen}</Link></li>
        <li><Link to="/exceptions?category=MAPPING">映射冲突：{data.queue.mappingOpen}</Link></li>
        <li>排队作业：{data.pendingJobs} · 24h 失败作业：{data.failedJobs24h} · 激活暂停：{data.activePauses}</li>
      </ul>
      <h2>今日上传</h2>
      <ul>
        <li>今日新建上传任务：{data.todayUploads.total}</li>
        <li>最终全部成功：{data.todayUploads.succeeded} · 成功率（结算口径=成功/(成功+失败)）：{pct(data.todayUploads.successRate)}</li>
      </ul>
      <h2>上传状态</h2>
      <ul>
        <li>上传任务总数：{total}；最终全部成功：{succeeded}（成功率口径=最终全部成功，部分成功/处理中/受理未知单列）</li>
      </ul>
      <table>
        <thead><tr><th>上传状态</th><th>数量</th></tr></thead>
        <tbody>
          {Object.entries(data.uploadTasksByState).map(([state, n]) => (
            <tr key={state}><td>{state}</td><td>{n}</td></tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function severityTag(severity: 'HIGH' | 'MEDIUM'): CSSProperties {
  return {
    padding: '1px 6px', borderRadius: 3, color: '#fff', fontSize: 12,
    background: severity === 'HIGH' ? '#c0392b' : '#e67e22',
  };
}
const alertHigh: CSSProperties = { borderLeft: '4px solid #c0392b', background: '#fdf0ef', padding: '6px 10px', marginBottom: 4 };
const alertMedium: CSSProperties = { borderLeft: '4px solid #e67e22', background: '#fdf6ec', padding: '6px 10px', marginBottom: 4 };