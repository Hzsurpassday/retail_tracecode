export default function EnvBadge({ env }: { env: string }) {
  const isLive = env === 'LIVE';
  return (
    <span style={{ padding: '2px 10px', borderRadius: 4, fontWeight: 700,
      background: isLive ? '#c0392b' : '#2980b9', color: '#fff' }}>
      {isLive ? 'LIVE 正式环境' : 'MOCK 模拟环境'}
    </span>
  );
}