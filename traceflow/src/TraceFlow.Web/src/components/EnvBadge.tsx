export default function EnvBadge({ env }: { env: string }) {
  // 只有明确收到 LIVE/MOCK 才宣称环境；''/`'?'`/未知值一律中性灰（never claim MOCK by default）。
  const kind = env === 'LIVE' ? 'live' : env === 'MOCK' ? 'mock' : 'unknown';
  const label = kind === 'live' ? 'LIVE 正式环境' : kind === 'mock' ? 'MOCK 模拟环境' : '环境未知';
  const background = kind === 'live' ? '#c0392b' : kind === 'mock' ? '#2980b9' : '#7f8c8d';
  return (
    <span style={{ padding: '2px 10px', borderRadius: 4, fontWeight: 700, background, color: '#fff' }}>
      {label}
    </span>
  );
}
