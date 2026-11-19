import userEvent from '@testing-library/user-event'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, createRoutesFromElements, Route, RouterProvider } from 'react-router-dom'
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
  const router = createMemoryRouter(createRoutesFromElements(<>
    <Route path="/tenants/:tenantId/discovery/instances/:instanceId" element={<InstanceDetailPage />} /><Route path="/tenants/:tenantId/backup-jobs/new" element={<BackupJobWizardPage />} /><Route path="/tenants/:tenantId/backup-jobs" element={<p>Saved jobs</p>} />
  </>), { initialEntries: ['/tenants/tenant-a/discovery/instances/inst-1'] })
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>)
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
    expect(await screen.findByText(/Veritabanlarını listelemek için giriş bilgilerini girin/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme işi oluştur/ })).not.toBeInTheDocument()
  })
  it('explains a login problem in one block and keeps the old list read-only', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail({ access: 'AuthenticationFailed', accessReasonCode: 'authentication_failed' }))
    mount()
    expect(await screen.findByText('Kimlik doğrulaması başarısız.')).toBeInTheDocument()
    expect(screen.getAllByText('Kimlik doğrulaması başarısız')).toHaveLength(1)
    expect(screen.queryByText(/eski bir aramadan kalmadır/)).not.toBeInTheDocument()
    expect(screen.getByText(/Giriş bilgilerini kontrol edip yeniden kaydedin/)).toBeInTheDocument()
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toBeVisible()
    expect(screen.getByRole('table')).toHaveTextContent('ERP')
    expect(screen.queryByLabelText('ERP seç')).not.toBeInTheDocument()
    expect(screen.queryByText(/henüz bağlanılamadığı için/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme işi oluştur/ })).not.toBeInTheDocument()
  })
  describe('server certificate', () => {
    const trustTitle = 'Bu sunucunun sertifikasına yine de güven'
    const untrusted = (patch = {}) => makeDetail({ access: 'Error', accessReasonCode: 'server_certificate_untrusted', credential: { bound: true, credentialReference: 'sql-main', integratedSecurity: false, encrypt: true, trustServerCertificate: false }, ...patch })
    it('has no trust box in the credential form, and keeps the stored trust when the login is saved', async () => {
      vi.mocked(getDiscoveryInstance).mockResolvedValue(untrusted({ accessReasonCode: 'authentication_failed', access: 'AuthenticationFailed', credential: { bound: true, credentialReference: 'sql-main', integratedSecurity: false, encrypt: true, trustServerCertificate: true } }))
      mount()
      await screen.findByLabelText('Veritabanı kullanıcı adı')
      expect(screen.queryByRole('checkbox', { name: /Sunucu sertifikasına güven/ })).not.toBeInTheDocument()
      fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'sa' } })
      fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'Secret123!' } })
      fireEvent.click(screen.getByRole('button', { name: 'Kaydet ve doğrula' }))
      await waitFor(() => expect(saveDiscoveryCredential).toHaveBeenCalledWith('tenant-a', 'inst-1', expect.objectContaining({ trustServerCertificate: true })))
    })
    it('shows the closed step and the risk sentence for the certificate failure, with no checkbox anywhere', async () => {
      vi.mocked(getDiscoveryInstance).mockResolvedValue(untrusted())
      mount()
      expect((await screen.findByText(trustTitle)).closest('details')).not.toHaveAttribute('open')
      expect(screen.getByText('Bu sunucunun kimliği doğrulanamaz; yalnızca kendi sunucunuz olduğundan eminseniz açın.')).toBeInTheDocument()
      expect(screen.queryByRole('checkbox', { name: /sertifika/i })).not.toBeInTheDocument()
    })
    it.each(['connection_failed', 'unreachable', 'authentication_failed', 'invalid_configuration'])('shows nothing to trust for the general %s failure', async (accessReasonCode) => {
      vi.mocked(getDiscoveryInstance).mockResolvedValue(untrusted({ accessReasonCode }))
      mount()
      await screen.findByLabelText('Veritabanı kullanıcı adı')
      expect(screen.queryByText(trustTitle)).not.toBeInTheDocument()
      expect(screen.queryByText(/yalnızca kendi sunucunuz olduğundan eminseniz/)).not.toBeInTheDocument()
    })
    it('says the trust is permanent, binds the stored login with the trust on and scans again', async () => {
      vi.mocked(getDiscoveryInstance).mockResolvedValue(untrusted())
      mount()
      fireEvent.click(await screen.findByText(trustTitle))
      expect(screen.getByText('Bu sunucunun sertifikası kalıcı olarak güvenilir sayılacak.')).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Güven ve yeniden dene' }))
      await waitFor(() => expect(saveDiscoveryCredential).toHaveBeenCalledWith('tenant-a', 'inst-1', { credentialReference: 'sql-main', databaseLogin: null, integratedSecurity: false, encrypt: true, trustServerCertificate: true }))
      await waitFor(() => expect(verifyDiscoveryInstance).toHaveBeenCalledWith('tenant-a', 'inst-1'))
    })
    it('is not offered to a Viewer', async () => {
      Object.assign(access, { canOperate: false, canAdmin: false })
      vi.mocked(getDiscoveryInstance).mockResolvedValue(untrusted())
      mount()
      await screen.findByText(/Sunucunun sertifikası doğrulanamadı/)
      expect(screen.queryByText(trustTitle)).not.toBeInTheDocument()
    })
  })
  it('says the missing address once instead of on every database row', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail({ port: null }))
    mount()
    expect(await screen.findAllByText(/adresi bilinmediği için/)).toHaveLength(1)
    expect(screen.queryByLabelText('ERP seç')).not.toBeInTheDocument()
    expect(screen.queryByText(/henüz bağlanılamadığı için/)).not.toBeInTheDocument()
  })
  it('links back to the computer and keeps per-database lines out of the technical details', async () => {
    mount()
    expect(await screen.findByRole('link', { name: 'SQL Agent' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-a')
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    expect(within(screen.getByRole('region', { name: 'Teknik ayrıntılar' })).queryByText('ERP')).not.toBeInTheDocument()
  })
  it('prefills the ticket 10 wizard and creates only the picked databases after confirmation', async () => {
    mount()
    expect(await screen.findByLabelText('ERP seç')).toBeEnabled()
    expect(screen.getByLabelText('master seç')).toBeDisabled()
    expect(screen.getByLabelText('Covered seç')).toBeDisabled()
    expect(screen.getByText('Sistem veritabanı; yedeklenmez.')).toBeInTheDocument()
    expect(screen.getByText('Zaten bir yedekleme işi koruyor.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Covered job' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-9')
    expect(screen.getByRole('button', { name: /Yedekleme işi oluştur/ })).toBeDisabled()
    fireEvent.click(screen.getByLabelText('ERP seç'))
    fireEvent.click(screen.getByLabelText('CRM seç'))
    fireEvent.click(screen.getByRole('button', { name: /Yedekleme işi oluştur/ }))
    fireEvent.change(await screen.findByLabelText('İş adı'), { target: { value: 'Finance' } })
    expect(await screen.findByRole('heading', { name: 'Seçilen kaynak' })).toBeInTheDocument()
    expect(screen.getByText('ERP, CRM')).toBeInTheDocument()
    expect(screen.queryByLabelText('Yedekleme bilgisayarı')).not.toBeInTheDocument()
    for (let i = 0; i < 3; i++) fireEvent.click(screen.getByRole('button', { name: 'İleri' }))
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
    const trigger = await screen.findByRole('button', { name: 'Teknik ayrıntılar' })
    expect(screen.queryByText('inst-1')).not.toBeInTheDocument()
    await userEvent.click(trigger)
    const details = within(screen.getByRole('region', { name: 'Teknik ayrıntılar' }))
    expect(details.getByText('inst-1')).toBeInTheDocument()
    expect(details.getByText('16.0')).toBeInTheDocument()
    expect(details.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })
  it('asks for credentials without revealing a stored password', async () => {
    mount()
    fireEvent.click(await screen.findByText('Teknik ayrıntılar'))
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toHaveValue('')
    expect(screen.getByLabelText('Veritabanı parolası')).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Bağlantıyı doğrula' })).not.toBeInTheDocument()
  })
  it('saves the typed credentials, verifies the connection by itself and keeps the typed values so a wrong character can be fixed', async () => {
    vi.mocked(verifyDiscoveryInstance).mockResolvedValue(makeStatus())
    mount()
    fireEvent.click(await screen.findByText('Teknik ayrıntılar'))
    fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'sa' } })
    fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'Secret123!' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet ve doğrula' }))
    await waitFor(() => expect(saveDiscoveryCredential).toHaveBeenCalledWith('tenant-a', 'inst-1', {
      credentialReference: 'sql-main', databaseLogin: { userName: 'sa', password: 'Secret123!' }, integratedSecurity: false, encrypt: true, trustServerCertificate: true,
    }))
    await waitFor(() => expect(verifyDiscoveryInstance).toHaveBeenCalledWith('tenant-a', 'inst-1'))
    expect(await screen.findByText(/Doğrulama istendi/)).toBeInTheDocument()
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toHaveValue('sa')
    expect(screen.getByLabelText('Veritabanı parolası')).toHaveValue('Secret123!')
    fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'Secret124!' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet ve doğrula' }))
    await waitFor(() => expect(saveDiscoveryCredential).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(verifyDiscoveryInstance).toHaveBeenCalledTimes(2))
  })
  it('does not verify when saving the credentials failed, and keeps what was typed', async () => {
    vi.mocked(saveDiscoveryCredential).mockRejectedValue(new ApiError(500, { error: 'internal' }))
    mount()
    fireEvent.click(await screen.findByText('Teknik ayrıntılar'))
    fireEvent.change(screen.getByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'sa' } })
    fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'Secret123!' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet ve doğrula' }))
    await waitFor(() => expect(saveDiscoveryCredential).toHaveBeenCalled())
    expect(verifyDiscoveryInstance).not.toHaveBeenCalled()
    expect(await screen.findByLabelText('Veritabanı parolası')).toHaveValue('Secret123!')
  })
  it('shows a failed save in the single notice block at the top, not next to the form', async () => {
    vi.mocked(getDiscoveryInstance).mockResolvedValue(makeDetail({ access: 'AuthenticationFailed', accessReasonCode: 'authentication_failed' }))
    vi.mocked(saveDiscoveryCredential).mockRejectedValue(new ApiError(500, { error: 'internal' }))
    mount()
    fireEvent.change(await screen.findByLabelText('Veritabanı kullanıcı adı'), { target: { value: 'sa' } })
    fireEvent.change(screen.getByLabelText('Veritabanı parolası'), { target: { value: 'Secret123!' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet ve doğrula' }))
    const notices = await screen.findByRole('region', { name: 'Uyarılar' })
    expect(within(notices).getAllByRole('alert').length).toBeGreaterThan(0)
    expect(within(notices).getByText(/Giriş bilgilerini kontrol edip yeniden kaydedin/)).toBeInTheDocument()
    const form = screen.getByLabelText('Veritabanı parolası').closest('form')!
    expect(within(form).queryByRole('alert')).not.toBeInTheDocument()
  })
  it('is read-only for a Viewer', async () => {
    Object.assign(access, { canOperate: false, canAdmin: false })
    mount()
    expect(await screen.findByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByLabelText('ERP seç')).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.queryByRole('button', { name: 'Kaydet ve doğrula' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Yedekleme işi oluştur/ })).not.toBeInTheDocument()
  })
})
