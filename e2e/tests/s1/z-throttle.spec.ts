import { expect, request, test } from '@playwright/test'
import { apiUrl } from '../../api'
import { axeViolations, isIsolatedStack, ISOLATED_SKIP, mailTo, newTotpUser, newUser, platformApi, resetIsolatedThrottle, s, signIn, totp, virtualAuthenticator } from './helpers'

test.beforeEach(() => {
  test.skip(!isIsolatedStack, ISOLATED_SKIP)
  resetIsolatedThrottle()
})
test.afterAll(() => {
  resetIsolatedThrottle()
})

const LOCKOUT_TEXT = /Çok fazla başarısız deneme/
const RETRY_TEXT = /\d+ saniye sonra tekrar deneyin/

test('password sign-in throttle (#243): 5 failures lock with Retry-After, ignore client X-Forwarded-For spoofing, and unlock after window', async ({ page, browser }) => {
  test.setTimeout(60_000)
  for (let i = 0; i < 5; i++) {
    await signIn(page, `e2e-s1-lock-${i}`, 'definitely-wrong-password')
    await expect(page.getByRole('alert')).toContainText('Kullanıcı adı veya şifre hatalı')
  }

  // 6th attempt even with valid credentials gets 429 lockout notice with Retry-After seconds
  await signIn(page, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('alert')).toContainText(LOCKOUT_TEXT)
  await expect(page.getByRole('alert')).toContainText('Giriş geçici olarak kilitlendi')
  await expect(page.getByRole('alert')).toContainText(RETRY_TEXT)
  expect(await axeViolations(page)).toEqual([])

  // Client-supplied X-Forwarded-For cannot bypass the IP throttle behind nginx (ForwardLimit = 1)
  const spoofCtx = await browser.newContext({ ignoreHTTPSErrors: true, extraHTTPHeaders: { 'X-Forwarded-For': '198.51.100.77' } })
  const spoofPage = await spoofCtx.newPage()
  await signIn(spoofPage, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
  await expect(spoofPage.getByRole('alert')).toContainText(LOCKOUT_TEXT)
  await spoofCtx.close()

  // Once the 5-second initial window elapses, valid credentials succeed again
  await page.waitForTimeout(5_500)
  await signIn(page, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
  await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}$`))
})

test('5 failures against a verified-email account emit a security warning e-mail and in-app notification', async ({ page }) => {
  test.setTimeout(60_000)
  test.skip(!process.env.E2E_MAILPIT_URL, 'needs E2E_MAILPIT_URL against bps1')
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  const email = `${u.login}@example.test`

  // Verify user's e-mail address first
  await signIn(page, u.login, u.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/security')
  const sentAt = Date.now() - 5_000
  await page.getByRole('button', { name: 'Doğrulama e-postası gönder' }).click()
  const verify = await mailTo(email, /doğrula|verify/i, sentAt)
  await page.goto(new URL(verify.link).pathname + new URL(verify.link).search)
  await page.getByRole('button', { name: 'E-posta adresini doğrula' }).click()
  await expect(page.getByText('E-posta adresiniz doğrulandı.')).toBeVisible()
  await page.context().clearCookies()

  // 5 failed password attempts against this account trip RecordRepeatedLoginFailuresAsync
  const failSince = Date.now() - 2_000
  for (let i = 0; i < 5; i++) {
    await signIn(page, u.login, 'definitely-wrong-password')
    await expect(page.getByRole('alert')).toContainText('Kullanıcı adı veya şifre hatalı')
  }
  const alertMail = await mailTo(email, /güvenli|bildirimi|security/i, failSince)
  expect(alertMail.text.length).toBeGreaterThan(20)
})

test('TOTP second-factor throttle: 5 wrong codes lock with Retry-After and valid code succeeds once the window expires', async ({ page }) => {
  test.setTimeout(60_000)
  const platform = await platformApi()
  const u = await newTotpUser(platform)
  await platform.dispose()

  await signIn(page, u.login, u.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İki faktörlü doğrulama' })).toBeVisible()
  for (let i = 0; i < 5; i++) {
    await page.getByLabel('Doğrulama kodu').fill('000000')
    await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
    await expect(page.getByRole('alert')).toContainText('Doğrulama kodu hatalı')
  }

  await page.getByLabel('Doğrulama kodu').fill('000000')
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText(LOCKOUT_TEXT)
  await expect(page.getByRole('alert')).toContainText(RETRY_TEXT)

  await page.waitForTimeout(5_500)
  await page.getByLabel('Doğrulama kodu').fill(await totp(u.secret))
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await expect(page).toHaveURL(/\/tenants\//)
})

test('recovery-code second-factor throttle: 5 wrong codes trigger 429 lockout notice', async ({ page }) => {
  const platform = await platformApi()
  const u = await newTotpUser(platform)
  await platform.dispose()

  await signIn(page, u.login, u.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  for (let i = 0; i < 5; i++) {
    await page.getByLabel('Kurtarma kodu').fill(`0000-0000-000${i}`)
    await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
    await expect(page.getByRole('alert')).toContainText('Kurtarma kodu geçersiz')
  }

  await page.getByLabel('Kurtarma kodu').fill('9999-9999-9999')
  await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
  await expect(page.getByRole('alert')).toContainText(LOCKOUT_TEXT)
  await expect(page.getByRole('alert')).toContainText(RETRY_TEXT)
})

test('passkey assertion throttle: 5 failed assertions lock passkey sign-in with 429 lockout notice', async ({ page, context }) => {
  await virtualAuthenticator(context, page)
  // Trigger 5 invalid passkey assertions against the API so the 6th browser attempt encounters 429
  const anon = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  const dummyAssertion = {
    id: 'AAAAAAAAAAAAAAAAAAAAAA',
    rawId: 'AAAAAAAAAAAAAAAAAAAAAA',
    type: 'public-key',
    response: { clientDataJSON: 'e30', authenticatorData: 'AAAAAAAAAAAAAAAAAAAAAA', signature: 'AA' },
    clientExtensionResults: {},
  }
  for (let i = 0; i < 5; i++) {
    const csrf = await (await anon.get('/api/auth/antiforgery')).json()
    const r = await anon.post('/api/auth/passkeys/login', {
      headers: { [csrf.headerName]: csrf.requestToken },
      data: { challengeId: '11111111-1111-4111-8111-111111111111', response: dummyAssertion, rememberMe: false },
    })
    expect(r.status()).toBe(401)
  }
  await anon.dispose()

  // Seed a valid passkey user so WebAuthn succeeds locally in Chromium and hits POST /api/auth/passkeys/login -> 429
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  await signIn(page, u.login, u.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill('Throttle key')
  await page.getByLabel('Mevcut şifreniz').fill(u.password)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByText('Throttle key')).toBeVisible()
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)

  await page.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' }).click()
  await expect(page.getByRole('alert')).toContainText(LOCKOUT_TEXT)
  await expect(page.getByRole('alert')).toContainText(RETRY_TEXT)
})

test('forgot-password throttle: 6th reset request from the same IP shows 429 rate-limit message', async ({ page }) => {
  for (let i = 0; i < 5; i++) {
    await page.goto('/forgot-password')
    await page.getByLabel('Kullanıcı adı').fill(`e2e-s1-reset-limit-${i}`)
    await page.getByRole('button', { name: 'Sıfırlama bağlantısı gönder' }).click()
    await expect(page.getByText('şifre sıfırlama bağlantısı gönderildi')).toBeVisible()
  }

  await page.goto('/forgot-password')
  await page.getByLabel('Kullanıcı adı').fill('e2e-s1-reset-limit-6')
  await page.getByRole('button', { name: 'Sıfırlama bağlantısı gönder' }).click()
  await expect(page.getByRole('alert')).toContainText('Çok fazla istek gönderildi')
})

test('cumulative password typos across distinct accounts on a shared IP do not lock out unrelated sessions (#267)', async ({ page }) => {
  const platform = await platformApi()
  const accounts = await Promise.all(Array.from({ length: 5 }, () => newUser(platform)))
  const victim = await newUser(platform)
  await platform.dispose()
  for (const a of accounts) {
    await signIn(page, a.login, 'definitely-wrong-password')
    await signIn(page, a.login, a.password)
    await page.waitForURL(/\/tenants\//)
    await page.context().clearCookies()
  }
  await signIn(page, victim.login, victim.password)
  await expect(page).toHaveURL(/\/tenants\//)
})

test('security dialog explains an active login IP limit (#267)', async ({ page }) => {
  const platform = await platformApi()
  const user = await newUser(platform)
  await platform.dispose()
  await signIn(page, user.login, user.password)
  await page.waitForURL(/\/tenants\//)

  const anon = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  for (let i = 0; i < 5; i++) {
    const csrf = await (await anon.get('/api/auth/antiforgery')).json()
    await anon.post('/api/auth/login', {
      headers: { [csrf.headerName]: csrf.requestToken },
      data: { loginIdentifier: `other-${i}`, password: 'definitely-wrong-password' },
    })
  }
  await anon.dispose()

  await page.goto('/account/security')
  await page.getByRole('button', { name: 'TOTP Etkinleştir' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Mevcut şifreniz').fill(user.password)
  await dialog.getByLabel('Doğrulama kodu').fill('000000')
  await dialog.getByRole('button', { name: 'Doğrula ve Etkinleştir' }).click()
  await expect(dialog.getByRole('alert')).toContainText('Çok fazla başarısız deneme')
})

test('passkey step-up under an active login throttle reports temporary lockout instead of 400 invalid password (#282)', async ({ page, context }) => {
  await virtualAuthenticator(context, page)
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()

  await signIn(page, u.login, u.password)
  await page.waitForURL(/\/tenants\//)

  // Trip the per-IP login throttle from another anonymous session
  const anon = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  for (let i = 0; i < 5; i++) {
    const csrf = await (await anon.get('/api/auth/antiforgery')).json()
    await anon.post('/api/auth/login', {
      headers: { [csrf.headerName]: csrf.requestToken },
      data: { loginIdentifier: `other-${i}`, password: 'definitely-wrong-password' },
    })
  }
  await anon.dispose()

  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill('Throttled key')
  await page.getByLabel('Mevcut şifreniz').fill(u.password)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.locator('form').getByRole('alert')).not.toContainText('Parola geçersiz')
})
