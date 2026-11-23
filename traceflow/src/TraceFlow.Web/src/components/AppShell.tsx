import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { api, type SessionData } from '../api';
import EnvBadge from './EnvBadge';

const NAV = [
  { to: '/', label: '工作台', ready: true },
  { to: '/products', label: '商品映射', ready: true },
  { to: '/imports', label: '主数据导入', ready: true },
  { to: '/suppliers', label: '供应商映射', ready: true },
  { to: '/upstream', label: '上游出库单', ready: false },
  { to: '/inbounds', label: '入库关联', ready: false },
  { to: '/sales', label: 'POS 销售', ready: false },
  { to: '/returns', label: '退货', ready: false },
  { to: '/codes', label: '码台账', ready: false },
  { to: '/uploads', label: '上报对账', ready: false },
  { to: '/exceptions', label: '人工补配与异常', ready: false },
  { to: '/settings', label: '设置', ready: true },
  { to: '/audit', label: '审计', ready: false },
];

export default function AppShell({ session, onLoggedOut, children }: {
  session: SessionData; onLoggedOut: () => Promise<void>; children: ReactNode;
}) {
  const [env, setEnv] = useState('');
  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);

  async function handleLogout() {
    try { await api('/api/v1/auth/logout', { method: 'POST' }); }
    catch { /* 登出请求失败也回到登录页 */ }
    finally { await onLoggedOut(); }
  }

  return (
    <div style={{ display: 'flex', minHeight: '100vh' }}>
      <nav style={{ width: 200, borderRight: '1px solid #ddd', padding: 12 }}>
        <h2>TraceFlow</h2>
        {NAV.map((item) =>
          item.ready ? (
            <p key={item.to}><Link to={item.to}>{item.label}</Link></p>
          ) : (
            <p key={item.to} style={{ color: '#999' }}>{item.label}（未接入）</p>
          ),
        )}
      </nav>
      <main style={{ flex: 1, padding: 16 }}>
        <header style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 16 }}>
          <EnvBadge env={env} />
          <span>{session.username} <button onClick={handleLogout}>退出</button></span>
        </header>
        {children}
      </main>
    </div>
  );
}
