import { fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, createRoutesFromElements, Route, RouterProvider } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { saveBackupJob } from '@/features/backup-jobs/api'
import { getDiscoveryAgents, getDiscoveryInstance, getDiscoveryInstances } from '@/features/discovery/api'
import { makeDetail, makeInstance, makeStatus } from '@/features/discovery/test-fixtures'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { BackupJobWizardPage } from './backup-job-wizard-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([{ agentId: 'agent-a', hostName: 'SQL Agent', operationalStatus: 'Online', capabilities: [{ identifier: 'discovery.database-inventory', support: 'Supported' }] }]) }))
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/discovery/api')
vi.mock('@/features/backup-jobs/api', () => ({ saveBackupJob: vi.fn(), getBackupJob: vi.fn(), getBackupJobHealth: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), startJobRun: vi.fn(), setJobEnabled: vi.fn() }))

function mount(route = '/tenants/tenant-a/backup-jobs/new') {
  const router = createMemoryRouter(createRoutesFromElements(<>
    <Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} /><Route path="/tenants/:tenantId/backup-jobs" element={<p>Job listesi</p>} />
  </>), { initialEntries: [route] })
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>)
}
const next = (times = 1) => { for (let i = 0; i < times; i++) fireEvent.click(screen.getByRole('button', { name: 'İleri' })) }
async function toSummary() {
  fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'ERP' } })
  next(3)
}
beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(access, { canOperate: true, canAdmin: true })
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [makeInstance()] })
  vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail())
  vi.mocked(saveBackupJob).mockResolvedValue({} as never)
})

describe('wizard with discovered databases', () => {
  it('prefills the source from the link and creates one job per database only after Kaydet', async () => {
    mount('/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1&database=ERP&database=CRM&database=master&database=Covered')
    await toSummary()
    expect(saveBackupJob).not.toHaveBeenCalled()
    expect(screen.getByText(/ERP – CRM/)).toBeInTheDocument()
    expect(screen.queryByText(/ERP – master/)).not.toBeInTheDocument()
    expect(screen.queryByText(/ERP – Covered/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(2))
    const calls = vi.mocked(saveBackupJob).mock.calls.map(call => call[1])
    expect(calls.map(input => input.name)).toEqual(['ERP – ERP', 'ERP – CRM'])
    expect(calls[0]).toMatchObject({ agentId: 'agent-a', configuration: { engine: 'mssql', host: 'localhost', port: 1433, database: 'ERP', databaseCredentialReference: 'sql-main', integratedSecurity: false, trustServerCertificate: true } })
    expect(calls[1].configuration.database).toBe('CRM')
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
  })
  it('reports per-database results and retries only the failed one', async () => {
    vi.mocked(saveBackupJob).mockResolvedValueOnce({} as never).mockRejectedValueOnce(new ApiError(400, { error: 'invalid_request' }))
    mount('/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1&database=ERP&database=CRM')
    await toSummary()
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByText(/Oluşturulamadı/)).toBeInTheDocument()
    expect(screen.getByText(/Oluşturuldu/)).toBeInTheDocument()
    expect(screen.queryByText('Job listesi')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Başarısızları yeniden dene' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(3))
    expect(vi.mocked(saveBackupJob).mock.calls[2][1].configuration.database).toBe('CRM')
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
  })
  it('does not create a job again that the inventory already shows for a failed create', async () => {
    vi.mocked(saveBackupJob).mockResolvedValueOnce({} as never).mockRejectedValueOnce(new Error('timeout'))
    mount('/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1&database=ERP&database=CRM')
    await toSummary()
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByText(/Oluşturulamadı/)).toBeInTheDocument()
    const detail = makeDetail()
    vi.mocked(getDiscoveryInstance).mockResolvedValue({ ...detail, databases: detail.databases.map(database => database.name === 'CRM' ? { ...database, jobs: [{ id: 'job-9', name: 'ERP – CRM', enabled: true }] } : database) })
    fireEvent.click(screen.getByRole('button', { name: 'Başarısızları yeniden dene' }))
    expect(await screen.findByText('Job listesi')).toBeInTheDocument()
    expect(saveBackupJob).toHaveBeenCalledTimes(2)
  })
  it('keeps manual entry reachable from the start, with its own computer question and validation', async () => {
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Elle yapılandır' }))
    await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
    next()
    expect(screen.getByLabelText('İş adı')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('Yedekleme bilgisayarı')).toHaveAttribute('aria-invalid', 'true')
    fireEvent.change(screen.getByLabelText('Yedekleme bilgisayarı'), { target: { value: 'agent-a' } })
    expect(screen.getByLabelText('Veritabanı')).toBeEnabled()
    expect(screen.queryByRole('button', { name: /Seçimi uygula/ })).not.toBeInTheDocument()
  })
})

describe('discovery-first source step', () => {
  it('shows a link-prefilled source as a read-only summary and never asks for the computer', async () => {
    mount('/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1&database=ERP&database=CRM')
    expect(await screen.findByRole('heading', { name: 'Seçilen kaynak' })).toBeInTheDocument()
    const summary = screen.getByRole('heading', { name: 'Seçilen kaynak' }).closest('div')!.parentElement!
    await waitFor(() => expect(summary).toHaveTextContent('SQL Agent'))
    expect(summary).toHaveTextContent('SQL Server (default) · MSSQL · localhost:1433')
    expect(summary).toHaveTextContent('ERP, CRM')
    expect(screen.queryByLabelText('Yedekleme bilgisayarı')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Veritabanı')).not.toBeInTheDocument()
    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
  })
  it('carries computer, instance and database from a pick made in the wizard, and Değiştir goes back to the choice', async () => {
    mount()
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'ERP' } })
    fireEvent.change(await screen.findByLabelText('Veritabanı Sunucusu'), { target: { value: 'inst-1' } })
    fireEvent.click(await screen.findByLabelText('ERP seç'))
    fireEvent.click(screen.getByRole('button', { name: 'Seçimi uygula (1)' }))
    expect(await screen.findByRole('heading', { name: 'Seçilen kaynak' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Yedekleme bilgisayarı')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir' }))
    expect(await screen.findByLabelText('Veritabanı Sunucusu')).toHaveValue('')
    expect(screen.queryByRole('heading', { name: 'Seçilen kaynak' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Veritabanı Sunucusu'), { target: { value: 'inst-1' } })
    fireEvent.click(await screen.findByLabelText('CRM seç'))
    fireEvent.click(screen.getByRole('button', { name: 'Seçimi uygula (1)' }))
    await screen.findByRole('heading', { name: 'Seçilen kaynak' })
    next(3)
    fireEvent.click(await screen.findByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][1]).toMatchObject({ agentId: 'agent-a', configuration: { engine: 'mssql', host: 'localhost', port: 1433, database: 'CRM', databaseCredentialReference: 'sql-main' } })
  })
  it('will not move on without a source and says so', async () => {
    mount()
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'ERP' } })
    next()
    expect(screen.getByRole('alert')).toHaveTextContent('Otomatik keşiften bir veritabanı seçin veya kaynağı elle yapılandırın.')
    expect(screen.queryByLabelText('Depolama bağlantısı')).not.toBeInTheDocument()
  })
  it('points to the discovery page when the computer has no usable server', async () => {
    vi.mocked(getDiscoveryInstances).mockResolvedValue({ items: [] })
    mount()
    expect(await screen.findByText('Bu bilgisayarda kullanılabilir veritabanı sunucusu yok.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Veritabanı bulma sayfasına gidin' })).toHaveAttribute('href', '/tenants/tenant-a/discovery')
    expect(screen.getByRole('button', { name: 'Elle yapılandır' })).toBeInTheDocument()
  })
  it('opens the choice on the linked computer and instance when none of the linked databases can be picked any more', async () => {
    mount('/tenants/tenant-a/backup-jobs/new?discoveryInstance=inst-1&database=Covered')
    expect(await screen.findByLabelText('Veritabanı Sunucusu')).toHaveValue('inst-1')
    expect(screen.queryByRole('heading', { name: 'Seçilen kaynak' })).not.toBeInTheDocument()
  })
  it('reports a link whose instance cannot be loaded and still offers the choice', async () => {
    vi.mocked(getDiscoveryInstance).mockRejectedValue(new ApiError(404, { error: 'not_found' }))
    mount('/tenants/tenant-a/backup-jobs/new?discoveryInstance=gone&database=ERP')
    expect(await screen.findByText('Keşfedilen veritabanları yüklenemedi')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Elle yapılandır' })).toBeInTheDocument()
  })
})
