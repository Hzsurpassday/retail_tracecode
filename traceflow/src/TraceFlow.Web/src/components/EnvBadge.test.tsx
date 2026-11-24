import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import EnvBadge from './EnvBadge';

describe('EnvBadge', () => {
  it('LIVE → LIVE 正式环境', () => {
    render(<EnvBadge env="LIVE" />);
    expect(screen.getByText('LIVE 正式环境')).toBeInTheDocument();
  });

  it('MOCK → MOCK 模拟环境', () => {
    render(<EnvBadge env="MOCK" />);
    expect(screen.getByText('MOCK 模拟环境')).toBeInTheDocument();
  });

  it('空串 → 环境未知', () => {
    render(<EnvBadge env="" />);
    expect(screen.getByText('环境未知')).toBeInTheDocument();
  });

  it("'?'（健康检查失败）→ 环境未知", () => {
    render(<EnvBadge env="?" />);
    expect(screen.getByText('环境未知')).toBeInTheDocument();
  });

  it("'PROD' 等未知值 → 环境未知（绝不默认宣称 MOCK）", () => {
    render(<EnvBadge env="PROD" />);
    expect(screen.getByText('环境未知')).toBeInTheDocument();
  });

  it('小写 mock / live 视为未知（只认大写精确值）', () => {
    const { unmount } = render(<EnvBadge env="mock" />);
    expect(screen.getByText('环境未知')).toBeInTheDocument();
    unmount();
    render(<EnvBadge env="live" />);
    expect(screen.getByText('环境未知')).toBeInTheDocument();
  });
});