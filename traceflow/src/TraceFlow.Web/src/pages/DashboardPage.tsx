import { useEffect, useState } from 'react';
import { api } from '../api';

interface Dashboard {
  env: string;
  openIngestExceptions: number;
  pendingJobs: number;
  uploadTasksByState: Record<string, number>;
  lastActivityUtc: string | null;
}

export default function DashboardPage() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const d = await api<Dashboard>('/api/v1/dashboard');
        if (active) { setData(d); setError(''); }
      } catch {
        if (active) setError('加载失败，10 秒后重试');
      }
    }
    load();
    const timer = setInterval(load, 10000); // 轮询本系统，不直接调用 TOP（UI-12）
    return () => { active = false; clearInterval(timer); };
  }, []);

  if (error) return <p role="alert">{error}</p>;
  if (data === null) return <p>加载中…</p>;
  const succeeded = data.uploadTasksByState['Succeeded'] ?? 0;
  const total = Object.values(data.uploadTasksByState).reduce((a, b) => a + b, 0);
  return (
    <section>
      <h1>工作台</h1>
      <ul>
        <li>接入异常（待处理）：{data.openIngestExceptions}</li>
        <li>排队作业：{data.pendingJobs}</li>
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
