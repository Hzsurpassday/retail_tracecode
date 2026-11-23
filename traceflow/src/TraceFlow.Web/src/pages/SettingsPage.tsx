import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { ConnectionTestResult, SettingView } from '../m1types';

function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }

export default function SettingsPage() {
  const [settings, setSettings] = useState<SettingView[] | null>(null);
  const [error, setError] = useState('');
  const [editTarget, setEditTarget] = useState<SettingView | null>(null);
  const [testMsg, setTestMsg] = useState('');
  const [busyKey, setBusyKey] = useState('');

  const load = useCallback(async () => {
    try { setSettings(await api<SettingView[]>('/api/v1/settings')); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, []);
  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);

  async function testConnection(row: SettingView) {
    setTestMsg(''); setBusyKey(row.key);
    try {
      const r = await api<ConnectionTestResult>('/api/v1/settings/connections/test', {
        method: 'POST', body: JSON.stringify({ configId: row.id }),
      });
      setTestMsg(`${r.ok ? '✅' : '❌'} ${r.message}`);
    } catch (e) { setTestMsg(errMsg(e)); }
    finally { setBusyKey(''); }
  }

  async function togglePause(row: SettingView) {
    const next = row.valueJson === 'true' ? 'false' : 'true';
    setBusyKey(row.key); setError('');
    try {
      await api(`/api/v1/settings/${row.key}`, {
        method: 'PUT', headers: idem(),
        body: JSON.stringify({ valueJson: next, expectedVersion: row.version, reason: '切换渠道暂停' }),
      });
      await load();
    } catch (e) { setError(errMsg(e)); }
    finally { setBusyKey(''); }
  }

  return (
    <section>
      <h1>系统设置</h1>
      {error && <p role="alert">{error}</p>}
      {testMsg && <p role="status">{testMsg}</p>}
      {settings === null ? <p>加载中…</p> : (
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>键</th><th>值</th><th>版本</th><th>更新者</th><th>理由</th><th>操作</th></tr></thead>
          <tbody>
            {settings.map((s) => (
              <tr key={s.key}>
                <td>{s.key}</td>
                <td style={{ maxWidth: 280, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.isSecret ? (s.hasSecretValue ? '••••（已保存，只写）' : '（未设置）') : (s.valueJson ?? '（默认）')}
                </td>
                <td>{s.version}</td><td>{s.updatedBy ?? '—'}</td><td>{s.reason ?? '—'}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button onClick={() => setEditTarget(s)}>编辑</button>{' '}
                  {s.key === 'erp.connection' && <button disabled={s.id === 0 || busyKey === s.key} onClick={() => void testConnection(s)}>连接测试</button>}{' '}
                  {s.key === 'sync.paused' && <button disabled={busyKey === s.key} onClick={() => void togglePause(s)}>{s.valueJson === 'true' ? '恢复渠道' : '暂停渠道'}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editTarget && <SettingEditDialog setting={editTarget} onClose={() => setEditTarget(null)} onDone={() => { setEditTarget(null); void load(); }} />}
    </section>
  );
}

function SettingEditDialog({ setting, onClose, onDone }: { setting: SettingView; onClose: () => void; onDone: () => void }) {
  const [valueJson, setValueJson] = useState(setting.valueJson ?? '');
  const [secretValue, setSecretValue] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit() {
    if (!reason.trim()) { setError('请填写修改理由'); return; }
    if (setting.isSecret && !secretValue) { setError('密钥不能为空'); return; }
    if (!setting.isSecret && !valueJson.trim()) { setError('值不能为空'); return; }
    setBusy(true); setError('');
    try {
      await api(`/api/v1/settings/${setting.key}`, {
        method: 'PUT', headers: idem(),
        body: JSON.stringify({
          valueJson: setting.isSecret ? null : valueJson,
          secretValue: setting.isSecret ? secretValue : null,
          expectedVersion: setting.version, reason,
        }),
      });
      onDone();
    } catch (e) { setError(errMsg(e)); setBusy(false); }
  }
  return (
    <div style={overlay}>
      <div style={panel}>
        <h3>编辑设置 · {setting.key}</h3>
        {setting.isSecret ? (
          <>
            <p>密钥只写：输入新值保存，不会回显已存明文。{setting.hasSecretValue ? '当前已保存密钥。' : '当前未设置。'}</p>
            <input type="password" value={secretValue} onChange={(e) => setSecretValue(e.target.value)} style={{ width: '100%' }} placeholder="新密钥/连接串" autoComplete="new-password" />
          </>
        ) : (
          <>
            <p>JSON 值（按配置类型校验）：</p>
            <textarea value={valueJson} onChange={(e) => setValueJson(e.target.value)} rows={4} style={{ width: '100%', fontFamily: 'monospace' }} />
          </>
        )}
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} rows={2} style={{ width: '100%', marginTop: 8 }} placeholder="修改理由（必填）" />
        {error && <p role="alert">{error}</p>}
        <div style={{ marginTop: 8 }}><button disabled={busy} onClick={submit}>保存</button> <button onClick={onClose}>取消</button></div>
      </div>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 640, width: '90%', maxHeight: '85vh', overflow: 'auto' };
