import AxeBuilder from '@axe-core/playwright'
import { execSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { expect, request, type BrowserContext, type Page } from '@playwright/test'
import { Api, apiUrl, ensureTenant, ensureTenantUser, ensureUnlimitedPlan } from '../../api'
import { state, type Account, type State } from '../../state'

export const s = state()
export const DESKTOP = { width: 1440, height: 900 }
export const MOBILE = { width: 390, height: 844 }
export const rand = () => Math.random().toString(36).slice(2, 7)
export const uniq = (what: string) => `e2e-s2-${what}-${rand()}`

const s2StateUrl = new URL('../../.state-s2.json', import.meta.url)
export const hasS2Stack = () => existsSync(s2StateUrl)
export const s2State = (): State => JSON.parse(readFileSync(s2StateUrl, 'utf8'))

type Cookies = Parameters<BrowserContext['addCookies']>[0]
const sessions = new Map<string, Promise<Cookies>>()

/** One API login per account + API origin; avoids burning login rate-limit budget across specs. */
function sessionCookies(account: Account, targetApiUrl = apiUrl) {
  const key = `${targetApiUrl}:${account.login}`
  let cookies = sessions.get(key)
  if (!cookies) {
    cookies = (async () => {
      const ctx = await request.newContext({ baseURL: targetApiUrl, ignoreHTTPSErrors: true })
      const token = await (await ctx.get('/api/auth/antiforgery')).json()
      const r = await ctx.post('/api/auth/login', {
        data: { loginIdentifier: account.login, password: account.password },
        headers: { [token.headerName]: token.requestToken },
      })
      expect(r.ok(), `API login ${account.login} on ${targetApiUrl}`).toBeTruthy()
      const { cookies } = await ctx.storageState()
      await ctx.dispose()
      return cookies
    })()
    sessions.set(key, cookies)
  }
  return cookies
}

export async function signInAs(page: Page, account: Account, opts: { baseUrl?: string; apiUrl?: string } = {}) {
  await page.context().addCookies(await sessionCookies(account, opts.apiUrl ?? apiUrl))
  await page.goto(opts.baseUrl ? `${opts.baseUrl}/` : '/')
}

export const signInTenant = (p: Page) => signInAs(p, s.accounts.userAdmin)
export const signInPlatform = (p: Page) => signInAs(p, s.accounts.platformAdmin)

export type WatchAllow = { status?: number; path?: RegExp }

/** Collects console errors/warnings, uncaught page errors, failed requests and >=400 HTTP responses. */
export function watch(page: Page, allow: WatchAllow[] = []) {
  const problems: string[] = []
  const isAllowedHttp = (status: number, rawUrl: string) => {
    try {
      const pathname = new URL(rawUrl).pathname
      return allow.some((a) => (a.status === undefined || a.status === status) && (!a.path || a.path.test(pathname)))
    } catch {
      return false
    }
  }
  page.on('console', (m) => {
    if (m.type() !== 'error' && m.type() !== 'warning') return
    const loc = m.location().url || ''
    if (loc && allow.some((a) => a.path?.test(new URL(loc, 'https://x').pathname))) return
    if (/Failed to load resource: the server responded with a status of (\d+)/.test(m.text())) {
      const code = Number(RegExp.$1)
      if (loc && isAllowedHttp(code, loc)) return
    }
    problems.push(`console.${m.type()}: ${m.text()}`)
  })
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('requestfailed', (r) => {
    if (!r.failure()?.errorText.includes('ABORTED')) problems.push(`requestfailed: ${r.url()} ${r.failure()?.errorText}`)
  })
  page.on('response', (r) => {
    if (r.status() >= 400 && !isAllowedHttp(r.status(), r.url())) {
      problems.push(`http ${r.status()} ${r.request().method()} ${r.url()}`)
    }
  })
  return problems
}

export async function noHorizontalOverflow(page: Page) {
  const o = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(o, 'horizontal overflow px').toBeLessThanOrEqual(1)
}

export async function axeViolations(page: Page) {
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze()
  return r.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s) e.g. ${v.nodes[0]?.target.join(' ')}`)
}

let tenant: Promise<Api> | undefined
let platform: Promise<Api> | undefined
export const tenantApi = () => (tenant ??= Api.signIn(s.accounts.userAdmin))
export const platformApi = () => (platform ??= Api.signIn(s.accounts.platformAdmin))

export type IsolatedTenant = { tenantId: string; tenantName: string; account: Account; api: Api }

/** Seeds an isolated tenant + UserAdmin via API so destructive settings/storage tests never collide with other slices. */
export async function seedIsolatedTenant(
  what: string,
  opts: { baseApiUrl?: string; platformAccount?: Account; limitBytes?: number } = {},
): Promise<IsolatedTenant> {
  const pApi = opts.baseApiUrl
    ? await Api.signIn(opts.platformAccount ?? s.accounts.platformAdmin, opts.baseApiUrl)
    : await platformApi()
  const tenantName = uniq(what)
  const tenantId = await ensureTenant(pApi, tenantName)
  const account: Account = { login: `${tenantName}.admin`, password: 'E2e-Passw0rd!2026' }
  await ensureTenantUser(pApi, [tenantId], account, { displayName: `Admin ${tenantName}` })
  if (opts.limitBytes !== undefined) {
    const planName = `${tenantName}-plan`
    const plan = await pApi.call('POST', '/api/platform/plans', {
      name: planName,
      storageLimitBytes: opts.limitBytes,
      unlimited: false,
      enabled: true,
      quotaPolicyReference: 'default',
    })
    await pApi.call('PUT', `/api/platform/tenants/${tenantId}/subscription`, { planId: plan.planId }, [200, 201, 204, 409])
  } else {
    await ensureUnlimitedPlan(pApi, tenantId, 'E2E Unlimited')
  }
  if (opts.baseApiUrl) await pApi.dispose()
  const api = await Api.signIn(account, opts.baseApiUrl ?? apiUrl)
  return { tenantId, tenantName, account, api }
}

/** Deletes tenant storage connections whose name starts with `e2e-s2-`. */
export async function cleanConnections(tenantId = s.tenantId, apiInstance?: Api) {
  const api = apiInstance ?? (await tenantApi())
  const list = await api.call('GET', `/api/tenants/${tenantId}/storage-connections`)
  for (const c of list.items.filter((i: { name: string }) => i.name.startsWith('e2e-s2-')))
    await api.call('DELETE', `/api/tenants/${tenantId}/storage-connections/${c.id}`, undefined, [200, 204, 404, 409])
}

export function sftpFingerprint(host = '192.168.122.1', port = 2222) {
  const scanned = execSync(`ssh-keyscan -p ${port} ${host} 2>/dev/null | grep ed25519 | ssh-keygen -lf - | awk '{print $2}'`, { shell: '/bin/bash' }).toString().trim()
  return scanned || 'SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU'
}

/** Mailpit & HTTPS webhook/SMS sink helpers for the isolated bps2 stack. */
export async function mailpitMessages(mailpitUrl = 'http://127.0.0.1:8027') {
  const ctx = await request.newContext({ baseURL: mailpitUrl })
  const list = await (await ctx.get('/api/v1/messages')).json()
  await ctx.dispose()
  return (list.messages ?? []) as { ID: string; Subject: string; To: { Address: string }[]; Snippet: string }[]
}

export async function mailpitMessageBody(id: string, mailpitUrl = 'http://127.0.0.1:8027'): Promise<{ Text: string; HTML: string }> {
  const ctx = await request.newContext({ baseURL: mailpitUrl })
  const msg = await (await ctx.get(`/api/v1/message/${id}`)).json()
  await ctx.dispose()
  return msg
}

export async function clearWebhookSink(sinkControlUrl = 'http://127.0.0.1:8028') {
  const ctx = await request.newContext({ baseURL: sinkControlUrl })
  await ctx.delete('/requests')
  await ctx.dispose()
}

export async function setWebhookFailMode(fail: boolean, sinkControlUrl = 'http://127.0.0.1:8028') {
  const ctx = await request.newContext({ baseURL: sinkControlUrl })
  await ctx.post(`/mode?fail=${fail}`)
  await ctx.dispose()
}

export async function webhookRequests(sinkControlUrl = 'http://127.0.0.1:8028') {
  const ctx = await request.newContext({ baseURL: sinkControlUrl })
  const data = await (await ctx.get('/requests')).json()
  await ctx.dispose()
  return data.items as { id: number; path: string; status: number; method: string; headers: Record<string, string>; body: string }[]
}
