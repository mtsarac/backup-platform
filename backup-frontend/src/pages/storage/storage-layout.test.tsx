import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as storageApi from '@/features/storage/api'
import * as connectionApi from '@/features/storage-connections/api'
import * as artifactApi from '@/features/artifacts/api'
import type { TenantQuota } from '@/features/storage/types'
import type { BackupArtifact } from '@/features/artifacts/types'
import { makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { formatBytes } from '@/lib/format'
import { StorageLayout } from './storage-layout'
import { ConnectionList } from '@/features/storage-connections/connection-list'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
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
  it('shows one failure with one retry when every request fails, and retries all of them', async () => {
    vi.mocked(storageApi.getTenantStorageQuota).mockRejectedValue(new Error('down'))
    vi.mocked(connectionApi.getTenantConnections).mockRejectedValue(new Error('down'))
    vi.mocked(artifactApi.listArtifacts).mockRejectedValue(new Error('down'))
    mount()
    expect(await screen.findByText('Depolama bilgileri alınamadı')).toBeInTheDocument()
    expect(screen.getAllByRole('alert')).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: 'Yeniden dene' })).toHaveLength(1)
    vi.mocked(storageApi.getTenantStorageQuota).mockClear()
    fireEvent.click(screen.getByRole('button', { name: 'Yeniden dene' }))
    await waitFor(() => expect(storageApi.getTenantStorageQuota).toHaveBeenCalledTimes(1))
  })
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
    expect(screen.getByRole('heading', { name: 'Silinemeyen yedek dosyaları' })).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(artifactApi.listArtifacts).toHaveBeenCalledWith('tenant-a', expect.objectContaining({ state: 'DeleteFailed' }), expect.any(AbortSignal))
  })
  it('pages failed artifacts through the server', async () => {
    vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [{ id: 'a', jobName: 'Muhasebe', replicas: [] } as unknown as BackupArtifact], totalCount: 30, page: 1, pageSize: 25 })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(artifactApi.listArtifacts).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ page: 2, state: 'DeleteFailed' }), expect.any(AbortSignal)))
  })
  describe('failed artifacts list', () => {
    const failed = (id: string, jobName: string, extra: Partial<BackupArtifact> = {}) => ({ id, runId: `run-${id}`, jobName, databaseName: 'Accounting', sizeBytes: 2048, createdAt: '2026-10-05T10:00:00Z', replicas: [], ...extra }) as unknown as BackupArtifact
    const lastQuery = () => vi.mocked(artifactApi.listArtifacts).mock.lastCall![1]
    beforeEach(() => {
      vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [failed('a', 'Muhasebe'), failed('b', 'Satış', { databaseName: 'Sales' })], totalCount: 2, page: 1, pageSize: 25 })
    })

    it('starts newest first without sort parameters and shows name, database, size and date', async () => {
      mount()
      const table = await screen.findByRole('table', { name: 'Silinemeyen yedek dosyaları' })
      expect(within(table).getByRole('link', { name: 'Satış' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-b')
      expect(within(table).getByText('Sales')).toBeInTheDocument()
      expect(within(table).getAllByText(formatBytes(2048))).toHaveLength(2)
      expect(lastQuery()).toEqual({ page: 1, pageSize: 25, state: 'DeleteFailed' })
    })

    it('searches on the server, back on page 1, and clears the search', async () => {
      mount()
      fireEvent.change(await screen.findByRole('searchbox', { name: 'Yedek dosyası ara' }), { target: { value: 'muh' } })
      await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, state: 'DeleteFailed', search: 'muh' }))
      vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 })
      fireEvent.change(screen.getByRole('searchbox', { name: 'Yedek dosyası ara' }), { target: { value: 'zzz' } })
      expect(await screen.findByText('Aramanızla eşleşen yedek dosyası yok.')).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
      await waitFor(() => expect(lastQuery()).toEqual({ page: 1, pageSize: 25, state: 'DeleteFailed' }))
    })

    it('sorts by the shown columns through the server and keeps the page when paging', async () => {
      vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [failed('a', 'Muhasebe')], totalCount: 30, page: 1, pageSize: 25 })
      mount()
      fireEvent.click(await screen.findByRole('button', { name: 'Boyut' }))
      await waitFor(() => expect(lastQuery()).toMatchObject({ sort: 'size', dir: 'desc' }))
      fireEvent.click(screen.getByRole('button', { name: 'Veritabanı' }))
      await waitFor(() => expect(lastQuery()).toMatchObject({ sort: 'databaseName', dir: 'asc' }))
      fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
      await waitFor(() => expect(lastQuery()).toMatchObject({ sort: 'databaseName', dir: 'asc', page: 2 }))
    })

    it('keeps the list visible while a search matches nothing, and hides it when no deletion failed at all', async () => {
      vi.mocked(artifactApi.listArtifacts).mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 })
      mount()
      await screen.findByText(/Toplam boyut/)
      expect(screen.queryByRole('heading', { name: 'Silinemeyen yedek dosyaları' })).not.toBeInTheDocument()
    })
  })
  it('names the system defaults "Yerel disk" and "Merkezi depolama" and badges only the local default', async () => {
    vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
    mount()
    expect(await screen.findByRole('article', { name: 'Yerel disk' })).toBeInTheDocument()
    expect(screen.getByRole('article', { name: 'Merkezi depolama' })).toBeInTheDocument()
    expect(screen.queryByText('Local')).not.toBeInTheDocument()
    expect(screen.queryByText('Managed Storage')).not.toBeInTheDocument()
    expect(screen.getAllByText('Varsayılan')).toHaveLength(1)
  })
  it('keeps the name of a default the owner renamed', async () => {
    vi.mocked(connectionApi.getTenantConnections).mockResolvedValue({ items: [makeConnection({ name: 'Ofis diski' })] })
    mount()
    expect(await screen.findByRole('article', { name: 'Ofis diski' })).toBeInTheDocument()
    expect(screen.getByText('Varsayılan')).toBeInTheDocument()
  })
})
