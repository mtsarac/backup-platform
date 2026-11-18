import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as storageApi from '@/features/storage/api'
import * as connectionApi from '@/features/storage-connections/api'
import * as artifactApi from '@/features/artifacts/api'
import type { TenantQuota } from '@/features/storage/types'
import type { BackupArtifact } from '@/features/artifacts/types'
import { makeConnection } from '@/features/storage-connections/test-fixtures'
import { StorageLayout } from './storage-layout'
import { ConnectionList } from '@/features/storage-connections/connection-list'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }) }))
vi.mock('@/features/storage/api')
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/artifacts/api')

const quota = { limitBytes: 10_000_000_000, usedBytes: 6_200_000_000, reservedBytes: 0, remainingBytes: 3_800_000_000, state: 'Normal', subscriptionStatus: 'Active' } as TenantQuota
function mount() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={['/tenants/tenant-a/storage']}><Routes><Route path="/tenants/tenant-a/storage" element={<StorageLayout />}><Route index element={<ConnectionList />} /></Route></Routes></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(storageApi.getTenantStorageQuota).mockResolvedValue(quota)
  vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [] })
  vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 })
})
describe('Depolama', () => {
  it('shows quota before connections and an actionable empty state', async () => {
    mount()
    expect(await screen.findByText(/Toplam boyut: 6,2 GB/)).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toBeInTheDocument()
    expect(await screen.findByText(/Henüz depolama bağlantısı yok/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Depolama bağlantısı ekle' })).toHaveAttribute('href', '/tenants/tenant-a/storage/connections/new')
    expect(screen.queryByText('Silinemeyen yedek dosyaları')).not.toBeInTheDocument()
  })
  it('hides the meter without a quota and links failed deletions to their attempts', async () => {
    vi.mocked(storageApi.getTenantStorageQuota).mockResolvedValue({ ...quota, limitBytes: null })
    vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [makeConnection()] })
    vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [{ id: 'a', runId: 'run-1', jobName: 'Muhasebe', replicas: [] } as unknown as BackupArtifact], totalCount: 1, page: 1, pageSize: 25 })
    mount()
    expect(await screen.findByRole('link', { name: 'Muhasebe' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-1')
    expect(screen.getByText('Silinemeyen yedek dosyaları')).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(artifactApi.listArtifacts).toHaveBeenCalledWith('tenant-a', expect.objectContaining({ state: 'DeleteFailed' }), expect.any(AbortSignal))
  })
})
