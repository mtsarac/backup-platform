import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, createRoutesFromElements, Route, RouterProvider } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgentDistribution, getAgents } from '@/features/agents/api'
import { saveBackupJob, startJobRun } from '@/features/backup-jobs/api'
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
  vi.mocked(startJobRun).mockImplementation(async id => ({ id: `run-${id}` }))
})

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
    expect(await screen.findByText('Ana sayfa içeriği')).toBeVisible()
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
    expect(await screen.findByText('Ana sayfa içeriği')).toBeVisible()
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
    expect(await screen.findByText('Ana sayfa içeriği')).toBeVisible()
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
})
