import AxeBuilder from '@axe-core/playwright'
import { execFileSync } from 'node:child_process'
import { createHmac } from 'node:crypto'
import { expect, type BrowserContext, type Page, type Response } from '@playwright/test'
import { Api, ensureTenant, ensureTenantUser } from '../../api'
import { state, type Account } from '../../state'

export const s = state()
export const isIsolatedStack = new URL(s.baseUrl).hostname === 'localhost'
export const ISOLATED_SKIP = 'mutates the 24h per-IP LoginThrottle (#267); run against the isolated bps1 auth stack'
export const rand = () => Math.random().toString(36).slice(2, 8)
export const MOBILE = { width: 390, height: 844 }
export const DESKTOP = { width: 1440, height: 900 }

/** Clears persisted throttle buckets on the isolated bps1 Postgres container between throttle-heavy spec files; never touches the shared bpe2e stack. */
export function resetIsolatedThrottle() {
  if (!isIsolatedStack) return
  execFileSync('docker', ['exec', 'bps1-postgres', 'psql', '-U', 'postgres', '-d', 'backup_platform_dev', '-q', '-c', 'TRUNCATE login_throttle_entries;'])
}

/** Collects console errors/warnings, page errors, failed requests and 4xx/5xx responses; `allow` filters expected ones (e.g. a deliberate 401/400/429). */
export function watch(page: Page) {
  const problems: string[] = []
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`console.${m.type()}: ${m.text()}`) })
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('requestfailed', (r) => r.failure()?.errorText !== 'net::ERR_ABORTED' && problems.push(`requestfailed: ${r.url()} ${r.failure()?.errorText}`))
  page.on('response', (r: Response) => { if (r.status() >= 400) problems.push(`${r.status()} ${r.request().method()} ${new URL(r.url()).pathname}`) })
  return {
    problems,
    reset() { problems.length = 0 },
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
  await page.getByRole('button', { name: 'Giriş yap', exact: true }).click()
}

export const signInAs = (page: Page, a: Account) => signIn(page, a.login, a.password)

export async function axeViolations(page: Page, includeSelector?: string) {
  let builder = new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa'])
  if (includeSelector) builder = builder.include(includeSelector)
  const r = await builder.analyze()
  return r.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s) e.g. ${v.nodes[0]?.target.join(' ')}`)
}

export async function expectNoHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow, 'horizontal overflow in px').toBeLessThanOrEqual(0)
}

/** Chromium virtual authenticator over CDP: a resident-key, user-verifying platform authenticator that approves every prompt. */
export async function virtualAuthenticator(context: BrowserContext, page: Page) {
  const cdp = await context.newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  const { authenticatorId } = await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  })
  return { cdp, authenticatorId }
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

/** Polls Mailpit for a message sent to `address` matching `subjectPart` since `since`. */
export async function mailTo(address: string, subjectPart: RegExp, since = Date.now() - 60_000): Promise<{ link: string; text: string }> {
  const mailpit = process.env.E2E_MAILPIT_URL ?? 'http://127.0.0.1:8026'
  for (let i = 0; i < 30; i++) {
    const list = (await (await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${address}`)}`)).json()) as { messages: { ID: string; Subject: string; Created: string }[] }
    const hit = list.messages.find((m) => subjectPart.test(m.Subject) && Date.parse(m.Created) >= since)
    if (hit) {
      const body = (await (await fetch(`${mailpit}/api/v1/message/${hit.ID}`)).json()) as { Text: string }
      const link = body.Text.match(/https?:\/\/\S+\?token=[\w-]+/)?.[0] ?? ''
      return { link, text: body.Text }
    }
    await new Promise((r) => setTimeout(r, 1000))
  }
  throw new Error(`no mail for ${address}`)
}
