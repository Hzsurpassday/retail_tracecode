import { useState, type FormEvent } from 'react';
import { api, ApiError } from '../api';

export default function LoginPage({ onLoggedIn }: { onLoggedIn: () => Promise<void> }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError('');
    try {
      await api('/api/v1/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) });
      await onLoggedIn();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '网络错误');
    }
  }

  return (
    <form onSubmit={handleSubmit}>
      <h1>TraceFlow 登录</h1>
      <input value={username} onChange={(e) => setUsername(e.target.value)} placeholder="用户名" autoComplete="username" />
      <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="密码" autoComplete="current-password" />
      <button type="submit">登录</button>
      {error && <p role="alert">{error}</p>}
    </form>
  );
}
