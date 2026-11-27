import { expect, test, type Page } from '@playwright/test'
import { state } from '../../state'
import { english, signIn, tenantUrl, watch, world, type World } from './support'

let w: World
test.beforeAll(async () => { w = await world() })
test.beforeEach(async ({ context }) => { await english(context) })

const runUrl = (id: string) => tenantUrl(w.populated, `/backup-runs/${id}`)
async function more(page: Page) {
  await page.getByText('More', { exact: true }).click()
}
/** The artifact's own More menu; the computer-logs panel has its own Download button. */
const moreMenu = (page: Page) => page.locator('details').filter({ has: page.locator('summary', { hasText: /^More$/ }) })

/** The dialog blocks until the artifact name is typed; the name is read from the label the user sees. */
async function confirmDelete(page: Page) {
  const dialog = page.getByRole('dialog')
  const label = await dialog.locator('label').innerText()
  await dialog.getByRole('textbox').fill(label.replace(/^Type the name to confirm:\s*/, '').trim())
  await dialog.getByRole('button', { name: 'Start grace period' }).click()
}

test.describe('artifact detail (inside the run detail)', () => {
  test('shows integrity, restore readiness and retention for a good backup', async ({ page }) => {
    const health = watch(page)
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.view))
    const main = page.getByRole('main')
    await expect(main.getByText('Integrity')).toBeVisible()
    await expect(main.getByText('Intact')).toBeVisible()
    await expect(main.getByText('Ready to restore')).toBeVisible()
    await expect(main.getByText('Being kept')).toBeVisible()
    await expect(main.getByText(/undefined|NaN|\[object/)).toHaveCount(0)
    health.expectClean()
  })

  test('replication tab lists the destination as verified', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.view))
    await page.getByRole('tab', { name: 'Replication' }).click()
    await expect(page.getByRole('tabpanel').getByText('Verified', { exact: true })).toBeVisible()
    await expect(page.getByRole('tabpanel').getByText('Replication healthy')).toBeVisible()
  })

  test('UserAdmin sees Download, Share and Delete under More', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.view))
    await more(page)
    for (const name of ['Download', 'Share', 'Delete']) await expect(moreMenu(page).getByRole('button', { name, exact: true })).toBeVisible()
  })

  test('PlatformAdmin cannot open a tenant backup detail', async ({ page }) => {
    await signIn(page, state().accounts.platformAdmin)
    await page.goto(runUrl(w.runs.view))
    await expect(page.getByRole('heading', { name: 'Page not found.' })).toBeVisible()
  })

  test('delete: dialog explains consequences, Escape leaves the artifact untouched', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.view))
    await more(page)
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Start grace period' })).toBeDisabled()
    await page.keyboard.press('Escape')
    await expect(dialog).toBeHidden()
    await expect(page.getByText('Being kept')).toBeVisible()
    const a = await w.populated.tenant.call('GET', `/api/tenants/${w.populated.tenantId}/backup-artifacts/${w.artifacts.view}`)
    expect(a.deleteAfter ?? null).toBeNull()
  })

  test('delete then cancel deletion; state survives a reload', async ({ page }) => {
    const health = watch(page)
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.remove))
    await more(page)
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await confirmDelete(page)
    await expect(page.getByText('Deletion request received', { exact: false })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Cancel deletion' })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('button', { name: 'Cancel deletion' })).toBeVisible()
    await page.getByRole('button', { name: 'Cancel deletion' }).click()
    await expect(page.getByText('Deletion canceled', { exact: false })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Cancel deletion' })).toHaveCount(0)
    const a = await w.populated.tenant.call('GET', `/api/tenants/${w.populated.tenantId}/backup-artifacts/${w.artifacts.remove}`)
    expect(a.state).toBe('Available')
    health.expectClean()
  })

  test('delete request failing on the server shows the error inside the dialog', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.share))
    await page.route('**/backup-artifacts/*', (route) => route.request().method() === 'DELETE'
      ? route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"boom"}' }) : route.continue())
    await more(page)
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await confirmDelete(page)
    await expect(page.getByRole('dialog').getByRole('alert')).toBeVisible()
    await expect(page.getByRole('dialog')).toBeVisible()
  })

  test('unknown run id shows a not-found state, not a blank page', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(runUrl('5b0e3c1a-9d3e-4a7b-8f55-2c1d6e9a7b10'))
    await expect(page.getByRole('main').getByText('The record was not found or is not accessible within this workspace.')).toBeVisible()
  })

  test('mobile: run detail has no horizontal overflow', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 740 })
    await signIn(page, w.populated.account)
    await page.goto(runUrl(w.runs.view))
    await expect(page.getByText('Integrity')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0)
  })
})
