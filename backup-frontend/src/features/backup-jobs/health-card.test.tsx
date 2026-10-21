import { act, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { apiRequest } from '@/lib/api/client'
import { BackupHealthCard } from './health-card'
import type { BackupJobHealth } from './health'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/lib/api/client', async original => ({ ...await original(), apiRequest: vi.fn() }))
const health: BackupJobHealth = {
  jobId: 'job-a', jobName: 'Nightly', score: 35, state: 'Critical', evaluatedAt: '2026-10-21T12:00:00Z',
  factors: {
    freshness: { score: 20, maxScore: 40, status: 'Degraded', reason: 'backup_stale' },
    replication: { score: 15, maxScore: 30, status: 'Degraded', reason: 'replicas_missing' },
    restoreVerification: { score: 0, maxScore: 20, status: 'Failed', reason: 'restore_failed' },
    storageCapacity: { score: 0, maxScore: 10, status: 'Unavailable', reason: 'capacity_unavailable' },
  },
}
function card(client: QueryClient) {
  return <QueryClientProvider client={client}><BackupHealthCard jobId="job-a" /></QueryClientProvider>
}
beforeEach(() => { vi.resetAllMocks(); access.tenantId = 'tenant-a' })

describe('backup job health card', () => {
  it('shows server score, textual state and every reduced factor without blocking operations', async () => {
    vi.mocked(apiRequest).mockResolvedValue(health)
    render(card(new QueryClient({ defaultOptions: { queries: { retry: false } } })))
    const region = screen.getByRole('region', { name: 'Yedekleme sağlığı' })
    expect(await within(region).findByText('35 / 100')).toBeInTheDocument()
    expect(within(region).getByText('Kritik')).toBeInTheDocument()
    expect(within(region).getByText('Son başarılı yedek, planlanan periyodu aştı.')).toBeInTheDocument()
    expect(within(region).getByText('Doğrulanmış kopya sayısı, gerekli kopya sayısından az.')).toBeInTheDocument()
    expect(within(region).getByText('Son izole geri yükleme testi başarısız oldu.')).toBeInTheDocument()
    expect(within(region).getByText('Güncel ve ölçülebilir depolama kapasitesi bilgisi yok.')).toBeInTheDocument()
    expect(within(region).getByText('Sağlık durumu yalnızca bilgilendirir; yedeklemeleri engellemez.')).toBeInTheDocument()
    expect(apiRequest).toHaveBeenCalledWith('/api/tenants/tenant-a/backup-jobs/job-a/health', expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })

  it('does not invent a score on request failure', async () => {
    vi.mocked(apiRequest).mockRejectedValue(new Error('offline'))
    render(card(new QueryClient({ defaultOptions: { queries: { retry: false } } })))
    expect(await screen.findByText('Sağlık bilgisi alınamadı.')).toBeInTheDocument()
    expect(screen.queryByText(/\/ 100/)).not.toBeInTheDocument()
  })

  it('never shows the previous tenant health when tenant selection changes', async () => {
    const other = Promise.withResolvers<BackupJobHealth>()
    vi.mocked(apiRequest).mockResolvedValueOnce(health).mockImplementationOnce(() => other.promise)
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const view = render(card(client))
    await screen.findByText('35 / 100')
    access.tenantId = 'tenant-b'
    view.rerender(card(client))
    expect(screen.queryByText('35 / 100')).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Sağlık bilgisi yükleniyor…')
    await waitFor(() => expect(apiRequest).toHaveBeenLastCalledWith('/api/tenants/tenant-b/backup-jobs/job-a/health', expect.anything()))
    await act(async () => other.resolve({ ...health, score: 90, state: 'Healthy' }))
    expect(await screen.findByText('90 / 100')).toBeInTheDocument()
    expect(screen.queryByText('35 / 100')).not.toBeInTheDocument()
  })
})
