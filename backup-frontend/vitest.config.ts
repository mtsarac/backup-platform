import path from 'node:path'
import { configDefaults, defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

/** Test files that only exercise frozen features (support sessions). `bun run test` skips them; `bun run test:frozen` runs only them. */
const frozenTests = [
  'src/features/platform/support/**/*.test.{ts,tsx}',
  'src/features/support-access/**/*.test.{ts,tsx}',
  'src/pages/platform/platform-support-*.test.tsx',
  'src/pages/support-access/**/*.test.{ts,tsx}',
]

export default defineConfig(({ mode }) => ({
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
    ...(mode === 'frozen' ? { include: frozenTests } : { exclude: [...configDefaults.exclude, ...frozenTests] }),
  },
}))
