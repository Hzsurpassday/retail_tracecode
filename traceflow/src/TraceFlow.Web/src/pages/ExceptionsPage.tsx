import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api';
import EnvBadge from '../components/EnvBadge';
import { useEscapeClose } from '../hooks/useEscapeClose';
import { fmtShanghai } from '../utils/time';

// 统一异常队列（spec §4.2）：接入异常 / 对账差异 / 上传任务 / 映射冲突四类聚合。
// 队列只做聚合与跳转：TASK/MAPPING 的处置仍在既有页面，仅 INGEST/RECON 可在此留证关闭。

type Category = 'INGEST' | 'RECON' | 'TASK' | 'MAPPING';

interface QueueItem {
  category: Category;
  id: string;
  status: 'OPEN' | 'RESOLVED';
  title: string;
  detailSnippet: string;
  objectRef: string | null;
  createdAt: string | null;
  env: string | null;
  deepLink: string | null;
}
interface Counts { ingestOpen: number; reconOpen: number; taskOpen: number; mappingOpen: number }
interface QueueData {
  items: QueueItem[]; counts: Counts; page: number; pageSize: number; total: number; env: string;
}
interface DetailResp { item: QueueItem; detail: unknown }

interface IngestDetail {
  exceptionType: string; sourceDocumentKey: string; sourceLineKey: string | null; detailText: string | null;
  status: string; resolvedBy: string | null; resolvedAt: string | null; resolutionEvidence: string | null;
}
interface ReconDetail {
  diffType: string; severity: string; objectRef: string; status: string; ownerRole: string | null;
  nextAction: string | null; createdAt: string; evidence: unknown; evidenceText: string | null;
  resolutionEvidence: unknown; resolutionEvidenceText: string | null; resolvedBy: string | null; resolvedAt: string | null;
  run: {
    runId: string; env: string; windowStart: string; windowEnd: string; businessType: string;
    status: string; startedAt: string; finishedAt: string | null;
  };
}
interface TaskAttempt {
  attemptKind: string; attemptNo: number; httpStatus: number | null; requestId: string | null;
  startedAt: string; finishedAt: string | null;
}
interface TaskDetail {
  state: string; stateReason: string | null; taskType: string; eventKey: string; env: string;
  createdAt: string; version: number; attempts: TaskAttempt[];
}
interface ProductMappingDetail {
  side: 'product';
  local: { spid: string; name: string; displayCode: string | null };
  platform: {
    platformDrugId: string; name: string | null; approvalNoNorm: string | null;
    manufacturerIdentity: string | null; dosageForm: string | null; strength: string | null; packageSpec: string | null;
  };
  mapping: { state: string; version: number; confirmedBy: string | null; confirmedAt: string | null };
  evidence: unknown; evidenceText: string | null;
}
interface SupplierMappingDetail {
  side: 'supplier';
  local: { dwbh: string; name: string };
  platform: { refEntId: string; entId: string; name: string; creditCode: string | null };
  mapping: { state: string; version: number };
  evidence: unknown; evidenceText: string | null;
}

const PAGE_SIZE = 50;
const TABS: { key: '' | Category; label: string }[] = [
  { key: '', label: '全部' },
  { key: 'INGEST', label: '接入异常' },
  { key: 'RECON', label: '对账差异' },
  { key: 'TASK', label: '上传任务' },
  { key: 'MAPPING', label: '映射冲突' },
];
const CATEGORY_ZH: Record<Category, string> = {
  INGEST: '接入异常', RECON: '对账差异', TASK: '上传任务', MAPPING: '映射冲突',
};
const INGEST_TYPE_ZH: Record<string, string> = {
  MISSING_DOC_KEY: '单头单据键为空', ORPHAN_LINE: '无单头行', BAD_BUSINESS_TIME: '业务时间无法解析',
  CROSS_DAY_REUSE: '跨日重复使用单据键', MISSING_LINE_KEY: '明细行键为空',
  DUPLICATE_LINE_KEY: '同单明细行键重复', EMPTY_DETAIL: '明细为空', QTY_NONPOSITIVE: '数量非正',
};
const DIFF_TYPE_ZH: Record<string, string> = {
  SOURCE_MISSING: '来源缺失', UPSTREAM_INCOMPLETE: '上游不完整', MATCH_REQUIRED: '需人工匹配',
  MISSING_CODE: '缺少追溯码', WAIT_VERIFY: '等待核验', WAIT_DEPENDENCY: '等待依赖',
  PLATFORM_MISSING: '平台查无', ACCEPTANCE_UNKNOWN: '受理未知', PARTIAL_SUCCESS: '部分成功',
  SOURCE_CHANGED: '来源变更', RESULT_UNRESOLVED: '结果未决',
};
const TASK_STATE_ZH: Record<string, string> = {
  ManualAction: '需人工核对', ResultUnresolved: '结果未决', AcceptanceUnknown: '受理未知',
};

function errMsg(e: unknown): string { return e instanceof ApiError ? `${e.code}: ${e.message}` : '网络错误'; }
function titleZh(item: QueueItem): string {
  if (item.category === 'INGEST') return INGEST_TYPE_ZH[item.title] ?? item.title;
  if (item.category === 'RECON') return DIFF_TYPE_ZH[item.title] ?? item.title;
  if (item.category === 'TASK') return TASK_STATE_ZH[item.title] ?? item.title;
  return item.title;
}
function statusZh(status: string): string { return status === 'RESOLVED' ? '已解决' : '待处理'; }
// 深链 ?category=RECON 等：工作台待办与站内告警可直接落到对应页签；非法值回落"全部"。
function normalizeCategory(raw: string | null): '' | Category {
  const value = (raw ?? '').trim().toUpperCase();
  return value === 'INGEST' || value === 'RECON' || value === 'TASK' || value === 'MAPPING' ? value : '';
}
function evidenceText(json: unknown, text: string | null): string {
  if (text !== null && text !== '') return text;
  if (json === null || json === undefined) return '（无证据内容）';
  try { return JSON.stringify(json, null, 2); } catch { return String(json); }
}
function openCount(counts: Counts): number {
  return counts.ingestOpen + counts.reconOpen + counts.taskOpen + counts.mappingOpen;
}

export default function ExceptionsPage() {
  const [env, setEnv] = useState('');
  const [searchParams, setSearchParams] = useSearchParams();
  const urlCategory = normalizeCategory(searchParams.get('category'));
  const [tab, setTab] = useState<'' | Category>(urlCategory);
  const [status, setStatus] = useState<'OPEN' | 'RESOLVED'>('OPEN');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<QueueData | null>(null);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<QueueItem | null>(null);
  const seqRef = useRef(0);
  const [reloadKey, setReloadKey] = useState(0);

  // 已挂载时的深链/浏览器前进后退：URL 页签变化 → 同步视图页签（并回到第 1 页）。
  useEffect(() => {
    if (urlCategory !== tab) {
      setTab(urlCategory);
      setPage(1);
    }
    // 仅跟随 URL 变化；tab 自身变更由 selectTab 同步写回 URL。
  }, [urlCategory]); // eslint-disable-line react-hooks/exhaustive-deps

  function selectTab(next: '' | Category) {
    setTab(next);
    setPage(1);
    const params = new URLSearchParams(searchParams);
    if (next) params.set('category', next);
    else params.delete('category');
    setSearchParams(params, { replace: true });
  }

  useEffect(() => {
    let active = true;
    api<{ env: string }>('/api/v1/health')
      .then((h) => { if (active) setEnv(h.env); })
      .catch(() => { if (active) setEnv('?'); });
    return () => { active = false; };
  }, []);

  const load = useCallback(async () => {
    const seq = ++seqRef.current;
    const qs = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), status });
    if (tab) qs.set('category', tab);
    try {
      const res = await api<QueueData>(`/api/v1/exceptions?${qs.toString()}`);
      if (seq !== seqRef.current) return;
      setData(res);
      setError('');
    } catch (e) {
      if (seq !== seqRef.current) return;
      setError(errMsg(e));
    }
  }, [tab, status, page, reloadKey]);

  // 筛选/页码变化：先清空陈旧行与旧错误，再按新条件取数（不留上一筛选的残留行）。
  useEffect(() => {
    setData(null);
    setError('');
    let active = true;
    (async () => { if (active) await load(); })();
    return () => { active = false; };
  }, [load]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;
  const counts = data?.counts;

  function resolveDone() {
    setSelected(null);
    setReloadKey(reloadKey + 1);
  }

  return (
    <section>
      <h1>人工补配与异常 <EnvBadge env={env} /></h1>
      <p>四类待处理项统一聚合：接入异常与对账差异可在此留证关闭；上传任务与映射冲突请在对应页面处置。</p>
      <div style={{ display: 'flex', gap: 8, marginBottom: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        {TABS.map((t) => {
          const n = counts === undefined ? null
            : t.key === '' ? openCount(counts)
            : t.key === 'INGEST' ? counts.ingestOpen
            : t.key === 'RECON' ? counts.reconOpen
            : t.key === 'TASK' ? counts.taskOpen
            : counts.mappingOpen;
          return (
            <button key={t.key} aria-pressed={tab === t.key}
              style={tab === t.key ? activeTab : undefined}
              onClick={() => { selectTab(t.key); }}>
              {t.label}{n !== null ? ` (${n})` : ''}
            </button>
          );
        })}
      </div>
      <div style={{ display: 'flex', gap: 8, marginBottom: 12, alignItems: 'center' }}>
        <label>状态
          <select aria-label="状态筛选" value={status}
            onChange={(e) => { setStatus(e.target.value === 'RESOLVED' ? 'RESOLVED' : 'OPEN'); setPage(1); }}>
            <option value="OPEN">待处理</option>
            <option value="RESOLVED">已解决</option>
          </select>
        </label>
        {data && <span style={{ color: '#666' }}>环境 {data.env}</span>}
      </div>
      {error && <p role="alert">{error}</p>}
      {data === null ? (error ? null : <p>加载中…</p>) : data.items.length === 0 ? (
        <p>暂无{status === 'OPEN' ? '待处理' : '已解决'}记录。</p>
      ) : (
        <>
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr>
                <th>类别</th><th>对象</th><th>类型 / 标题</th><th>摘要</th><th>状态</th><th>时间</th><th>操作</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={`${r.category}:${r.id}`}>
                  <td>{CATEGORY_ZH[r.category]}</td>
                  <td style={{ fontFamily: 'monospace' }}>{r.objectRef ?? '—'}</td>
                  <td title={r.title}>{titleZh(r)}</td>
                  <td title={r.detailSnippet}>{r.detailSnippet || '—'}</td>
                  <td>{statusZh(r.status)}</td>
                  <td>{fmtShanghai(r.createdAt)}</td>
                  <td>
                    <button onClick={() => setSelected(r)}>查看</button>
                    {(r.category === 'INGEST' || r.category === 'RECON') && r.status === 'OPEN' && (
                      <button style={{ marginLeft: 6 }} onClick={() => setSelected(r)}>留证关闭</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center' }}>
            <button disabled={page <= 1} onClick={() => setPage(page - 1)}>上一页</button>
            <span>第 {data.page} / {totalPages} 页 · 共 {data.total} 条 · 每页 {data.pageSize} 条</span>
            <button disabled={page >= totalPages} onClick={() => setPage(page + 1)}>下一页</button>
          </div>
        </>
      )}
      {selected !== null && (
        <ExceptionDrawer item={selected} onClose={() => setSelected(null)} onResolved={resolveDone} />
      )}
    </section>
  );
}

function ExceptionDrawer({ item, onClose, onResolved }: {
  item: QueueItem; onClose: () => void; onResolved: () => void;
}) {
  const [resp, setResp] = useState<DetailResp | null>(null);
  const [error, setError] = useState('');
  const [evidence, setEvidence] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [idemKey, setIdemKey] = useState(() => crypto.randomUUID());
  // 抽屉仅在打开时挂载：Esc 复用同一 onClose（父级只清 selected），不另造关闭路径。
  useEscapeClose(true, onClose);
  const canResolve = (item.category === 'INGEST' || item.category === 'RECON') && item.status === 'OPEN';

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const qs = item.env ? `?env=${encodeURIComponent(item.env)}` : '';
        const d = await api<DetailResp>(
          `/api/v1/exceptions/${item.category}/${encodeURIComponent(item.id)}${qs}`);
        if (active) { setResp(d); setError(''); }
      } catch (e) {
        if (active) setError(errMsg(e));
      }
    })();
    return () => { active = false; };
  }, [item]);

  async function submit() {
    const text = evidence.trim();
    if (!canResolve || text === '' || submitting) return;
    setSubmitting(true);
    setError('');
    try {
      await api(`/api/v1/exceptions/${item.category}/${encodeURIComponent(item.id)}/resolve`, {
        method: 'POST',
        headers: { 'Idempotency-Key': idemKey },
        body: JSON.stringify({ resolutionEvidence: text }),
      });
      onResolved();
    } catch (e) {
      setError(errMsg(e));
    } finally {
      setSubmitting(false);
      // 每次提交后轮换幂等键：证据文本被修改重试不会与旧请求体冲突。
      setIdemKey(crypto.randomUUID());
    }
  }

  return (
    <div style={overlay}>
      <div style={panel} role="dialog" aria-modal="true"
        aria-label={`${CATEGORY_ZH[item.category]} · ${item.objectRef ?? item.id}`}>
        <h3>{CATEGORY_ZH[item.category]} · {item.objectRef ?? item.id}</h3>
        <p>
          标题 {titleZh(item)} · 状态 {statusZh(item.status)} · 环境 {item.env ?? '—'}
          {' · 时间 '}{fmtShanghai(item.createdAt)} · 记录 {item.category}:{item.id}
        </p>
        {item.deepLink && (
          <p>
            处置入口：{item.category === 'TASK' ? (
              <Link to={item.deepLink} onClick={onClose}>打开上传任务</Link>
            ) : item.category === 'MAPPING' ? (
              <Link to={item.deepLink} onClick={onClose}>前往映射页面</Link>
            ) : (
              <Link to={item.deepLink} onClick={onClose}>前往导入页面</Link>
            )}
            <span style={{ color: '#666' }}>（{item.deepLink}）</span>
          </p>
        )}
        {error && <p role="alert">{error}</p>}
        {resp === null ? (error ? null : <p>加载中…</p>) : (
          <CategoryDetail item={resp.item} detail={resp.detail} />
        )}
        {canResolve ? (
          <div style={{ marginTop: 12 }}>
            <h4>留证关闭</h4>
            <textarea aria-label="解决证据" value={evidence} maxLength={4000} rows={4}
              style={{ width: '100%' }}
              placeholder="填写处置依据（必填，≤4000 字；将写入解决证据与审计摘要）"
              onChange={(e) => setEvidence(e.target.value)} />
            <div style={{ marginTop: 8, display: 'flex', gap: 8 }}>
              <button disabled={submitting || evidence.trim() === ''} onClick={() => { void submit(); }}>
                {submitting ? '提交中…' : '留证关闭'}
              </button>
              <button onClick={onClose}>取消</button>
            </div>
          </div>
        ) : (
          <p style={{ marginTop: 12, color: '#666' }}>
            {item.status === 'RESOLVED'
              ? '该记录已解决，无需重复处理。'
              : item.category === 'TASK'
                ? '上传任务在“上传中心”处置（人工核对/受控重试），队列不提供直接关闭。'
                : '映射冲突在“商品映射/供应商映射”页面确认或改绑，队列不提供直接关闭。'}
          </p>
        )}
        <div style={{ marginTop: 12 }}>
          <button onClick={onClose}>关闭窗口</button>
        </div>
      </div>
    </div>
  );
}

function CategoryDetail({ item, detail }: { item: QueueItem; detail: unknown }) {
  if (item.category === 'INGEST') {
    const d = detail as IngestDetail;
    return (
      <div>
        <h4>接入异常</h4>
        <p>
          异常类型 {d.exceptionType} · 单据键 {d.sourceDocumentKey}
          {d.sourceLineKey ? ` · 行键 ${d.sourceLineKey}` : ''} · 状态 {d.status}
        </p>
        <pre style={pre}>{d.detailText ?? '（无明细）'}</pre>
        {d.resolvedBy && (
          <p>解决人 {d.resolvedBy} · 解决时间 {fmtShanghai(d.resolvedAt)} · 证据 {d.resolutionEvidence ?? '—'}</p>
        )}
      </div>
    );
  }
  if (item.category === 'RECON') {
    const d = detail as ReconDetail;
    return (
      <div>
        <h4>对账差异</h4>
        <p>
          差异类型 {d.diffType} · 严重度 {d.severity} · 责任角色 {d.ownerRole ?? '—'}
          {' · 对象 '}{d.objectRef} · 状态 {d.status}
        </p>
        <p>
          对账运行 #{d.run.runId} · 环境 {d.run.env} · {d.run.businessType} · 状态 {d.run.status}
          {' · 窗口 '}{fmtShanghai(d.run.windowStart)} ~ {fmtShanghai(d.run.windowEnd)}
        </p>
        {d.nextAction && <p>建议动作：{d.nextAction}</p>}
        <h4>证据</h4>
        <pre style={pre}>{evidenceText(d.evidence, d.evidenceText)}</pre>
        {(d.resolutionEvidence !== null || d.resolutionEvidenceText !== null) && (
          <>
            <h4>解决证据</h4>
            <pre style={pre}>{evidenceText(d.resolutionEvidence, d.resolutionEvidenceText)}</pre>
            <p>解决人 {d.resolvedBy ?? '—'} · 解决时间 {fmtShanghai(d.resolvedAt)}</p>
          </>
        )}
      </div>
    );
  }
  if (item.category === 'TASK') {
    const d = detail as TaskDetail;
    return (
      <div>
        <h4>上传任务</h4>
        <p>
          状态 {d.state} · 任务类型 {d.taskType} · 事件键 {d.eventKey} · 环境 {d.env}
          {' · 版本 '}{d.version} · 创建 {fmtShanghai(d.createdAt)}
        </p>
        {d.stateReason && <p>状态原因：{d.stateReason}</p>}
        <h4>最近尝试（最多 5 次）</h4>
        {d.attempts.length === 0 ? <p>暂无尝试记录。</p> : (
          <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
            <thead>
              <tr><th>类型</th><th>序号</th><th>HTTP</th><th>请求 id</th><th>开始</th><th>结束</th></tr>
            </thead>
            <tbody>
              {d.attempts.map((a) => (
                <tr key={`${a.attemptKind}:${a.attemptNo}`}>
                  <td>{a.attemptKind}</td><td>{a.attemptNo}</td><td>{a.httpStatus ?? '—'}</td>
                  <td style={{ fontFamily: 'monospace' }}>{a.requestId ?? '—'}</td>
                  <td>{fmtShanghai(a.startedAt)}</td><td>{fmtShanghai(a.finishedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    );
  }
  if ((detail as ProductMappingDetail).side === 'product') {
    const d = detail as ProductMappingDetail;
    return (
      <div>
        <h4>商品映射冲突</h4>
        <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
          <thead><tr><th>字段</th><th>本地商品</th><th>平台药品</th></tr></thead>
          <tbody>
            <tr><td>标识</td><td>{d.local.spid}</td><td>{d.platform.platformDrugId}</td></tr>
            <tr><td>名称</td><td>{d.local.name}</td><td>{d.platform.name ?? '—'}</td></tr>
            <tr><td>显示码</td><td>{d.local.displayCode ?? '—'}</td><td>{d.platform.approvalNoNorm ?? '—'}</td></tr>
            <tr><td>生产厂家</td><td>—</td><td>{d.platform.manufacturerIdentity ?? '—'}</td></tr>
            <tr><td>剂型/规格</td><td>—</td>
              <td>{[d.platform.dosageForm, d.platform.strength, d.platform.packageSpec].filter(Boolean).join(' · ') || '—'}</td>
            </tr>
          </tbody>
        </table>
        <p>映射态 {d.mapping.state} · 版本 {d.mapping.version} · 确认人 {d.mapping.confirmedBy ?? '—'}
          {' · 确认时间 '}{fmtShanghai(d.mapping.confirmedAt)}</p>
        <h4>证据</h4>
        <pre style={pre}>{evidenceText(d.evidence, d.evidenceText)}</pre>
      </div>
    );
  }
  const d = detail as SupplierMappingDetail;
  return (
    <div>
      <h4>供应商映射冲突</h4>
      <table border={1} cellPadding={4} style={{ borderCollapse: 'collapse', width: '100%' }}>
        <thead><tr><th>字段</th><th>本地供应商</th><th>平台企业</th></tr></thead>
        <tbody>
          <tr><td>标识</td><td>{d.local.dwbh}</td><td>{d.platform.refEntId}</td></tr>
          <tr><td>名称</td><td>{d.local.name}</td><td>{d.platform.name}</td></tr>
          <tr><td>企业 id</td><td>—</td><td>{d.platform.entId}</td></tr>
          <tr><td>统一社会信用代码</td><td>—</td><td>{d.platform.creditCode ?? '—'}</td></tr>
        </tbody>
      </table>
      <p>映射态 {d.mapping.state} · 版本 {d.mapping.version}</p>
      <h4>证据</h4>
      <pre style={pre}>{evidenceText(d.evidence, d.evidenceText)}</pre>
    </div>
  );
}

const overlay: CSSProperties = { position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 };
const panel: CSSProperties = { background: '#fff', padding: 20, borderRadius: 8, maxWidth: 1040, width: '95%', maxHeight: '85vh', overflow: 'auto' };
const pre: CSSProperties = { background: '#f6f8fa', padding: 8, maxHeight: 240, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all' };
const activeTab: CSSProperties = { fontWeight: 'bold', borderColor: '#333' };