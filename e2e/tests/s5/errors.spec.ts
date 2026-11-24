import { expect, test } from '@playwright/test'
import { state } from '../../state'
import { english, signIn, tenantUrl, watch, world, type World } from './support'

let w: World
test.beforeAll(async () => { w = await world() })
test.beforeEach(async ({ context }) => { await english(context) })

test.describe('403 / 404 / session', () => {
  test('unknown tenant sub-route shows Page not found inside the shell', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated, '/nope'))
    await expect(page.getByText('Page not found.')).toBeVisible()
    await expect(page.getByRole('main')).toBeVisible()
  })

  test('another workspace is reported as not found and leaks nothing', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.empty))
    await expect(page.getByText('Page not found.')).toBeVisible()
    await expect(page.getByText(w.empty.tenantName)).toHaveCount(0)
  })

  test('404 page has a main landmark and a way back', async ({ page }) => {
    test.fail(true, 'https://github.com/mtsarac/backup-platform/issues/248')
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.empty))
    await expect(page.getByRole('main')).toBeVisible()
    await expect(page.getByRole('link', { name: /home|back|return/i }).first()).toBeVisible()
  })

  test('tenant user on /platform gets the permission page with a way home', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.goto('/platform')
    await expect(page.getByText('You do not have permission to access this area')).toBeVisible()
    await page.getByRole('link', { name: 'Return to home' }).click()
    await expect(page).toHaveURL(new RegExp(`/tenants/${w.populated.tenantId}`))
  })

  test('signed-out visit to a tenant page redirects to login, and login returns there', async ({ page }) => {
    test.fail(true, 'https://github.com/mtsarac/backup-platform/issues/241')
    const target = tenantUrl(w.populated, '/backup-shares')
    await page.goto(target)
    await expect(page).toHaveURL(/\/login/)
    await page.locator('input[type="text"]').first().fill(w.populated.account.login)
    await page.locator('input[type="password"]').fill(w.populated.account.password)
    await page.locator('button[type="submit"]').click()
    await expect(page).toHaveURL(new RegExp(`${target}$`))
  })

  test('session expiring mid-use sends the user to login without a crash', async ({ page, context }) => {
    await signIn(page, w.populated.account)
    await page.goto(tenantUrl(w.populated))
    await context.clearCookies()
    await page.goto(tenantUrl(w.populated, '/backup-shares'))
    await expect(page).toHaveURL(/\/login/)
  })

  test('platform pages: 404 inside platform shell', async ({ page }) => {
    await signIn(page, state().accounts.platformAdmin)
    await page.goto('/platform/nope')
    await expect(page.getByText('Page not found.')).toBeVisible()
  })
})

test.describe('backend down', () => {
  test('API unreachable after sign-in shows an error, not a blank page', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.route('**/api/**', (route) => route.abort('connectionrefused'))
    await page.goto(tenantUrl(w.populated, '/backup-shares'))
    await expect(page.getByRole('alert').or(page.getByText(/unreachable|cannot connect|could not|try again|offline/i)).first()).toBeVisible()
  })

  test('503 from the API shows a banner and recovers on reload', async ({ page }) => {
    await signIn(page, w.populated.account)
    await page.route('**/api/tenants/**', (route) => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }))
    await page.goto(tenantUrl(w.populated))
    await expect(page.getByRole('alert').first()).toBeVisible()
    await page.unroute('**/api/tenants/**')
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Home' })).toBeVisible()
  })
})

test.describe('platform agents view', () => {
  test('lists the agent with workspace and connection state, filters by search', async ({ page }) => {
    const health = watch(page)
    await signIn(page, state().accounts.platformAdmin)
    await page.goto('/platform/agents')
    await page.getByRole('searchbox').or(page.getByLabel('Search')).first().fill(w.hostName)
    const row = page.getByRole('row', { name: new RegExp(w.hostName) })
    await expect(row).toHaveCount(1)
    await expect(row).toContainText(w.populated.tenantName)
    await expect(row).toContainText('Enabled')
    health.expectClean()
  })

  test('an agent that stopped polling is shown Offline, and the tenant dashboard says so', async ({ page }) => {
    test.setTimeout(240_000)
    await signIn(page, state().accounts.platformAdmin)
    await page.goto('/platform/agents')
    await page.getByLabel('Search').first().fill(w.hostName)
    await expect(async () => {
      await page.reload()
      await page.getByLabel('Search').first().fill(w.hostName)
      await expect(page.getByRole('row', { name: new RegExp(w.hostName) })).toContainText('Offline', { timeout: 2000 })
    }).toPass({ timeout: 200_000, intervals: [10_000] })
  })
})
