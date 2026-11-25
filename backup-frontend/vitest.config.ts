import path from 'node:path'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'


export default defineConfig({
  plugins: [react()],
  define: { 'import.meta.env.VITE_API_BASE_URL': JSON.stringify('http://localhost/api') },
  resolve: {
    alias: [
      { find: /^@testing-library\/react$/, replacement: path.resolve(import.meta.dirname, './src/test/render.tsx') },
      { find: '@', replacement: path.resolve(import.meta.dirname, './src') },
    ],
  },
  test: {
    environment: 'jsdom', pool: 'vmThreads', maxWorkers: 2, setupFiles: ['./src/test/setup.ts'], restoreMocks: true,
    // Cold vmThreads transforms and findBy/userEvent polling exceed the 5s default when CPUs are busy, so heavy files failed at random; a real hang still fails.
    testTimeout: 20_000, hookTimeout: 20_000,
  },
})
