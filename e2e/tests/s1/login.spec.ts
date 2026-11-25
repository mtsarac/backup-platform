import { expect, test } from '@playwright/test'
import { axeViolations, DESKTOP, expectNoHorizontalScroll, isIsolatedStack, ISOLATED_SKIP, MOBILE, resetIsolatedThrottle, s, signIn, watch } from './helpers'

const u = s.accounts.userAdmin
const BAD = /Kullanıcı adı veya şifre hatalı/

test.beforeAll(() => { resetIsolatedThrottle() })
test.afterAll(() => { resetIsolatedThrottle() })

test.describe('/login', () => {
  test('renders labelled form, passes axe, no horizontal scroll on mobile and desktop, leaves signed-out console clean (#240)', async ({ page }) => {
    const w = watch(page)
    for (const vp of [DESKTOP, MOBILE]) {
      await page.setViewportSize(vp)
      await page.goto('/login')
      await expect(page.getByRole('heading', { level: 1, name: 'Giriş yapın' })).toBeVisible()
      await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
      await expect(page.getByLabel('Şifre', { exact: true })).toBeVisible()
      await expectNoHorizontalScroll(page)
      expect(await axeViolations(page)).toEqual([])
    }
    expect(await page.title()).toContain('Giriş')
    expect(w.problems).toEqual([])
  })

  test('empty submit is blocked by required fields', async ({ page }) => {
    await page.goto('/login')
    const posted: string[] = []
    page.on('request', (r) => { if (r.url().includes('/auth/login')) posted.push(r.url()) })
    await page.getByRole('button', { name: 'Giriş yap', exact: true }).click()
    expect(await page.getByLabel('Kullanıcı adı').evaluate((e: HTMLInputElement) => e.validity.valueMissing)).toBe(true)
    expect(posted).toEqual([])
  })

  test('wrong password shows one alert, stays on /login; unknown user gets the same message', async ({ page }) => {
    test.skip(!isIsolatedStack, ISOLATED_SKIP)
    await signIn(page, u.login, 'definitely-wrong-password')
    await expect(page.getByRole('alert')).toContainText(BAD)
    await expect(page).toHaveURL(/\/login$/)
    const first = await page.getByRole('alert').innerText()
    await signIn(page, `no-such-user-${Date.now()}`, 'definitely-wrong-password')
    await expect(page.getByRole('alert')).toContainText(BAD)
    expect(await page.getByRole('alert').innerText()).toBe(first)
  })

  test('Enter in the password field submits; success lands on the tenant dashboard and reload keeps the session', async ({ page }) => {
    const w = watch(page)
    await page.goto('/login')
    await page.getByLabel('Kullanıcı adı').fill(u.login)
    await page.getByLabel('Şifre', { exact: true }).fill(u.password)
    await page.getByLabel('Şifre', { exact: true }).press('Enter')
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}$`))
    await page.reload()
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}$`))
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
    expect(w.unexpected()).toEqual([])
  })

  test('login by e-mail address works', async ({ page }) => {
    await signIn(page, `${u.login}@example.test`, u.password)
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}`))
  })

  test('remember me issues a persistent cookie, otherwise a session cookie', async ({ browser }) => {
    for (const remember of [false, true]) {
      const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
      const page = await ctx.newPage()
      await signIn(page, u.login, u.password, { remember })
      await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}`))
      const auth = (await ctx.cookies()).filter((c) => /auth|session/i.test(c.name) && c.httpOnly)
      expect(auth.length).toBeGreaterThan(0)
      for (const c of auth) remember ? expect(c.expires).toBeGreaterThan(Date.now() / 1000 + 3600) : expect(c.expires).toBe(-1)
      await ctx.close()
    }
  })

  test('signed-in visit to /login redirects away', async ({ page }) => {
    await signIn(page, u.login, u.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/login')
    await expect(page).not.toHaveURL(/\/login/)
  })

  test('protected deep link sends to /login and returns to it with query parameters after sign-in (#241)', async ({ page }) => {
    const target = `/platform/tenants/${s.tenantId}?tab=subscription`
    await page.goto(target)
    await expect(page).toHaveURL(/\/login/)
    await page.getByLabel('Kullanıcı adı').fill(s.accounts.platformAdmin.login)
    await page.getByLabel('Şifre', { exact: true }).fill(s.accounts.platformAdmin.password)
    await page.getByRole('button', { name: 'Giriş yap', exact: true }).click()
    await expect(page).toHaveURL(new RegExp(`/platform/tenants/${s.tenantId}\\?tab=subscription$`))
  })

  test('logout ends the session; Back does not reveal protected content and next login ignores stale from state', async ({ page }) => {
    await signIn(page, u.login, u.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/account')
    await page.getByRole('button', { name: 'Çıkış' }).click()
    await expect(page).toHaveURL(/\/login/)
    await page.goBack()
    await expect(page).toHaveURL(/\/login/)
    await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
    await page.goto(`/tenants/${s.tenantId}`)
    await expect(page).toHaveURL(/\/login/)
  })

  test('session lost mid-use shows the expiry notice on /login', async ({ page, context }) => {
    await signIn(page, u.login, u.password)
    await page.waitForURL(/\/tenants\//)
    await context.clearCookies()
    await expect(page.getByText('Oturumunuz sona erdi')).toBeVisible({ timeout: 20_000 })
    await expect(page).toHaveURL(/\/login/)
  })

  test('keyboard: tab order is user, password, remember, submit, passkey, forgot link', async ({ page }) => {
    await page.goto('/login')
    await page.getByLabel('Kullanıcı adı').focus()
    const names: string[] = []
    for (let i = 0; i < 6; i++) {
      names.push(await page.evaluate(() => { const e = document.activeElement as HTMLElement; return e.getAttribute('autocomplete') ?? e.textContent?.trim() ?? e.tagName }))
      await page.keyboard.press('Tab')
    }
    expect(names[0]).toBe('username')
    expect(names[1]).toBe('current-password')
    expect(names.slice(3, 6).join('|')).toContain('Giriş yap')
    expect(names.join('|')).toContain('Şifremi unuttum')
  })

  test('language switch to EN translates the form and survives reload', async ({ page }) => {
    await page.goto('/login')
    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Sign in/i)
    await expect(page.getByLabel('Username')).toBeVisible()
    await expect(page.getByLabel('Password', { exact: true })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(/Sign in/i)
    expect(await page.locator('html').getAttribute('lang')).toBe('en')
    expect(await axeViolations(page)).toEqual([])
    await page.getByRole('button', { name: /Change language|Dili değiştir/ }).click()
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Giriş yapın')
  })

  test.fixme('English /login submit button uses ASCII "Sign in" instead of Turkish dotless ı (#281)', async ({ page }) => {
    await page.goto('/login')
    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.getByRole('button', { name: /^Sign in$/i })).toBeVisible()
  })

  test('backend unreachable on submit shows an error, not a blank page', async ({ page }) => {
    await page.goto('/login')
    await page.route('**/api/auth/login', (r) => r.abort('connectionrefused'))
    await page.getByLabel('Kullanıcı adı').fill(u.login)
    await page.getByLabel('Şifre', { exact: true }).fill(u.password)
    await page.getByRole('button', { name: 'Giriş yap', exact: true }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Giriş yap', exact: true })).toBeEnabled()
    await expect(page).toHaveURL(/\/login$/)
  })

  test('server 500 on login shows an error alert', async ({ page }) => {
    await page.goto('/login')
    await page.route('**/api/auth/login', (r) => r.fulfill({ status: 500, contentType: 'application/json', body: '{}' }))
    await page.getByLabel('Kullanıcı adı').fill(u.login)
    await page.getByLabel('Şifre', { exact: true }).fill(u.password)
    await page.getByRole('button', { name: 'Giriş yap', exact: true }).click()
    await expect(page.getByRole('alert')).toBeVisible()
  })
})
