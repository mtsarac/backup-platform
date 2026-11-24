import { expect, request, test } from '@playwright/test'
import { Api, apiUrl } from '../../api'
import { axeViolations, expectNoHorizontalScroll, MOBILE, newUser, platformApi, signIn, watch } from './helpers'

const MAILPIT = process.env.E2E_MAILPIT_URL ?? 'http://127.0.0.1:8025'
const NEW = 'E2e-S1-Recovered!2026-xyz'

let available = false
test.beforeAll(async () => {
  const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  available = ((await (await ctx.get('/api/auth/email-recovery')).json()) as { available?: boolean }).available === true
  await ctx.dispose()
})

const NEEDS_MAIL = 'needs e-mail recovery: Notifications__PublicBaseUrl + AllowedPrivateNetworks on the API, an enabled platform e-mail transport and a mail sink (mailpit at E2E_MAILPIT_URL)'

async function mailTo(address: string, subjectPart: RegExp, since = Date.now() - 60_000): Promise<{ link: string }> {
  for (let i = 0; i < 30; i++) {
    const list = (await (await fetch(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`)).json()) as { messages: { ID: string; Subject: string; Created: string }[] }
    const hit = list.messages.find((m) => subjectPart.test(m.Subject) && Date.parse(m.Created) >= since)
    if (hit) {
      const body = (await (await fetch(`${MAILPIT}/api/v1/message/${hit.ID}`)).json()) as { Text: string }
      const link = body.Text.match(/https?:\/\/\S+\?token=[\w-]+/)?.[0]
      if (link) return { link }
    }
    await new Promise((r) => setTimeout(r, 1000))
  }
  throw new Error(`no mail for ${address}`)
}

test.describe('recovery pages without a token (always available)', () => {
  test('/reset-password and /verify-email without token say the link is invalid and offer a way back; axe, mobile', async ({ page }) => {
    const w = watch(page)
    await page.goto('/reset-password')
    await expect(page.getByRole('heading', { level: 1, name: 'Şifre sıfırlama' })).toBeVisible()
    await expect(page.getByText('Sıfırlama bağlantısı geçersiz veya süresi dolmuş.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Girişe dön' })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
    await page.goto('/verify-email')
    await expect(page.getByRole('heading', { level: 1, name: 'E-posta doğrulama' })).toBeVisible()
    await expect(page.getByText('Doğrulama bağlantısı geçersiz veya süresi dolmuş.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Çalışma alanlarına dön' })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
    await page.setViewportSize(MOBILE)
    await expectNoHorizontalScroll(page)
    expect(w.unexpected()).toEqual([])
  })

  test('/reset-password with a bogus token: mismatch and short password are caught, a wrong token is rejected with guidance', async ({ page }) => {
    await page.goto('/reset-password?token=not-a-real-token')
    await page.getByLabel('Yeni şifre', { exact: true }).fill(NEW)
    await page.getByLabel('Yeni şifre (tekrar)').fill(`${NEW}x`)
    await page.getByRole('button', { name: 'Şifreyi sıfırla' }).click()
    await expect(page.getByRole('alert')).toContainText('eşleşmiyor')
    await page.getByLabel('Yeni şifre (tekrar)').fill(NEW)
    await page.getByRole('button', { name: 'Şifreyi sıfırla' }).click()
    await expect(page.getByRole('alert')).toContainText('geçersiz veya süresi dolmuş')
    await expect(page.getByRole('alert')).toContainText('Yeni bir sıfırlama bağlantısı isteyin')
  })

  test('/verify-email with a bogus token: confirm shows the invalid-link error', async ({ page }) => {
    await page.goto('/verify-email?token=not-a-real-token')
    await page.getByRole('button', { name: 'E-posta adresini doğrula' }).click()
    await expect(page.getByRole('alert')).toContainText('geçersiz veya süresi dolmuş')
  })

  test('/forgot-password: when e-mail recovery is unavailable the page says so and offers the admin route; when available it asks for the login', async ({ page }) => {
    const w = watch(page)
    await page.goto('/forgot-password')
    await expect(page.getByRole('heading', { level: 1, name: 'Şifremi unuttum' })).toBeVisible()
    if (available) await expect(page.getByLabel('Kullanıcı adı')).toBeVisible()
    else await expect(page.getByText('E-posta ile kurtarma bu platformda kullanılamıyor. Bir Platform Yöneticisinden geçici şifre isteyin.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Girişe dön' })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
    await page.getByRole('link', { name: 'Girişe dön' }).click()
    await expect(page).toHaveURL(/\/login/)
    expect(w.unexpected()).toEqual([])
  })

  test('security page tells an unverified user why e-mail verification is unavailable', async ({ page }) => {
    test.skip(available, 'only meaningful while e-mail recovery is unavailable')
    const platform = await platformApi()
    const u = await newUser(platform)
    await platform.dispose()
    await signIn(page, u.login, u.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/account/security')
    await expect(page.getByText('E-posta ile kurtarma bu platformda kullanılamıyor.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Doğrulama e-postası gönder' })).toHaveCount(0)
  })
})

test.describe('e-mail verification and password reset (needs a mail sink)', () => {
  test('verify address from the security page, then reset the password through the e-mailed link; the link works once and old sessions end', async ({ page, browser }) => {
    test.skip(!available, NEEDS_MAIL)
    const platform = await platformApi()
    const u = await newUser(platform)
    await platform.dispose()
    const email = `${u.login}@example.test`

    await signIn(page, u.login, u.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/account/security')
    const sentAt = Date.now() - 5_000
    await page.getByRole('button', { name: 'Doğrulama e-postası gönder' }).click()
    await expect(page.getByText('Doğrulama e-postası gönderildi.')).toBeVisible()
    const verify = await mailTo(email, /doğrula|verify/i, sentAt)
    await page.goto(new URL(verify.link).pathname + new URL(verify.link).search)
    await page.getByRole('button', { name: 'E-posta adresini doğrula' }).click()
    await expect(page.getByText('E-posta adresiniz doğrulandı.')).toBeVisible()

    // a second browser holds a live session that the reset must end
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
    const other = await ctx.newPage()
    await signIn(other, u.login, u.password)
    await other.waitForURL(/\/tenants\//)

    const fresh = await ctx.browser()!.newContext({ ignoreHTTPSErrors: true })
    const p = await fresh.newPage()
    await p.goto('/forgot-password')
    const requestedAt = Date.now() - 5_000
    await p.getByLabel('Kullanıcı adı').fill(u.login)
    await p.getByRole('button', { name: 'Sıfırlama bağlantısı gönder' }).click()
    await expect(p.getByText('şifre sıfırlama bağlantısı gönderildi')).toBeVisible()
    const reset = await mailTo(email, /şifre|password|reset/i, requestedAt)
    const resetPath = new URL(reset.link).pathname + new URL(reset.link).search
    await p.goto(resetPath)
    await p.getByLabel('Yeni şifre', { exact: true }).fill(NEW)
    await p.getByLabel('Yeni şifre (tekrar)').fill(NEW)
    await p.getByRole('button', { name: 'Şifreyi sıfırla' }).click()
    await expect(p.getByText('Şifreniz güncellendi.')).toBeVisible()

    // same link again is refused
    await p.goto(resetPath)
    await p.getByLabel('Yeni şifre', { exact: true }).fill(`${NEW}-2`)
    await p.getByLabel('Yeni şifre (tekrar)').fill(`${NEW}-2`)
    await p.getByRole('button', { name: 'Şifreyi sıfırla' }).click()
    await expect(p.getByRole('alert')).toContainText('geçersiz veya süresi dolmuş')

    await other.goto('/account')
    await expect(other).toHaveURL(/\/login/)
    await signIn(p, u.login, NEW)
    await expect(p).toHaveURL(/\/tenants\//)
    await ctx.close(); await fresh.close()
  })

  test('forgot-password answers identically for an unknown login and an unverified one (no account enumeration)', async ({ page }) => {
    test.skip(!available, NEEDS_MAIL)
    const platform = await platformApi()
    const u = await newUser(platform)
    await platform.dispose()
    const texts: string[] = []
    for (const login of [u.login, `no-such-user-${Date.now()}`]) {
      await page.goto('/forgot-password')
      await page.getByLabel('Kullanıcı adı').fill(login)
      await page.getByRole('button', { name: 'Sıfırlama bağlantısı gönder' }).click()
      await expect(page.getByRole('status').or(page.getByText('gönderildi')).first()).toBeVisible()
      texts.push(await page.locator('main').innerText())
    }
    expect(texts[0]).toBe(texts[1])
  })
})
