import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, createRoutesFromElements, Route, RouterProvider } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgentDistribution, getAgents } from '@/features/agents/api'
import { saveBackupJob, startJobRun } from '@/features/backup-jobs/api'
import { getBackup } from '@/features/backups/api'
import { makeRun } from '@/features/backups/test-data'
import { getRestore, getRestores, getRestoreTargets, startRestoreVerification } from '@/features/restores/api'
import { getDiscoveryAgents, getDiscoveryInstance, getDiscoveryInstances, rescanDiscoveryAgent } from '@/features/discovery/api'
import { makeDatabase, makeDetail, makeInstance, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection } from '@/features/storage-connections/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { FirstBackupPage } from './first-backup-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/agents/api')
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/backups/api')
vi.mock('@/features/restores/api')
vi.mock('@/features/discovery/api')
vi.mock('@/features/storage-connections/api')

function mount() {
  const router = createMemoryRouter(createRoutesFromElements(<>
    <Route path="/tenants/:tenantId/first-backup" element={<FirstBackupPage />} />
    <Route path="/tenants/:tenantId" element={<p>Ana sayfa içeriği</p>} />
    <Route path="/tenants/:tenantId/backup-jobs/new" element={<p>İş sihirbazı</p>} />
  </>), { initialEntries: ['/tenants/tenant-a/first-backup'] })
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>)
  return router
}
const detail = makeDetail({ databases: [makeDatabase({ name: 'Muhasebe' }), makeDatabase({ name: 'CRM' }), makeDatabase({ name: 'master', isSystem: true })] })
async function toSummary(user: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Muhasebe')
  await user.click(screen.getByRole('button', { name: 'İleri' }))
  await screen.findByRole('heading', { name: 'Özet ve başla' })
}

beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(access, { canOperate: true, canAdmin: true })
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
  it('starts at "Ne yedeklensin" when a computer exists, scans on its own and lists the found data ticked with generated names', async () => {
    mount()
    expect(await screen.findByRole('checkbox', { name: /Muhasebe/ })).toBeChecked()
    expect(screen.getByRole('checkbox', { name: /CRM/ })).toBeChecked()
    expect(screen.queryByText(/master/)).not.toBeInTheDocument()
    expect(rescanDiscoveryAgent).toHaveBeenCalledWith('tenant-a', 'agent-a')
    expect(screen.getByText(/Muhasebe veritabanı yedeği/)).toBeVisible()
    expect(screen.queryByLabelText('İş adı')).not.toBeInTheDocument()
  })

  it('renames a job only through "Adı değiştir"', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Muhasebe: Adı değiştir/ }))
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

  it('shows where, when and how long with "Değiştir" on each, and only states that backups are encrypted', async () => {
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    expect(screen.getAllByRole('definition').map(row => row.textContent)).toEqual(['Yerel disk', 'Her gün 02:00', '30 gün'])
    expect(screen.getAllByRole('button', { name: /Değiştir/ })).toHaveLength(3)
    expect(screen.getByText('Yedekler otomatik şifrelenir.')).toBeVisible()
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/şifre/i)).not.toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
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
      configuration: { engine: 'mssql', host: 'localhost', port: 1433, database: 'Muhasebe', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Optional', stagingReference: 'default', storagePrefix: '', requiredVerifiedReplicaCount: 1,
        destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }] },
    })
    expect(inputs[1].configuration.database).toBe('CRM')
    expect(vi.mocked(startJobRun).mock.calls.map(call => call[1])).toEqual(['job-Muhasebe', 'job-CRM'])
  })

  it('"Sadece oluştur" creates the jobs without starting a backup, with the changed answers', async () => {
    const user = userEvent.setup()
    mount()
    await toSummary(user)
    await user.click(screen.getByRole('button', { name: /Ne zaman: Değiştir/ }))
    await user.selectOptions(screen.getByLabelText('Hazır zamanlamalar'), 'Elle çalıştır')
    await user.click(screen.getByRole('button', { name: /Saklama: Değiştir/ }))
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

  it('starts with adding a computer when there is none', async () => {
    vi.mocked(getAgents).mockResolvedValue([])
    vi.mocked(getAgentDistribution).mockResolvedValue({ serverUrl: 'https://backup.example', packages: {} } as never)
    mount()
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('Yedekleme bilgisayarı ekle')).toBeVisible()
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
