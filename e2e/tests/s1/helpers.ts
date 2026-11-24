import AxeBuilder from '@axe-core/playwright'
import { createHmac } from 'node:crypto'
import { expect, type Page, type Response } from '@playwright/test'
import { Api, ensureTenant, ensureTenantUser } from '../../api'
import { state, type Account } from '../../state'

export const s = state()
export const rand = () => Math.random().toString(36).slice(2, 8)
export const MOBILE = { width: 390, height: 844 }
export const DESKTOP = { width: 1440, height: 900 }

/** Collects console errors/warnings, page errors, failed requests and 4xx/5xx responses; `allow` filters expected ones (e.g. a deliberate 401). */
export function watch(page: Page, opts: { strict?: boolean } = {}) {
  // The anonymous /api/me 401 probe is reported once as #240; non-strict watchers skip it so every other assertion stays meaningful.
  const isMe401 = (url: string) => !opts.strict && new URL(url).pathname === '/api/me'
  const problems: string[] = []
  page.on('console', (m) => { if (isMe401(m.location().url || 'http://x/')) return; if (m.type() === 'error' || m.type() === 'warning') problems.push(`console.${m.type()}: ${m.text()}`) })
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('requestfailed', (r) => r.failure()?.errorText !== 'net::ERR_ABORTED' && problems.push(`requestfailed: ${r.url()} ${r.failure()?.errorText}`))
  page.on('response', (r: Response) => { if (r.status() >= 400 && !(r.status() === 401 && isMe401(r.url()))) problems.push(`${r.status()} ${r.request().method()} ${new URL(r.url()).pathname}`) })
  return {
    problems,
    /** Problems left after dropping those matching any `allow` pattern. */
    unexpected(allow: RegExp[] = []) {
      return problems.filter((p) => !allow.some((a) => a.test(p)))
    },
  }
}

export async function signIn(page: Page, login: string, password: string, opts: { remember?: boolean } = {}) {
  await page.goto('/login')
  await page.getByLabel('Kullanıcı adı').fill(login)
  await page.getByLabel('Şifre', { exact: true }).fill(password)
  if (opts.remember) await page.getByLabel('Beni hatırla').check()
  await page.getByRole('button', { name: 'Giriş Yap', exact: true }).click()
}

export const signInAs = (page: Page, a: Account) => signIn(page, a.login, a.password)

export async function axeViolations(page: Page) {
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze()
  return r.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s) e.g. ${v.nodes[0]?.target.join(' ')}`)
}

export async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow, 'horizontal overflow in px').toBeLessThanOrEqual(0)
}

export const platformApi = () => Api.signIn(s.accounts.platformAdmin)

/** Fresh UserAdmin in the shared tenant, usable immediately (password already changed) unless keepForcedChange. */
export async function newUser(platform: Api, opts: { keepForcedChange?: boolean; tenantIds?: string[] } = {}): Promise<Account & { userId: string }> {
  const account = { login: `e2e-s1-${rand()}`, password: 'E2e-S1-Passw0rd!2026' }
  const userId = await ensureTenantUser(platform, opts.tenantIds ?? [s.tenantId], account, { displayName: `S1 ${account.login}`, keepForcedChange: opts.keepForcedChange })
  return { ...account, userId }
}

export { ensureTenant }

const B32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
function totpAtStep(secretBase32: string, step: number): string {
  const bits = [...secretBase32.replace(/[\s=]/g, '').toUpperCase()].map((c) => B32.indexOf(c).toString(2).padStart(5, '0')).join('')
  const key = Buffer.from(bits.match(/.{8}/g)!.map((b) => parseInt(b, 2)))
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(step))
  const h = createHmac('sha1', key).update(counter).digest()
  const o = h[h.length - 1] & 15
  return String(((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000)).padStart(6, '0')
}


const lastUsedStep = new Map<string, number>()
const nowStep = () => Math.floor(Date.now() / 30000)
/** A code the server accepts: it refuses replays, so each call uses a step later than any this secret already produced (waits for the clock if needed; the server tolerates one step of skew). */
export async function totp(secretBase32: string): Promise<string> {
  for (;;) {
    const step = Math.max(nowStep() - 1, (lastUsedStep.get(secretBase32) ?? 0) + 1)
    if (step <= nowStep() + 1) { lastUsedStep.set(secretBase32, step); return totpAtStep(secretBase32, step) }
    await new Promise((r) => setTimeout(r, 1000))
  }
}

/** A UserAdmin that already has TOTP enrolled through the API; `secret` feeds `totp()`. */
export async function newTotpUser(platform: Api): Promise<Account & { userId: string; secret: string }> {
  const u = await newUser(platform)
  const own = await Api.signIn(u)
  const setup = await own.call('POST', '/api/auth/security/totp/setup')
  await own.call('POST', '/api/auth/security/totp/enroll', { setupToken: setup.setupToken, code: await totp(setup.manualEntryKey), currentPassword: u.password })
  await own.dispose()
  return { ...u, secret: setup.manualEntryKey }
}
