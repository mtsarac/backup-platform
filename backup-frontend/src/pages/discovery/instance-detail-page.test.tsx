import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getAgents } from '@/features/agents/api'
import { saveBackupJob } from '@/features/backup-jobs/api'
import { getDiscoveryAgents, getDiscoveryInstance, saveDiscoveryCredential, verifyDiscoveryInstance } from '@/features/discovery/api'
import { makeDatabase, makeDetail, makeStatus, supportedAgent } from '@/features/discovery/test-fixtures'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import { ApiError } from '@/lib/api/client'
import { BackupJobWizardPage } from '@/pages/backup-jobs/backup-job-wizard-page'
import { InstanceDetailPage } from './instance-detail-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme' }, canOperate: true, canAdmin: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/discovery/api')
vi.mock('@/features/agents/api')
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/backup-jobs/api')

function mount() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter initialEntries={['/tenants/tenant-a/discovery/instances/inst-1']}><Routes>
    <Route path="/tenants/:tenantId/discovery/instances/:instanceId" element={<InstanceDetailPage />} /><Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} /><Route path="/tenants/:tenantId/backup-jobs" element={<p>Saved jobs</p>} />
  </Routes></MemoryRouter></QueryClientProvider>)
}
beforeEach(() => {
  vi.clearAllMocks()
  Object.assign(access, { canOperate: true, canAdmin: true })
  vi.mocked(getAgents).mockResolvedValue([supportedAgent] as never)
  vi.mocked(getDiscoveryAgents).mockResolvedValue({ items: [makeStatus()] })
  vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail())
  vi.mocked(saveDiscoveryCredential).mockResolvedValue(makeDetail())
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection(), makeManaged()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
  vi.mocked(saveBackupJob).mockResolvedValue({} as never)
})

describe('instance detail page', () => {
  it('does not claim no databases exist when credentials are required', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail({ access: 'CredentialsRequired', databases: [], credential: { bound: false, credentialReference: null, integratedSecurity: false, encrypt: true, trustServerCertificate: false } }))
    mount()
    expect(await screen.findByText(/veritabanları listelenemedi/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme işi oluştur/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Bağlantıyı doğrula' })).toBeDisabled()
  })
  it('prefills the ticket 10 wizard and creates only the picked databases after confirmation', async () => {
    mount()
    expect(await screen.findByLabelText('ERP seç')).toBeEnabled()
    expect(screen.getByLabelText('master seç')).toBeDisabled()
    expect(screen.getByLabelText('Covered seç')).toBeDisabled()
    expect(screen.getByText('Sistem veritabanı; yedeklenmez.')).toBeInTheDocument()
    expect(screen.getByText('Zaten bir yedekleme işi koruyor.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Covered job' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-9/edit')
    expect(screen.getByRole('button', { name: /Yedekleme işi oluştur/ })).toBeDisabled()
    fireEvent.click(screen.getByLabelText('ERP seç'))
    fireEvent.click(screen.getByLabelText('CRM seç'))
    fireEvent.click(screen.getByRole('button', { name: /Yedekleme işi oluştur/ }))
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'Finance' } })
    fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
    expect(screen.getByLabelText('Yedekleme bilgisayarı')).toHaveValue('agent-a')
    fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
    expect(screen.getByLabelText('Veritabanı')).toHaveValue('ERP')
    expect(screen.getByLabelText('Veritabanı')).toBeDisabled()
    for (let i = 0; i < 5; i++) fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
    expect(saveBackupJob).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(2))
    const jobs = vi.mocked(saveBackupJob).mock.calls.map(call => call[1])
    expect(jobs.map(job => job.configuration.database)).toEqual(['ERP', 'CRM'])
    expect(jobs[0]).toMatchObject({ agentId: 'agent-a', configuration: { host: 'localhost', port: 1433, engine: 'mssql', databaseCredentialReference: 'sql-main' } })
    expect(await screen.findByText('Saved jobs')).toBeInTheDocument()
  })
  it('keeps other unbackupable rows unselectable with a reason each', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail({ databases: [makeDatabase({ name: 'Offline', state: 'Offline' }), makeDatabase({ name: 'Unsupported', fullBackup: 'Unsupported', fullBackupReasonCode: 'unknown_code' }), makeDatabase({ name: 'Missing', presence: 'Missing' })] }))
    mount()
    for (const name of ['Offline', 'Unsupported', 'Missing']) expect(await screen.findByLabelText(`${name} seç`)).toBeDisabled()
    expect(screen.getByText('Veritabanı çevrimiçi değil.')).toBeInTheDocument()
    expect(screen.getByText('Bu veritabanı tam yedeği desteklemiyor.')).toBeInTheDocument()
    expect(screen.getByText('Son aramada bulunamadı.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Yedekleme işi oluştur/ })).toBeDisabled()
  })
  it('keeps identifiers, versions and raw reasons collapsed and copyable', async () => {
    mount()
    const summary = await screen.findByText('Teknik ayrıntılar')
    const details = summary.closest('details')!
    expect(details).not.toHaveAttribute('open')
    fireEvent.click(summary)
    expect(within(details).getByText('inst-1')).toBeInTheDocument()
    expect(within(details).getByText('16.0')).toBeInTheDocument()
    expect(within(details).getByRole('button', { name: 'Kopyala: Sunucu kimliği' })).toBeInTheDocument()
  })
  it('asks for credentials without revealing a stored password and verifies the connection', async () => {
    vi.mocked(verifyDiscoveryInstance).mockRejectedValue(new ApiError(409, { error: 'discovery_unsupported' }))
    mount()
    fireEvent.click(await screen.findByText('Teknik ayrıntılar'))
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toHaveValue('')
    expect(screen.getByLabelText('Veritabanı parolası')).toHaveValue('')
    fireEvent.click(screen.getByRole('button', { name: 'Bağlantıyı doğrula' }))
    await waitFor(() => expect(verifyDiscoveryInstance).toHaveBeenCalledWith('tenant-a', 'inst-1'))
    expect(await screen.findByText(/Yedekleme bilgisayarı sürümü keşfi desteklemiyor/)).toBeInTheDocument()
  })
  it('sends typed credentials once and clears them on submission', async () => {
    mount()
    fireEvent.click(await screen.findByText('Teknik ayrıntılar'))
    fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'sa' } })
    fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'Secret123!' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveDiscoveryCredential).toHaveBeenCalledWith('tenant-a', 'inst-1', {
      credentialReference: 'sql-main', databaseLogin: { userName: 'sa', password: 'Secret123!' }, integratedSecurity: false, encrypt: true, trustServerCertificate: true,
    }))
    expect(screen.getByLabelText('Veritabanı parolası')).toHaveValue('')
    expect(await screen.findByText(/Veritabanı girişi kaydedildi/)).toBeInTheDocument()
  })
  it('is read-only for a Viewer', async () => {
    Object.assign(access, { canOperate: false, canAdmin: false })
    mount()
    expect(await screen.findByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByLabelText('ERP seç')).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.queryByRole('button', { name: 'Kaydet' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Bağlantıyı doğrula' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme işi oluştur/ })).not.toBeInTheDocument()
  })
})
