import { expect, test, type Page } from '@playwright/test'
import { axeViolations, DESKTOP, expectNoHorizontalScroll, MOBILE, s, signIn, watch } from './helpers'

const PUBLIC_SCREENS = [
  ['/login', 'Giriş yapın', 'Sign in'],
  ['/forgot-password', 'Şifremi unuttum', 'Forgot password'],
  ['/reset-password', 'Şifre sıfırlama', 'Password reset'],
  ['/reset-password?token=sample-token', 'Şifre sıfırlama', 'Password reset'],
  ['/verify-email', 'E-posta doğrulama', 'Email verification'],
  ['/verify-email?token=sample-token', 'E-posta doğrulama', 'Email verification'],
] as const

const PLATFORM_SCREENS = [
  ['/platform', 'Platform özeti', 'Platform overview'],
  ['/platform/tenants', 'Çalışma alanları', 'Workspaces'],
  [`/platform/tenants/${s.tenantId}`, s.tenantName, s.tenantName],
  ['/platform/users', 'Kullanıcılar', 'Users'],
  ['/platform/plans', 'Planlar', 'Plans'],
  ['/platform/quota-policies', 'Kota politikaları', 'Quota policies'],
  ['/platform/audit', 'Denetim kaydı', 'Audit log'],
] as const

async function inspectScreen(page: Page, path: string, trHeading: string, w: ReturnType<typeof watch>) {
  w.reset()
  await page.goto(path)
  await expect(page.getByRole('main'), `${path} main`).toBeVisible()
  await expect(page.getByRole('heading', { level: 1 }), `${path} h1 count`).toHaveCount(1)
  await expect(page.getByRole('heading', { level: 1, name: trHeading }), `${path} h1`).toBeVisible()
  await expectNoHorizontalScroll(page)
  await expect(page.getByText(/undefined|NaN|\[object Object\]/), `${path} raw values`).toHaveCount(0)
  expect(await axeViolations(page), `${path} axe`).toEqual([])
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: trHeading }), `${path} reload`).toBeVisible()
  expect(w.unexpected(), `${path} console/network`).toEqual([])
}

test.describe('S1 screen checklist sweep', () => {
  for (const [vpName, vp] of [['desktop', DESKTOP], ['mobile', MOBILE]] as const) {
    test(`public auth & recovery screens pass axe, overflow, reload, and console cleanliness on ${vpName}`, async ({ page }) => {
      await page.setViewportSize(vp)
      const w = watch(page)
      for (const [path, trHeading] of PUBLIC_SCREENS) {
        await inspectScreen(page, path, trHeading, w)
      }
    })

    test(`platform screens pass axe, overflow, reload, and console cleanliness on ${vpName}`, async ({ page }) => {
      test.setTimeout(90_000)
      await page.setViewportSize(vp)
      const w = watch(page)
      await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
      await page.waitForURL((u) => u.pathname !== '/login')
      for (const [path, trHeading] of PLATFORM_SCREENS) {
        await inspectScreen(page, path, trHeading, w)
      }
    })
  }

  test('keyboard: skip-to-main link, dialog initial focus + Escape dismissal, and back/forward across S1 shells', async ({ page }) => {
    const w = watch(page)
    await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
    await page.waitForURL((u) => u.pathname !== '/login')

    // Skip link on /account shell
    await page.goto('/account')
    await expect(page.getByRole('heading', { level: 1, name: 'Hesap' })).toBeVisible()
    await page.keyboard.press('Tab')
    await expect(page.getByRole('link', { name: 'Ana içeriğe geç' })).toBeFocused()
    await page.keyboard.press('Enter')
    await expect(page).toHaveURL(/#main$/)

    // Skip link on /platform shell + dialog Escape dismissal across platform dialogs
    await page.goto('/platform/tenants')
    await expect(page.getByRole('heading', { level: 1, name: 'Çalışma alanları' })).toBeVisible()
    await page.keyboard.press('Tab')
    await expect(page.getByRole('link', { name: 'Ana içeriğe geç' })).toBeFocused()

    for (const [path, trigger] of [
      ['/platform/tenants', /Yeni çalışma alanı/],
      ['/platform/users', 'Yeni kullanıcı'],
      ['/platform/plans', 'Yeni plan'],
      ['/platform/quota-policies', 'Yeni politika'],
    ] as const) {
      await page.goto(path)
      await page.getByRole('button', { name: trigger }).click()
      await expect(page.getByRole('dialog'), path).toBeVisible()
      expect(await page.evaluate(() => document.activeElement?.closest('[role="dialog"]') !== null), `${path} focus inside dialog`).toBe(true)
      await page.keyboard.press('Escape')
      await expect(page.getByRole('dialog'), path).toBeHidden()
    }

    // Back / forward across platform routes
    await page.goto('/platform/users')
    await page.goto('/platform/plans')
    await page.goBack()
    await expect(page).toHaveURL(/\/platform\/users$/)
    await page.goForward()
    await expect(page).toHaveURL(/\/platform\/plans$/)
    expect(w.unexpected()).toEqual([])
  })

  test('network failures (500 and connection refused) show alerts and recover cleanly across S1 screens', async ({ page }) => {
    // Public /forgot-password availability endpoint failure + retry
    let failForgot = true
    await page.route('**/api/auth/email-recovery', (r) => failForgot ? r.fulfill({ status: 500, contentType: 'application/json', body: '{}' }) : r.continue())
    await page.goto('/forgot-password')
    await expect(page.getByRole('alert')).toBeVisible()
    failForgot = false
    await page.getByRole('button', { name: /Yeniden dene|Tekrar dene/ }).click()
    await expect(page.getByRole('link', { name: 'Girişe dön' })).toBeVisible()
    await page.unroute('**/api/auth/email-recovery')

    // Platform list endpoints: 500 + reload/retry recovery
    await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
    await page.waitForURL((u) => u.pathname !== '/login')

    for (const [path, glob, readyHeading] of [
      ['/platform/tenants', '**/api/platform/tenants**', 'Çalışma alanları'],
      ['/platform/users', '**/api/platform/users**', 'Kullanıcılar'],
      ['/platform/plans', '**/api/platform/plans**', 'Planlar'],
      ['/platform/quota-policies', '**/api/platform/quota-policies**', 'Kota politikaları'],
    ] as const) {
      await page.route(glob, (r) => r.fulfill({ status: 500, contentType: 'application/json', body: '{}' }))
      await page.goto(path)
      await expect(page.getByRole('alert').first(), `${path} 500 alert`).toBeVisible()
      await page.unroute(glob)
      const retry = page.getByRole('button', { name: /Yeniden dene|Tekrar dene/ })
      if (await retry.isVisible()) await retry.click()
      else await page.reload()
      await expect(page.getByRole('heading', { level: 1, name: readyHeading }), `${path} recovered`).toBeVisible()
      await expect(page.getByRole('alert'), `${path} cleared`).toHaveCount(0)
    }

    // Connection refused (offline) on /platform/audit shows alert, not a blank screen
    await page.route('**/api/platform/audit**', (r) => r.abort('connectionrefused'))
    await page.goto('/platform/audit')
    await expect(page.getByRole('alert').first()).toBeVisible()
    await page.unroute('**/api/platform/audit**')
  })

  test('TR / EN language switcher translates recovery and platform screens cleanly with zero axe violations', async ({ page }) => {
    test.setTimeout(90_000)
    const w = watch(page)
    await page.goto('/forgot-password')
    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')

    for (const [path, , enHeading] of PUBLIC_SCREENS.slice(1)) {
      await page.goto(path)
      await expect(page.getByRole('heading', { level: 1, name: enHeading }), path).toBeVisible()
      await expect(page.getByText(/undefined|NaN|\[object Object\]/)).toHaveCount(0)
      expect(await axeViolations(page), `${path} EN axe`).toEqual([])
    }

    // Switch back to TR to sign in cleanly, then switch to EN inside platform shell
    await page.getByRole('button', { name: /Change language|Dili değiştir/ }).click()
    await expect(page.locator('html')).toHaveAttribute('lang', 'tr')
    await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
    await page.waitForURL((u) => u.pathname !== '/login')

    await page.goto('/platform')
    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')

    for (const [path, , enHeading] of PLATFORM_SCREENS) {
      await page.goto(path)
      await expect(page.getByRole('heading', { level: 1, name: enHeading }), path).toBeVisible()
      await expect(page.getByText(/undefined|NaN|\[object Object\]/)).toHaveCount(0)
      expect(await axeViolations(page), `${path} EN axe`).toEqual([])
    }

    await page.getByRole('button', { name: /Change language|Dili değiştir/ }).click()
    await expect(page.locator('html')).toHaveAttribute('lang', 'tr')
    await expect(page.getByRole('heading', { level: 1, name: 'Denetim kaydı' })).toBeVisible()
    expect(w.unexpected()).toEqual([])
  })
})
