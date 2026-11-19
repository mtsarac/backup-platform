import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, createRoutesFromElements, Link, Outlet, Route, RouterProvider, useLocation, useNavigate } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
import { BackupJobWizardPage } from './backup-job-wizard-page'
import { saveBackupJob } from '@/features/backup-jobs/api'
import { ApiError } from '@/lib/api/client'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { getAgents } from '@/features/agents/api'
import { changeLanguage } from '@/lib/i18n/runtime'
import { setViewport } from '@/test/viewport'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([{ agentId: 'agent-a', hostName: 'SQL Agent', operationalStatus: 'Online' }]) }))
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/backup-jobs/api', () => ({ saveBackupJob: vi.fn(), getBackupJobs: vi.fn(), startJobRun: vi.fn() }))
/** Stands in for the storage wizard: it hands back exactly what the real one does. */
function StorageWizardStub() {
  const navigate = useNavigate()
  const { returnTo, returnState } = useLocation().state as { returnTo: string; returnState: unknown }
  return <><button onClick={() => void navigate(returnTo, { state: { returnState, storageConnectionId: 'conn-central' } })}>Bu depolamayı kullan</button><button onClick={() => void navigate(returnTo, { state: { returnState } })}>Vazgeç</button></>
}
function makeRouter(route = '/tenants/tenant-a/backup-jobs/new', initialEntries = [route]) {
  return createMemoryRouter(createRoutesFromElements(<Route element={<><nav><Link to="/tenants/tenant-a/agents">Computers</Link></nav><Outlet /></>}>
    <Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} />
    <Route path="/tenants/:tenantId/backup-jobs" element={<p>Job listesi</p>} />
    <Route path="/tenants/:tenantId/storage/connections/new" element={<StorageWizardStub />} />
    <Route path="/tenants/:tenantId/agents" element={<p>Computer list</p>} />
  </Route>), { initialEntries })
}
function page(cache: QueryClient, route = '/tenants/tenant-a/backup-jobs/new') {
  return <QueryClientProvider client={cache}><RouterProvider router={makeRouter(route)} /></QueryClientProvider>
}
function renderPage(route?: string) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return { ...render(page(cache, route)), cache }
}
function renderPageWithRouter() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const router = makeRouter()
  return { ...render(<QueryClientProvider client={cache}><RouterProvider router={router} /></QueryClientProvider>), router }
}
async function submit() {
  await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
}
const next = (times = 1) => { for (let i = 0; i < times; i++) fireEvent.click(screen.getByRole('button', { name: 'İleri' })) }
const goTo = (row: string) => fireEvent.click(screen.getByRole('button', { name: `Değiştir: ${row}` }))
/** Fills the required fields of the create flow and stops on step index `target` (0 Kaynak, 1 Depolama, 2 Zamanlama ve Koruma, 3 Özet ve onay). */
async function reach(target: number) {
  fireEvent.click(await screen.findByRole('button', { name: 'Elle yapılandır' }))
  await fill(target)
}
/** The same on the manual form, once it is already open. */
async function fill(target: number) {
  fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'ERP backup' } })
  await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
  fireEvent.change(screen.getByLabelText('Yedekleme bilgisayarı'), { target: { value: 'agent-a' } })
  fireEvent.change(screen.getByLabelText("SQL Server ana bilgisayarı / veritabanı sunucusu"), { target: { value: 'sql' } })
  fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: 'ERP' } })
  next(target)
}
beforeEach(() => {
  vi.clearAllMocks()
  access.tenantId = 'tenant-a'
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
})
describe('iş formu', () => {
  const pinned = {
    enabled: true, overlapPolicy: 'Skip',
    schedule: { enabled: true, cronExpression: '0 2 * * *', timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone, missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' },
    retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 600, backoff: 'Exponential', jitter: 0.2 },
    retention: { enabled: true, keepForSeconds: 2592000, deleteGracePeriodSeconds: 86400 },
  }
  const sourceKinds = [
    ['SQL Server', () => undefined, { port: null, integratedSecurity: true, copyOnly: true, checksumPolicy: 'Required' }],
    ['PostgreSQL', () => {
      fireEvent.click(screen.getByRole('radio', { name: 'PostgreSQL' }))
      fireEvent.change(screen.getByLabelText('PostgreSQL ana bilgisayarı'), { target: { value: 'pg' } })
      fireEvent.change(screen.getByLabelText('Veritabanı'), { target: { value: 'crm' } })
      fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'u' } })
      fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'p' } })
    }, { port: 5432, integratedSecurity: false, copyOnly: false, checksumPolicy: 'Preferred' }],
    ['file', () => {
      fireEvent.click(screen.getByRole('radio', { name: 'Dosya / Klasör' }))
      fireEvent.change(screen.getByLabelText('Dosya kaynağı referansı'), { target: { value: 'documents' } })
      fireEvent.change(screen.getByLabelText('Yedeklenecek dosya ve klasörler'), { target: { value: 'reports' } })
    }, { port: null, checksumPolicy: 'Required', files: { paths: ['reports'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' } }],
  ] as const
  it.each(sourceKinds)('creates a %s job with every pinned value sent explicitly', async (_kind, pick, source) => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(0)
    pick()
    next(3)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1]).toMatchObject({
      ...pinned,
      configuration: {
        ...source, encrypt: true, trustServerCertificate: false, compressionPolicy: 'Preferred', encryptionPolicy: 'Off', stagingReference: 'default', storagePrefix: '', requiredVerifiedReplicaCount: 1,
        destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
      },
    })
  })
  it('localizes the protocol mismatch enum in the computer picker and review without translating its name', async () => {
    vi.mocked(getAgents).mockResolvedValueOnce([{
      agentId: 'agent-a', hostName: 'SQL Agent', status: 'Active', connectivity: 'online', operationalStatus: 'VersionMismatch',
      lastSeenAt: null, lastHeartbeatAt: null, lastConnectedAt: null, platform: 'Windows',
      agentVersion: '1', protocolVersion: '0', capabilityRevision: null, capabilities: [],
    }])
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Elle yapılandır' }))
    expect(await screen.findByRole('option', { name: 'SQL Agent · Protokol sürümü uyumsuz' })).toBeInTheDocument()
    await fill(3)
    expect(screen.getByText('SQL Agent · Protokol sürümü uyumsuz')).toBeInTheDocument()
    act(() => changeLanguage('en'))
    expect(screen.getByText('SQL Agent · Incompatible protocol version')).toBeInTheDocument()
    expect(screen.queryByText(/VersionMismatch/)).not.toBeInTheDocument()
  })

  it('saves the file paths of a file source; include, exclude and symlink settings are not offered', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(0)
    fireEvent.click(screen.getByRole('radio', { name: 'Dosya / Klasör' }))
    expect(screen.queryByLabelText('Veritabanı parolası')).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Dosya kaynağı referansı'), { target: { value: 'documents' } })
    fireEvent.change(screen.getByLabelText('Yedeklenecek dosya ve klasörler'), { target: { value: '../outside' } })
    next()
    expect(screen.getByLabelText('Yedeklenecek dosya ve klasörler')).toHaveAttribute('aria-invalid', 'true')
    fireEvent.change(screen.getByLabelText('Yedeklenecek dosya ve klasörler'), { target: { value: 'reports\nnotes.txt\n' } })
    for (const label of ['Dahil Edilecekler', 'Hariç Tutulacaklar', 'Sembolik Link Politikası']) expect(screen.queryByLabelText(label)).not.toBeInTheDocument()
    next(3)
    expect(screen.getByText(/documents/)).toBeInTheDocument()
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({
      engine: 'file-system', host: 'localhost', port: null, database: 'documents', integratedSecurity: true,
      databaseCredentialReference: null, databaseLogin: null, copyOnly: true,
      files: { paths: ['reports', 'notes.txt'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
    })
  })
  it('gives the database fields back when switching from a file source to the database engine again; file-only configuration is not sent', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(0)
    fireEvent.click(screen.getByRole('radio', { name: 'Dosya / Klasör' }))
    fireEvent.change(screen.getByLabelText('Dosya kaynağı referansı'), { target: { value: 'documents' } })
    fireEvent.click(screen.getByRole('radio', { name: 'Microsoft SQL Server' }))
    expect(screen.getByLabelText('SQL Server ana bilgisayarı / veritabanı sunucusu')).toHaveValue('sql')
    expect(screen.getByLabelText('Veritabanı')).toHaveValue('ERP')
    next(3)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({ engine: 'mssql', host: 'sql', database: 'ERP', files: null })
  })
  it('keeps what was typed for each engine when switching engines back and forth', async () => {
    renderPage()
    await reach(0)
    fireEvent.click(screen.getByRole('radio', { name: 'PostgreSQL' }))
    expect(screen.getByLabelText('PostgreSQL ana bilgisayarı')).toHaveValue('')
    fireEvent.change(screen.getByLabelText('PostgreSQL ana bilgisayarı'), { target: { value: 'pg' } })
    fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'u' } })
    fireEvent.click(screen.getByRole('radio', { name: 'Microsoft SQL Server' }))
    expect(screen.getByLabelText('SQL Server ana bilgisayarı / veritabanı sunucusu')).toHaveValue('sql')
    expect(screen.queryByLabelText('Veritabanı kullanıcı adı')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('radio', { name: 'PostgreSQL' }))
    expect(screen.getByLabelText('PostgreSQL ana bilgisayarı')).toHaveValue('pg')
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toHaveValue('u')
  })
  it('sends a password-authenticated PostgreSQL job on its default port, with byte-safe names', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(0)
    fireEvent.click(screen.getByRole('radio', { name: 'PostgreSQL' }))
    fireEvent.change(screen.getByLabelText("PostgreSQL ana bilgisayarı"), { target: { value: 'pg-host' } })
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: '数'.repeat(22) } })
    fireEvent.change(screen.getByLabelText("Veritabanı kullanıcı adı"), { target: { value: 'pg_user' } })
    fireEvent.change(screen.getByLabelText("Veritabanı parolası"), { target: { value: 'pg-secret' } })
    next()
    expect(screen.getByLabelText("Veritabanı")).toHaveAttribute('aria-invalid', 'true')
    expect(saveBackupJob).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText("Veritabanı"), { target: { value: '数'.repeat(21) } })
    next(3)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration).toMatchObject({
      engine: 'postgres', port: 5432, integratedSecurity: false, databaseLogin: { userName: 'pg_user', password: 'pg-secret' },
      database: '数'.repeat(21), copyOnly: false, checksumPolicy: 'Preferred',
    })
  })
  it('PostgreSQL kimlik doğrulamasında kullanıcı adı ve parola girilmeden Kaynak adımından ilerlemez', async () => {
    renderPage()
    await reach(0)
    fireEvent.click(screen.getByRole('radio', { name: 'PostgreSQL' }))
    fireEvent.change(screen.getByLabelText('PostgreSQL ana bilgisayarı'), { target: { value: 'pg' } })
    fireEvent.change(screen.getByLabelText('Veritabanı'), { target: { value: 'crm' } })
    next()
    expect(screen.getByLabelText("Veritabanı kullanıcı adı")).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText("Veritabanı parolası")).toHaveAttribute('aria-invalid', 'true')
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('routes each server field error to the step that shows it, one button per step', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400, { errors: { name: ['x'], agentId: ['x'], 'retention.keepForSeconds': ['x'], 'configuration.unknown': ['x'] } }))
    renderPage()
    await reach(3)
    await submit()
    await screen.findByRole('button', { name: 'Hatalı alana git: Kaynak' })
    expect(screen.getAllByRole('button', { name: /^Hatalı alana git/ }).map(button => button.getAttribute('aria-label'))).toEqual(['Hatalı alana git: Kaynak', 'Hatalı alana git: Zamanlama ve Koruma'])
    fireEvent.click(screen.getByRole('button', { name: 'Hatalı alana git: Zamanlama ve Koruma' }))
    expect(screen.getByRole('button', { name: '30 gün' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Hatalı alana git: Kaynak' }))
    expect(document.getElementById('name-error')).toHaveTextContent(/.+/)
  })
  it('adım doğrulaması eksik zorunlu alanlarda İleri düğmesini engeller', async () => {
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Elle yapılandır' }))
    next()
    expect(screen.getByLabelText('İş adı')).toHaveAttribute('aria-invalid', 'true')
    expect(document.getElementById('name-error')).toHaveTextContent(/.+/)
    expect(screen.getByLabelText('Yedekleme bilgisayarı')).toHaveAttribute('aria-invalid', 'true')
    expect(document.getElementById('agentId-error')).toHaveTextContent(/.+/)
    expect(document.getElementById('configuration.host-error')).toHaveTextContent(/.+/)
    expect(document.getElementById('configuration.database-error')).toHaveTextContent(/.+/)
    expect(screen.queryByLabelText('Depolama bağlantısı')).not.toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
    await fill(1)
    expect(screen.getByLabelText('Depolama bağlantısı')).toBeInTheDocument()
  })
  it('ara adımda Enter sonraki adıma ilerler; kaydetme isteği göndermez', async () => {
    renderPage()
    await reach(0)
    await userEvent.click(screen.getByLabelText('İş adı'))
    await userEvent.keyboard('{Enter}')
    expect(screen.getByLabelText('Depolama bağlantısı')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('yeni iş tek bir birincil local hedefle ve varsayılan bağlantı (null) ile gönderilir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(1)
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    expect(select).toHaveValue('')
    expect(within(select).getByRole('option', { name: 'Yerel disk · Kullanılabilir', selected: true })).toBeEnabled()
    expect(within(select).queryByRole('option', { name: 'Yerel disk · Yerel yedekleme bilgisayarı diski · Kullanılabilir' })).not.toBeInTheDocument()
    next(2)
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
    await reach(1)
    expect(await screen.findByText(/Yeni iş veya yeni hedef/)).toHaveTextContent('varsayılan seçimi bu engeli aşmaz')
    expect(within(screen.getByLabelText('Depolama bağlantısı')).getByRole('option', { name: 'Yerel disk · Devre dışı' })).toBeDisabled()
    next(2)
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    fireEvent.submit(screen.getByRole('button', { name: 'Kaydet' }).closest('form')!)
    expect(saveBackupJob).not.toHaveBeenCalled()
    goTo('Depolama')
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })
    next(2)
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
    await reach(1)
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    expect(within(select).getByRole('option', { name: 'Yerel disk · Kullanılabilir' })).toBeEnabled()
    expect(within(select).queryByRole('option', { name: 'Yerel disk · Yerel yedekleme bilgisayarı diski · Kullanılabilir' })).not.toBeInTheDocument()
    for (const name of [/Kilitli.*Platform kilidi/, /Kapalı.*Devre dışı/]) {
      expect(within(select).getByRole('option', { name })).toBeDisabled()
    }
    fireEvent.change(select, { target: { value: 'locked' } })
    expect(select).toHaveValue('')
    // Only the connections of the selected provider are listed.
    expect(within(select).queryByRole('option', { name: /Planlanan|Bilinmeyen|Uyumsuz sahiplik|Managed/ })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })
    expect(within(select).getByRole('option', { name: 'Merkezi depolama · Platform yönetimli · Kullanılabilir' })).toBeEnabled()
    expect(within(select).getByRole('option', { name: /Merkezi depolama · Merkezi yönetilen depolama · Platform yönetimli · Kullanılabilir/ })).toBeEnabled()
    expect(within(select).getByRole('option', { name: /Uyumsuz sahiplik.*Desteklenmeyen/ })).toBeDisabled()
    expect(within(select).queryByRole('option', { name: /Kilitli|Kapalı/ })).not.toBeInTheDocument()
  })
  it('seçilen gerçek kimliği, sağlayıcıyı ve uyumlu yürütme konumunu gönderir', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(1)
    const select = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(select).toBeEnabled())
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })
    fireEvent.change(select, { target: { value: 'conn-central' } })
    expect(screen.getByLabelText("Depolama Sağlayıcısı")).toHaveValue('central')
    next(2)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.destinations).toEqual([{ storageProvider: 'central', storageConnectionId: 'conn-central', storageExecutionLocation: 'Backend', isPrimary: true }])
    expect(vi.mocked(saveBackupJob).mock.calls[0][0]).toBe('tenant-a')
  })
  it.each(['storage_connection_disabled', 'storage_connection_unavailable'])('sonradan %s hatasında API durumunu yeniler; kimliği korur ve sahte başarı sunmaz', async code => {
    vi.mocked(getTenantConnections).mockResolvedValueOnce({ items: [makeConnection(), makeManaged()] }).mockResolvedValue({ items: [makeConnection(), makeManaged({ administrativelyDisabled: true })] })
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(409, { code, field: 'configuration.destinations[0].storageConnectionId' }))
    const { cache } = renderPage()
    await reach(1)
    const first = screen.getByLabelText('Depolama bağlantısı')
    await waitFor(() => expect(first).toBeEnabled())
    fireEvent.change(screen.getByLabelText("Depolama Sağlayıcısı"), { target: { value: 'central' } })
    fireEvent.change(first, { target: { value: 'conn-central' } })
    next(2)
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
  it.each([['Bu depolamayı kullan', 'Merkezi depolama'], ['Vazgeç', 'Yerel disk']])('"Yeni depolama ekle" opens the storage wizard and "%s" returns to the review with the draft kept', async (button, target) => {
    const { router } = renderPageWithRouter()
    await reach(3)
    fireEvent.click(screen.getByRole('button', { name: 'Yeni depolama ekle' }))
    fireEvent.click(await screen.findByRole('button', { name: button }))
    expect(await screen.findByText('ERP backup')).toBeInTheDocument()
    expect(await screen.findByText(target)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeInTheDocument()
    expect(router.state.location.search).toBe('')
    expect(sessionStorage.length + localStorage.length).toBe(0)
  })
  it('liste isteği başarısızsa varsayılan sahte başarıya dönüşmez ve tekrar yüklenebilir', async () => {
    vi.mocked(getTenantConnections).mockRejectedValueOnce(new ApiError(503)).mockResolvedValue({ items: [makeConnection()] })
    renderPage()
    await reach(1)
    expect(await screen.findByRole('button', { name: 'Bağlantıları yeniden yükle' })).toBeInTheDocument()
    next(2)
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Adıma git: Depolama (hedef sorunu)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Bağlantıları yeniden yükle' }))
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    next(2)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled())
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('tenant değişiminde yeni form anahtarı eski seçimi ve yerel alanları temizler', async () => {
    const { cache, rerender } = renderPage()
    await reach(1)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Depolama bağlantısı'), { target: { value: 'conn-central' } })
    access.tenantId = 'tenant-b'
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection({ tenantId: 'tenant-b', id: 'b-local', name: 'B Local' })] })
    vi.mocked(getTenantProviders).mockResolvedValue({ items: [catalog[0]] })
    rerender(page(cache))
    expect(screen.getByLabelText('İş adı')).toHaveValue('')
    await reach(1)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    expect(screen.getByLabelText('Depolama bağlantısı')).toHaveValue('')
    expect(screen.queryByRole('option', { name: /Yönetilen Depolama/ })).not.toBeInTheDocument()
    expect(getTenantConnections).toHaveBeenLastCalledWith('tenant-b', expect.any(AbortSignal))
  })
})

describe('adım adım sihirbaz', () => {
  const labels = ['Kaynak', 'Depolama', 'Zamanlama ve Koruma', 'Özet ve onay']
  it.each([0, 1, 2, 3])('step %i shows its position among the four steps; every step explains why it is asked', async target => {
    renderPage()
    await reach(target)
    const items = within(screen.getByRole('navigation', { name: 'Adımlar' })).getAllByRole('listitem')
    expect(items).toHaveLength(4)
    items.forEach((item, index) => expect(item).toHaveTextContent(labels[index]))
    expect(items.map(item => item.getAttribute('aria-current'))).toEqual(items.map((_, index) => index === target ? 'step' : null))
    expect(screen.queryAllByText(/Neden soruluyor\?/).length).toBeGreaterThan(0)
  })
  it('nests schedule and protection questions below their section headings', async () => {
    renderPage()
    await reach(2)
    const schedule = screen.getByRole('region', { name: 'Zamanlama' })
    const protection = screen.getByRole('region', { name: 'Koruma' })
    expect(within(schedule).getByRole('heading', { name: 'Zamanlama', level: 2 })).toBeInTheDocument()
    expect(within(schedule).getByRole('heading', { name: 'Yedek ne zaman alınsın?', level: 3 })).toBeInTheDocument()
    expect(within(schedule).getByRole('heading', { name: 'Yedekler ne kadar süre saklansın?', level: 3 })).toBeInTheDocument()
    expect(within(protection).getByRole('heading', { name: 'Koruma', level: 2 })).toBeInTheDocument()
    expect(within(protection).getByRole('heading', { name: 'Yedekler şifrelensin mi?', level: 3 })).toBeInTheDocument()
  })
  it('blocks a fractional required replica count before leaving storage', async () => {
    renderPage()
    await reach(1)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Yeni Çoğaltma Hedefi Ekle'), { target: { value: 'conn-central' } })
    const count = screen.getByLabelText('Gerekli Doğrulanmış Kopya Sayısı')
    fireEvent.change(count, { target: { value: '1.5' } })
    next()
    expect(count).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('alert')).toHaveTextContent('1 ile 2 arasında bir tam sayı girin.')
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('starts a new job from discovery: the name, then a ranked choice, with no computer or typed source yet', async () => {
    renderPage()
    expect(screen.getByLabelText('İş adı')).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: /Otomatik keşiften seç/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Elle yapılandır' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Yedekleme bilgisayarı')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Veritabanı')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Depolama bağlantısı')).not.toBeInTheDocument()
  })
  it('points to the manual path when no computer can discover, and asks for a source before moving on', async () => {
    renderPage()
    expect(await screen.findByText(/Otomatik keşfi destekleyen bilgisayar yok/)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'ERP' } })
    next()
    expect(screen.getByRole('alert')).toHaveTextContent(/Otomatik keşiften bir veritabanı seçin veya kaynağı elle yapılandırın/)
    expect(screen.queryByLabelText('Depolama bağlantısı')).not.toBeInTheDocument()
  })
  it('asks for the name, the computer and the source together on the manual path, and can go back to discovery', async () => {
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Elle yapılandır' }))
    expect(screen.getByLabelText('Yedekleme bilgisayarı')).toBeInTheDocument()
    expect(screen.getByLabelText('Veritabanı')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Otomatik keşiften seç' }))
    expect(screen.queryByLabelText('Yedekleme bilgisayarı')).not.toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Elle yapılandır' })).toBeInTheDocument()
  })
  it('offers no port, encryption, certificate, Windows sign-in or job switch on the manual source', async () => {
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Elle yapılandır' }))
    for (const engine of ['Microsoft SQL Server', 'PostgreSQL']) {
      fireEvent.click(screen.getByRole('radio', { name: engine }))
      for (const label of [/Bağlantı noktası/, /Bağlantıyı şifrele/, /sertifika/i, /Windows kimlik/, 'İş etkin']) expect(screen.queryByLabelText(label)).not.toBeInTheDocument()
    }
  })
  it('shows the pinned settings nowhere in the wizard, and never the server-certificate checkbox', async () => {
    renderPage()
    await reach(0)
    for (let step = 0; step < 4; step++) {
      for (const label of [/sertifika/i, /Bağlantı noktası/, 'Cron ifadesi', /Saat dilimi/, /saniye\)/, /CHECKSUM/, /Kopya yedek/, /Sıkıştırma/, 'Geçici alan referansı', 'Hedef öneki (göreli anahtar)', /jitter|Rastgele gecikme/]) expect(screen.queryByLabelText(label)).not.toBeInTheDocument()
      expect(screen.queryByText('Teknik ayrıntılar')).not.toBeInTheDocument()
      if (step < 3) next()
    }
  })
  it('announces the step position in the live summary, which follows what is entered', async () => {
    renderPage()
    await reach(1)
    const summary = within(screen.getByRole('complementary', { name: 'Canlı özet' }))
    expect(summary.getByText('Adım 2 / 4: Depolama')).toBeInTheDocument()
    expect(summary.getByText('ERP backup')).toBeInTheDocument()
    expect(summary.getByText('Her gün 02:00')).toBeInTheDocument()
    expect(summary.getByText('30 gün')).toBeInTheDocument()
    expect(summary.getByText('Kapalı')).toBeInTheDocument()
    next()
    fireEvent.click(screen.getByRole('button', { name: 'Hiç silinmesin' }))
    expect(within(screen.getByRole('complementary', { name: 'Canlı özet' })).getByText('Adım 3 / 4: Zamanlama ve Koruma')).toBeInTheDocument()
    expect(within(screen.getByRole('complementary', { name: 'Canlı özet' })).getByText('Hiç silinmesin')).toBeInTheDocument()
  })
  it('keeps the live summary in a closed block on a narrow screen', async () => {
    setViewport(false)
    renderPage()
    await reach(0)
    const block = within(screen.getByRole('complementary', { name: 'Canlı özet' })).getByText('Özet', { selector: 'summary' }).closest('details')
    expect(block).not.toHaveAttribute('open')
    expect(block).toHaveTextContent('Adım 1 / 4: Kaynak')
  })
  it('checks every step again before saving when an earlier step was emptied on the way back', async () => {
    renderPage()
    await reach(3)
    goTo('İş adı')
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: '' } })
    next(3)
    expect(screen.getByLabelText('İş adı')).toHaveAttribute('aria-invalid', 'true')
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it('schedule presets are the only schedule choices; daily 02:00 is the default and "elle" removes the schedule', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(2)
    const presets = within(screen.getByRole('group', { name: 'Hazır zamanlamalar' }))
    expect(presets.getAllByRole('button').map(button => button.textContent)).toEqual(['Her gün 02:00', 'Her gün 08:00', 'Günde 2 kez (08:00 ve 20:00)', 'Elle çalıştır'])
    expect(screen.getByRole('button', { name: 'Her gün 02:00' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Elle çalıştır' })).toHaveAttribute('aria-pressed', 'false')
    expect(screen.queryByLabelText('Cron ifadesi')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Günde 2 kez (08:00 ve 20:00)' }))
    next(1)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].schedule?.cronExpression).toBe('0 8,20 * * *')
    fireEvent.click(await screen.findByRole('button', { name: 'Değiştir: Zamanlama' }))
    fireEvent.click(screen.getByRole('button', { name: 'Elle çalıştır' }))
    next(1)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(2))
    expect(vi.mocked(saveBackupJob).mock.calls[1][1].schedule).toBeNull()
  })
  it('retention is one of 7, 14, 30 or 90 days or "Hiç silinmesin", 30 days by default, and never raw seconds', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(2)
    const choices = within(screen.getByRole('group', { name: 'Saklama süresi' }))
    expect(choices.getAllByRole('button').map(button => button.textContent)).toEqual(['7 gün', '14 gün', '30 gün', '90 gün', 'Hiç silinmesin'])
    expect(screen.getByRole('button', { name: '30 gün' })).toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(screen.getByRole('button', { name: '90 gün' }))
    next(1)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].retention).toMatchObject({ enabled: true, keepForSeconds: 7776000 })
    fireEvent.click(await screen.findByRole('button', { name: 'Değiştir: Saklama' }))
    fireEvent.click(screen.getByRole('button', { name: 'Hiç silinmesin' }))
    next(1)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(2))
    expect(vi.mocked(saveBackupJob).mock.calls[1][1].retention?.enabled).toBe(false)
  })
  it('encryption defaults to Kapalı; turning it on links to the recovery key without blocking and sends Required', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(2)
    const choice = screen.getByRole('switch', { name: 'Şifreleme: Kapalı' })
    expect(choice).not.toBeChecked()
    expect(screen.queryByRole('link', { name: 'Kurtarma anahtarı' })).not.toBeInTheDocument()
    fireEvent.click(choice)
    expect(screen.getByRole('switch', { name: 'Şifreleme: Açık' })).toBeChecked()
    expect(screen.getByRole('link', { name: 'Kurtarma anahtarı' })).toHaveAttribute('href', '/tenants/tenant-a/settings/recovery-key')
    next(1)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1].configuration.encryptionPolicy).toBe('Required')
  })
  it('the storage step starts with one local destination; "Başka hedef ekle" opens more destinations and the copy count', async () => {
    vi.mocked(saveBackupJob).mockRejectedValue(new ApiError(400))
    renderPage()
    await reach(1)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    const more = screen.getByText('Başka hedef ekle', { selector: 'summary' }).closest('details')!
    expect(more).not.toHaveAttribute('open')
    fireEvent.change(within(more).getByLabelText('Yeni Çoğaltma Hedefi Ekle'), { target: { value: 'conn-central' } })
    fireEvent.change(within(more).getByLabelText('Gerekli Doğrulanmış Kopya Sayısı'), { target: { value: '2' } })
    next(2)
    await submit()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    const configuration = vi.mocked(saveBackupJob).mock.calls[0][1].configuration
    expect(configuration.destinations).toHaveLength(2)
    expect(configuration.destinations).toEqual(expect.arrayContaining([expect.objectContaining({ storageProvider: 'central', storageConnectionId: 'conn-central', isPrimary: false })]))
    expect(configuration.requiredVerifiedReplicaCount).toBe(2)
  })
  it('the last step summarises the job in plain words before anything is created and holds no technical values', async () => {
    renderPage()
    await reach(2)
    next(1)
    expect(saveBackupJob).not.toHaveBeenCalled()
    expect(screen.getByText('ERP backup')).toBeInTheDocument()
    expect(screen.getByText('Her gün 02:00')).toBeInTheDocument()
    expect(screen.getByText('30 gün')).toBeInTheDocument()
    expect(screen.queryByText('0 2 * * *')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Teknik ayrıntılar' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeInTheDocument()
  })
  it('each summary row leads back to the step that owns it', async () => {
    renderPage()
    await reach(3)
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir: Zamanlama' }))
    expect(screen.getByRole('button', { name: 'Her gün 02:00' })).toBeInTheDocument()
    next(1)
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir: Şifreleme' }))
    expect(screen.getByRole('switch', { name: /^Şifreleme:/ })).toBeInTheDocument()
    next()
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir: Bilgisayar' }))
    expect(screen.getByLabelText('Yedekleme bilgisayarı')).toBeInTheDocument()
  })
})
describe('kaydedilmemiş değişiklik uyarısı', () => {
  const unload = () => {
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    return event.defaultPrevented
  }
  it('does not warn before anything was entered and leaves without asking', async () => {
    renderPage()
    expect(unload()).toBe(false)
    fireEvent.click(screen.getByRole('link', { name: 'Vazgeç' }))
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
  })
  it('warns the browser once data was entered in the wizard, and asks before cancelling', async () => {
    renderPage()
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Half done' } })
    expect(unload()).toBe(true)
    fireEvent.click(screen.getByRole('link', { name: 'Vazgeç' }))
    const dialog = await screen.findByRole('dialog', { name: 'Kaydedilmemiş değişiklikler atılsın mı?' })
    expect(screen.queryByText('Job listesi')).toBeNull()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByLabelText('İş adı')).toHaveValue('Half done')
    fireEvent.click(screen.getByRole('link', { name: 'Vazgeç' }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Değişiklikleri at' }))
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
  })
  it.each(['/tenants/tenant-a/backup-jobs/new'])('guards internal links from %s, retaining values on stay and resuming the requested destination on discard', async route => {
    renderPage(route)
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'Keep this value' } })
    fireEvent.click(screen.getByRole('link', { name: 'Computers' }))
    let dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByLabelText('İş adı')).toHaveValue('Keep this value')
    expect(unload()).toBe(true)
    fireEvent.click(screen.getByRole('link', { name: 'Computers' }))
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Değişiklikleri at' }))
    expect(await screen.findByText('Computer list')).toBeInTheDocument()
    expect(screen.queryByText('Job listesi')).toBeNull()
    expect(unload()).toBe(false)
    expect(saveBackupJob).not.toHaveBeenCalled()
  })
  it.each(['/tenants/tenant-a/backup-jobs/new'])('guards browser back from %s without replacing the pending history destination', async route => {
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const router = makeRouter(route, ['/tenants/tenant-a/agents', route])
    render(<QueryClientProvider client={cache}><RouterProvider router={router} /></QueryClientProvider>)
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'Keep on back' } })
    await act(async () => { await router.navigate(-1) })
    let dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(router.state.location.pathname).toBe(route)
    expect(screen.getByLabelText('İş adı')).toHaveValue('Keep on back')
    await act(async () => { await router.navigate(-1) })
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Değişiklikleri at' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(await screen.findByText('Computer list')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/tenants/tenant-a/agents')
    await act(async () => { await router.navigate(1) })
    expect(await screen.findByLabelText('İş adı')).not.toHaveValue('Keep on back')
  })
  it.each(['/tenants/tenant-a/backup-jobs/new'])('allows clean internal navigation from %s without confirmation', async route => {
    renderPage(route)
    await screen.findByLabelText('İş adı')
    fireEvent.click(screen.getByRole('link', { name: 'Computers' }))
    expect(await screen.findByText('Computer list')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
  })
  it('clears the wizard guard on a successful create without asking to discard saved values', async () => {
    vi.mocked(saveBackupJob).mockResolvedValue({} as never)
    renderPage()
    await reach(3)
    expect(unload()).toBe(true)
    await submit()
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(unload()).toBe(false)
  })
  it.each(['/tenants/tenant-a/backup-jobs/new'])('rearms the guard in a fresh form after saving from %s and returning', async route => {
    vi.mocked(saveBackupJob).mockResolvedValue({} as never)
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const router = makeRouter(route)
    render(<QueryClientProvider client={cache}><RouterProvider router={router} /></QueryClientProvider>)
    await reach(3)
    await submit()
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
    await act(async () => { await router.navigate(-1) })
    await screen.findByLabelText('İş adı')
    expect(unload()).toBe(false)
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Unsaved after returning' } })
    expect(unload()).toBe(true)
    fireEvent.click(screen.getByRole('link', { name: 'Computers' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
  })
})
