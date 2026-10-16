import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getTenantStorageQuota } from '@/features/storage/api'
import type { TenantQuota } from '@/features/storage/types'
import * as connectionApi from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { ConnectionList } from '@/features/storage-connections/connection-list'
import { ApiError } from '@/lib/api/client'
import { StorageLayout } from './storage-layout'
import { StorageUsageTab } from './storage-usage-tab'
import { StorageProvidersTab } from './storage-providers-tab'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', tenant: { name: 'Acme A.Ş.' }, canAdmin: true, canOperate: true }) }))
vi.mock('@/features/storage/api')
vi.mock('@/features/storage-connections/api')

const quota: TenantQuota = {
  limitBytes: 10_000_000_000, unlimited: false, usedBytes: 6_200_000_000, reservedBytes: 0, remainingBytes: 3_800_000_000, state: 'Normal', warningThreshold: 80, criticalThreshold: 95,
  accountingMode: 'LogicalArtifactBytes', enforcementMode: 'BlockNewBackups', subscriptionStatus: 'Active', newBackupsAllowed: true, denialCode: null,
  plan: { name: 'Standard 10 GB', isSystem: false, systemKey: null, subscriptionEndsAt: null }, includedStorageScopes: [{ kind: 'quotaScope', values: ['managed'] }], calculatedAt: '2026-10-08T12:00:00Z',
}

function mount(suffix = '') {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={[`/tenants/tenant-a/storage${suffix}`]}><Routes>
    <Route path="/tenants/tenant-a/storage" element={<StorageLayout />}>
      <Route index element={<ConnectionList />} /><Route path="usage" element={<StorageUsageTab />} /><Route path="providers" element={<StorageProvidersTab />} />
    </Route>
  </Routes></MemoryRouter></QueryClientProvider>)
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(getTenantStorageQuota).mockResolvedValue(quota)
  vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(connectionApi.getTenantProviders).mockResolvedValue({ items: catalog })
})

describe('Storage hub', () => {
  it('keeps destinations, quotas and provider capabilities accessible on separate subpages', async () => {
    mount()
    expect(await screen.findByRole('article', { name: "Managed Storage" })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Plan ve abonelik' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Kullanım ve kota' }))
    expect(await screen.findByRole('heading', { name: 'Plan ve abonelik' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Kullanım' })).toBeInTheDocument()
    expect(screen.getByText('Kota kapsamı: managed')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('link', { name: 'Sağlayıcılar' }))
    expect(await screen.findByRole('heading', { name: 'SFTP' })).toBeInTheDocument()
  })

  it('does not make destinations depend on a successful quota read', async () => {
    vi.mocked(getTenantStorageQuota).mockRejectedValue(new ApiError(500))
    mount()
    expect(await screen.findByRole('article', { name: "Local" })).toBeInTheDocument()
    expect(getTenantStorageQuota).not.toHaveBeenCalled()
  })

  it('rejects non-member quota reads without requesting destinations', async () => {
    vi.mocked(getTenantStorageQuota).mockRejectedValue(new ApiError(403))
    mount('/usage')
    expect(await screen.findByText("Bu çalışma alanının depolama bilgilerine erişiminiz yok")).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Depolama hedefleri' })).not.toBeInTheDocument()
    expect(connectionApi.getTenantConnections).not.toHaveBeenCalled()
  })
})
