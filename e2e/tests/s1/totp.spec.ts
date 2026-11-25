import { expect, test, type Page } from '@playwright/test'
import { axeViolations, MOBILE, newUser, platformApi, resetIsolatedThrottle, signIn, totp, watch } from './helpers'

test.describe.configure({ mode: 'serial' })

let user: Awaited<ReturnType<typeof newUser>>
let secret = ''
let recoveryCodes: string[] = []

test.beforeAll(async () => {
  resetIsolatedThrottle()
  const platform = await platformApi()
  user = await newUser(platform)
  await platform.dispose()
})
test.afterAll(async () => {
  resetIsolatedThrottle()
  const platform = await platformApi()
  await platform.call('POST', `/api/platform/users/${user.userId}/disable`, undefined, [200, 204, 404, 409]).catch(() => {})
  await platform.dispose()
})

const dialog = (page: Page) => page.getByRole('dialog')

test('security page offers TOTP enrolment; setup dialog validates and enables TOTP, then shows recovery codes in an accessible dialog (#244)', async ({ page }) => {
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

  // required fields and wrong verification code are rejected without enrolling
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  expect(await dialog(page).getByLabel('Mevcut şifreniz').evaluate((e: HTMLInputElement) => e.validity.valueMissing)).toBe(true)
  await dialog(page).getByLabel('Mevcut şifreniz').fill(user.password)
  await dialog(page).getByLabel('Doğrulama kodu').fill('000000')
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()

  await dialog(page).getByLabel('Doğrulama kodu').fill(await totp(secret))
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  const codesDialog = page.getByRole('dialog', { name: 'Kurtarma kodlarınız' })
  await expect(codesDialog).toBeVisible()
  await expect(codesDialog.getByRole('button', { name: 'Tamam' })).toBeFocused()
  expect(await axeViolations(page, '[role="dialog"]')).toEqual([])
  recoveryCodes = (await codesDialog.locator('.font-mono span').allInnerTexts()).map((c) => c.trim())
  expect(recoveryCodes.length).toBeGreaterThanOrEqual(8)
  await page.keyboard.press('Escape')
  await expect(codesDialog).toBeHidden()
  await expect(page.getByText(/Kalan kullanılabilir kurtarma kodu: \d+/)).toBeVisible()
  await expect(page.getByRole('button', { name: /TOTP’yi Kaldır/ })).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  expect(w.unexpected([/400 POST \/api\/auth\/security\/totp\/enroll/, /Failed to load resource.*400/])).toEqual([])
})

test('regenerating recovery codes accepts TOTP proof as well as password, and shows new codes', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await expect(page.getByLabel('Doğrulama kodu')).toBeFocused()
  await page.getByLabel('Doğrulama kodu').fill(await totp(secret))
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/security')
  await page.getByRole('button', { name: 'Kurtarma kodlarını yeniden oluştur' }).click()
  await dialog(page).getByLabel('Doğrulama kodu (TOTP)').fill(await totp(secret))
  await dialog(page).getByRole('button', { name: 'Yeniden oluştur' }).click()
  const codesDialog = page.getByRole('dialog', { name: 'Kurtarma kodlarınız' })
  await expect(codesDialog).toBeVisible()
  recoveryCodes = (await codesDialog.locator('.font-mono span').allInnerTexts()).map((c) => c.trim())
  await codesDialog.getByRole('button', { name: 'Tamam' }).click()
  await expect(codesDialog).toBeHidden()
})

test('password sign-in focuses the code field (#245); wrong code is rejected, valid TOTP completes', async ({ page }) => {
  await signIn(page, user.login, user.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İki faktörlü doğrulama' })).toBeVisible()
  await expect(page.getByLabel('Doğrulama kodu')).toBeFocused()
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

test('a recovery code focuses its input (#245), signs in once, and is then rejected', async ({ page }) => {
  const code = recoveryCodes[0]
  await signIn(page, user.login, user.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  await expect(page.getByLabel('Kurtarma kodu')).toBeFocused()
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

test.fail('TOTP setup and removal dialogs explain wrong code or wrong password instead of generic unexpected failure (#266)', async ({ page }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  await signIn(page, u.login, u.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/security')
  await page.getByRole('button', { name: 'TOTP Etkinleştir' }).click()
  await dialog(page).getByLabel('Mevcut şifreniz').fill(u.password)
  await dialog(page).getByLabel('Doğrulama kodu').fill('000000')
  await dialog(page).getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  await expect(dialog(page).getByRole('alert')).not.toContainText('Beklenmeyen bir sorun oluştu')
})
