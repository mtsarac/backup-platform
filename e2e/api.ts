// Reusable API seeding helpers for specs and seed.ts. Rule: test DATA goes in through the API here; the flow under test is driven through the UI.
import { request, type APIRequestContext } from '@playwright/test'
import type { Account } from './state'

const host = process.env.PUBLIC_HOST ?? '192.168.122.1'
export const apiUrl = process.env.E2E_API_URL ?? `https://${host}:5252`

export class Api {
  private constructor(private ctx: APIRequestContext, readonly baseUrl = apiUrl) {}

  /** Signed-in API session for an account (cookie + antiforgery handled). */
  static async signIn(account: Account, base = apiUrl): Promise<Api> {
    const api = new Api(await request.newContext({ baseURL: base, ignoreHTTPSErrors: true }), base)
    await api.call('POST', '/api/auth/login', { loginIdentifier: account.login, password: account.password })
    return api
  }

  async call(method: string, url: string, data?: unknown, ok: number[] = [200, 201, 204], headers: Record<string, string> = {}): Promise<any> {
    const t = await (await this.ctx.get('/api/auth/antiforgery')).json()
    const r = await this.ctx.fetch(url, { method, data, headers: { ...headers, [t.headerName]: t.requestToken } })
    if (!ok.includes(r.status())) throw new Error(`${method} ${url} -> ${r.status()} ${(await r.text()).slice(0, 300)}`)
    return r.status() === 204 || !(r.headers()['content-type'] ?? '').includes('json') ? null : r.json()
  }

  dispose() { return this.ctx.dispose() }
}

const find = (page: any, key: string, value: string) => page.items.find((i: any) => i[key] === value)

export async function ensureTenant(platform: Api, name: string): Promise<string> {
  const page = await platform.call('GET', `/api/platform/tenants?search=${encodeURIComponent(name)}&pageSize=100`)
  const t = find(page, 'name', name) ?? (await platform.call('POST', '/api/platform/tenants', { name }))
  return t.tenantId ?? t.id
}

/** Creates a UserAdmin in the tenants and completes the forced first-login password change, so `account` is immediately usable. Pass `keepForcedChange` to leave that for a UI test. */
export async function ensureTenantUser(platform: Api, tenantIds: string[], account: Account, opts: { displayName?: string; keepForcedChange?: boolean } = {}): Promise<string> {
  const users = await platform.call('GET', `/api/platform/users?search=${encodeURIComponent(account.login)}&pageSize=100`)
  const existing = find(users, 'username', account.login)
  if (existing) return existing.userId
  const initial = opts.keepForcedChange ? account.password : `${account.password}-initial`
  const user = await platform.call('POST', '/api/platform/users', { username: account.login, email: `${account.login}@example.test`, displayName: opts.displayName ?? account.login, password: initial, role: 'UserAdmin', tenantIds })
  if (!opts.keepForcedChange) {
    const own = await Api.signIn({ login: account.login, password: initial }, platform.baseUrl)
    await own.call('POST', '/api/auth/change-password', { currentPassword: initial, newPassword: account.password }, [200, 204])
    await own.dispose()
  }
  return user.userId
}

export async function ensureUnlimitedPlan(platform: Api, tenantId: string, planName: string) {
  const plans = await platform.call('GET', `/api/platform/plans?search=${encodeURIComponent(planName)}&pageSize=100`)
  const plan = find(plans, 'name', planName) ?? (await platform.call('POST', '/api/platform/plans', { name: planName, storageLimitBytes: 1_000_000_000_000, unlimited: false, enabled: true, quotaPolicyReference: 'default' }))
  await platform.call('PUT', `/api/platform/tenants/${tenantId}/subscription`, { planId: plan.planId }, [200, 201, 204, 409])
  await platform.call('PUT', `/api/platform/tenants/${tenantId}/subscription/storage-limit-override`, { kind: 'unlimited' })
}

export const enableAgentEnrollment = (platform: Api, tenantId: string) => platform.call('PUT', `/api/platform/tenants/${tenantId}/agent-enrollment`, { enabled: true })

/** One-time agent enrollment token, created as a tenant user (what the UI "Add computer" dialog does). */
export async function createEnrollmentToken(tenantUser: Api, tenantId: string): Promise<string> {
  return (await tenantUser.call('POST', `/api/tenants/${tenantId}/agent-enrollment-tokens`, {})).token
}
