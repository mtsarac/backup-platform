// Idempotent seed through the real HTTP API (see api.ts). Writes e2e/.state.json.
import { writeFileSync } from 'node:fs'
import { Api, ensureTenant, ensureTenantUser, ensureUnlimitedPlan, enableAgentEnrollment } from './api'
import type { Account, State } from './state'

const host = process.env.PUBLIC_HOST ?? '192.168.122.1'
const baseUrl = process.env.E2E_BASE_URL ?? `https://${host}:5173`
const apiUrl = process.env.E2E_API_URL ?? baseUrl
const pw = process.env.E2E_PASSWORD ?? 'E2e-Passw0rd!2026'
const admin: Account = { login: process.env.BOOTSTRAP_ADMIN_LOGIN ?? 'admin', password: process.env.BOOTSTRAP_ADMIN_PASSWORD ?? 'PlatformAdmin123!' }
const TENANT = 'E2E Tenant'

const platform = await Api.signIn(admin)
const tenantId = await ensureTenant(platform, TENANT)
const userAdmin: Account = { login: 'e2e.useradmin', password: pw }
const userId = await ensureTenantUser(platform, [tenantId], userAdmin, { displayName: 'E2E User Admin' })
await platform.call('POST', `/api/platform/tenants/${tenantId}/memberships`, { userId }, [200, 201, 409])
await ensureUnlimitedPlan(platform, tenantId, 'E2E Unlimited')
await enableAgentEnrollment(platform, tenantId)

const state: State = { baseUrl, apiUrl, tenantId, tenantName: TENANT, accounts: { platformAdmin: admin, userAdmin } }
writeFileSync(new URL(`./${process.env.E2E_STATE_FILE ?? '.state.json'}`, import.meta.url), JSON.stringify(state, null, 2), { mode: 0o600 })
console.log(`seeded tenant ${tenantId}; accounts: ${admin.login}, ${userAdmin.login}`)
await platform.dispose()
