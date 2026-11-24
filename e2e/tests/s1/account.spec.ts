import { expect, test } from '@playwright/test'
import { Api } from '../../api'
import { axeViolations, DESKTOP, expectNoHorizontalScroll, MOBILE, newUser, platformApi, signIn, watch } from './helpers'

let user: Awaited<ReturnType<typeof newUser>>
test.beforeAll(async () => {
  const platform = await platformApi()
  user = await newUser(platform)
  await platform.dispose()
})

const PAGES = [
  ['/account', 'Hesap'],
  ['/account/sessions', 'Oturumlarım'],
  ['/account/security', 'Güvenlik ayarları'],
  ['/account/passkeys', 'Geçiş anahtarları'],
] as const

test.describe('/account', () => {
  test('every account page: heading, tab navigation, axe, no overflow at 1440 and 390, clean console', async ({ page }) => {
    const w = watch(page)
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    for (const vp of [DESKTOP, MOBILE]) {
      await page.setViewportSize(vp)
      for (const [path, heading] of PAGES) {
        await page.goto(path)
        await expect(page.getByRole('heading', { name: heading, exact: true }).first()).toBeVisible()
        await expect(page.getByRole('navigation', { name: 'Hesap ayarları' })).toBeVisible()
        await expectNoHorizontalScroll(page)
        expect(await axeViolations(page), `${path} @${vp.width}`).toEqual([])
      }
    }
    expect(w.unexpected()).toEqual([])
  })

  test('tab bar links all four account pages, including Güvenlik', async ({ page }) => {
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/account')
    const nav = page.getByRole('navigation', { name: 'Hesap ayarları' })
    for (const name of ['Hesap', 'Oturumlarım', 'Güvenlik', 'Geçiş anahtarları']) await expect(nav.getByRole('link', { name })).toBeVisible()
  })

  test('profile shows the display name; reload and back/forward keep the page', async ({ page }) => {
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/account')
    await expect(page.locator('main').getByText(`S1 ${user.login}`)).toBeVisible()
    await page.goto('/account/sessions')
    await page.goBack()
    await expect(page).toHaveURL(/\/account$/)
    await page.goForward()
    await expect(page).toHaveURL(/\/account\/sessions$/)
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Oturumlarım', exact: true }).first()).toBeVisible()
  })

  test('legacy short routes /sessions /security /passkeys redirect into /account', async ({ page }) => {
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    for (const p of ['sessions', 'security', 'passkeys']) {
      await page.goto(`/${p}`)
      await expect(page).toHaveURL(new RegExp(`/account/${p}$`))
    }
  })

  test('account pages require sign-in', async ({ page }) => {
    for (const [path] of PAGES) {
      await page.goto(path)
      await expect(page).toHaveURL(/\/login/)
    }
  })

  test('sessions: current session is marked; ending another session removes it; ending this one signs out', async ({ page }) => {
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    const other = await Api.signIn(user) // a second live session
    await page.goto('/account/sessions')
    const items = page.locator('li').filter({ has: page.getByRole('button', { name: /oturumunu sonlandır/ }) })
    await expect(items.first()).toBeVisible()
    const before = await items.count()
    expect(before).toBeGreaterThanOrEqual(2)
    await expect(page.getByText('Bu oturum')).toHaveCount(1)
    // end a non-current session: confirm dialog names the consequence, Cancel keeps it
    const others = items.filter({ hasNotText: 'Bu oturum' })
    await others.first().getByRole('button', { name: /oturumunu sonlandır/ }).click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await page.getByRole('dialog').getByRole('button', { name: /İptal|Vazgeç/ }).click()
    await expect(items).toHaveCount(before)
    await others.first().getByRole('button', { name: /oturumunu sonlandır/ }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Oturumu sonlandır' }).click()
    await expect(items).toHaveCount(before - 1)
    await other.dispose()
    // end the current one
    await items.filter({ hasText: 'Bu oturum' }).getByRole('button', { name: /oturumunu sonlandır/ }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Oturumu sonlandır' }).click()
    await expect(page).toHaveURL(/\/login/)
  })

  test('sessions: "Tüm oturumları sonlandır" signs this and the other browser out', async ({ browser }) => {
    const a = await browser.newContext({ ignoreHTTPSErrors: true })
    const b = await browser.newContext({ ignoreHTTPSErrors: true })
    const pa = await a.newPage()
    const pb = await b.newPage()
    await signIn(pa, user.login, user.password)
    await signIn(pb, user.login, user.password)
    await pa.waitForURL(/\/tenants\//)
    await pb.waitForURL(/\/tenants\//)
    await pa.goto('/account/sessions')
    await pa.getByRole('button', { name: 'Tüm oturumları sonlandır' }).click()
    await expect(pa.getByRole('dialog')).toContainText('Bu cihaz dahil')
    await pa.getByRole('dialog').getByRole('button', { name: 'Tüm oturumları sonlandır' }).click()
    await expect(pa).toHaveURL(/\/login/)
    await pb.goto(`/tenants/${user.userId ? '' : ''}`)
    await expect(pb).toHaveURL(/\/login/)
    await a.close(); await b.close()
  })

  test('sessions: list request failure shows an error with retry', async ({ page }) => {
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    let fail = true
    await page.route('**/api/auth/sessions', (r) => fail ? r.fulfill({ status: 500, contentType: 'application/json', body: '{}' }) : r.continue())
    await page.goto('/account/sessions')
    await expect(page.getByRole('alert')).toBeVisible()
    fail = false
    await page.getByRole('button', { name: /Yeniden dene|Tekrar dene/ }).click()
    await expect(page.getByText('Bu oturum')).toBeVisible()
  })

  test('language: EN switch translates account pages and persists across navigation', async ({ page }) => {
    await signIn(page, user.login, user.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/account')
    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.getByRole('heading', { name: 'Account', exact: true })).toBeVisible()
    for (const [path] of PAGES.slice(1)) {
      await page.goto(path)
      await expect(page.locator('main')).not.toContainText(/Oturumlarım|Güvenlik ayarları|Geçiş anahtarları/)
      expect(await axeViolations(page)).toEqual([])
    }
    await page.getByRole('button', { name: /Change language|Dili değiştir/ }).click()
  })
})
