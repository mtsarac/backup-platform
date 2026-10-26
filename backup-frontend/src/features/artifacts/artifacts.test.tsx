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
const permissions = vi.hoisted(() => ({ canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: permissions.canAdmin, canOperate: permissions.canOperate }) }))

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
    jobName: 'Günlük SQL', databaseName: 'Accounting', agentName: 'SQL Agent', backupType: 'Full', replicas: [{ ...replica }], ...overrides,
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
beforeEach(() => { permissions.canAdmin = true; permissions.canOperate = true })
const openDetails = () => fireEvent.click(screen.getByText('Teknik ayrıntılar', { selector: 'summary' }))

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
    await screen.findByText('Ne zaman silinecek?')
    expect(screen.getByText('UTF8')).not.toBeVisible()
    openDetails()
    expect(screen.getByRole('heading', { name: /PostgreSQL/ })).toBeVisible()
    expect(screen.getByText('UTF8')).toBeVisible()
    expect(screen.getByText('hstore')).toBeInTheDocument()
    expect(screen.queryByText("Kurtarma modeli")).not.toBeInTheDocument()
    expect(screen.queryByText("SQL Server sürüm türü")).not.toBeInTheDocument()
  })
  it('clears the opened details and destructive confirmation when navigating to another Artifact', async () => {
    request.mockResolvedValue(artifact())
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const router = createMemoryRouter([{ path: '/tenants/:tenantId/artifacts/:id', element: <ArtifactDetailPage /> }], { initialEntries: ['/tenants/tenant-a/artifacts/artifact-a'] })
    render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>)
    await screen.findByText('Ne zaman silinecek?')
    openDetails()
    fireEvent.click(screen.getByRole('button', { name: 'Silme isteği oluştur' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    await act(async () => { await router.navigate('/tenants/tenant-a/artifacts/artifact-b') })
    await screen.findByText('Ne zaman silinecek?')
    expect(screen.getByText('Teknik ayrıntılar', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(request.mock.calls.some(([, options]) => options?.method === 'DELETE')).toBe(false)
  })

  it('silme isteği için dosyanın adını yazdırır ve fiziksel silme yerine bekleme süresini başlatır', async () => {
    let current = artifact()
    request.mockImplementation(async (_path, options) => {
      if (options?.method === 'DELETE') current = pending()
      return current
    })
    const client = mount()
    const invalidate = vi.spyOn(client, 'invalidateQueries')
    fireEvent.click(await screen.findByRole('button', { name: 'Silme isteği oluştur' }))
    const dialog = await screen.findByRole('dialog')
    for (const heading of ["Ne olacak?", "Kimleri etkiler?", "Geri alınabilir mi?"]) expect(within(dialog).getByText(heading)).toBeInTheDocument()
    const confirm = within(dialog).getByRole('button', { name: 'Bekleme süresini başlat' })
    expect(confirm).toBeDisabled()
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'accounting' } })
    expect(confirm).toBeDisabled()
    fireEvent.click(confirm)
    expect(request.mock.calls.some(([, options]) => options?.method === 'DELETE')).toBe(false)
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Accounting' } })
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)
    await screen.findByText(/Silme isteği alındı/)
    expect(request).toHaveBeenCalledWith('tenants/tenant-a/backup-artifacts/artifact-a', { method: 'DELETE' })
    expect(screen.getByText(/Fiziksel silmeye kalan süre/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Silmeyi iptal et' })).toBeEnabled()
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
    expect(await screen.findByText(/Fiziksel silme için yedekleme bilgisayarı bekleniyor/)).toBeVisible()
    expect(screen.getByRole('button', { name: 'Silmeyi iptal et' })).toBeInTheDocument()
    expect(screen.getByText('Sonraki silme denemesi')).toBeInTheDocument()
    expect(screen.getByText('Silme yürütücüsü')).toBeInTheDocument()
  })

  it('silme başlamış veya başarısız olmuşsa iptal sunmaz; hata içeriğini basmaz', async () => {
    request.mockResolvedValue(pending({ status: 'DeleteFailed', deleteAttempts: 1, deleteFailureCode: 'secret path /db/password=hidden' }))
    mount()
    await screen.findByText(/Fiziksel silme başladığı için/)
    expect(screen.queryByRole('button', { name: 'Silmeyi iptal et' })).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Silme tamamlanamadı')
    expect(screen.queryByText(/password=hidden/)).not.toBeInTheDocument()
  })

  it('silinmiş kaydı fiziksel dosyasız geçmiş olarak tutar ve storage key veya iç kimlikleri göstermez', async () => {
    request.mockResolvedValue(artifact({ state: 'Deleted', replicas: [{ ...replica, status: 'Deleted', usable: false, deletedAt: '2026-10-06T10:00:00Z' }] }))
    mount()
    await screen.findByText(/Bu kayıt yalnızca yedekleme geçmişidir/)
    openDetails()
    expect(await screen.findByText(/Fiziksel dosya kaldırıldı/)).toBeVisible()
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

  it('önce dosyanın sağlam olup olmadığını, geri yüklenip yüklenemeyeceğini ve ne zaman silineceğini söyler', async () => {
    request.mockResolvedValue(artifact())
    mount()
    await screen.findByText('Dosya sağlam mı?')
    for (const question of ['Geri yüklenebilir mi?', 'Ne zaman silinecek?']) expect(screen.getByText(question)).toBeVisible()
    expect(screen.getByText('Sağlam')).toBeVisible()
    expect(screen.getByText('Geri yüklenebilir')).toBeVisible()
    expect(screen.getByRole('link', { name: 'Geri yükle' })).toHaveAttribute('href', '/tenants/tenant-a/artifacts/artifact-a/restore')
    // The checksums, replicas and metadata wait behind the collapsed details.
    for (const checksum of screen.getAllByText('abc123')) expect(checksum).not.toBeVisible()
    expect(screen.getByText('Teknik ayrıntılar', { selector: 'summary' }).closest('details')).not.toHaveAttribute('open')
    openDetails()
    expect(screen.getAllByText('abc123')[0]).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: Yedek dosyası sağlama toplamı' })).toBeVisible()
  })

  it('doğrulanmış kopya yoksa sağlam demez; silinmiş dosya geri yüklenemez', async () => {
    request.mockResolvedValue(artifact({ replicas: [{ ...replica, verificationState: 'Pending' }] }))
    mount()
    await screen.findByText('Dosya sağlam mı?')
    expect(screen.queryByText('Sağlam')).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Geri yükle' })).not.toBeInTheDocument()
    cleanup()
    request.mockResolvedValue(artifact({ state: 'Deleted', replicas: [{ ...replica, status: 'Deleted', usable: false, deletedAt: '2026-10-06T10:00:00Z' }] }))
    mount()
    await screen.findByText('Dosya sağlam mı?')
    expect(screen.getByText('Geri yüklenemez')).toBeVisible()
    expect(screen.queryByRole('link', { name: 'Geri yükle' })).not.toBeInTheDocument()
  })

  it('geri yükleme yetkisi olmayana Yöneticinize danışın der', async () => {
    permissions.canOperate = false
    request.mockResolvedValue(artifact())
    mount()
    await screen.findByText('Dosya sağlam mı?')
    expect(screen.queryByRole('link', { name: 'Geri yükle' })).not.toBeInTheDocument()
    expect(screen.getByText(/Yöneticinize danışın/)).toBeVisible()
  })

  it('saklama süresini ve etkin silme korumasını silme cevabında gösterir', async () => {
    request.mockResolvedValue(artifact({ retentionExpiresAt: '2099-01-01T00:00:00Z', retentionPinnedAt: null }))
    mount()
    await screen.findByText('Ne zaman silinecek?')
    expect(screen.getByText(/Saklama süresi dolunca silinir/)).toBeVisible()
    cleanup()
    request.mockResolvedValue(artifact({ retentionExpiresAt: '2026-11-05T10:02:00Z', retentionPinnedAt: '2026-10-07T00:00:00Z' }))
    mount()
    await screen.findByText('Ne zaman silinecek?')
    expect(screen.getByText(/Otomatik silme koruması etkin/)).toBeVisible()
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
