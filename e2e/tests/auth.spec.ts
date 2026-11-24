import { expect, test } from '@playwright/test'
import { state } from '../state'

const s = state()

async function signIn(page: import('@playwright/test').Page, login: string, password: string) {
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(login)
  await page.locator('input[type="password"]').fill(password)
  await page.locator('button[type="submit"]').click()
}

test('login page renders', async ({ page }) => {
  await page.goto('/login')
  await expect(page.locator('input[type="password"]')).toBeVisible()
})

test('wrong password is rejected and stays on /login', async ({ page }) => {
  await signIn(page, s.accounts.userAdmin.login, 'definitely-wrong-password')
  await expect(page).toHaveURL(/\/login/)
  await expect(page.getByRole('alert').or(page.locator('[role=status]')).first()).toBeVisible()
})

test('tenant user admin signs in and reaches the tenant dashboard', async ({ page }) => {
  await signIn(page, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
  await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}`))
})

test('platform admin signs in and can open /platform', async ({ page }) => {
  await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto('/platform')
  await expect(page).toHaveURL(/\/platform/)
})

test('unauthenticated /platform redirects to /login', async ({ page }) => {
  await page.goto('/platform')
  await expect(page).toHaveURL(/\/login/)
})
