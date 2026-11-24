import { expect, test, type Page } from '@playwright/test'
import { Api } from '../../api'
import { axeViolations, newUser, platformApi, signIn, totp, watch, MOBILE } from './helpers'

test.describe.configure({ mode: 'serial' })

let user: Awaited<ReturnType<typeof newUser>>
let secret = ''
let recoveryCodes: string[] = []

test.beforeAll(async () => {
  const platform = await platformApi()
  user = await newUser(platform)
  await platform.dispose()
})
test.afterAll(async () => {
  const platform = await platformApi()
  await platform.call('POST', `/api/platform/users/${user.userId}/disable`, undefined, [200, 204, 404, 409]).catch(() => {})
  await platform.dispose()
})

const dialog = (page: Page) => page.getByRole('dialog')

test('security page offers TOTP enrolment; setup dialog validates and enables TOTP, then shows recovery codes once', async ({ page }) => {
  const w = watch(page)
  await signIn(page, user.login, user.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/security')
  expect(await axeViolations(page)).toEqual([])
  await page.getByRole('button', { name: 'TOTP Etkinleştir' }).click()
  await expect(dialog(page)).toBeVisible()
  secret = (await dialog(page).locator('code').innerText()).trim()
  expect(secret).toMatch(/^[A-Z2-7 ]{16,}$/)
  await expect(dialog(page).locator('svg')).toBeVisible()
  expect(await axeViolations(page)).toEqual([])

  // wrong code and wrong password are rejected without enrolling
  await dialog(page).getByLabel('Mevcut şifreniz').fill('wrong-password-value')
  await dialog(page).getByLabel('Doğrulama kodu').fill(await totp(secret))
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
  await dialog(page).getByLabel('Mevcut şifreniz').fill(user.password)
  await dialog(page).getByLabel('Doğrulama kodu').fill('000000')
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()

  await dialog(page).getByLabel('Doğrulama kodu').fill(await totp(secret))
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  await expect(page.getByRole('heading', { name: 'Kurtarma kodlarınız' })).toBeVisible()
  recoveryCodes = (await page.locator('.font-mono span').allInnerTexts()).map((c) => c.trim())
  expect(recoveryCodes.length).toBeGreaterThanOrEqual(8)
  await page.getByRole('button', { name: 'Tamam' }).click()
  await expect(page.getByText(/Kalan kullanılabilir kurtarma kodu: \d+/)).toBeVisible()
  await expect(page.getByRole('button', { name: /TOTP’yi Kaldır/ })).toBeVisible()
  expect(w.unexpected([/400 POST \/api\/auth\/security\/totp\/enroll/, /Failed to load resource.*400/])).toEqual([])
})

test('regenerating recovery codes needs proof and shows new codes', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await page.getByLabel('Doğrulama kodu').fill(await totp(secret))
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/security')
  await page.getByRole('button', { name: 'Kurtarma kodlarını yeniden oluştur' }).click()
  await dialog(page).getByLabel('Mevcut şifre').fill(user.password)
  await dialog(page).getByRole('button', { name: 'Yeniden oluştur' }).click()
  await expect(page.getByRole('heading', { name: 'Kurtarma kodlarınız' })).toBeVisible()
  recoveryCodes = (await page.locator('.font-mono span').allInnerTexts()).map((c) => c.trim())
})

test.fixme('recovery-codes modal is an accessible dialog: role, focus, Escape (#244)', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  await page.getByLabel('Kurtarma kodu').fill(recoveryCodes[2])
  await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
  await page.goto('/account/security')
  await page.getByRole('button', { name: 'Kurtarma kodlarını yeniden oluştur' }).click()
  await dialog(page).getByLabel('Mevcut şifre').fill(user.password)
  await dialog(page).getByRole('button', { name: 'Yeniden oluştur' }).click()
  await expect(page.getByRole('dialog', { name: 'Kurtarma kodlarınız' })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: 'Kurtarma kodlarınız' })).toBeHidden()
})

test('password sign-in now asks for a code; wrong code is rejected, valid TOTP completes', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İki faktörlü doğrulama' })).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  await page.getByLabel('Doğrulama kodu').fill('000000')
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('Doğrulama kodu hatalı')
  await page.getByLabel('Doğrulama kodu').fill(await totp(secret))
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await expect(page).toHaveURL(/\/tenants\//)
})

test('back to login from the code step signs out; reload mid-challenge does not grant access', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await expect(page.getByLabel('Doğrulama kodu')).toBeVisible()
  await page.goto('/')
  await expect(page).toHaveURL(/\/login/)
  await signIn(page, user.login, user.password)
  await page.getByRole('button', { name: 'Girişe dön' }).click()
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
})

test('a recovery code signs in once and is then rejected', async ({ page }) => {
  const code = recoveryCodes[0]
  await signIn(page, user.login, user.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  await page.getByLabel('Kurtarma kodu').fill(code)
  await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
  await expect(page).toHaveURL(/\/tenants\//)
  await page.context().clearCookies()
  await signIn(page, user.login, user.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  await page.getByLabel('Kurtarma kodu').fill(code)
  await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
  await expect(page.getByRole('alert')).toContainText('Kurtarma kodu geçersiz')
})

test('mobile: code step has no horizontal scroll', async ({ page }) => {
  await page.setViewportSize(MOBILE)
  await signIn(page, user.login, user.password)
  await expect(page.getByLabel('Doğrulama kodu')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0)
})

test('removing TOTP requires proof; with the password it is removed and sign-in no longer asks for a code', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  await page.getByLabel('Kurtarma kodu').fill(recoveryCodes[1])
  await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/security')
  await page.getByRole('button', { name: /TOTP’yi Kaldır/ }).click()
  await dialog(page).getByRole('button', { name: 'Kaldır' }).click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
  await dialog(page).getByLabel('Mevcut şifre').fill(user.password)
  await dialog(page).getByRole('button', { name: 'Kaldır' }).click()
  await expect(page.getByRole('button', { name: 'TOTP Etkinleştir' })).toBeVisible()
  await page.context().clearCookies()
  await signIn(page, user.login, user.password)
  await expect(page).toHaveURL(/\/tenants\//)
})

test.fixme('code step moves focus to the code field (#245)', async ({ page }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  const own = await Api.signIn(u)
  const setup = await own.call('POST', '/api/auth/security/totp/setup')
  await own.call('POST', '/api/auth/security/totp/enroll', { setupToken: setup.setupToken, code: await totp(setup.manualEntryKey), currentPassword: u.password })
  await signIn(page, u.login, u.password)
  await expect(page.getByLabel('Doğrulama kodu')).toBeFocused()
})
