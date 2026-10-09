import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BackupJobWizardPage } from './backup-job-wizard-page'
import { getBackupJob, getOccurrences, saveBackupJob, setJobEnabled } from '@/features/backup-jobs/api'
import type { BackupJob } from '@/features/backup-jobs/types'
import { ApiError } from '@/lib/api/client'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged, makeProvider } from '@/features/storage-connections/test-fixtures'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([{ agentId: 'agent-a', hostName: 'SQL Agent', operationalStatus: 'Online' }]) }))
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/backup-jobs/api', () => ({ saveBackupJob: vi.fn(), getBackupJob: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), startJobRun: vi.fn(), setJobEnabled: vi.fn() }))
function page(cache: QueryClient, route = '/tenants/tenant-a/backup-jobs/new') {
  return <QueryClientProvider client={cache}><MemoryRouter initialEntries={[route]}><Routes><Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} /><Route path="/tenants/:tenantId/backup-jobs/:id/edit" element={<BackupJobWizardPage />} /></Routes></MemoryRouter></QueryClientProvider>
}
function renderPage(route?: string) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return { ...render(page(cache, route)), cache }
}
async function submit() {
  await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
}
function makeJob(storageConnectionId: string | null = 'conn-local'): BackupJob {
  return {
    id: 'job-a', backupType: 'Full', createdAt: '2026-10-01T08:00:00Z', name: 'ERP backup', agentId: 'agent-a', enabled: true, overlapPolicy: 'Skip', schedule: null, nextOccurrenceAt: null, lastRun: null,
    configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storageProvider: 'local', storageConnectionId, storagePrefix: '', storageExecutionLocation: 'Agent', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred' },
    retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 },
    retention: { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 },
  }
}
beforeEach(() => {
  vi.clearAllMocks()
  access.tenantId = 'tenant-a'; access.canOperate = true; access.canAdmin = true
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
  vi.mocked(getBackupJob).mockResolvedValue(makeJob())
  vi.mocked(getOccurrences).mockResolvedValue([])
})
describe('iş formu', () => {
  it('Viewer rolüne düzenleme formunu sunmaz', () => {
    access.canOperate = false
    renderPage()
    expect(screen.getByRole('alert')).toHaveTextContent('yetkiniz yok')
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
  })
  it('cron alanını yerel olarak yeniden doğrulamaz ve sunucu alan hatasını gösterir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'cronExpression', code: 'invalid_cron' }))
    renderPage()
    fireEvent.change(screen.getByLabelText('Cron ifadesi'), { target: { value: 'invalid cron' } })
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].schedule?.cronExpression).toBe('invalid cron')
    await waitFor(() => expect(document.getElementById('cronExpression-error')).toHaveTextContent(/.+/))
    expect(screen.getByLabelText('Cron ifadesi')).toHaveAttribute('aria-invalid', 'true')
  })
  it('geçersiz zamanlama kaydı alan hatasını gösterir ve başka istek göndermez', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { error: 'invalid_schedule', field: 'cronExpression', code: 'invalid_cron' }))
    renderPage()
    fireEvent.change(screen.getByLabelText('Cron ifadesi'), { target: { value: 'x' } })
    await submit()
    await waitFor(() => expect(document.getElementById('cronExpression-error')).toHaveTextContent(/.+/))
    expect(screen.getByLabelText('Cron ifadesi')).toHaveValue('x')
    expect(saveBackupJob).toHaveBeenCalledTimes(1)
    expect(setJobEnabled).not.toHaveBeenCalled()
    expect(screen.queryByText(/uygulanmış olabilir/)).toBeNull()
  })
  it('saklama süresi hatasını sunucudan alır', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'retention.keepForSeconds', code: 'invalid_request' }))
    renderPage()
    fireEvent.change(screen.getByLabelText('KeepFor — saklama süresi (saniye)'), { target: { value: '-1' } })
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention?.keepForSeconds).toBe(-1)
    await waitFor(() => expect(document.getElementById('retention.keepForSeconds-error')).toHaveTextContent(/.+/))
  })
  it('Operator saklama politikasını değiştiremez veya gönderemez', async () => {
    access.canAdmin = false
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    expect(screen.getByLabelText('KeepFor — saklama süresi (saniye)')).toBeDisabled()
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention).toBeUndefined()
  })
  it('gönderilen yapılandırmada storageConnectionId varsayılan olarak null gider', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.storageConnectionId).toBeNull()
  })
  it('varsayılan hedef devre dışıysa yeni iş kaydında varsayılan seçimi engeli aşamaz', async () => {
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection({ enabled: false }), makeManaged()] })
    renderPage()
    expect(await screen.findByText(/Yeni iş veya yeni hedef/)).toHaveTextContent('varsayılan seçimi bu engeli aşmaz')
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    expect(within(screen.getByLabelText('Depolama bağlantısı')).getByRole('option', { name: /Varsayılan/ })).toBeDisabled()
    fireEvent.submit(screen.getByRole('button', { name: 'Kaydet' }).closest('form')!)
    expect(saveBackupJob).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText('Storage Provider'), { target: { value: 'central' } })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({ storageProvider: 'central', storageConnectionId: null, storageExecutionLocation: 'Backend' })
  })
  it('tüm bağlantıları sahiplik ve kullanılabilirlikle listeler; kilitli, kapalı, planlanan ve desteklenmeyen hedefleri seçtirmez', async () => {
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [
      makeConnection(), makeManaged(),
      makeConnection({ id: 'locked', name: 'Kilitli', administrativelyDisabled: true, isDefault: false }),
      makeConnection({ id: 'off', name: 'Kapalı', enabled: false, isDefault: false }),
      makeConnection({ id: 'planned', name: 'Planlanan', providerType: 'sftp', providerDisplayName: 'SFTP', availability: 'Planned', isDefault: false }),
      makeConnection({ id: 'unknown', name: 'Bilinmeyen', providerType: 'unsupported', isDefault: false }),
      makeConnection({ id: 'ownership', name: 'Uyumsuz sahiplik', providerType: 'central', isDefault: false }),
    ] })
    renderPage()
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    expect(within(select).getByRole('option', { name: /Local · Local \(Agent diski\) · Tenant yönetimli · Kullanılabilir/ })).toBeEnabled()
    expect(within(select).getByRole('option', { name: /Managed Storage · Managed Storage \(Central\) · Platform yönetimli · Kullanılabilir/ })).toBeEnabled()
    for (const name of [/Kilitli.*Platform kilidi/, /Kapalı.*Devre dışı/, /Planlanan.*kullanılamıyor/, /Bilinmeyen.*Desteklenmeyen/, /Uyumsuz sahiplik.*Desteklenmeyen/]) {
      expect(within(select).getByRole('option', { name })).toBeDisabled()
    }
    fireEvent.change(select, { target: { value: 'locked' } })
    expect(select).toHaveValue('')
  })
  it('seçilen gerçek kimliği, sağlayıcıyı ve uyumlu yürütme konumunu gönderir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    fireEvent.change(select, { target: { value: 'conn-central' } })
    expect(screen.getByLabelText('Storage Provider')).toHaveValue('central')
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({ storageProvider: 'central', storageConnectionId: 'conn-central', storageExecutionLocation: 'Backend' })
    expect(vi.mocked(saveBackupJob).mock.calls[0][0]).toBe('tenant-a')
  })
  it.each(['disabled', 'locked', 'missing', 'unsupported'] as const)('düzenlemede mevcut %s hedef kimliğini korur ve değiştirmeden kaydedebilir', async state => {
    const connection = makeConnection({ enabled: state !== 'disabled', administrativelyDisabled: state === 'locked' })
    vi.mocked(getTenantConnections).mockResolvedValue({ items: state === 'missing' ? [makeManaged()] : [connection, makeManaged()] })
    if (state === 'unsupported') vi.mocked(getTenantProviders).mockResolvedValue({ items: [makeProvider({ supportedOwnerships: [] }), catalog[1]] })
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage('/tenants/tenant-a/backup-jobs/job-a/edit')
    const select = await screen.findByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    expect(select).toHaveValue('conn-local')
    expect(within(select).getByRole('option', { selected: true })).toBeDisabled()
    expect(await screen.findByText(/Mevcut hedef kimliği korunarak/)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Changed name' } })
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0]).toEqual(['tenant-a', expect.objectContaining({ name: 'Changed name', configuration: expect.objectContaining({ storageConnectionId: 'conn-local', storageProvider: 'local' }) }), 'job-a'])
  })
  it('başka sağlayıcıya geçip geri gelse bile mevcut açık kimliği otomatik olarak geri atamaz', async () => {
    renderPage('/tenants/tenant-a/backup-jobs/job-a/edit')
    const select = await screen.findByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Storage Provider'), { target: { value: 'central' } })
    expect(select).toHaveValue('')
    fireEvent.change(screen.getByLabelText('Storage Provider'), { target: { value: 'local' } })
    expect(select).toHaveValue('')
  })
  it.each(['storage_connection_disabled', 'storage_connection_unavailable'])('sonradan %s hatasında API durumunu yeniler; kimliği korur ve sahte başarı sunmaz', async code => {
    vi.mocked(getTenantConnections).mockResolvedValueOnce({ items: [makeConnection(), makeManaged()] }).mockResolvedValue({ items: [makeConnection(), makeManaged({ administrativelyDisabled: true })] })
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(409, { code, field: 'configuration.storageConnectionId' }))
    const { cache } = renderPage()
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    fireEvent.change(select, { target: { value: 'conn-central' } })
    await submit()
    await waitFor(() => expect(getTenantConnections).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled())
    expect(select).toHaveValue('conn-central')
    expect(within(select).getByRole('option', { selected: true })).toBeDisabled()
    expect(document.getElementById('configuration.storageConnectionId-error')).toHaveTextContent('Hedef depolama bağlantısı')
    expect(cache.getQueryData(['tenant', 'tenant-a', 'storage-connections', 'list'])).toEqual({ items: [makeConnection(), makeManaged({ administrativelyDisabled: true })] })
    expect(screen.getByRole('heading', { name: 'Yeni yedekleme işi' })).toBeInTheDocument()
    expect(saveBackupJob).toHaveBeenCalledTimes(1)
  })
  it('liste isteği başarısızsa varsayılan sahte başarıya dönüşmez ve tekrar yüklenebilir', async () => {
    vi.mocked(getTenantConnections).mockRejectedValueOnce(new ApiError(503)).mockResolvedValue({ items: [makeConnection()] })
    renderPage()
    expect(await screen.findByRole('button', { name: 'Bağlantıları yeniden yükle' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Bağlantıları yeniden yükle' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('tenant değişiminde yeni form anahtarı eski seçimi ve yerel alanları temizler', async () => {
    const { cache, rerender } = renderPage()
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Depolama bağlantısı'), { target: { value: 'conn-central' } })
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Tenant A private' } })
    access.tenantId = 'tenant-b'
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection({ tenantId: 'tenant-b', id: 'b-local', name: 'B Local' })] })
    vi.mocked(getTenantProviders).mockResolvedValue({ items: [catalog[0]] })
    rerender(page(cache))
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    expect(screen.getByLabelText('Depolama bağlantısı')).toHaveValue('')
    expect(screen.getByLabelText('İş adı')).toHaveValue('')
    expect(screen.queryByRole('option', { name: /Managed Storage/ })).not.toBeInTheDocument()
    expect(getTenantConnections).toHaveBeenLastCalledWith('tenant-b', expect.any(AbortSignal))
  })
})
