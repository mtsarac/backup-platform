import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, createRoutesFromElements, Route, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createEnrollmentToken, getAgent, getAgentDistribution, getAgents, getEnrollmentTokenStatus } from '@/features/agents/api'
import { saveBackupJob, startJobRun } from '@/features/backup-jobs/api'
import { getBackup } from '@/features/backups/api'
import { makeRun } from '@/features/backups/test-data'
import { getRestore, getRestores, getRestoreTargets, startRestoreVerification } from '@/features/restores/api'
import { getDiscoveryAgents, getDiscoveryInstance, getDiscoveryInstances, rescanDiscoveryAgent } from '@/features/discovery/api'
import { makeDatabase, makeDetail, makeInstance, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { useLocation } from 'react-router-dom'
import { FirstBackupPage } from './first-backup-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/agents/api')
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/backups/api')
vi.mock('@/features/restores/api')
vi.mock('@/features/discovery/api')
vi.mock('@/features/storage-connections/api')

function LocationState() { return <p>Bulunan veritabanları {JSON.stringify(useLocation().state)}</p> }
function mount(seed?: (client: QueryClient) => void) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  seed?.(client)
  const router = createMemoryRouter(createRoutesFromElements(<>
    <Route path="/tenants/:tenantId/first-backup" element={<FirstBackupPage />} />
    <Route path="/tenants/:tenantId" element={<p>Ana sayfa içeriği</p>} />
    <Route path="/tenants/:tenantId/backup-jobs/new" element={<p>İş sihirbazı</p>} />
    <Route path="/tenants/:tenantId/agents/databases" element={<LocationState />} />
  </>), { initialEntries: ['/tenants/tenant-a/first-backup'] })
  render(<QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>)
  return router
}
const detail = makeDetail({ databases: [makeDatabase({ name: 'Muhasebe' }), makeDatabase({ name: 'CRM' }), makeDatabase({ name: 'master', isSystem: true })] })
async function toSummary(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('checkbox', { name: /Muhasebe/ }))
  await user.click(screen.getByRole('checkbox', { name: /CRM/ }))
  await user.click(screen.getByRole('button', { name: 'İleri' }))
  await screen.findByRole('heading', { name: 'Özet ve başla' })
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getAgents).mockResolvedValue([{ ...supportedAgent, connectivity: 'online' }] as never)
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(rescanDiscoveryAgent).mockResolvedValue(makeStatus())
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance()] })
  vi.mocked(getDiscoveryInstance).mockResolvedValue(detail)
  vi.mocked(saveBackupJob).mockImplementation(async (_tenant, input) => ({ id: `job-${input.configuration.database}` }) as never)
  vi.mocked(startJobRun).mockImplementation(async (_tenant, id) => ({ id: `run-${id}` }))
  vi.mocked(getBackup).mockImplementation(async (_tenant, id) => makeRun({ id }))
  vi.mocked(getRestores).mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 10 })
})
const artifact = { id: 'artifact-1', jobId: 'job-Muhasebe', runId: 'run-job-Muhasebe', backupType: 'Full', state: 'Available', sizeBytes: 2_000_000, replicas: [{ id: 'r1', verificationState: 'Verified', usable: true }] }
const succeeded = (id: string) => makeRun({ id, status: 'Succeeded', completedAt: '2026-10-06T10:05:00Z', attempts: [{ ...makeRun().attempts[0], status: 'Succeeded', progressStage: 'Completed', progressPercent: 100, artifacts: [artifact as never] }] })

describe('first backup flow', () => {
  it('starts at "Ne yedeklensin" when a computer exists, scans on its own and lists the found data with nothing ticked', async () => {
    const user = userEvent.setup()
    mount()
    expect(await screen.findByRole('checkbox', { name: /Muhasebe/ })).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: /CRM/ })).not.toBeChecked()
    expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
    expect(screen.queryByText(/master/)).not.toBeInTheDocument()
    expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a')
    expect(screen.queryByText(/Muhasebe veritabanı yedeği/)).not.toBeInTheDocument()
    await user.click(screen.getByRole('checkbox', { name: /Muhasebe/ }))
    expect(screen.getByText(/Muhasebe veritabanı yedeği/)).toBeVisible()
    expect(screen.queryByLabelText('İş adı')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'İleri' })).toBeEnabled()
  })

  it('does not request a scan while one is already queued: no request, no notice, and it keeps waiting for that scan', async () => {
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ scanState: 'Requested' })] })
    mount()
    expect(await screen.findByText('Bilgisayar taranıyor…')).toBeVisible()
    expect(rescanDiscoveryAgent).not.toHaveBeenCalled()
    expect(screen.queryByText(/Az önce tarama istendi/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Yeniden dene' })).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('filters the found list by database or server name and keeps picks that the filter hides', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('checkbox', { name: /Muhasebe/ }))
    await user.type(screen.getByRole('searchbox', { name: 'Veritabanı ara' }), 'crm')
    expect(screen.queryByRole('checkbox', { name: /Muhasebe/ })).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /CRM/ })).toBeVisible()
    await user.clear(screen.getByRole('searchbox', { name: 'Veritabanı ara' }))
    await user.type(screen.getByRole('searchbox', { name: 'Veritabanı ara' }), 'yok-böyle-bir-şey')
    expect(screen.getByText('Aramaya uyan veritabanı yok.')).toBeVisible()
    await user.clear(screen.getByRole('searchbox', { name: 'Veritabanı ara' }))
    expect(screen.getByRole('checkbox', { name: /Muhasebe/ })).toBeChecked()
  })

  it('does not wait for another computer\'s scan: only the chosen computer\'s own scan counts', async () => {
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus(), makeStatus({ agentId: 'agent-other', scanState: 'Requested' })] })
    mount()
    expect(await screen.findByRole('checkbox', { name: /Muhasebe/ })).toBeVisible()
    expect(screen.queryByText('Bilgisayar taranıyor…')).not.toBeInTheDocument()
  })

  it('links servers that need credentials instead of claiming no data was found', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [
      makeInstance({ access: 'CredentialsRequired' }),
      makeInstance({ id: 'inst-2', displayName: 'PostgreSQL', access: 'CredentialsRequired' }),
    ] })
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus(), makeStatus({ agentId: 'revoked', scanState: 'Requested' })] })
    mount()
    expect(await screen.findByText('Veritabanı sunucusu bulundu ama listelemek için giriş bilgisi gerekiyor.')).toBeVisible()
    expect(screen.getByRole('link', { name: 'SQL Server (default)' })).toHaveAttribute('href', '/tenants/tenant-a/discovery/instances/inst-1')
    expect(screen.getByRole('link', { name: 'PostgreSQL' })).toHaveAttribute('href', '/tenants/tenant-a/discovery/instances/inst-2')
    expect(screen.queryByText('Veri bulunamadı')).not.toBeInTheDocument()
  })

  it('explains an offline queued scan without waiting for it forever', async () => {
    vi.mocked(getAgents).mockResolvedValue([{ ...supportedAgent, connectivity: 'offline' }] as never)
    vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus({ scanState: 'Requested' })] })
    mount()
    expect(await screen.findByText('Bu bilgisayar şu anda çevrimdışı; tarama bilgisayar yeniden bağlandığında çalışır.')).toBeVisible()
    expect(await screen.findByRole('checkbox', { name: /Muhasebe/ })).toBeVisible()
    expect(screen.queryByText('Bilgisayar taranıyor…')).not.toBeInTheDocument()
  })

  it('decides where to start from the fresh list, not from a stale cached one', async () => {
    mount(client => client.setQueryData(['tenant', 'tenant-a', 'agents'], []))
    expect(await screen.findByRole('checkbox', { name: /Muhasebe/ })).toBeVisible()
    expect(screen.queryByRole('heading', { name: 'Yedekleme bilgisayarı ekle' })).not.toBeInTheDocument()
  })

  it('waits for a just-added computer to report what it can do, then scans it and names it', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const computer = { ...supportedAgent, hostName: 'win11-virtual', connectivity: 'online' }
    vi.mocked(getAgents).mockResolvedValueOnce([{ ...computer, capabilities: [], capabilitiesReportedAt: null }] as never).mockResolvedValue([computer] as never)
    mount()
    expect(await screen.findByText('Bilgisayar hazırlanıyor…')).toBeVisible()
    expect(screen.queryByText('Otomatik taramayı destekleyen bilgisayar yok.')).not.toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(3_000) })
    expect(await screen.findByRole('checkbox', { name: /Muhasebe/ })).toBeVisible()
    expect(screen.getByText('win11-virtual')).toBeVisible()
    expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a')
    vi.useRealTimers()
  })

  it('renames a job only through "Adı değiştir"', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('checkbox', { name: /Muhasebe/ }))
    await user.click(await screen.findByRole('button', { name: /Adı değiştir — Muhasebe/ }))
    const name = screen.getByLabelText('İş adı')
    await user.clear(name)
    await user.type(name, 'Defter')
    await user.tab()
    expect(screen.getByText(/Defter/)).toBeVisible()
  })

  it('offers manual configuration when the scan finds nothing', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue({ ...detail, databases: [] })
    const user = userEvent.setup()
    mount()
    expect(await screen.findByText('Veri bulunamadı')).toBeVisible()
    await user.click(screen.getByRole('link', { name: 'Elle yapılandır' }))
    expect(await screen.findByText('İş sihirbazı')).toBeVisible()
  })

  it('shows where, when, how long and encryption (off) with "Değiştir" on each', async () => {
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    expect(screen.getAllByRole('definition').map(row => row.textContent)).toEqual(['Yerel disk', 'Her gün 02:00', '30 gün', 'Kapalı'])
    expect(screen.getByText('Şifreleme')).toBeVisible()
    expect(screen.getAllByRole('button', { name: /Değiştir/ })).toHaveLength(4)
    expect(screen.queryByText('Yedekler otomatik şifrelenir.')).not.toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
  })

  it('lists the local disk once and the system defaults by their plain names in "Nereye"', async () => {
    vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    await user.click(screen.getByRole('button', { name: 'Değiştir — Nereye' }))
    const options = within(screen.getByLabelText('Depolama')).getAllByRole('option').map(option => option.textContent)
    expect(options).toEqual(['Yerel disk', 'Merkezi depolama'])
  })

  it('switching encryption on in the summary sends Required; untouched it sends Off', async () => {
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    await user.click(screen.getByRole('button', { name: 'Değiştir — Şifreleme' }))
    await user.click(screen.getByRole('switch', { name: 'Şifreleme: Kapalı' }))
    expect(screen.getByRole('switch', { name: 'Şifreleme: Açık' })).toBeChecked()
    expect(screen.getAllByRole('definition').map(row => row.textContent)).toEqual(['Yerel disk', 'Her gün 02:00', '30 gün', 'Açık'])
    await user.click(screen.getByRole('button', { name: 'Sadece oluştur' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(2))
    expect(vi.mocked(saveBackupJob).mock.calls.map(call => call[1].configuration.encryptionPolicy)).toEqual(['Required', 'Required'])
  })

  it('creates one job per pick with the pinned values and starts each first backup', async () => {
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    await user.click(screen.getByRole('button', { name: 'Yedeklemeyi oluştur ve ilk yedeği başlat' }))
    expect(await screen.findByRole('heading', { name: 'İlk yedek' })).toBeVisible()
    const inputs = vi.mocked(saveBackupJob).mock.calls.map(call => call[1])
    expect(inputs.map(input => input.name)).toEqual(['Muhasebe veritabanı yedeği', 'CRM veritabanı yedeği'])
    expect(inputs[0]).toMatchObject({
      agentId: 'agent-a', enabled: true, overlapPolicy: 'Skip', retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 600, backoff: 'Exponential', jitter: 0.2 },
      schedule: { enabled: true, cronExpression: '0 2 * * *', missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' },
      retention: { enabled: true, keepForSeconds: 30 * 86400, deleteGracePeriodSeconds: 86400 },
      configuration: { engine: 'mssql', host: 'localhost', port: 1433, database: 'Muhasebe', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off', stagingReference: 'default', storagePrefix: '', requiredVerifiedReplicaCount: 1,
        destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }] },
    })
    expect(inputs[1].configuration.database).toBe('CRM')
    expect(vi.mocked(startJobRun).mock.calls.map(call => call[1])).toEqual(['job-Muhasebe', 'job-CRM'])
  })

  it('"Sadece oluştur" creates the jobs without starting a backup, with the changed answers', async () => {
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    await user.click(screen.getByRole('button', { name: /Değiştir — Ne zaman/ }))
    await user.selectOptions(screen.getByLabelText('Hazır zamanlamalar'), 'Elle çalıştır')
    await user.click(screen.getByRole('button', { name: /Değiştir — Saklama/ }))
    await user.selectOptions(screen.getByLabelText('Saklama süresi'), '7 gün')
    await user.click(screen.getByRole('button', { name: 'Sadece oluştur' }))
    expect(await screen.findByText(/Yedekleme elle çalıştırılır/)).toBeVisible()
    expect(saveBackupJob).toHaveBeenCalledTimes(2)
    expect(vi.mocked(saveBackupJob).mock.calls[0][1]).toMatchObject({ schedule: null, retention: { keepForSeconds: 7 * 86400 } })
    expect(startJobRun).not.toHaveBeenCalled()
  })

  it('retries only what failed', async () => {
    vi.mocked(saveBackupJob).mockResolvedValueOnce({ id: 'job-Muhasebe' } as never).mockRejectedValueOnce(new ApiError(400, { error: 'invalid_request' }))
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    await user.click(screen.getByRole('button', { name: 'Yedeklemeyi oluştur ve ilk yedeği başlat' }))
    expect(await screen.findByRole('alert')).toBeVisible()
    expect(screen.queryByText('Ana sayfa içeriği')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Başarısızları yeniden dene' }))
    expect(await screen.findByRole('heading', { name: 'İlk yedek' })).toBeVisible()
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(3))
    expect(startJobRun).toHaveBeenCalledTimes(2)
  })

  describe('adding a computer', () => {
    const windowsBrowser = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/150.0 Safari/537.36'
    const linuxBrowser = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/150.0 Safari/537.36'
    const computer = { ...supportedAgent, hostName: 'win11-virtual', connectivity: 'online' }
    const detailOf = (connectivity: string) => ({ ...computer, connectivity, osDescription: 'Windows 11', agentVersion: '1.0.0', platform: 'Windows' }) as never
    // A key lives for a day; a far-future date would overflow the monitor's expiry timer.
    const expiresAt = new Date(Date.now() + 86_400_000).toISOString()
    const distribution = { serverUrl: 'https://backup.example', packages: { 'win-x64': true, 'linux-x64': true, 'win-x64-setup': false } }

    beforeEach(() => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      vi.spyOn(window.navigator, 'userAgent', 'get').mockReturnValue(windowsBrowser)
      vi.mocked(getAgents).mockResolvedValue([])
      vi.mocked(getAgentDistribution).mockResolvedValue(distribution as never)
      vi.mocked(createEnrollmentToken).mockResolvedValue({ tokenId: 'token-a', token: 'secret-key', expiresAt })
      vi.mocked(getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Active', agentId: null })
      vi.mocked(getAgent).mockResolvedValue(detailOf('unknown'))
    })
    afterEach(() => { vi.useRealTimers() })

    const advance = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
    async function issueCommand(name = 'win11-virtual') {
      fireEvent.change(await screen.findByRole('textbox', { name: 'Bilgisayarın adı' }), { target: { value: name } })
      await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Kurulum komutunu oluştur' })) })
    }
    const commandText = () => (screen.getByRole('textbox', { name: 'Tek kullanımlık kurulum komutu' }) as HTMLTextAreaElement).value

    it('is one screen: name, system defaulting to Windows, no command until asked, other ways closed', async () => {
      mount()
      expect(await screen.findByRole('heading', { name: 'Yedekleme bilgisayarı ekle' })).toBeVisible()
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(screen.getByRole('combobox', { name: 'İşletim sistemi' })).toHaveValue('Windows')
      expect(screen.getByRole('listitem', { current: 'step' })).toHaveTextContent('Bilgisayar')
      expect(screen.getByRole('button', { name: 'Kurulum komutunu oluştur' })).toBeDisabled()
      expect(createEnrollmentToken).not.toHaveBeenCalled()
      await issueCommand('Ofis')
      expect(createEnrollmentToken).toHaveBeenCalledTimes(1)
      expect(commandText()).toContain('powershell -NoProfile -ExecutionPolicy Bypass -File')
      expect(commandText()).toContain("-Token 'secret-key' -AgentName 'Ofis'")
      expect(screen.getByText('Bu bilgisayarda zaten bir ajan kuruluysa komut çalışmaz; önce mevcut ajanı kaldırın.')).toBeVisible()
      expect(screen.getByRole('button', { name: 'Kurulum komutunu kopyala' })).toBeEnabled()
      expect(screen.getByText('Komutun çalıştırılması bekleniyor')).toBeVisible()
      expect(screen.getByText('Başka yollarla kur').closest('details')).not.toHaveAttribute('open')
    })

    it('offers Linux first when the visitor browses from Linux', async () => {
      vi.spyOn(window.navigator, 'userAgent', 'get').mockReturnValue(linuxBrowser)
      mount()
      expect(await screen.findByRole('combobox', { name: 'İşletim sistemi' })).toHaveValue('Linux')
      await issueCommand()
      expect(commandText()).toContain("sudo bash")
    })

    it('shows the live status, keeps the screen while the computer is only enrolled, and opens the discovery page by itself once it is online', async () => {
      vi.mocked(getAgents).mockResolvedValueOnce([])
      mount()
      await issueCommand()
      vi.mocked(getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Consumed', agentId: 'agent-a' })
      await advance(5_000)
      expect(screen.getByText('Kayıt tamamlandı')).toBeVisible()
      expect(screen.queryByRole('textbox', { name: 'Tek kullanımlık kurulum komutu' })).not.toBeInTheDocument()
      await advance(5_000)
      expect(screen.getByText('Bilgisayar bağlanıyor')).toBeVisible()
      // The list learns about the new computer before it is online (window focus, another tab): that must not end this screen.
      vi.mocked(getAgents).mockResolvedValue([{ ...computer, connectivity: 'offline', capabilities: [], capabilitiesReportedAt: null }] as never)
      await act(async () => { window.dispatchEvent(new Event('visibilitychange')) })
      await advance(1_000)
      expect(screen.getByText('Bilgisayar bağlanıyor')).toBeVisible()
      expect(screen.queryByRole('heading', { name: 'Ne yedeklensin?' })).not.toBeInTheDocument()
      vi.mocked(getAgent).mockResolvedValue(detailOf('online'))
      vi.mocked(getAgents).mockResolvedValue([computer] as never)
      await advance(5_000)
      expect(await screen.findByText('Bulunan veritabanları {"addedAgentId":"agent-a"}')).toBeVisible()
      expect(screen.queryByRole('heading', { name: 'Ne yedeklensin?' })).not.toBeInTheDocument()
    })

    it('says in a plain sentence, not an error, that a long wait is still going on', async () => {
      mount()
      await issueCommand()
      expect(screen.queryByText(/Beklenenden uzun sürüyor/)).not.toBeInTheDocument()
      await advance(130_000)
      expect(screen.getByText(/Beklenenden uzun sürüyor/)).toBeVisible()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })

    it('says the key ran out in a sentence and offers a new command', async () => {
      mount()
      await issueCommand()
      vi.mocked(getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-a', expiresAt, state: 'Expired', agentId: null })
      await advance(5_000)
      expect(screen.getByText('Kayıt anahtarının süresi doldu')).toBeVisible()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
      vi.mocked(createEnrollmentToken).mockResolvedValue({ tokenId: 'token-b', token: 'second-key', expiresAt })
      vi.mocked(getEnrollmentTokenStatus).mockResolvedValue({ tokenId: 'token-b', expiresAt, state: 'Active', agentId: null })
      await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Yeni komut oluştur' })) })
      expect(commandText()).toContain("-Token 'second-key'")
    })

    it('offers the Windows setup wizard under "Başka yollarla kur" only when it is published', async () => {
      mount()
      await issueCommand()
      expect(screen.queryByRole('link', { name: 'Kurulum sihirbazını indir (.exe)' })).not.toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Kurulum scriptini indir' })).toHaveAttribute('href', 'https://backup.example/api/agent-dist/install.ps1')
      expect(screen.getByRole('link', { name: 'Windows zip paketini indir' })).toBeInTheDocument()
      expect(screen.getByRole('textbox', { name: 'Tek kullanımlık kayıt anahtarı' })).toHaveValue('secret-key')
    })

    it('shows the wizard link when it is published', async () => {
      vi.mocked(getAgentDistribution).mockResolvedValue({ ...distribution, packages: { ...distribution.packages, 'win-x64-setup': true } } as never)
      mount()
      await issueCommand()
      expect(screen.getByRole('link', { name: 'Kurulum sihirbazını indir (.exe)' })).toHaveAttribute('href', 'https://backup.example/api/agent-dist/packages/win-x64-setup')
    })

    it('explains instead of offering a command when no package is published for the system', async () => {
      vi.mocked(getAgentDistribution).mockResolvedValue({ ...distribution, packages: { ...distribution.packages, 'win-x64': false } } as never)
      mount()
      fireEvent.change(await screen.findByRole('textbox', { name: 'Bilgisayarın adı' }), { target: { value: 'Ofis' } })
      expect(await screen.findByRole('alert')).toHaveTextContent('Bu işletim sistemi için kurulum paketi yayımlanmamış.')
      expect(screen.getByRole('button', { name: 'Kurulum komutunu oluştur' })).toBeDisabled()
    })
  })

  describe('first backup screen', () => {
    async function start(user: ReturnType<typeof userEvent.setup>) {
      await toSummary(user)
      await user.click(screen.getByRole('button', { name: 'Yedeklemeyi oluştur ve ilk yedeği başlat' }))
    }

    it('shows each item\'s stage and progress to assistive tech, and cannot go back to earlier steps', async () => {
      vi.mocked(getBackup).mockImplementation(async (_tenant, id) => id === 'run-job-CRM'
        ? makeRun({ id, attempts: [{ ...makeRun().attempts[0], progressStage: 'Verifying', progressPercent: 90 }] })
        : makeRun({ id }))
      const user = userEvent.setup()
      mount()
      await start(user)
      const bar = await screen.findByRole('progressbar', { name: 'Muhasebe veritabanı yedeği: ilerleme' })
      expect(bar).toHaveAttribute('aria-valuenow', '35')
      expect(bar).toHaveAttribute('aria-valuetext', 'Yedek alınıyor, %35')
      expect(screen.getByRole('progressbar', { name: 'CRM veritabanı yedeği: ilerleme' })).toHaveAttribute('aria-valuetext', 'Doğrulanıyor, %90')
      expect(screen.getByText('Muhasebe veritabanı yedeği: Yedek alınıyor', { selector: '[role=status]' })).toBeInTheDocument()
      expect(screen.getByRole('listitem', { current: 'step' })).toHaveTextContent('İlk yedek')
      expect(screen.queryByRole('button', { name: 'Geri' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Yedeklemeyi oluştur ve ilk yedeği başlat' })).not.toBeInTheDocument()
    })

    it('"Arka planda devam etsin" returns to the home page', async () => {
      const user = userEvent.setup()
      mount()
      await start(user)
      await user.click(await screen.findByRole('link', { name: 'Arka planda devam etsin' }))
      expect(await screen.findByText('Ana sayfa içeriği')).toBeVisible()
    })

    it('"Sadece oluştur" says the first backup waits for its time and offers "Şimdi başlat"', async () => {
      const user = userEvent.setup()
      mount()
      await toSummary(user)
      await user.click(screen.getByRole('button', { name: 'Sadece oluştur' }))
      expect(await screen.findByText('İlk yedek zamanlanan saatte alınacak (Her gün 02:00).')).toBeVisible()
      expect(startJobRun).not.toHaveBeenCalled()
      await user.click(screen.getByRole('button', { name: 'Şimdi başlat' }))
      expect(await screen.findByRole('progressbar', { name: /Muhasebe/ })).toBeVisible()
      expect(vi.mocked(startJobRun).mock.calls.map(call => call[1])).toEqual(['job-Muhasebe', 'job-CRM'])
      expect(saveBackupJob).toHaveBeenCalledTimes(2)
    })

    it('shows the finished result with size, time and storage, then a trial restore that can be started as a new database', async () => {
      vi.mocked(getBackup).mockImplementation(async (_tenant, id) => succeeded(id))
      vi.mocked(getRestoreTargets).mockResolvedValue({ artifactId: 'artifact-1', sourceDatabase: 'Muhasebe', backupType: 'Full', sizeBytes: 1,
        agents: [{ agentId: 'agent-a', hostName: 'PC', status: 'Active', connectivity: 'online', canRestore: true, canPrecheck: true, canDiscover: true, canVerify: true, sourceEligible: true, sourceAccess: 'CentralStorage', rejectionCode: null, rejectionMessage: null }],
        targets: [{ jobId: 'job-Muhasebe', jobName: 'x', agentId: 'agent-a', database: 'Muhasebe', target: { host: 'h', port: 1433 } as never }] } as never)
      vi.mocked(startRestoreVerification).mockResolvedValue({ id: 'ver-1' } as never)
      vi.mocked(getRestore).mockResolvedValue({ id: 'ver-1', status: 'Running' } as never)
      const user = userEvent.setup()
      mount()
      await start(user)
      expect((await screen.findAllByText('İlk yedeğiniz alındı ve doğrulandı'))[0]).toBeVisible()
      expect(screen.getAllByText('2 MB')[0]).toBeVisible()
      expect(screen.getAllByText('Yerel disk')[0]).toBeVisible()
      expect(screen.queryByText(/Yerine geç/)).not.toBeInTheDocument()
      await user.click((await screen.findAllByRole('button', { name: 'Geri yüklemeyi dene' }))[0])
      expect(startRestoreVerification).toHaveBeenCalledTimes(1)
      expect(await screen.findByRole('link', { name: 'Ayrıntı' })).toHaveAttribute('href', '/tenants/tenant-a/restores/ver-1')
    })

    it('skipping the trial restore says the home page keeps a reminder', async () => {
      vi.mocked(getBackup).mockImplementation(async (_tenant, id) => succeeded(id))
      vi.mocked(getRestoreTargets).mockResolvedValue({ artifactId: 'artifact-1', sourceDatabase: 'Muhasebe', backupType: 'Full', sizeBytes: 1, agents: [], targets: [] })
      const user = userEvent.setup()
      mount()
      await start(user)
      await user.click((await screen.findAllByRole('button', { name: 'Atla' }))[0])
      expect(screen.getByText(/Atlandı\. Ana sayfada .* hatırlatma kalır/)).toBeVisible()
      expect(startRestoreVerification).not.toHaveBeenCalled()
    })
  })
})
