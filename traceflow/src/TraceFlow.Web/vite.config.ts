import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../TraceFlow.Host/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:5152' } },
  test: {
    environment: 'jsdom',
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    restoreMocks: true,
  },
});