import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getTenantStorageQuota } from '@/features/storage/api'
import type { TenantQuota } from '@/features/storage/types'
import * as connectionApi from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { StoragePage } from './storage-page'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Acme A.Ş.' }, canAdmin: true, canOperate: true }) }))
vi.mock('@/features/storage/api')
vi.mock('@/features/storage-connections/api')

const quota: TenantQuota = {
  limitBytes: 10_000_000_000, unlimited: false, usedBytes: 6_200_000_000, reservedBytes: 0, remainingBytes: 3_800_000_000, state: 'Normal', warningThreshold: 80, criticalThreshold: 95,
  accountingMode: 'LogicalArtifactBytes', enforcementMode: 'BlockNewBackups', subscriptionStatus: 'Active', newBackupsAllowed: true, denialCode: null,
  plan: { name: 'Standard 10 GB', isSystem: false, systemKey: null, subscriptionEndsAt: null }, includedStorageScopes: [{ kind: 'quotaScope', values: ['managed'] }], calculatedAt: '2026-10-08T12:00:00Z',
}

function mount() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter><StoragePage /></MemoryRouter></QueryClientProvider>)
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(getTenantStorageQuota).mockResolvedValue(quota)
  vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(connectionApi.getTenantProviders).mockResolvedValue({ items: catalog })
})

describe('tenant Storage page', () => {
  it('keeps the quota sections and adds the destinations and the provider capability panel', async () => {
    mount()
    expect(await screen.findByRole('heading', { name: 'Plan ve abonelik' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Kullanım' })).toBeInTheDocument()
    expect(screen.getByText('Kota kapsamı: managed')).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: 'Depolama hedefleri' })).toBeInTheDocument()
    expect(await screen.findByRole('article', { name: 'Managed Storage' })).toBeInTheDocument()
    expect(screen.getByRole('article', { name: 'Local' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Sağlayıcılar ve yetenekler' })).toBeInTheDocument()
  })

  it('still renders the destinations when only the quota read fails', async () => {
    vi.mocked(getTenantStorageQuota).mockRejectedValue(new ApiError(500))
    mount()
    expect(await screen.findByRole('article', { name: 'Local' })).toBeInTheDocument()
  })

  it('shows neither quota nor destinations to a non-member (403)', async () => {
    vi.mocked(getTenantStorageQuota).mockRejectedValue(new ApiError(403))
    mount()
    expect(await screen.findByText("Bu tenant'ın depolama bilgisine erişiminiz yok")).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Depolama hedefleri' })).not.toBeInTheDocument()
    expect(connectionApi.getTenantConnections).not.toHaveBeenCalled()
  })
})
