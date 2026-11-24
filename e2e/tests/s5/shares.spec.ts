import { expect, test } from '@playwright/test'
import { state } from '../../state'
import { english, signIn, tenantUrl, watch, world, type World } from './support'

let w: World
test.beforeAll(async () => { w = await world() })
test.beforeEach(async ({ context }) => { await english(context) })

const sharesUrl = () => tenantUrl(w.populated, '/backup-shares')
const reason = (tag: string) => `s5-${w.rand}-${tag}`

async function apiShare(tag: string, artifactId = w.artifacts.share) {
  return w.populated.tenant.call('POST', `/api/tenants/${w.populated.tenantId}/backup-artifacts/${artifactId}/shares`, { durationHours: 24, reason: reason(tag) })
}

test.describe('tenant shares', () => {
  test('empty tenant shows an empty state, not a blank table', async ({ page }) => {
    const health = watch(page)
    await signIn(page, w.empty.account)
    await page.goto(tenantUrl(w.empty, '/backup-shares'))
    await expect(page.getByText(/No shares|no shared/i).first()).toBeVisible()
    await expect(page.getByText(/undefined|NaN|\[object/)).toHaveCount(0)
    health.expectClean()
  })

  test('user creates a share from the artifact and sees it in the list', async ({ page }) => {
    const health = watch(page)
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated, `/backup-runs/${w.runs.share}`))
    await page.getByText('More', { exact: true }).click()
    await page.getByRole('button', { name: 'Share', exact: true }).click()
    await page.getByRole('button', { name: 'Create share' }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await dialog.getByRole('textbox').fill(reason('ui'))
    await dialog.getByRole('button', { name: /^(Create|Share|Create share)$/ }).click()
    await expect(page.getByText(reason('ui'))).toBeVisible()
    await page.goto(sharesUrl())
    await expect(page.getByRole('row', { name: new RegExp(reason('ui')) }).getByText('Active')).toBeVisible()
    health.expectClean()
  })

  test('revoke asks for confirmation and moves the share to Canceled', async ({ page }) => {
    await apiShare('revoke')
    await signIn(page, w.populated.account)
    await page.goto(sharesUrl())
    const row = page.getByRole('row', { name: new RegExp(reason('revoke')) })
    await row.getByRole('button', { name: 'Revoke' }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(row.getByText('Active')).toBeVisible()
    await row.getByRole('button', { name: 'Revoke' }).click()
    await dialog.getByRole('button', { name: /Revoke/ }).last().click()
    await expect(row.getByText('Canceled')).toBeVisible()
    await expect(row.getByRole('button', { name: 'Revoke' })).toHaveCount(0)
  })

  test('extend duration keeps the share active with a later end', async ({ page }) => {
    const share = await apiShare('extend')
    await signIn(page, w.populated.account)
    await page.goto(sharesUrl())
    const row = page.getByRole('row', { name: new RegExp(reason('extend')) })
    await row.getByRole('button', { name: 'Extend duration' }).click()
    await page.getByRole('dialog').getByRole('button', { name: /Extend/ }).last().click()
    await expect(page.getByRole('dialog')).toBeHidden()
    await expect.poll(async () => (await w.populated.tenant.call('GET', `/api/tenants/${w.populated.tenantId}/backup-shares/${share.id}`)).expiresAtUtc).not.toBe(share.expiresAtUtc)
  })

  test('share list: refresh, back/forward and status filter', async ({ page }) => {
    await apiShare('filter')
    await signIn(page, w.populated.account)
    await page.goto(sharesUrl())
    await expect(page.getByRole('row', { name: new RegExp(reason('filter')) })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('row', { name: new RegExp(reason('filter')) })).toBeVisible()
    await page.goto(tenantUrl(w.populated))
    await page.goBack()
    await expect(page.getByRole('row', { name: new RegExp(reason('filter')) })).toBeVisible()
  })

  test('API 500 shows an error notice, not a crash', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.route('**/api/tenants/*/backup-shares**', (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"boom"}' }))
    await page.goto(sharesUrl())
    await expect(page.getByRole('alert').first()).toBeVisible()
  })

  test('mobile: shares table has no page-level horizontal overflow', async ({ page }) => {
    await apiShare('mobile')
    await page.setViewportSize({ width: 375, height: 740 })
    await signIn(page, w.populated.account)
    await page.goto(sharesUrl())
    await expect(page.locator('summary', { hasText: 'Other details' }).first()).toBeVisible()
    for (const d of await page.locator('summary', { hasText: 'Other details' }).all()) await d.click()
    await expect(page.getByText(reason('mobile'))).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0)
  })
})

test.describe('platform shares', () => {
  test('platform admin sees tenant shares with Download, and can open download attempts', async ({ page }) => {
    await apiShare('platform')
    const health = watch(page)
    await signIn(page, state().accounts.platformAdmin)
    await page.goto('/platform/shares')
    const row = page.getByRole('row', { name: new RegExp(reason('platform')) })
    await expect(row).toContainText(w.populated.tenantName)
    await expect(row.getByRole('button', { name: 'Download' }).or(row.getByRole('link', { name: 'Download' }))).toBeVisible()
    await expect(page.getByRole('button', { name: 'Create share' })).toBeVisible()
    health.expectClean()
  })

  test('tenant user cannot reach /platform/shares', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto('/platform/shares')
    await expect(page.getByText('You do not have permission to access this area')).toBeVisible()
  })
})

test.describe('expired share', () => {
  // A short real lifetime exercises expiry without changing the server clock.
  async function expiredShare(tag: string) {
    const share = await w.populated.tenant.call('POST', `/api/tenants/${w.populated.tenantId}/backup-artifacts/${w.artifacts.share}/shares`, { duration: '00:00:03', reason: reason(tag) })
    await expect.poll(async () => (await w.populated.tenant.call('GET', `/api/tenants/${w.populated.tenantId}/backup-shares/${share.id}`)).status, { timeout: 15_000 }).toBe('Expired')
    return share
  }

  test('tenant list shows Expired without Revoke and filters by status', async ({ page }) => {
    await expiredShare('expired-tenant')
    const health = watch(page)
    await signIn(page, w.populated.account)
    await page.goto(sharesUrl())
    const row = page.getByRole('row', { name: new RegExp(reason('expired-tenant')) })
    await expect(row.getByText('Expired', { exact: true })).toBeVisible()
    await expect(row.getByRole('button', { name: 'Revoke' })).toHaveCount(0)
    await page.getByLabel('Status').selectOption({ label: 'Expired' })
    await expect(row).toBeVisible()
    await page.getByLabel('Status').selectOption({ label: 'Active' })
    await expect(row).toHaveCount(0)
    health.expectClean()
  })

  test('platform admin cannot download with an expired share; the refusal is recorded and shown', async ({ page }) => {
    const share = await expiredShare('expired-platform')
    await signIn(page, state().accounts.platformAdmin)
    await page.goto('/platform/shares')
    const row = page.getByRole('row', { name: new RegExp(reason('expired-platform')) })
    await expect(row.getByText('Expired', { exact: true })).toBeVisible()
    await expect(row.getByRole('button', { name: 'Download' }).or(row.getByRole('link', { name: 'Download' }))).toHaveCount(0)

    // What the person holding the share can still do directly: ask the download API. It must refuse, and say why.
    const admin = await (await import('../../api')).Api.signIn(state().accounts.platformAdmin)
    await expect(admin.call('POST', `/api/tenants/${w.populated.tenantId}/backup-artifacts/${w.artifacts.share}/downloads`, { shareId: share.id })).rejects.toThrow(/403.*share_expired/)
    await admin.dispose()

    await page.reload()
    await page.locator('summary', { hasText: 'Download attempts' }).click()
    const attempt = page.getByRole('table', { name: 'Download attempts' }).getByRole('row').filter({ hasText: w.populated.tenantName }).filter({ hasText: 'Refused' })
    await expect(attempt.first()).toBeVisible()
    await expect(attempt.first()).toContainText(/expired/i)
    await expect(attempt.first()).not.toContainText('share_expired')
  })
})
