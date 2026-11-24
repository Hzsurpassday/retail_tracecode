import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useEscapeClose } from './useEscapeClose';

function Harness({ open, onClose }: { open: boolean; onClose: () => void }) {
  useEscapeClose(open, onClose);
  return <div data-testid="harness">{open ? 'open' : 'closed'}</div>;
}

describe('useEscapeClose', () => {
  it('open=true 时 Esc 触发回调一次', () => {
    const onClose = vi.fn();
    render(<Harness open onClose={onClose} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('其他按键不触发回调', () => {
    const onClose = vi.fn();
    render(<Harness open onClose={onClose} />);
    fireEvent.keyDown(document, { key: 'Enter' });
    fireEvent.keyDown(document, { key: 'a' });
    fireEvent.keyDown(document, { key: 'Esc' });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('open=false 时 Esc 不触发回调', () => {
    const onClose = vi.fn();
    render(<Harness open={false} onClose={onClose} />);
    expect(screen.getByTestId('harness')).toHaveTextContent('closed');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('关闭后（rerender open=false）监听已移除', () => {
    const onClose = vi.fn();
    const { rerender, unmount } = render(<Harness open onClose={onClose} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    rerender(<Harness open={false} onClose={onClose} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    // 卸载同样移除监听
    unmount();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('onClose 更换后仍调用最新回调', () => {
    const first = vi.fn();
    const second = vi.fn();
    const { rerender } = render(<Harness open onClose={first} />);
    rerender(<Harness open onClose={second} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledTimes(1);
  });
});