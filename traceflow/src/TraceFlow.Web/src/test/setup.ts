import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// vitest 未启用 globals：RTL 16 不会自动注册清理，必须手动 afterEach。
afterEach(() => {
  cleanup();
});