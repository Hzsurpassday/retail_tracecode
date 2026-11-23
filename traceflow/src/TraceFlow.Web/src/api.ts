export interface SessionData {
  authenticated: boolean;
  username?: string;
  permissions?: string[];
  csrfToken: string;
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryable: boolean) {
    super(message);
  }
}

let csrfToken = '';
let unauthorizedHandler: (() => void) | null = null;

export function setUnauthorizedHandler(fn: () => void): void { unauthorizedHandler = fn; }

export async function loadSession(): Promise<SessionData> {
  const resp = await fetch('/api/v1/auth/session', { credentials: 'include' });
  const json = await resp.json();
  csrfToken = json.data.csrfToken;
  return json.data as SessionData;
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers);
  if (method !== 'GET' && method !== 'HEAD') headers.set('X-CSRF-TOKEN', csrfToken);
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const resp = await fetch(path, { ...init, headers, credentials: 'include' });
  if (resp.status === 401) unauthorizedHandler?.();
  const json: unknown = await resp.json().catch(() => null);
  if (!resp.ok) {
    const e = json as { code?: string; message?: string; retryable?: boolean } | null;
    throw new ApiError(resp.status, e?.code ?? 'HTTP_' + resp.status, e?.message ?? '请求失败', e?.retryable ?? false);
  }
  if (json === null || typeof json !== 'object') {
    throw new ApiError(resp.status, 'NON_JSON', '成功响应不是合法 JSON', false);
  }
  return (json as { data: T }).data;
}
