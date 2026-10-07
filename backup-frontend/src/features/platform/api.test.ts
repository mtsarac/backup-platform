import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetAntiforgery } from '@/lib/api/client'
import { addMembership, getAudit, getPlatformAgent, getPlatformAgents, getTenants, setTenantEnrollment } from './api'

let requests: Request[] = []
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  requests = []
  resetAntiforgery()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init)
    requests.push(request)
    if (new URL(request.url).pathname === '/api/auth/antiforgery') return json({ requestToken: 'token', headerName: 'X-CSRF-TOKEN' })
    return json({ items: [], totalCount: 0, page: 1, pageSize: 25 })
  }))
})
afterEach(() => vi.unstubAllGlobals())

const last = () => requests.at(-1)!

describe('platform API paths', () => {
  it('sends only non-empty list parameters under /api/platform', async () => {
    await getTenants({ search: 'acme corp', page: 2, pageSize: 25 })
    expect(last().url).toMatch(/\/api\/platform\/tenants\?search=acme\+corp&page=2&pageSize=25$/)
    await getPlatformAgents({ tenantId: '', connectivity: 'online', status: undefined, search: '', page: 1, pageSize: 25 })
    expect(last().url).toMatch(/\/api\/platform\/agents\?connectivity=online&page=1&pageSize=25$/)
    await getAudit({ page: 1 })
    expect(last().url).toMatch(/\/api\/platform\/audit\?page=1$/)
  })

  it('encodes ids and sends the CSRF header on mutations', async () => {
    await getPlatformAgent('tenant/a', 'agent b')
    expect(new URL(last().url).pathname).toBe('/api/platform/tenants/tenant%2Fa/agents/agent%20b')
    await setTenantEnrollment('tenant-a', false)
    expect(last().method).toBe('PUT')
    expect(new URL(last().url).pathname).toBe('/api/platform/tenants/tenant-a/agent-enrollment')
    expect(last().headers.get('X-CSRF-TOKEN')).toBe('token')
    expect(await last().json()).toEqual({ enabled: false })
    await addMembership('tenant-a', 'user-1', 'Viewer')
    expect(last().method).toBe('POST')
    expect(await last().json()).toEqual({ userId: 'user-1', role: 'Viewer' })
  })
})
