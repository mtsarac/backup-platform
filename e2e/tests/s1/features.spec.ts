import { expect, request, test } from '@playwright/test'
import { apiUrl } from '../../api'
import { s, signInAs } from './helpers'

test('GET /api/features is public and returns every flag as a boolean', async () => {
  const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  const r = await ctx.get('/api/features')
  expect(r.status()).toBe(200)
  const flags = (await r.json()) as Record<string, unknown>
  for (const k of ['sms', 'smb', 'sizeAnomaly', 'windowsVss', 'linuxSnapshots', 'externalSecrets', 'replicaRepair']) expect(typeof flags[k], k).toBe('boolean')
  await ctx.dispose()
})

test('a disabled flag hides its UI: with sms off the platform e-mail/SMS transport page offers no SMS transport', async ({ page }) => {
  const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  const flags = (await (await ctx.get('/api/features')).json()) as { sms: boolean }
  await ctx.dispose()
  test.skip(flags.sms, 'sms flag is on in this stack')
  await signInAs(page, s.accounts.platformAdmin)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto('/platform/notification-transports')
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(page.locator('main')).not.toContainText(/SMS/)
})
