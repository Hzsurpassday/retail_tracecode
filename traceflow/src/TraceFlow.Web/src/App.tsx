import { useEffect, useState } from 'react';
import { Route, Routes } from 'react-router-dom';
import { loadSession, type SessionData } from './api';
import AppShell from './components/AppShell';
import LoginPage from './pages/LoginPage';
import DashboardPage from './pages/DashboardPage';

export default function App() {
  const [session, setSession] = useState<SessionData | null>(null);

  useEffect(() => {
    loadSession().then(setSession).catch(() => setSession({ authenticated: false, csrfToken: '' }));
  }, []);

  if (session === null) return <p>加载中…</p>;
  if (!session.authenticated) return <LoginPage onLoggedIn={async () => setSession(await loadSession())} />;
  return (
    <AppShell session={session} onLoggedOut={async () => setSession(await loadSession())}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="*" element={<p>该模块在后续阶段交付（见路线图），当前未接入。</p>} />
      </Routes>
    </AppShell>
  );
}
