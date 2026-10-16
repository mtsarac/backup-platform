import { fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
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
vi.mock('@/features/backup-jobs/api', () => ({ saveBackupJob: vi.fn(), getBackupJob: vi.fn(), getBackupJobs: vi.fn(), getOccurrences: vi.fn(), startJobRun: vi.fn(), setJobEnabled: vi.fn() }))

function mount(route = '/tenants/tenant-a/backup-jobs/new') {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter initialEntries={[route]}><Routes>
    <Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} /><Route path="/tenants/:tenantId/backup-jobs" element={<p>Job listesi</p>} />
  </Routes></MemoryRouter></QueryClientProvider>)
}
const next = (times = 1) => { for (let i = 0; i < times; i++) fireEvent.click(screen.getByRole('button', { name: 'İleri' })) }
async function toSummary() {
  fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'ERP' } })
  next(5)
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
  it('keeps manual entry working and makes no discovery request until the picker is opened', async () => {
    mount()
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'ERP' } })
    await screen.findByRole('option', { name: /(?:SQL Agent|SQL Ajanı)/ })
    fireEvent.change(screen.getByLabelText("Ajan"), { target: { value: 'agent-a' } })
    next()
    expect(screen.getByLabelText("Veritabanı")).toBeEnabled()
    expect(getDiscoveryInstances).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Keşfedilen veritabanlarından seç' }))
    expect(screen.getByLabelText("Keşif Ajanı")).toBeInTheDocument()
  })
})
