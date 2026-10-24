import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, MemoryRouter, Route, RouterProvider, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ArtifactsPage } from '@/pages/artifacts/artifacts-page'
import { ArtifactDetailPage } from '@/pages/artifacts/artifact-detail-page'
import { apiRequest } from '@/lib/api/client'
import type { BackupArtifact, BackupReplica } from './types'
import { canCancelDeletion, deletionInProgress } from './format'
import type * as ArtifactApi from './api'

vi.mock('@/lib/api/client', () => ({
  apiRequest: vi.fn(),
  API_BASE_URL: '/api',
  tenantPath: (tenantId: string, path: string) => `tenants/${tenantId}/${path}`,
  ApiError: class ApiError extends Error {},
}))
const permissions = vi.hoisted(() => ({ canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: permissions.canAdmin, canOperate: true }) }))

vi.mock('./api', async (importOriginal) => ({
  ...await importOriginal<typeof ArtifactApi>(),
  getDownloadAvailability: vi.fn().mockResolvedValue({ available: false, reason: 'replica_unavailable', replicas: [], checksum: 'abc123', sizeBytes: 4096 }),
  listReplicaRepairs: vi.fn().mockResolvedValue([]),
}))
const replica: BackupReplica = {
  id: 'replica-internal', storageProvider: 'Local', storageConnectionId: 'connection-local', storageTarget: 'Backup storage', key: 'not-shown-key',
  sizeBytes: 4096, checksum: 'abc123', checksumAlgorithm: 'SHA256', verificationState: 'Verified', verificationMethod: 'ReadBack',
  createdAt: '2026-10-05T10:00:00Z', verifiedAt: '2026-10-05T10:01:00Z', status: 'Available', usable: true,
  deleteRequestedAt: null, deleteAfter: null, deleteReason: null, deletedAt: null, deleteAttempts: 0,
  deleteFailureCode: null, deleteNextAttemptAt: null, deletionExecutor: 'Agent', waitingForAgent: false,
}
function artifact(overrides: Partial<BackupArtifact> = {}): BackupArtifact {
  return {
    id: 'artifact-a', sizeBytes: 4096, sourceChecksum: 'source123', artifactChecksum: 'abc123', checksumAlgorithm: 'SHA256',
    createdAt: '2026-10-05T10:00:00Z', acceptedAt: '2026-10-05T10:02:00Z', retentionExpiresAt: '2026-11-05T10:02:00Z', retentionPinnedAt: null,
    state: 'Available', deleteAfter: null, deleteReason: null, runId: 'run-internal', jobId: 'job-internal', attemptId: 'attempt-internal',
    jobName: 'Günlük SQL', databaseName: 'Accounting', agentName: 'SQL Agent', replicas: [{ ...replica }], ...overrides,
  }
}
function pending(overrides: Partial<BackupReplica> = {}) {
  return artifact({ state: 'DeletePending', deleteAfter: '2099-10-06T10:00:00Z', replicas: [{ ...replica, status: 'DeletePending', deleteAfter: '2099-10-06T10:00:00Z', ...overrides }] })
}
function mount(page: 'list' | 'detail' = 'detail') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[page === 'list' ? '/tenants/tenant-a/artifacts' : '/tenants/tenant-a/artifacts/artifact-a']}><Routes>
    <Route path="/tenants/:tenantId/artifacts" element={<ArtifactsPage />} />
    <Route path="/tenants/:tenantId/artifacts/:id" element={<ArtifactDetailPage />} />
  </Routes></MemoryRouter></QueryClientProvider>)
  return client
}
const request = vi.mocked(apiRequest)
afterEach(() => { cleanup(); vi.clearAllMocks() })
beforeEach(() => { permissions.canAdmin = true })

describe('Backup dosyaları', () => {
  it('shows PostgreSQL archive facts without SQL Server physical backup metadata', async () => {
    request.mockResolvedValue(artifact({ backupMetadata: {
      engine: 'postgres', databaseName: 'sales', backupType: 'Full', backupStartedAt: null, backupFinishedAt: null,
      instance: { serverName: 'pg-host', version: '18.6', instanceName: null, edition: null, hostPlatform: null }, sqlDetails: null,
      verification: { state: 'Passed', method: 'pg_restore-full-read', startedAt: null, finishedAt: null },
      appliedOptions: { copyOnly: false, checksum: true, compression: true },
      postgreSqlDetails: { archiveFormat: 'custom', clientVersion: '18.6', encoding: 'UTF8', collation: 'C', dataChecksums: 'on', databaseSizeBytes: '16384', extensions: 'hstore' },
    } }))
    mount()
    fireEvent.click(await screen.findByRole('tab', { name: /üst veri/i }))
    expect(screen.getByRole('heading', { name: /PostgreSQL/ })).toBeInTheDocument()
    expect(screen.getByText('UTF8')).toBeInTheDocument()
    expect(screen.getByText('hstore')).toBeInTheDocument()
    expect(screen.queryByText("Kurtarma modeli")).not.toBeInTheDocument()
    expect(screen.queryByText("SQL Server sürüm türü")).not.toBeInTheDocument()
  })
  it('clears the selected tab and destructive confirmation when navigating to another Artifact', async () => {
    request.mockResolvedValue(artifact())
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const router = createMemoryRouter([{ path: '/tenants/:tenantId/artifacts/:id', element: <ArtifactDetailPage /> }], { initialEntries: ['/tenants/tenant-a/artifacts/artifact-a'] })
    render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>)
    fireEvent.click(await screen.findByRole('tab', { name: /üst veri/i }))
    fireEvent.click(screen.getByRole('button', { name: 'Silme isteği oluştur' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    await act(async () => { await router.navigate('/tenants/tenant-a/artifacts/artifact-b') })
    expect(await screen.findByRole('tab', { name: 'Özet' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(request.mock.calls.some(([, options]) => options?.method === 'DELETE')).toBe(false)
  })

  it('silmeden önce erişilebilir onay ister ve fiziksel silme yerine bekleme süresini başlatır', async () => {
    let current = artifact()
    request.mockImplementation(async (_path, options) => {
      if (options?.method === 'DELETE') current = pending()
      return current
    })
    const client = mount()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    fireEvent.click(await screen.findByRole('button', { name: 'Silme isteği oluştur' }))
    const dialog = await screen.findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Vazgeç' })).toHaveFocus())
    for (const heading of ["Ne olacak?", "Kimleri etkiler?", "Geri alınabilir mi?"]) expect(within(dialog).getByText(heading)).toBeInTheDocument()
    expect(request.mock.calls.some(([, options]) => options?.method === 'DELETE')).toBe(false)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Bekleme süresini başlat' }))
    await screen.findByText(/Silme isteği alındı/)
    expect(request).toHaveBeenCalledWith('tenants/tenant-a/backup-artifacts/artifact-a', { method: 'DELETE' })
    expect(screen.getByText(/Fiziksel silmeye kalan süre/)).toBeInTheDocument()
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'backup-artifacts'] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'dashboard'] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['tenant', 'tenant-a', 'backup-runs'] })
  })

  it('fiziksel silme başlamadan önce iptal eder', async () => {
    let current = pending()
    request.mockImplementation(async (_path, options) => {
      if (options?.method === 'POST') current = artifact()
      return current
    })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Silmeyi iptal et' }))
    await screen.findByText("Silme iptal edildi. Yedekleme dosyaları korunuyor.")
    expect(request).toHaveBeenCalledWith('tenants/tenant-a/backup-artifacts/artifact-a/cancel-delete', { method: 'POST' })
    expect(screen.queryByRole('button', { name: 'Silmeyi iptal et' })).not.toBeInTheDocument()
  })

  it('Agent bekleyişini açıkça gösterir ve henüz başlanmamış silmeyi iptal edilebilir tutar', async () => {
    request.mockResolvedValue(pending({ waitingForAgent: true, deleteFailureCode: 'agent_unavailable', deleteNextAttemptAt: '2099-10-06T10:05:00Z' }))
    mount()
    await screen.findByRole('button', { name: 'Silmeyi iptal et' })
    fireEvent.click(screen.getByRole('tab', { name: "Kopyalar" }))
    expect(await screen.findByText(/Fiziksel silme için Ajan bekleniyor/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Silmeyi iptal et' })).toBeInTheDocument()
    expect(screen.getByText('Sonraki silme denemesi')).toBeInTheDocument()
    expect(screen.getByText('Silme yürütücüsü')).toBeInTheDocument()
  })

  it('silme başlamış veya başarısız olmuşsa iptal sunmaz; hata içeriğini basmaz', async () => {
    request.mockResolvedValue(pending({ status: 'DeleteFailed', deleteAttempts: 1, deleteFailureCode: 'secret path /db/password=hidden' }))
    mount()
    await screen.findByText(/Fiziksel silme başladığı için/)
    expect(screen.queryByRole('button', { name: 'Silmeyi iptal et' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: "Kopyalar" }))
    expect(screen.getByRole('alert')).toHaveTextContent('Silme tamamlanamadı')
    expect(screen.queryByText(/password=hidden/)).not.toBeInTheDocument()
  })

  it('silinmiş kaydı fiziksel dosyasız geçmiş olarak tutar ve storage key veya iç kimlikleri göstermez', async () => {
    request.mockResolvedValue(artifact({ state: 'Deleted', replicas: [{ ...replica, status: 'Deleted', usable: false, deletedAt: '2026-10-06T10:00:00Z' }] }))
    mount()
    await screen.findByText(/Bu kayıt yalnızca yedekleme geçmişidir/)
    fireEvent.click(screen.getByRole('tab', { name: "Kopyalar" }))
    expect(await screen.findByText(/Fiziksel dosya kaldırıldı/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Silme isteği oluştur' })).not.toBeInTheDocument()
    expect(screen.queryByText('replica-internal')).not.toBeInTheDocument()
    expect(screen.queryByText('not-shown-key')).not.toBeInTheDocument()
    expect(screen.queryByText('attempt-internal')).not.toBeInTheDocument()
  })

  it('Viewer ve Operator için yıkıcı işlemleri gizler', async () => {
    permissions.canAdmin = false
    request.mockResolvedValue(artifact())
    mount()
    await screen.findByText(/yalnızca yöneticilere açıktır/)
    expect(screen.queryByRole('button', { name: 'Silme isteği oluştur' })).not.toBeInTheDocument()
  })

  it('kullanılabilir ve silinmiş dosyaları listeler; filtre sunucu sorgusuna ve tenant kapsamına gider', async () => {
    request.mockResolvedValue({ items: [artifact(), artifact({ id: 'artifact-b', databaseName: 'History', state: 'Deleted', replicas: [{ ...replica, status: 'Deleted', usable: false }] })], totalCount: 2, page: 1, pageSize: 25 })
    mount('list')
    await screen.findByRole('link', { name: 'Accounting' })
    expect(screen.getByRole('link', { name: 'History' })).toHaveAttribute('href', '/tenants/tenant-a/artifacts/artifact-b')
    expect(screen.getAllByRole('link', { name: 'Günlük SQL' })[0]).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs')
    expect(screen.getAllByRole('link', { name: 'Çalıştırmayı aç' })[0]).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-internal')
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'DeleteFailed' } })
    await waitFor(() => expect(request).toHaveBeenCalledWith(expect.stringContaining('state=DeleteFailed'), expect.objectContaining({ signal: expect.any(AbortSignal) })))
  })

  it('yükleniyor, boş liste ve sunucu hatasını gösterir', async () => {
    request.mockReturnValue(Promise.race<unknown>([]))
    mount('list')
    expect(screen.getByRole('status')).toHaveTextContent('Yükleniyor')
    cleanup()
    request.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 })
    mount('list')
    await screen.findByText(/Bu filtrelere uygun yedekleme dosyası yok/)
    cleanup()
    request.mockRejectedValue(new Error('private server detail'))
    mount('list')
    expect(await screen.findByRole('alert')).toHaveTextContent('Sunucuya ulaşılamadı')
    expect(screen.queryByText('private server detail')).not.toBeInTheDocument()
  })

  it('read-only kullanıcıya bekleyen silmeyi iptal etme yetkisi vermez', async () => {
    permissions.canAdmin = false
    request.mockResolvedValue(pending())
    mount()
    await screen.findByRole('region', { name: 'Silme süreci' })
    expect(screen.queryByRole('button', { name: 'Silmeyi iptal et' })).not.toBeInTheDocument()
  })

  it('bekleyen ve başarısız silmeyi takip eder, terminal geçmişte polling durur', () => {
    expect(deletionInProgress(pending())).toBe(true)
    expect(deletionInProgress(pending({ status: 'DeleteFailed', deleteAttempts: 1 }))).toBe(true)
    expect(deletionInProgress(artifact())).toBe(false)
    expect(deletionInProgress(artifact({ state: 'Deleted' }))).toBe(false)
    expect(canCancelDeletion(pending())).toBe(true)
    expect(canCancelDeletion(pending({ status: 'Deleting', deleteAttempts: 1 }))).toBe(false)
    expect(canCancelDeletion(pending({ deleteAttempts: 1 }))).toBe(false)
    expect(canCancelDeletion({ ...pending(), cancelDeleteAllowed: false })).toBe(false)
  })

  it('sayfalama sunucu sorgusuna gider ve filtre değişikliği ilk sayfaya döner', async () => {
    request.mockResolvedValue({ items: [artifact()], totalCount: 51, page: 1, pageSize: 25 })
    mount('list')
    fireEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(request).toHaveBeenCalledWith(expect.stringContaining('page=2&'), expect.objectContaining({ signal: expect.any(AbortSignal) })))
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Available' } })
    await waitFor(() => expect(request).toHaveBeenCalledWith(expect.stringContaining('page=1&pageSize=25&state=Available'), expect.objectContaining({ signal: expect.any(AbortSignal) })))
  })
})
