import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { Api } from '../../api'
import { axeViolations, MOBILE, newTotpUser, newUser, platformApi, signIn, totp, watch } from './helpers'

test.describe.configure({ mode: 'serial' })

let user: Awaited<ReturnType<typeof newUser>>
let configured = false
const SKIP = 'stack has no WebAuthn relying party (Authentication__Passkeys__RelyingPartyId/Origins); passkeys need a hostname such as https://localhost:5173, not an IP'
test.beforeAll(async () => {
  const platform = await platformApi()
  user = await newUser(platform)
  await platform.dispose()
  const own = await Api.signIn(user)
  configured = await own.call('POST', '/api/auth/passkeys/register/options', undefined, [200, 503]).then((r) => r?.error !== 'passkeys_not_configured')
  await own.dispose()
})

/** Chromium virtual authenticator over CDP: a resident-key, user-verifying platform authenticator that approves every prompt. */
async function virtualAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  const { authenticatorId } = await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  return { cdp, authenticatorId }
}

const NAME = 'E2E laptop key'

test('empty state, validation, wrong password, then a passkey is registered and listed', async ({ page, context }) => {
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
  await expect(page.getByText(NAME)).toBeVisible()
  await expect(page.getByText(/Kayıt tarihi/)).toBeVisible()
  await expect(page.getByRole('button', { name: `${NAME} geçiş anahtarını kaldır` })).toBeVisible()
  await page.reload()
  await expect(page.getByText(NAME)).toBeVisible()
  expect(w.unexpected()).toEqual([])
})

async function registerViaUi(page: Page, u: { login: string; password: string }, name: string) {
  await signIn(page, u.login, u.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill(name)
  await page.getByLabel('Mevcut şifreniz').fill(u.password)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByText(name)).toBeVisible()
}

test('passkey sign-in: button without username and with username; sign-out between', async ({ page, context }) => {
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

test.fixme('wrong password while adding a passkey keeps the session and shows an error (#257)', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  await signIn(page, user.login, user.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto('/account/passkeys')
  await page.getByRole('button', { name: 'Geçiş anahtarı ekle' }).click()
  await page.getByLabel('Geçiş anahtarı adı').fill('E2E wrong password key')
  await page.getByLabel('Mevcut şifreniz').fill('wrong-password-value')
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.locator('form').getByRole('alert')).toBeVisible()
  await expect(page).toHaveURL(/\/account\/passkeys/)
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

test.fixme('password sign-in of a passkey-only user opens the passkey step, not a TOTP code prompt (#260)', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await registerViaUi(page, fresh, 'E2E passkey-only key')
  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)
  await signIn(page, fresh.login, fresh.password)
  await expect(page.getByRole('heading', { level: 1, name: 'İkinci faktör doğrulaması' })).toBeVisible()
  await expect(page.getByLabel('Doğrulama kodu')).toHaveCount(0)
})

test.fixme('failed passkey second factor shows an error message (#250)', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  const { cdp, authenticatorId } = await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newTotpUser(p); await p.dispose(); return n })
  await signIn(page, fresh.login, fresh.password)
  await page.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }).click() // switch the code step to passkey mode
  await cdp.send('WebAuthn.removeVirtualAuthenticator', { authenticatorId }) // no authenticator answers
  await page.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }).first().click()
  await expect(page.getByRole('alert')).toBeVisible()
})

test('removing a passkey needs the password; Cancel keeps it, the right password empties the list', async ({ page, context }) => {
  test.skip(!configured, SKIP)
  await virtualAuthenticator(context, page)
  const fresh = await platformApi().then(async (p) => { const n = await newUser(p); await p.dispose(); return n })
  await registerViaUi(page, fresh, 'E2E removable key')
  await page.getByRole('button', { name: 'E2E removable key geçiş anahtarını kaldır' }).click()
  const form = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Geçiş anahtarı kaldır' }) })
  await expect(form.getByRole('button', { name: 'Kaldır' })).toBeDisabled()
  await form.getByRole('button', { name: 'Vazgeç' }).click()
  await expect(form).toHaveCount(0)
  await page.getByRole('button', { name: 'E2E removable key geçiş anahtarını kaldır' }).click()
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
