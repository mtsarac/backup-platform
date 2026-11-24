import { expect, test, type Page } from '@playwright/test'
import { axeViolations, expectNoHorizontalScroll, MOBILE, newUser, platformApi, signIn, watch } from './helpers'


const NEW = 'E2e-S1-Changed!2026-xyz'
const form = (page: Page) => ({
  current: page.getByLabel('Mevcut şifre'),
  next: page.getByLabel('Yeni şifre', { exact: true }),
  again: page.getByLabel('Yeni şifre (tekrar)'),
  submit: page.getByRole('button', { name: 'Şifreyi Değiştir ve Giriş Yap' }),
})

async function forcedUser() {
  const platform = await platformApi()
  const u = await newUser(platform, { keepForcedChange: true })
  await platform.dispose()
  return u
}

test('first sign-in of a new account is forced into the password change form', async ({ page }) => {
  const u = await forcedUser()
  await signIn(page, u.login, u.password)
  await expect(page.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
  await expect(page).toHaveURL(/\/login$/)
  await expect(form(page).current).toHaveValue(u.password)
  expect(await axeViolations(page)).toEqual([])
  await page.setViewportSize(MOBILE)
  await expectNoHorizontalScroll(page)
})

test.fixme('password change step moves focus into the form (#245)', async ({ page }) => {
  const u = await forcedUser()
  await signIn(page, u.login, u.password)
  await expect(page.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
  expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY')
})

test('validation: too short, mismatch, same as current, breached password; nothing is changed', async ({ page }) => {
  const u = await forcedUser()
  await signIn(page, u.login, u.password)
  const f = form(page)
  await f.next.fill('short'); await f.again.fill('short'); await f.submit.click()
  await expect(page.getByRole('alert')).toContainText('en az 12 karakter')
  await f.next.fill(NEW); await f.again.fill(`${NEW}-different`); await f.submit.click()
  await expect(page.getByRole('alert')).toContainText('eşleşmiyor')
  await f.next.fill(u.password); await f.again.fill(u.password); await f.submit.click()
  await expect(page.getByRole('alert')).toContainText('mevcut şifreyle aynı')
  await f.next.fill('password1234'); await f.again.fill('password1234'); await f.submit.click()
  await expect(page.getByRole('alert')).toContainText(/yaygın|sızdırılmış/)
  await expect(page).toHaveURL(/\/login$/)
})

test('valid new password signs in; the new password works on the next sign-in', async ({ page, context }) => {
  const u = await forcedUser()
  const w = watch(page)
  await signIn(page, u.login, u.password)
  const f = form(page)
  await f.next.fill(NEW); await f.again.fill(NEW); await f.submit.click()
  await expect(page).toHaveURL(/\/tenants\//)
  await context.clearCookies()
  await signIn(page, u.login, NEW)
  await expect(page).toHaveURL(/\/tenants\//)
  expect(w.unexpected()).toEqual([])
})

test('cancel signs out; the forced change is asked again next time', async ({ page }) => {
  const u = await forcedUser()
  await signIn(page, u.login, u.password)
  await page.getByRole('button', { name: 'İptal et ve Çıkış Yap' }).click()
  await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
  await signIn(page, u.login, u.password)
  await expect(page.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
})

test('until the password is changed the session reaches no protected page', async ({ page }) => {
  const u = await forcedUser()
  await signIn(page, u.login, u.password)
  await expect(page.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
  for (const path of ['/', '/account', '/platform']) {
    await page.goto(path)
    await expect(page.getByRole('heading', { name: 'Şifrenizi değiştirin' }).or(page.getByLabel('Kullanıcı adı'))).toBeVisible()
    await expect(page).not.toHaveURL(/\/(account|platform|tenants)/)
  }
})

test('server rejection of the change shows an alert and keeps the form', async ({ page }) => {
  const u = await forcedUser()
  await signIn(page, u.login, u.password)
  await page.route('**/api/auth/change-password', (r) => r.fulfill({ status: 500, contentType: 'application/json', body: '{}' }))
  const f = form(page)
  await f.next.fill(NEW); await f.again.fill(NEW); await f.submit.click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(f.submit).toBeEnabled()
})
