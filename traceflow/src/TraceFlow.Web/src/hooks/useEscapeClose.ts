import { useEffect, useRef } from 'react';

/**
 * 弹窗 Esc 关闭：仅 `open` 为真时在 document 上挂 keydown 监听，Esc 调用最新 `onClose`。
 * 不拦截其他按键、不 preventDefault；卸载或 `open` 变假时移除监听。
 */
export function useEscapeClose(open: boolean, onClose: () => void): void {
  // onClose 用 ref 保存：监听只按 open 绑定一次，回调更换不重复绑定。
  const handlerRef = useRef(onClose);
  useEffect(() => {
    handlerRef.current = onClose;
  });

  useEffect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') handlerRef.current();
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open]);
}