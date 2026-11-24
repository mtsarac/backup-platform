import { defineConfig } from '@playwright/test'

const host = process.env.PUBLIC_HOST ?? '192.168.122.1'

export default defineConfig({
  testDir: './tests',
  workers: 1, // one shared stack and tenant; slices run in separate worktrees/stacks, not in parallel here
  retries: 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? `https://${host}:5173`,
    ignoreHTTPSErrors: true, // lab certificate is self-signed
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: { executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium', args: ['--no-sandbox'] },
  },
})
