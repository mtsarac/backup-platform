import { expect, test, type Page } from '@playwright/test'
import { Api } from '../../api'
import { acknowledgeRecoveryCodes, axeViolations, MOBILE, newTotpUser, newUser, platformApi, resetIsolatedThrottle, signIn, totp, virtualAuthenticator, watch } from './helpers'

test.describe.configure({ mode: 'serial' })

let user: Awaited<ReturnType<typeof newUser>>
let configured = false
const SKIP = 'stack has no WebAuthn relying party (Authentication__Passkeys__RelyingPartyId/Origins); passkeys need a hostname such as https://localhost:6273, not an IP'

test.beforeAll(async () => {
  resetIsolatedThrottle()
  const platform = await platformApi()
  user = await newUser(platform)
  await platform.dispose()
  const own = await Api.signIn(user)
  configured = await own.call('POST', '/api/auth/passkeys/register/options', undefined, [200, 503]).then((r) => r?.error !== 'passkeys_not_configured')
  await own.dispose()
})
test.afterAll(() => {
  resetIsolatedThrottle()
})

const NAME = 'E2E laptop key'

async function registerViaUi(page: Page, u: { login: string; password: string }, name: string): Promise<string[]> {
  await signIn(page, u.login, u.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill(name)
  await page.getByLabel('Mevcut şifreniz').fill(u.password)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByText(name)).toBeVisible()
  return acknowledgeRecoveryCodes(page)
}

test('empty state, validation, first passkey issues recovery codes, and key is listed with accessible remove button', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  const w = watch(page)
  await virtualAuthenticator(context, page)
  await signIn(page, user.login, user.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await expect(page.getByText('Henüz geçiş anahtarı eklenmedi.')).toBeVisible()
  expect(await axeViolations(page)).toEqual([])

  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  const form = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı ekle' }) })
  await expect(form.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
  expect(await axeViolations(page)).toEqual([])
  await form.getByLabel('Geçiş anahtarı adı').fill(NAME)
  await form.getByLabel('Mevcut şifreniz').fill(user.password)
  await form.getByRole('button', { name: 'Kaydet' }).click()
  expect((await acknowledgeRecoveryCodes(page)).length).toBeGreaterThanOrEqual(8)
  await expect(page.getByText(NAME)).toBeVisible()
  await expect(page.getByText(/Kayıt tarihi/)).toBeVisible()
  await expect(page.getByRole('button', { name: `Kaldır — ${NAME}` })).toBeVisible()
  await page.reload()
  await expect(page.getByText(NAME)).toBeVisible()
  expect(w.unexpected()).toEqual([])
})

test('passkey sign-in: button without username and with username; updates last-used timestamp', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  const w = watch(page)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await registerViaUi(page, fresh, 'E2E sign-in key')
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)

  // discoverable credential: no username typed
  await page.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' }).click()
  await expect(page).toHaveURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await expect(page.getByText(/Son kullanım/)).toBeVisible()
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)

  // with the username filled in
  await page.getByLabel('Kullanıcı adı').fill(fresh.login)
  await page.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' }).click()
  await expect(page).toHaveURL(/\/tenants\//)
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)

  expect(w.unexpected()).toEqual([])
})

test('wrong password while adding or removing a passkey keeps the session and shows an error (#257)', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await signIn(page, fresh.login, fresh.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')

  // First try adding with a wrong password before any credential exists on this authenticator
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  const addForm = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı ekle' }) })
  await addForm.getByLabel('Geçiş anahtarı adı').fill('E2E protected key')
  await addForm.getByLabel('Mevcut şifreniz').fill('wrong-password-value')
  await addForm.getByRole('button', { name: 'Kaydet' }).click()
  await expect(addForm.getByRole('alert')).toContainText('Parola geçersiz')
  await expect(page).toHaveURL(/\/account\/passkeys/)

  // Now register with the valid password, then try removing with a wrong password
  await addForm.getByLabel('Mevcut şifreniz').fill(fresh.password)
  await addForm.getByRole('button', { name: 'Kaydet' }).click()
  await acknowledgeRecoveryCodes(page)
  await expect(page.getByText('E2E protected key')).toBeVisible()

  await page.getByRole('button', { name: 'Kaldır — E2E protected key' }).click()
  const delForm = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı kaldır' }) })
  await delForm.getByLabel('Mevcut şifreniz').fill('wrong-password-value')
  await delForm.getByRole('button', { name: 'Kaldır' }).click()
  await expect(delForm.getByRole('alert')).toContainText('Parola geçersiz')
  await expect(page).toHaveURL(/\/account\/passkeys/)
  await expect(page.getByRole('button', { name: 'Kaldır — E2E protected key' })).toBeVisible()
})

test('re-registering the same authenticator is refused clearly; existing passkey can verify removal without typing the password', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  const w = watch(page)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await registerViaUi(page, fresh, 'E2E primary key')

  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  const addForm = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı ekle' }) })
  await addForm.getByLabel('Geçiş anahtarı adı').fill('E2E duplicate device key')
  await addForm.getByLabel('Mevcut şifreniz').fill(fresh.password)
  await addForm.getByRole('button', { name: 'Kaydet' }).click()
  await expect(addForm.getByRole('alert')).toContainText('zaten kayıtlı')
  await addForm.getByRole('button', { name: 'Vazgeç' }).click()

  await page.getByRole('button', { name: 'Kaldır — E2E primary key' }).click()
  const delForm = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı kaldır' }) })
  await delForm.getByRole('button', { name: 'Geçiş anahtarıyla doğrula' }).click()
  await expect(delForm.getByLabel('Mevcut şifreniz')).toHaveCount(0)
  await delForm.getByRole('button', { name: 'Kaldır' }).click()
  await expect(page.getByText('Henüz geçiş anahtarı eklenmedi.')).toBeVisible()
  expect(w.unexpected()).toEqual([])
})

test('passkey sign-in without a usable credential shows an error and stays on /login', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page) // empty authenticator
  await page.goto('/login')
  await page.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page).toHaveURL(/\/login$/)
})

test('a TOTP user can switch the code step to a passkey and complete sign-in with it', async ({ page, context }) => {
  test.setTimeout(90_000)
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newTotpUser(p); await p.dispose(); return n })
  await signIn(page, fresh.login, fresh.password)
  await page.getByLabel('Doğrulama kodu').fill(await totp(fresh.secret))
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click()
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill('E2E 2fa key')
  await page.getByLabel('Mevcut şifreniz').fill(fresh.password)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByText('E2E 2fa key')).toBeVisible()
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)
  await signIn(page, fresh.login, fresh.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İki faktörlü doğrulama' })).toBeVisible()
  await page.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'İkinci faktör doğrulaması' })).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  await page.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }).first().click()
  await expect(page).toHaveURL(/\/tenants\//)
})

test('password sign-in of a passkey-only user opens the passkey step (#260), focuses it (#245), and supports recovery code fallback', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  const codes = await registerViaUi(page, fresh, 'E2E passkey-only key')
  expect(codes.length).toBeGreaterThanOrEqual(8)
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)

  await signIn(page, fresh.login, fresh.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İkinci faktör doğrulaması' })).toBeVisible()
  await expect(page.getByLabel('Doğrulama kodu')).toHaveCount(0)
  const verifyBtn = page.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }).first()
  await expect(verifyBtn).toBeFocused()
  await verifyBtn.click()
  await expect(page).toHaveURL(/\/tenants\//)

  // recovery code also works for passkey-only 2FA
  await page.context().clearCookies()
  await signIn(page, fresh.login, fresh.password)
  await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click()
  await page.getByLabel('Kurtarma kodu').fill(codes[0])
  await page.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }).click()
  await expect(page).toHaveURL(/\/tenants\//)
})

test('failed passkey second factor shows an error message (#250)', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  const { cdp, authenticatorId } = await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await registerViaUi(page, fresh, 'E2E vanishing key')
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)
  // the account has a passkey, but the authenticator no longer holds it: the assertion must fail visibly
  await cdp.send('WebAuthn.clearCredentials', { authenticatorId })
  await signIn(page, fresh.login, fresh.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İkinci faktör doğrulaması' })).toBeVisible()
  await page.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }).first().click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page).toHaveURL(/\/login/)
})

test('removing a passkey needs the password; Cancel keeps it, the right password empties the list', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await registerViaUi(page, fresh, 'E2E removable key')
  await page.getByRole('button', { name: 'Kaldır — E2E removable key' }).click()
  const form = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı kaldır' }) })
  await expect(form.getByRole('button', { name: 'Kaldır' })).toBeDisabled()
  await form.getByRole('button', { name: 'Vazgeç' }).click()
  await expect(form).toHaveCount(0)
  await page.getByRole('button', { name: 'Kaldır — E2E removable key' }).click()
  await form.getByLabel('Mevcut şifreniz').fill(fresh.password)
  await form.getByRole('button', { name: 'Kaldır' }).click()
  await expect(page.getByText('Henüz geçiş anahtarı eklenmedi.')).toBeVisible()
})

test('without server configuration the UI says passkeys are not configured, for both add and sign-in', async ({ page, context }) => {
  test.skip(configured, 'only meaningful while the stack has no relying party')
  await virtualAuthenticator(context, page)
  await page.goto('/login')
  await page.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await signIn(page, user.login, user.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill('x')
  await page.getByLabel('Mevcut şifreniz').fill(user.password)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByRole('alert')).toContainText('henüz yapılandırılmamış')
})

test('mobile: passkeys page has no horizontal scroll with the add form open', async ({ page }) => {
  await page.setViewportSize(MOBILE)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await signIn(page, fresh.login, fresh.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0)
})
