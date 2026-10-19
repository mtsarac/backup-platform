import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
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
const next = (times = 1) => { for (let i = 0; i < times; i++) fireEvent.click(screen.getByRole('button', { name: 'İleri' })) }
const goTo = (step: string) => fireEvent.click(screen.getByRole('button', { name: `Adıma git: ${step}` }))
/** Fills the required fields of the create flow and stops on step index `target` (0 Genel … 5 Özet). */
async function reach(target: number) {
  await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
  fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'ERP backup' } })
  fireEvent.change(screen.getByLabelText("Ajan"), { target: { value: 'agent-a' } })
  if (target > 0) next()
  if (target > 1) {
    fireEvent.change(screen.getByLabelText("SQL Server ana bilgisayarı / veritabanı sunucusu"), { target: { value: 'sql' } })
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: 'ERP' } })
    next()
  }
  if (target > 2) next(target - 2)
}
function makeJob(storageConnectionId: string | null = 'conn-local'): BackupJob {
  return {
    id: 'job-a', backupType: 'Full', createdAt: '2026-10-01T08:00:00Z', name: 'ERP backup', agentId: 'agent-a', enabled: true, overlapPolicy: 'Skip', schedule: null, nextOccurrenceAt: null, lastRun: null,
    configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId, storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off' },
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
  it('does not resubmit the obsolete copy-only switch when editing a PostgreSQL job', async () => {
    const job = makeJob()
    vi.mocked(getBackupJob).mockResolvedValue({ ...job, configuration: {
      ...job.configuration, engine: 'postgres', integratedSecurity: false, databaseCredentialReference: 'pg-local', port: 5432, copyOnly: true,
    } })
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage('/tenants/tenant-a/backup-jobs/job-a/edit')
    await screen.findByRole('button', { name: 'Kaydet' })
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({ engine: 'postgres', copyOnly: false, databaseCredentialReference: 'pg-local' })
  })
  it('switches engine defaults and sends a password-authenticated PostgreSQL Full job with byte-safe names', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(1)
    fireEvent.click(screen.getByRole('radio', { name: 'PostgreSQL' }))
    expect(screen.getByLabelText(/Bağlantı noktası/)).toHaveValue(5432)
    expect(screen.queryByLabelText(/Windows kimlik/)).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText("PostgreSQL ana bilgisayarı"), { target: { value: 'pg-host' } })
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: '数'.repeat(22) } })
    fireEvent.change(screen.getByLabelText("Veritabanı kullanıcı adı"), { target: { value: 'pg_user' } })
    fireEvent.change(screen.getByLabelText("Veritabanı parolası"), { target: { value: 'pg-secret' } })
    next()
    expect(screen.getByLabelText("Veritabanı")).toHaveAttribute('aria-invalid', 'true')
    expect(saveBackupJob).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: '数'.repeat(21) } })
    next(3)
    expect(screen.queryByRole('checkbox', { name: /Bağımsız yedekleme/ })).not.toBeInTheDocument()
    expect(screen.getByLabelText(/data_checksums/)).toHaveValue('Preferred')
    next()
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({
      engine: 'postgres', port: 5432, integratedSecurity: false, databaseLogin: { userName: 'pg_user', password: 'pg-secret' },
      database: '数'.repeat(21), copyOnly: false, checksumPolicy: 'Preferred',
    })
  })
  it('Viewer rolüne düzenleme formunu sunmaz', () => {
    access.canOperate = false
    renderPage()
    expect(screen.getByRole('alert')).toHaveTextContent('yetkiniz yok')
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
  })
  it('cron alanını yerel olarak yeniden doğrulamaz ve sunucu alan hatasını gösterir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'cronExpression', code: 'invalid_cron' }))
    renderPage()
    await reach(3)
    fireEvent.change(screen.getByLabelText('Cron ifadesi'), { target: { value: 'invalid cron' } })
    next(2)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].schedule?.cronExpression).toBe('invalid cron')
    fireEvent.click(await screen.findByRole('button', { name: 'Hatalı alana git: Zamanlama' }))
    await waitFor(() => expect(document.getElementById('cronExpression-error')).toHaveTextContent(/.+/))
    expect(screen.getByLabelText('Cron ifadesi')).toHaveAttribute('aria-invalid', 'true')
  })
  it('SQL kimlik doğrulamasında kullanıcı adı ve parola girilmeden Kaynak adımından ilerlemez', async () => {
    renderPage()
    await reach(1)
    fireEvent.click(screen.getByLabelText("Windows kimlik doğrulaması (tümleşik güvenlik)"))
    fireEvent.change(screen.getByLabelText("SQL Server ana bilgisayarı / veritabanı sunucusu"), { target: { value: 'sql' } })
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: 'ERP' } })
    next()
    expect(screen.getByLabelText("Veritabanı kullanıcı adı")).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText("Veritabanı parolası")).toHaveAttribute('aria-invalid', 'true')
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('instance adı içeren host ile portu birlikte kabul etmez', async () => {
    renderPage()
    await reach(1)
    fireEvent.change(screen.getByLabelText("SQL Server ana bilgisayarı / veritabanı sunucusu"), { target: { value: 'srv\\SQL2022' } })
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: 'ERP' } })
    fireEvent.change(screen.getByLabelText(/Bağlantı noktası/), { target: { value: '1433' } })
    next()
    expect(screen.getByLabelText(/Bağlantı noktası/)).toHaveAttribute('aria-invalid', 'true')
  })

  it('geçersiz zamanlama kaydı alan hatasını gösterir ve başka istek göndermez', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { error: 'invalid_schedule', field: 'cronExpression', code: 'invalid_cron' }))
    renderPage()
    await reach(3)
    fireEvent.change(screen.getByLabelText('Cron ifadesi'), { target: { value: 'x' } })
    next(2)
    await submit()
    fireEvent.click(await screen.findByRole('button', { name: 'Hatalı alana git: Zamanlama' }))
    await waitFor(() => expect(document.getElementById('cronExpression-error')).toHaveTextContent(/.+/))
    expect(screen.getByLabelText('Cron ifadesi')).toHaveValue('x')
    expect(saveBackupJob).toHaveBeenCalledTimes(1)
    expect(setJobEnabled).not.toHaveBeenCalled()
    expect(screen.queryByText(/uygulanmış olabilir/)).toBeNull()
  })
  it('saklama süresi hatasını sunucudan alır', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { field: 'retention.keepForSeconds', code: 'invalid_request' }))
    renderPage()
    await reach(4)
    fireEvent.change(screen.getByLabelText("Saklama süresi (saniye)"), { target: { value: '-1' } })
    next()
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention?.keepForSeconds).toBe(-1)
    fireEvent.click(await screen.findByRole('button', { name: 'Hatalı alana git: Saklama & seçenekler' }))
    await waitFor(() => expect(document.getElementById('retention.keepForSeconds-error')).toHaveTextContent(/.+/))
  })
  it('adım doğrulaması eksik zorunlu alanlarda İleri düğmesini engeller', async () => {
    renderPage()
    await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
    next()
    expect(screen.getByLabelText('İş adı')).toHaveAttribute('aria-invalid', 'true')
    expect(document.getElementById('name-error')).toHaveTextContent(/.+/)
    expect(document.getElementById('agentId-error')).toHaveTextContent(/.+/)
    expect(screen.queryByLabelText("SQL Server ana bilgisayarı / veritabanı sunucusu")).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'ERP backup' } })
    fireEvent.change(screen.getByLabelText("Ajan"), { target: { value: 'agent-a' } })
    next()
    next()
    expect(document.getElementById('configuration.host-error')).toHaveTextContent(/.+/)
    expect(document.getElementById('configuration.database-error')).toHaveTextContent(/.+/)
    expect(screen.queryByLabelText('Depolama bağlantısı')).not.toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('ara adımda Enter sonraki adıma ilerler; kaydetme isteği göndermez', async () => {
    renderPage()
    await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'ERP backup' } })
    fireEvent.change(screen.getByLabelText("Ajan"), { target: { value: 'agent-a' } })
    await userEvent.click(screen.getByLabelText('İş adı'))
    await userEvent.keyboard('{Enter}')
    expect(screen.getByLabelText("SQL Server ana bilgisayarı / veritabanı sunucusu")).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('Operator saklama politikasını değiştiremez veya gönderemez', async () => {
    access.canAdmin = false
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(4)
    expect(screen.getByLabelText("Saklama süresi (saniye)")).toBeDisabled()
    next()
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention).toBeUndefined()
  })
  it('yeni iş tek bir birincil local hedefle ve varsayılan bağlantı (null) ile gönderilir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(5)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    const configuration = vi.mocked(saveBackupJob).mock.calls[0][1].configuration
    expect(configuration.destinations).toEqual([{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }])
    expect(configuration.requiredVerifiedReplicaCount).toBe(1)
    expect(configuration).not.toHaveProperty('storageProvider')
    expect(configuration).not.toHaveProperty('storageConnectionId')
    expect(configuration).not.toHaveProperty('storageExecutionLocation')
  })
  it('varsayılan hedef devre dışıysa yeni iş kaydında varsayılan seçimi engeli aşamaz', async () => {
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection({ enabled: false }), makeManaged()] })
    renderPage()
    await reach(2)
    expect(await screen.findByText(/Yeni iş veya yeni hedef/)).toHaveTextContent('varsayılan seçimi bu engeli aşmaz')
    expect(within(screen.getByLabelText('Depolama bağlantısı')).getByRole('option', { name: /Varsayılan/ })).toBeDisabled()
    next(3)
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('button', { name: 'Kaydet' }).closest('form')!)
    expect(saveBackupJob).not.toHaveBeenCalled()
    goTo('Depolama')
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })
    next(3)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.destinations).toEqual([{ storageProvider: 'central', storageConnectionId: null, storageExecutionLocation: 'Backend', isPrimary: true }])
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
    await reach(2)
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    expect(within(select).getByRole('option', { name: /Local · Local \((?:Agent|Ajan) diski\) · (?:Çalışma Alanı|Çalışma alanı) yönetimli · Kullanılabilir/ })).toBeEnabled()
    expect(within(select).getByRole('option', { name: /(?:Managed Depolama|Managed Storage) · (?:Managed Depolama|Managed Storage) \(Central\) · Platform yönetimli · Kullanılabilir/ })).toBeEnabled()
    for (const name of [/Kilitli.*Platform kilidi/, /Kapalı.*Devre dışı/, /Planlanan.*kullanılamıyor/, /Bilinmeyen.*Desteklenmeyen/, /Uyumsuz sahiplik.*Desteklenmeyen/]) {
      expect(within(select).getByRole('option', { name })).toBeDisabled()
    }
    fireEvent.change(select, { target: { value: 'locked' } })
    expect(select).toHaveValue('')
  })
  it('seçilen gerçek kimliği, sağlayıcıyı ve uyumlu yürütme konumunu gönderir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(2)
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    fireEvent.change(select, { target: { value: 'conn-central' } })
    expect(screen.getByLabelText("Depolama Sağlayıcısı")).toHaveValue('central')
    next(3)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.destinations).toEqual([{ storageProvider: 'central', storageConnectionId: 'conn-central', storageExecutionLocation: 'Backend', isPrimary: true }])
    expect(vi.mocked(saveBackupJob).mock.calls[0][0]).toBe('tenant-a')
  })
  it.each(['disabled', 'locked', 'missing', 'unsupported'] as const)('düzenlemede mevcut %s hedef kimliğini korur ve değiştirmeden kaydedebilir', async state => {
    const connection = makeConnection({ enabled: state !== 'disabled', administrativelyDisabled: state === 'locked' })
    vi.mocked(getTenantConnections).mockResolvedValue({ items: state === 'missing' ? [makeManaged()] : [connection, makeManaged()] })
    if (state === 'unsupported') vi.mocked(getTenantProviders).mockResolvedValue({ items: [makeProvider({ supportedOwnerships: [] }), catalog[1]] })
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage('/tenants/tenant-a/backup-jobs/job-a/edit')
    fireEvent.click(await screen.findByRole('button', { name: 'Adıma git: Depolama' }))
    const select = await screen.findByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    expect(select).toHaveValue('conn-local')
    expect(within(select).getByRole('option', { selected: true })).toBeDisabled()
    expect(await screen.findByText(/Mevcut hedef kimliği korunarak/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Özete dön' }))
    goTo('Genel')
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Changed name' } })
    fireEvent.click(screen.getByRole('button', { name: 'Özete dön' }))
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0]).toEqual(['tenant-a', expect.objectContaining({ name: 'Changed name', configuration: expect.objectContaining({ destinations: [{ storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: true }] }) }), 'job-a'])
  })
  it('başka sağlayıcıya geçip geri gelse bile mevcut açık kimliği otomatik olarak geri atamaz', async () => {
    renderPage('/tenants/tenant-a/backup-jobs/job-a/edit')
    fireEvent.click(await screen.findByRole('button', { name: 'Adıma git: Depolama' }))
    const select = await screen.findByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })
    expect(select).toHaveValue('')
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'local' } })
    expect(select).toHaveValue('')
  })
  it.each(['storage_connection_disabled', 'storage_connection_unavailable'])('sonradan %s hatasında API durumunu yeniler; kimliği korur ve sahte başarı sunmaz', async code => {
    vi.mocked(getTenantConnections).mockResolvedValueOnce({ items: [makeConnection(), makeManaged()] }).mockResolvedValue({ items: [makeConnection(), makeManaged({ administrativelyDisabled: true })] })
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(409, { code, field: 'configuration.destinations[0].storageConnectionId' }))
    const { cache } = renderPage()
    await reach(2)
    const first = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(first).toBeEnabled())
    fireEvent.change(first, { target: { value: 'conn-central' } })
    next(3)
    await submit()
    await waitFor(() => expect(getTenantConnections).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled())
    goTo('Depolama')
    const select = screen.getByLabelText('Depolama bağlantısı')
    expect(select).toHaveValue('conn-central')
    expect(within(select).getByRole('option', { selected: true })).toBeDisabled()
    expect(document.getElementById('primary-storage-connection-error')).toHaveTextContent('Hedef depolama bağlantısı')
    expect(cache.getQueryData(['tenant', 'tenant-a', 'storage-connections', 'list'])).toEqual({ items: [makeConnection(), makeManaged({ administrativelyDisabled: true })] })
    expect(screen.getByRole('heading', { name: 'Yeni yedekleme işi' })).toBeInTheDocument()
    expect(saveBackupJob).toHaveBeenCalledTimes(1)
  })
  it('liste isteği başarısızsa varsayılan sahte başarıya dönüşmez ve tekrar yüklenebilir', async () => {
    vi.mocked(getTenantConnections).mockRejectedValueOnce(new ApiError(503)).mockResolvedValue({ items: [makeConnection()] })
    renderPage()
    await reach(2)
    expect(await screen.findByRole('button', { name: 'Bağlantıları yeniden yükle' })).toBeInTheDocument()
    next(3)
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Adıma git: Depolama (hedef sorunu)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Bağlantıları yeniden yükle' }))
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    next(3)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('tenant değişiminde yeni form anahtarı eski seçimi ve yerel alanları temizler', async () => {
    const { cache, rerender } = renderPage()
    await reach(2)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Depolama bağlantısı'), { target: { value: 'conn-central' } })
    access.tenantId = 'tenant-b'
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection({ tenantId: 'tenant-b', id: 'b-local', name: 'B Local' })] })
    vi.mocked(getTenantProviders).mockResolvedValue({ items: [catalog[0]] })
    rerender(page(cache))
    expect(screen.getByLabelText('İş adı')).toHaveValue('')
    await reach(2)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    expect(screen.getByLabelText('Depolama bağlantısı')).toHaveValue('')
    expect(screen.queryByRole('option', { name: /Yönetilen Depolama/ })).not.toBeInTheDocument()
    expect(getTenantConnections).toHaveBeenLastCalledWith('tenant-b', expect.any(AbortSignal))
  })
})
