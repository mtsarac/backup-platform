import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { storageConnectionKeys, useCreatePlatformConnection, usePlatformConnectionOps, useTenantConnectionOps, useTenantConnections } from './hooks'
import { makeConnection } from './test-fixtures'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('./api')

function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const invalidate = vi.spyOn(client, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>
  const keys = () => invalidate.mock.calls.map(([filters]) => filters?.queryKey)
  return { client, wrapper, keys }
}

const connection = makeConnection({ tenantId: 'tenant-b' })

beforeEach(() => {
  vi.resetAllMocks()
})

describe('storage connection query keys', () => {
  it('uses the tenant and platform namespaces', () => {
    expect(storageConnectionKeys.tenantList('t1')).toEqual(['tenant', 't1', 'storage-connections', 'list'])
    expect(storageConnectionKeys.platformList({ page: 1 })).toEqual(['platform', 'storage-connections', 'list', { page: 1 }])
    expect(storageConnectionKeys.platformTenantList('t1')).toEqual(['platform', 'storage-connections', 'tenant', 't1'])
  })

  it('caches the list under the tenant list key', async () => {
    vi.mocked(api.getTenantConnections).mockResolvedValue({ items: [connection] })
    const { client, wrapper } = setup()
    const { result } = renderHook(() => useTenantConnections(), { wrapper })
    await waitFor(() => expect(result.current.data?.items).toHaveLength(1))
    expect(client.getQueryData(storageConnectionKeys.tenantList('tenant-a'))).toBeDefined()
  })
})

describe('tenant mutations', () => {
  it('refresh the connection list, the quota and the dashboard, on success and on failure', async () => {
    vi.mocked(api.setTenantConnectionEnabled).mockResolvedValueOnce(connection).mockRejectedValueOnce(new ApiError(409, { error: 'platform_managed' }))
    const { wrapper, keys } = setup()
    const { result } = renderHook(() => useTenantConnectionOps(), { wrapper })
    result.current.setEnabled.mutate({ connection, enabled: false })
    await waitFor(() => expect(keys()).toHaveLength(3))
    expect(keys()).toEqual([
      ['tenant', 'tenant-a', 'storage-connections', 'list'],
      ['tenant', 'tenant-a', 'storage-quota'],
      ['tenant', 'tenant-a', 'dashboard'],
    ])
    expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', false)
    result.current.setEnabled.mutate({ connection, enabled: true })
    await waitFor(() => expect(keys()).toHaveLength(6))
  })

  it('send secret values only as the request body and keep them out of every query key', async () => {
    vi.mocked(api.setTenantConnectionSecret).mockResolvedValue(connection)
    const { client, wrapper } = setup()
    const { result } = renderHook(() => useTenantConnectionOps(), { wrapper })
    result.current.setSecret.mutate({ connection, values: { password: 'hunter2-value' } })
    await waitFor(() => expect(api.setTenantConnectionSecret).toHaveBeenCalledWith('tenant-a', 'conn-local', { password: 'hunter2-value' }))
    expect(JSON.stringify(client.getQueryCache().getAll().map((entry) => entry.queryKey))).not.toContain('hunter2')
    result.current.setSecret.reset()
    await waitFor(() => expect(client.getMutationCache().getAll()).toHaveLength(0))
  })
})

describe('platform mutations', () => {
  it('refresh platform lists, quota views and audit, plus the affected tenant\'s own caches', async () => {
    vi.mocked(api.updatePlatformConnection).mockResolvedValue(connection)
    const { wrapper, keys } = setup()
    const { result } = renderHook(() => usePlatformConnectionOps(), { wrapper })
    result.current.update.mutate({ connection, input: { quotaScope: 'managed' } })
    await waitFor(() => expect(keys().length).toBeGreaterThanOrEqual(7))
    expect(api.updatePlatformConnection).toHaveBeenCalledWith('conn-local', { quotaScope: 'managed' })
    expect(keys()).toEqual(expect.arrayContaining([
      ['platform', 'storage-connections', 'list'],
      ['platform', 'storage-connections', 'tenant'],
      ['platform', 'storage'],
      ['platform', 'audit'],
      ['tenant', 'tenant-b', 'storage-connections', 'list'],
      ['tenant', 'tenant-b', 'storage-quota'],
      ['tenant', 'tenant-b', 'dashboard'],
    ]))
  })

  it('creating a managed target invalidates the chosen tenant\'s views', async () => {
    vi.mocked(api.createPlatformConnection).mockResolvedValue(connection)
    const { wrapper, keys } = setup()
    const { result } = renderHook(() => useCreatePlatformConnection(), { wrapper })
    result.current.mutate({ tenantId: 'tenant-c', input: { providerType: 'central', name: 'X', ownership: 'PlatformManaged' } })
    await waitFor(() => expect(keys()).toContainEqual(['tenant', 'tenant-c', 'storage-quota']))
    expect(api.createPlatformConnection).toHaveBeenCalledWith('tenant-c', { providerType: 'central', name: 'X', ownership: 'PlatformManaged' })
  })
})
