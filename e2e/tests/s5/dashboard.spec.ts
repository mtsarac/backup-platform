import { expect, test } from '@playwright/test'
import { english, signIn, tenantUrl, watch, world, type World } from './support'

let w: World
test.beforeAll(async () => { w = await world() })
test.beforeEach(async ({ context }) => { await english(context) })

test.describe('dashboard, populated', () => {
  test('shows summary, protected data per computer, storage and recent activity', async ({ page }) => {
    const health = watch(page)
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible()
    await expect(page.getByText(/1 protected/)).toBeVisible()
    const group = page.getByRole('region', { name: w.hostName })
    await expect(group.getByText('Online')).toBeVisible()
    await expect(group.getByRole('link', { name: w.jobName, exact: true })).toBeVisible()
    await expect(page.getByText('Latest successful backup')).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Last activity' })).toBeVisible()
    health.expectClean()
  })

  test('protection strip is announced with the job name and per-day detail', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    const strip = page.getByRole('region', { name: w.hostName }).getByRole('list', { name: new RegExp(`Last 14 days · ${w.jobName}`) })
    await expect(strip).toBeVisible()
    await expect(strip.getByRole('listitem')).toHaveCount(14)
    await expect(strip.getByRole('listitem').last()).toHaveAccessibleName(/taken, 4 attempts/)
  })

  test('restore link goes to the restore wizard of the last good run', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await page.getByRole('region', { name: w.hostName }).getByRole('link', { name: new RegExp(`Restore.*${w.jobName}|${w.jobName}.*restore`, 'i') }).click()
    await expect(page).toHaveURL(/\/backup-runs\/[0-9a-f-]+\/restore$/)
  })

  test('refresh and back/forward keep the dashboard', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await page.reload()
    await expect(page.getByText(/1 protected/)).toBeVisible()
    await page.getByRole('link', { name: w.jobName }).first().click()
    await expect(page).toHaveURL(/\/backup-jobs\//)
    await page.goBack()
    await expect(page.getByText(/1 protected/)).toBeVisible()
    await page.goForward()
    await expect(page).toHaveURL(/\/backup-jobs\//)
  })

  test('mobile viewport: no horizontal scroll, content reachable', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 740 })
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByText(/1 protected/)).toBeVisible()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(0)
  })

  test('keyboard: skip link is first tab stop and lands on main', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible()
    await page.keyboard.press('Tab')
    await expect(page.getByRole('link', { name: 'Skip to main content' })).toBeFocused()
    await page.keyboard.press('Enter')
    await expect(page.locator('main')).toBeFocused()
  })

  test('failed run is shown to the user with a reason in the run list', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated, `/backup-runs/${w.runs.failed}`))
    const detail = page.getByRole('main')
    await expect(detail.getByText('The backup attempt failed')).toBeVisible()
    await expect(detail.getByText('The backup configuration is invalid. Check and correct the connection details.')).toBeVisible()
    await expect(detail.getByRole('button', { name: 'Retry' })).toBeVisible()
  })

  test('latest run failed: banner does not claim "All up to date"', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible()
    await expect(page.getByText('Last activity')).toBeVisible()
    await expect(page.getByText('All up to date')).toHaveCount(0)
  })
})

test.describe('dashboard, empty tenant', () => {
  test('explains how to start instead of showing blanks', async ({ page }) => {
    const health = watch(page)
    await signIn(page, w.empty.account)
    await page.goto(tenantUrl(w.empty))
    await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible()
    await expect(page.getByText('Your protected data will appear here.')).toBeVisible()
    await expect(page.getByText(/undefined|NaN|\[object/)).toHaveCount(0)
    health.expectClean()
  })
})

test.describe('dashboard, loading and failure states', () => {
  test('slow API shows a loading state, not a blank page', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.route('**/api/tenants/*/backup-jobs**', async (route) => { await new Promise((r) => setTimeout(r, 1500)); await route.continue() })
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByRole('status').or(page.getByText(/loading/i)).first()).toBeVisible()
    await expect(page.getByText(/1 protected/)).toBeVisible()
  })

  test('API 500 shows an error notice with retry, not a crash', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.route('**/api/tenants/*/backup-jobs**', (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"boom"}' }))
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByRole('alert').first()).toBeVisible()
    await expect(page.getByText(/uncaught|Unexpected Application Error/i)).toHaveCount(0)
  })
})
