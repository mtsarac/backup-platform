import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetAntiforgery } from '@/lib/api/client'
import * as api from './api'

let requests: Request[] = []
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  requests = []
  resetAntiforgery()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init)
    requests.push(request)
    if (new URL(request.url).pathname === '/api/auth/antiforgery') return json({ requestToken: 'token', headerName: 'X-CSRF-TOKEN' })
    return json({ items: [] })
  }))
})
afterEach(() => vi.unstubAllGlobals())

const last = () => requests.at(-1)!
const path = () => new URL(last().url).pathname

describe('tenant storage connection API', () => {
  it('reads providers and connections under the tenant route', async () => {
    await api.getTenantProviders('tenant-a')
    expect(path()).toBe('/api/tenants/tenant-a/storage-providers')
    await api.getTenantConnections('tenant-a')
    expect(path()).toBe('/api/tenants/tenant-a/storage-connections')
    expect(last().method).toBe('GET')
  })

  it('creates, renames and deletes with the right verbs and bodies', async () => {
    await api.createTenantConnection('tenant-a', { providerType: 'fake-s3', name: 'Archive', configuration: { bucket: 'b' }, secret: { accessKey: 'k' } })
    expect(last().method).toBe('POST')
    expect(path()).toBe('/api/tenants/tenant-a/storage-connections')
    expect(await last().json()).toEqual({ providerType: 'fake-s3', name: 'Archive', configuration: { bucket: 'b' }, secret: { accessKey: 'k' } })
    await api.updateTenantConnection('tenant-a', 'conn/1', { name: 'Yeni' })
    expect(last().method).toBe('PUT')
    expect(path()).toBe('/api/tenants/tenant-a/storage-connections/conn%2F1')
    expect(await last().json()).toEqual({ name: 'Yeni' })
    await api.deleteTenantConnection('tenant-a', 'conn-1')
    expect(last().method).toBe('DELETE')
    expect(last().headers.get('X-CSRF-TOKEN')).toBe('token')
  })

  it('sends secret values only in the body, never in the URL', async () => {
    await api.setTenantConnectionSecret('tenant-a', 'conn-1', { password: 'p@ss/word?x=1' })
    expect(last().method).toBe('PUT')
    expect(last().url).not.toContain('p@ss')
    expect(path()).toBe('/api/tenants/tenant-a/storage-connections/conn-1/secret')
    expect(await last().json()).toEqual({ values: { password: 'p@ss/word?x=1' } })
    await api.clearTenantConnectionSecret('tenant-a', 'conn-1')
    expect(last().method).toBe('DELETE')
    expect(path()).toBe('/api/tenants/tenant-a/storage-connections/conn-1/secret')
  })

  it('tests, enables and disables with POST', async () => {
    await api.testTenantConnection('tenant-a', 'conn-1')
    expect([last().method, path()]).toEqual(['POST', '/api/tenants/tenant-a/storage-connections/conn-1/test'])
    await api.setTenantConnectionEnabled('tenant-a', 'conn-1', false)
    expect([last().method, path()]).toEqual(['POST', '/api/tenants/tenant-a/storage-connections/conn-1/disable'])
    await api.setTenantConnectionEnabled('tenant-a', 'conn-1', true)
    expect(path()).toBe('/api/tenants/tenant-a/storage-connections/conn-1/enable')
  })

  it('gets capacity and probes capacity with correct endpoints and verbs', async () => {
    await api.getTenantConnectionCapacity('tenant-a', 'conn-1')
    expect([last().method, path()]).toEqual(['GET', '/api/tenants/tenant-a/storage-connections/conn-1/capacity'])
    await api.probeTenantConnectionCapacity('tenant-a', 'conn-1')
    expect([last().method, path()]).toEqual(['POST', '/api/tenants/tenant-a/storage-connections/conn-1/probe'])
  })
})

describe('platform storage connection API', () => {
  it('sends only set filters of the paged list', async () => {
    await api.getPlatformConnections({ ownership: 'PlatformManaged', providerType: '', tenantId: undefined, enabled: false, search: 'acme', page: 2, pageSize: 25 })
    expect(last().url).toMatch(/\/api\/platform\/storage-connections\?ownership=PlatformManaged&enabled=false&search=acme&page=2&pageSize=25$/)
  })

  it('reads providers and a tenant\'s connections', async () => {
    await api.getPlatformProviders()
    expect(path()).toBe('/api/platform/storage-providers')
    await api.getPlatformTenantConnections('tenant/a')
    expect(path()).toBe('/api/platform/tenants/tenant%2Fa/storage-connections')
  })

  it('creates a managed target for a tenant', async () => {
    await api.createPlatformConnection('tenant-a', { providerType: 'central', name: 'Arşiv', ownership: 'PlatformManaged', quotaScope: 'managed' })
    expect([last().method, path()]).toEqual(['POST', '/api/platform/tenants/tenant-a/storage-connections'])
    expect(await last().json()).toEqual({ providerType: 'central', name: 'Arşiv', ownership: 'PlatformManaged', quotaScope: 'managed' })
  })

  it('updates, rotates secret, tests, toggles and deletes by connection id', async () => {
    await api.updatePlatformConnection('conn-1', { quotaScope: 'tenant' })
    expect([last().method, path()]).toEqual(['PUT', '/api/platform/storage-connections/conn-1'])
    expect(await last().json()).toEqual({ quotaScope: 'tenant' })
    await api.setPlatformConnectionSecret('conn-1', { password: 'x' })
    expect(path()).toBe('/api/platform/storage-connections/conn-1/secret')
    expect(await last().json()).toEqual({ values: { password: 'x' } })
    await api.clearPlatformConnectionSecret('conn-1')
    expect(last().method).toBe('DELETE')
    await api.testPlatformConnection('conn-1')
    expect([last().method, path()]).toEqual(['POST', '/api/platform/storage-connections/conn-1/test'])
    await api.setPlatformConnectionEnabled('conn-1', false)
    expect(path()).toBe('/api/platform/storage-connections/conn-1/disable')
    await api.deletePlatformConnection('conn-1')
    expect([last().method, path()]).toEqual(['DELETE', '/api/platform/storage-connections/conn-1'])
  })
})
