import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import { api, ApiError } from '../api';
import type { ConnectionTestResult, SettingView } from '../m1types';
import type { ServiceAccount, TopBudgetView } from '../m2types';

function idem(): Record<string, string> { return { 'Idempotency-Key': crypto.randomUUID() }; }
function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
// 后端 datetime2 出网不带偏移量（"YYYY-MM-DDTHH:mm:ss"），故无偏移时显式按 UTC 解释，再转 Asia/Shanghai 显示
const SHANGHAI = new Intl.DateTimeFormat('en-US', {
  timeZone: 'Asia/Shanghai', hourCycle: 'h23',
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
});
function fmt(d: string | null): string {
  if (!d) return '—';
  const iso = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(d) ? d : d.length === 10 ? `${d}T00:00:00Z` : `${d}Z`;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return d.replace('T', ' ').slice(0, 19);
  const parts = SHANGHAI.formatToParts(new Date(t));
  const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((p) => p.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')} ${part('hour')}:${part('minute')}:${part('second')}`;
}
// §4.7：限值 NULL=本地不设帽（与 T12 裁定一致），不虚构数字
const limit = (n: number | null): string => (n === null ? '—（不设帽）' : String(n));

export default function SettingsPage() {
  const [settings, setSettings] = useState<SettingView[] | null>(null);
  const [account, setAccount] = useState<ServiceAccount | null>(null);
  const [budgets, setBudgets] = useState<TopBudgetView | null>(null);
  const [budgetsError, setBudgetsError] = useState('');
  const [error, setError] = useState('');
  const [editTarget, setEditTarget] = useState<SettingView | null>(null);
  const [testMsg, setTestMsg] = useState('');
  const [busyKey, setBusyKey] = useState('');

  const load = useCallback(async () => {
    try { setSettings(await api<SettingView[]>('/api/v1/settings')); setError(''); }
    catch (e) { setError(errMsg(e)); }
  }, []);
  useEffect(() => { let active = true; (async () => { if (active) await load(); })(); return () => { active = false; }; }, [load]);
  useEffect(() => {
    let active = true;
    api<ServiceAccount>('/api/v1/settings/service-account').then((a) => { if (active) setAccount(a); }).catch(() => { /* 面板降级为不显示 */ });
    // 终审修复 F3（NFR-02）：预算面板失败必须显性告警，静默吞错会让面板永远停在「加载中…」，
    // 403/503/断网都被伪装成“还在加载”。失败只影响本面板，其余设置项与账号面板照常可用。
    api<TopBudgetView>('/api/v1/top-budgets')
      .then((b) => { if (active) { setBudgets(b); setBudgetsError(''); } })
      .catch((e) => { if (active) setBudgetsError(errMsg(e)); });
    return () => { active = false; };
  }, []);

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

  const activePauses = budgets ? budgets.pauses.filter((p) => p.isActive) : [];

  return (
    <section>
      <h1>系统设置</h1>
      {error && <p role="alert">{error}</p>}
      {testMsg && <p role="status">{testMsg}</p>}
      <h3>凭证保护与运行账号（§4.7）</h3>
      {account && (
        <p>
          服务运行账号：<code>{account.serviceAccount}</code> · 机器：<code>{account.machineName}</code>。
          密钥以 Windows DPAPI 保护，作用域 CurrentUser 绑定该账号：<strong>换机或恢复备份后密钥不可解密，须重新录入</strong>；
          密文不可解密时系统停止对应调用并告警，无默认凭据兜底。
        </p>
      )}
      <h3>限流预算用量（只读）</h3>
      {budgetsError && <p role="alert">限流预算用量加载失败：{budgetsError}</p>}
      {budgets === null ? (budgetsError ? null : <p>加载中…</p>) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse' }}>
            <thead><tr><th>预算键</th><th>日用量/限值</th><th>分用量/限值</th><th>秒用量/限值</th><th>下次允许时间</th></tr></thead>
            <tbody>
              {budgets.budgets.length === 0 ? <tr><td colSpan={5}>暂无用量记录</td></tr> : budgets.budgets.map((b) => (
                <tr key={b.budgetKey}>
                  <td>{b.budgetKey}</td>
                  <td>{b.dayCount} / {limit(b.dayLimit)}</td>
                  <td>{b.minuteCount} / {limit(b.minuteLimit)}</td>
                  <td>{b.secondCount} / {limit(b.secondLimit)}</td>
                  <td>{fmt(b.nextAllowedAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {activePauses.length > 0 && (
            <>
              <h4>生效中的授权暂停</h4>
              <ul>
                {activePauses.map((p) => (
                  <li key={p.id}>{p.scopeKey} · {p.reason} · {p.pausedBy} 于 {fmt(p.pausedAtUtc)}（恢复须管理员人工动作，M3 交付入口）</li>
                ))}
              </ul>
            </>
          )}
        </>
      )}
      <h3>配置项</h3>
      {settings === null ? <p>加载中…</p> : (
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>键</th><th>值</th><th>保护作用域</th><th>版本</th><th>更新时间</th><th>更新者</th><th>理由</th><th>操作</th></tr></thead>
          <tbody>
            {settings.map((s) => (
              <tr key={s.key}>
                <td>{s.key}</td>
                <td style={{ maxWidth: 280, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.isSecret ? (s.hasSecretValue ? '••••（已保存，只写）' : '（未设置）') : (s.valueJson ?? '（默认）')}
                </td>
                <td>{s.protectionScope ?? '—'}</td>
                <td>{s.version}</td><td>{fmt(s.updatedAtUtc)}</td><td>{s.updatedBy ?? '—'}</td><td>{s.reason ?? '—'}</td>
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
            <p>密钥只写：输入新值保存，不会回显已存明文。{setting.hasSecretValue ? '当前已保存密钥。' : '当前未设置。'}保护作用域：{setting.protectionScope ?? 'CurrentUser（保存时绑定）'}</p>
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
