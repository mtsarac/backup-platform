// Idempotent seed through the real HTTP API: platform admin -> tenant, Owner/Admin/Operator users, unlimited plan, agent enrollment. Writes e2e/.state.json.
import { request } from '@playwright/test'
import { writeFileSync } from 'node:fs'
import type { Account, State } from './state'

const host = process.env.PUBLIC_HOST ?? '192.168.122.1'
const baseUrl = process.env.E2E_BASE_URL ?? `https://${host}:5173`
const apiUrl = process.env.E2E_API_URL ?? `https://${host}:5252`
const pw = process.env.E2E_PASSWORD ?? 'E2e-Passw0rd!2026'
const admin: Account = { login: process.env.BOOTSTRAP_ADMIN_LOGIN ?? 'admin', password: process.env.BOOTSTRAP_ADMIN_PASSWORD ?? 'PlatformAdmin123!' }
const TENANT = 'E2E Tenant'
const PLAN = 'E2E Unlimited'

const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
const token = async (c = ctx) => {
  const t = await (await c.get('/api/auth/antiforgery')).json()
  return { [t.headerName]: t.requestToken }
}
const call = async (method: string, url: string, data?: unknown, ok: number[] = [200, 201, 204], c = ctx) => {
  const r = await c.fetch(url, { method, data, headers: await token(c) })
  if (!ok.includes(r.status())) throw new Error(`${method} ${url} -> ${r.status()} ${(await r.text()).slice(0, 300)}`)
  return r.status() === 204 || !(r.headers()['content-type'] ?? '').includes('json') ? null : r.json()
}

await call('POST', '/api/auth/login', { loginIdentifier: admin.login, password: admin.password })

const tenants = await call('GET', `/api/platform/tenants?search=${encodeURIComponent(TENANT)}&pageSize=100`)
const tenant = tenants.items.find((t: any) => t.name === TENANT) ?? (await call('POST', '/api/platform/tenants', { name: TENANT }))
const tenantId = tenant.tenantId ?? tenant.id

const accounts = {} as State['accounts']
accounts.platformAdmin = admin
// Roles are PlatformAdmin and UserAdmin only; a UserAdmin works inside the tenants it is a member of.
const login = 'e2e.useradmin'
const users = await call('GET', `/api/platform/users?search=${login}&pageSize=100`)
let user = users.items.find((u: any) => u.username === login)
if (!user) {
  // A new account must change its initial password at first sign-in; do it here so the seeded password is the usable one.
  const initial = `${pw}-initial`
  user = await call('POST', '/api/platform/users', { username: login, email: `${login}@example.test`, displayName: 'E2E User Admin', password: initial, role: 'UserAdmin', tenantIds: [tenantId] })
  const own = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  await call('POST', '/api/auth/login', { loginIdentifier: login, password: initial }, [200], own)
  await call('POST', '/api/auth/change-password', { currentPassword: initial, newPassword: pw }, [200, 204], own)
  await own.dispose()
}
await call('POST', `/api/platform/tenants/${tenantId}/memberships`, { userId: user.userId }, [200, 201, 409])
accounts.userAdmin = { login, password: pw }

const plans = await call('GET', '/api/platform/plans?pageSize=100')
const plan = plans.items.find((p: any) => p.name === PLAN) ?? (await call('POST', '/api/platform/plans', { name: PLAN, storageLimitBytes: 1_000_000_000_000, unlimited: false, enabled: true, quotaPolicyReference: 'default' }))
await call('PUT', `/api/platform/tenants/${tenantId}/subscription`, { planId: plan.planId }, [200, 201, 204, 409])
await call('PUT', `/api/platform/tenants/${tenantId}/subscription/storage-limit-override`, { kind: 'unlimited' })
await call('PUT', `/api/platform/tenants/${tenantId}/agent-enrollment`, { enabled: true })

const state: State = { baseUrl, apiUrl, tenantId, tenantName: TENANT, accounts }
writeFileSync(new URL('./.state.json', import.meta.url), JSON.stringify(state, null, 2), { mode: 0o600 })
console.log(`seeded tenant ${tenantId}; accounts: ${Object.values(accounts).map((a) => a.login).join(', ')}`)
await ctx.dispose()
