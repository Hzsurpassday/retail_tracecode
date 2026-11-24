import { useEffect, useState } from 'react';
import { Route, Routes } from 'react-router-dom';
import { loadSession, setUnauthorizedHandler, type SessionData } from './api';
import AppShell from './components/AppShell';
import LoginPage from './pages/LoginPage';
import DashboardPage from './pages/DashboardPage';
import ProductsPage from './pages/ProductsPage';
import ImportWizard from './pages/ImportWizard';
import SuppliersPage from './pages/SuppliersPage';
import UpstreamBillsPage from './pages/UpstreamBillsPage';
import InboundsPage from './pages/InboundsPage';
import SalesPage from './pages/SalesPage';
import ReturnsPage from './pages/ReturnsPage';
import UploadCenterPage from './pages/UploadCenterPage';
import OpsPage from './pages/OpsPage';
import CodesPage from './pages/CodesPage';
import ExceptionsPage from './pages/ExceptionsPage';
import SettingsPage from './pages/SettingsPage';

export default function App() {
  const [session, setSession] = useState<SessionData | null>(null);

  useEffect(() => {
    setUnauthorizedHandler(() => setSession({ authenticated: false, csrfToken: '' }));
    loadSession().then(setSession).catch(() => setSession({ authenticated: false, csrfToken: '' }));
  }, []);

  if (session === null) return <p>加载中…</p>;
  if (!session.authenticated) return <LoginPage onLoggedIn={async () => setSession(await loadSession())} />;
  return (
    <AppShell session={session} onLoggedOut={async () => setSession(await loadSession())}>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/products" element={<ProductsPage />} />
        <Route path="/imports" element={<ImportWizard />} />
        <Route path="/suppliers" element={<SuppliersPage />} />
        <Route path="/upstream" element={<UpstreamBillsPage />} />
        <Route path="/inbounds" element={<InboundsPage />} />
        <Route path="/sales" element={<SalesPage />} />
        <Route path="/returns" element={<ReturnsPage />} />
        <Route path="/uploads" element={<UploadCenterPage />} />
        <Route path="/ops" element={<OpsPage />} />
        <Route path="/codes" element={<CodesPage />} />
        <Route path="/exceptions" element={<ExceptionsPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="*" element={<p>该模块在后续阶段交付（见路线图），当前未接入。</p>} />
      </Routes>
    </AppShell>
  );
}
