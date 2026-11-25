import { expect, request, test } from '@playwright/test'
import { apiUrl } from '../../api'
import { s, signInAs, watch } from './helpers'

test('GET /api/features is public and returns every flag as a boolean', async () => {
  const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  const r = await ctx.get('/api/features')
  expect(r.status()).toBe(200)
  const flags = (await r.json()) as Record<string, unknown>
  for (const k of ['sms', 'smb', 'sizeAnomaly', 'windowsVss', 'linuxSnapshots', 'externalSecrets', 'replicaRepair']) {
    expect(typeof flags[k], k).toBe('boolean')
  }
  await ctx.dispose()
})

test('disabled flags hide gated UI on both platform and tenant screens', async ({ page, browser }) => {
  const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  const flags = (await (await ctx.get('/api/features')).json()) as Record<string, boolean>
  await ctx.dispose()
  test.skip(flags.sms && flags.smb, 'both sms and smb flags are on in this stack')

  const w = watch(page)
  if (!flags.sms) {
    await signInAs(page, s.accounts.platformAdmin)
    await page.waitForURL((u) => u.pathname !== '/login')
    await page.goto('/platform/notification-transports')
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
    await expect(page.locator('main')).not.toContainText(/SMS/)
  }
  expect(w.unexpected()).toEqual([])

  if (!flags.smb) {
    const tCtx = await browser.newContext({ ignoreHTTPSErrors: true })
    const tPage = await tCtx.newPage()
    const tw = watch(tPage)
    await signInAs(tPage, s.accounts.userAdmin)
    await tPage.waitForURL(/\/tenants\//)
    await tPage.goto(`/tenants/${s.tenantId}/storage/connections/new`)
    await expect(tPage.getByRole('heading', { level: 1 })).toBeVisible()
    await expect(tPage.locator('main')).not.toContainText(/SMB|Samba/i)
    expect(tw.unexpected()).toEqual([])
    await tCtx.close()
  }
})
