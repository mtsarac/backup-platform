import { act, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent, { type UserEvent } from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from './api'
import { makeConnection, makeProvider, tenantAdminPermissions } from './test-fixtures'
import { ConnectionWizard } from './connection-wizard'
import type { ConnectionTestResponse, ConnectionView, ProviderDescriptor } from './types'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }) }))
vi.mock('./api')

const site = { name: 'site', label: 'Physical site / failure domain', kind: 'Text' as const, required: false, maxLength: 64 }
const mediaClass = { name: 'mediaClass', label: 'Media class (disk, object-storage, tape)', kind: 'Text' as const, required: false, maxLength: 32 }
const text = (name: string, label: string, required = true) => ({ name, label, kind: 'Text' as const, required, maxLength: 100 })
const creatable = { availability: 'Available' as const, tenantCreatable: true, maxConnectionsPerTenant: null }

const providers: ProviderDescriptor[] = [
  makeProvider({ ...creatable, providerType: 'google-drive', displayName: 'Google Drive', topology: 'BackendRelayed', executionLocation: 'Backend', testMode: 'BackendRoundTrip', configFields: [text('accountEmail', 'Google hesabı', false), site, mediaClass], secretFields: [{ name: 'refreshToken', label: 'Refresh token', required: false }] }),
  makeProvider({
    ...creatable, providerType: 'sftp', displayName: 'SFTP', topology: 'BackendRelayed', executionLocation: 'Backend', testMode: 'BackendRoundTrip',
    configFields: [text('host', 'Host'), { name: 'port', label: 'Port', kind: 'Number', required: false, maxLength: 5 }, text('username', 'Username'), text('authenticationType', 'Authentication type'), text('remoteBaseDirectory', 'Remote base directory'), text('hostKeyFingerprint', 'SSH host key fingerprint (SHA256)'), site, mediaClass],
    secretFields: [{ name: 'password', label: 'Password', required: false }, { name: 'privateKey', label: 'SSH private key', required: false }, { name: 'passphrase', label: 'Private key passphrase', required: false }],
  }),
  makeProvider({ ...creatable, providerType: 'local', displayName: 'Local (Agent diski)', maxConnectionsPerTenant: 1, configFields: [text('volume', "Disk adı (agent'ta tanımlı)"), site, mediaClass] }),
  makeProvider({
    ...creatable, providerType: 's3', displayName: 'S3', topology: 'BackendRelayed', executionLocation: 'Backend', testMode: 'BackendReadAccess',
    configFields: [text('endpoint', 'HTTPS endpoint'), text('bucket', 'Bucket'), text('region', 'Signing region'), text('objectLockMode', 'Object Lock (COMPLIANCE or GOVERNANCE)', false), { name: 'retentionDays', label: 'WORM retention days', kind: 'Number', required: false, maxLength: 5 }, site, mediaClass],
    secretFields: [{ name: 'accessKey', label: 'Access key', required: true }, { name: 'secretKey', label: 'Secret key', required: true }, { name: 'sessionToken', label: 'Session token', required: false }],
  }),
  makeProvider({
    ...creatable, providerType: 'smb', displayName: 'SMB / NAS', testMode: 'AgentSide',
    configFields: [text('host', 'NAS host'), text('share', 'Share name'), site, mediaClass],
    secretFields: [{ name: 'username', label: 'Username', required: true }, { name: 'password', label: 'Password', required: true }, { name: 'domain', label: 'Domain', required: false }],
  }),
  makeProvider({ providerType: 'central', displayName: 'Managed Storage (Central)', availability: 'Available', tenantCreatable: false, topology: 'BackendRelayed', executionLocation: 'Backend', supportedOwnerships: ['PlatformManaged'], testMode: 'BackendRoundTrip' }),
]

const saved = (overrides: Partial<ConnectionView> = {}) => makeConnection({ id: 'new', name: 'SFTP (backup.internal)', providerType: 'sftp', providerDisplayName: 'SFTP', isDefault: false, hasSecret: true, permissions: { ...tenantAdminPermissions }, ...overrides })
const tested = (connection: ConnectionView, status: 'Succeeded' | 'Failed' | 'NotTestable', code: string | null = 'ok', message = ''): ConnectionTestResponse => ({ connection, test: { status, code, message, testedAt: '2026-10-06T10:00:00Z', durationMs: 12 } })

function Where() {
  const { pathname, state } = useLocation()
  return <p data-testid="where">{pathname} {JSON.stringify(state)}</p>
}

function mount(entry: string | { pathname: string; state: unknown } = '/storage/connections/new') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[entry]}><Routes>
    <Route path="/storage/connections/new" element={<ConnectionWizard />} />
    <Route path="*" element={<Where />} />
  </Routes></MemoryRouter></QueryClientProvider>)
}

const next = () => userEvent.click(screen.getByRole('button', { name: 'İleri' }))
const create = () => userEvent.click(screen.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }))

async function fillSftp(user: UserEvent) {
  await user.click(await screen.findByRole('radio', { name: /SFTP/ }))
  await next()
  await user.type(screen.getByLabelText('Sunucu adresi'), 'backup.internal')
  await user.type(screen.getByLabelText('Kullanıcı adı'), 'backup-user')
  await user.type(screen.getByLabelText('Yedeklerin saklanacağı klasör'), '/backups')
  await user.type(screen.getByLabelText('Sunucunun güvenlik parmak izi (SHA256)'), 'SHA256:trusted')
  await user.type(screen.getByLabelText('Parola'), 'hunter2-secret')
  await next()
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenantConnections).mockResolvedValue({ items: [] })
  vi.mocked(api.getTenantProviders).mockResolvedValue({ items: providers })
})

describe('Storage connection wizard: providers and steps', () => {
  it('lists Google Drive, SFTP and Yerel disk first, S3 and SMB under "Diğer depolama", and central as a passive card', async () => {
    mount()
    await screen.findByRole('radio', { name: /Google Drive/ })
    const radios = screen.getAllByRole('radio').map((radio) => radio.closest('label')?.textContent ?? '')
    expect(radios.map((label) => /Google Drive|SFTP|Yerel disk|S3|SMB|Merkezi/.exec(label)?.[0])).toEqual(['Google Drive', 'SFTP', 'Yerel disk', 'S3', 'SMB', 'Merkezi'])
    const other = screen.getByRole('heading', { name: 'Diğer depolama' })
    expect(other.compareDocumentPosition(screen.getByRole('radio', { name: /S3/ })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(other.compareDocumentPosition(screen.getByRole('radio', { name: /Yerel disk/ })) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy()
    expect(screen.getByRole('radio', { name: /Merkezi/ })).toBeDisabled()
  })

  it('shows at most three steps for every provider', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('radio', { name: /SFTP/ }))
    expect(within(screen.getByRole('navigation', { name: 'Adımlar' })).getAllByRole('listitem')).toHaveLength(3)
    await user.click(screen.getByRole('radio', { name: /Google Drive/ }))
    const steps = within(screen.getByRole('navigation', { name: 'Adımlar' })).getAllByRole('listitem')
    expect(steps).toHaveLength(3)
    expect(steps[1]).toHaveTextContent('Google ile bağlan')
  })
})

describe('Storage connection wizard: details', () => {
  it('asks SFTP for address with a filled Port 22 beside it, user, folder, sign-in method and a fingerprint explained in one sentence with an example', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('radio', { name: /SFTP/ }))
    await next()
    expect(screen.getByLabelText('Sunucu adresi')).toBeInTheDocument()
    expect(screen.getByLabelText('Bağlantı noktası')).toHaveValue(22)
    for (const label of ['Kullanıcı adı', 'Yedeklerin saklanacağı klasör', 'Giriş yöntemi', 'Sunucunun güvenlik parmak izi (SHA256)']) expect(screen.getByLabelText(label)).toBeInTheDocument()
    expect(screen.getByText(/ssh-keygen -lf/)).toHaveTextContent(/SHA256:/)
    expect(screen.queryByLabelText(/Physical site|Media class|Fiziksel|Ortam/)).not.toBeInTheDocument()
    expect(screen.getByLabelText('Parola')).toHaveAttribute('type', 'password')
    await user.selectOptions(screen.getByLabelText('Giriş yöntemi'), 'private-key')
    expect(screen.queryByLabelText('Parola')).not.toBeInTheDocument()
    expect(screen.getByLabelText('SSH özel anahtarı')).toBeInTheDocument()
  })

  it('hides Object Lock, WORM days and session token for S3, and the domain for SMB', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('radio', { name: /S3/ }))
    await next()
    expect(screen.getByLabelText('Kova')).toBeInTheDocument()
    expect(screen.getByLabelText('Erişim anahtarı')).toBeInTheDocument()
    for (const label of [/Object Lock/, /WORM/, /Oturum/, /Session/]) expect(screen.queryByLabelText(label)).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Geri' }))
    await user.click(screen.getByRole('radio', { name: /SMB/ }))
    await next()
    expect(screen.getByLabelText('Paylaşım adı')).toBeInTheDocument()
    expect(screen.queryByLabelText(/Domain|Alan adı/)).not.toBeInTheDocument()
  })

  it('takes a free-text "Disk adı" for Yerel disk', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('radio', { name: /Yerel disk/ }))
    await next()
    expect(screen.getByLabelText('Disk adı')).toHaveAttribute('type', 'text')
    expect(screen.getByText('Yedekleme bilgisayarında tanımlı disk adı')).toBeInTheDocument()
  })
})

describe('Storage connection wizard: generated name', () => {
  it.each([
    ['sftp', /SFTP/, 'Sunucu adresi', 'sunucu', 'SFTP (sunucu)'],
    ['local', /Yerel disk/, 'Disk adı', 'D', 'Yerel disk (D)'],
    ['smb', /SMB/, 'Paylaşım adı', 'paylaşım', 'NAS (paylaşım)'],
    ['s3', /S3/, 'Kova', 'bucket', 'S3 (bucket)'],
  ])('names %s from its identifying field and lets it be changed on the summary', async (type, radio, label, value, expected) => {
    const user = userEvent.setup()
    const connection = saved({ providerType: type, name: expected })
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connection, 'Succeeded'))
    mount()
    await user.click(await screen.findByRole('radio', { name: radio }))
    await next()
    await user.type(screen.getByLabelText(label), value)
    for (const required of screen.queryAllByRole('textbox')) if ((required as HTMLInputElement).value === '' && required.hasAttribute('required')) await user.type(required, 'x')
    for (const spin of screen.queryAllByRole('spinbutton')) if ((spin as HTMLInputElement).value === '') await user.type(spin, '1')
    for (const secret of document.querySelectorAll<HTMLInputElement>('input[type=password]')) await user.type(secret, 'secret-value')
    await next()
    expect(screen.getByText(expected)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Adı değiştir' }))
    const name = screen.getByRole('textbox', { name: 'Ad' })
    expect(name).toHaveValue(expected)
    await user.clear(name)
    await user.type(name, 'Ofis deposu')
    await create()
    await screen.findByText('Bağlantı çalışıyor')
    expect(vi.mocked(api.createTenantConnection).mock.calls[0][1]).toMatchObject({ providerType: type, name: 'Ofis deposu' })
  })

  it('sends the generated name unchanged when it is left alone, and never sends hidden fields', async () => {
    const user = userEvent.setup()
    const connection = saved()
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connection, 'Succeeded'))
    mount()
    await fillSftp(user)
    await create()
    await screen.findByText('Bağlantı çalışıyor')
    expect(api.createTenantConnection).toHaveBeenCalledExactlyOnceWith('tenant-a', {
      providerType: 'sftp', name: 'SFTP (backup.internal)',
      configuration: { host: 'backup.internal', port: '22', username: 'backup-user', authenticationType: 'password', remoteBaseDirectory: '/backups', hostKeyFingerprint: 'SHA256:trusted' },
      secret: { password: 'hunter2-secret' },
    })
  })
})

describe('Storage connection wizard: result', () => {
  it('reports a working connection and returns to Depolama', async () => {
    const user = userEvent.setup()
    const connection = saved()
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connection, 'Succeeded'))
    mount()
    await fillSftp(user)
    await create()
    expect(await screen.findByText('Bağlantı çalışıyor')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Depolamaya dön' })).toHaveAttribute('href', '/tenants/tenant-a/storage')
    expect(document.body.textContent).not.toContain('hunter2-secret')
  })

  it('names one reason, says it was saved but not verified, keeps the raw message in closed details and offers only the fix and the retest', async () => {
    const user = userEvent.setup()
    const connection = saved()
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connection, 'Failed', 'authentication_failed', 'Permission denied (publickey).'))
    mount()
    await fillSftp(user)
    await create()
    const alert = within(await screen.findByRole('alert'))
    expect(alert.getByText(/kaydedildi ama doğrulanmadı/i)).toBeInTheDocument()
    expect(screen.queryByText('Permission denied (publickey).')).not.toBeInTheDocument()
    await user.click(alert.getByText('Teknik ayrıntılar'))
    expect(await within(screen.getByRole('region', { name: 'Teknik ayrıntılar' })).findByText('Permission denied (publickey).')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Bilgileri düzelt' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Yeniden test et' })).toBeInTheDocument()
    expect(screen.queryByText('Yapılandırmayı düzenle')).not.toBeInTheDocument()
    expect(screen.queryByText('Bağlantıyı görüntüle')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Yeniden test et' }))
    await waitFor(() => expect(api.testTenantConnection).toHaveBeenCalledTimes(2))
  })

  it('"Bilgileri düzelt" returns to the details with the credential cleared and corrects the saved connection instead of adding another', async () => {
    const user = userEvent.setup()
    const connection = saved()
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValueOnce(tested(connection, 'Failed', 'authentication_failed')).mockResolvedValueOnce(tested(connection, 'Succeeded'))
    vi.mocked(api.updateTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.setTenantConnectionSecret).mockResolvedValue(connection)
    mount()
    await fillSftp(user)
    await create()
    await user.click(await screen.findByRole('button', { name: 'Bilgileri düzelt' }))
    expect(screen.getByLabelText('Sunucu adresi')).toHaveValue('backup.internal')
    expect(screen.getByLabelText('Parola')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Kaydet ve yeniden test et' })).toBeDisabled()
    await user.type(screen.getByLabelText('Parola'), 'new-secret-value')
    await user.click(screen.getByRole('button', { name: 'Kaydet ve yeniden test et' }))
    expect(await screen.findByText('Bağlantı çalışıyor')).toBeInTheDocument()
    expect(api.createTenantConnection).toHaveBeenCalledOnce()
    expect(api.updateTenantConnection).toHaveBeenCalledWith('tenant-a', 'new', expect.objectContaining({ name: 'SFTP (backup.internal)' }))
    expect(api.setTenantConnectionSecret).toHaveBeenCalledWith('tenant-a', 'new', { password: 'new-secret-value' })
  })

  it.each([['local', /Yerel disk/, 'Disk adı'], ['smb', /SMB/, 'NAS adı']])('does not present %s as a success but as a neutral note', async (type, radio, label) => {
    const user = userEvent.setup()
    const connection = saved({ providerType: type, name: 'x' })
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connection, 'NotTestable', 'agent_side_storage'))
    mount()
    await user.click(await screen.findByRole('radio', { name: radio }))
    await next()
    for (const field of screen.getAllByRole('textbox')) await user.type(field, 'D')
    for (const secret of document.querySelectorAll<HTMLInputElement>('input[type=password]')) await user.type(secret, 'secret-value')
    expect(label).toBeTruthy()
    await next()
    await create()
    expect(await screen.findByText('Yedekleme bilgisayarı ilk yedekte denetler; sorun olursa orada görürsünüz.')).toBeInTheDocument()
    expect(screen.queryByText('Bağlantı çalışıyor')).not.toBeInTheDocument()
  })

  it('offers "Bu depolamayı kullan" when the wizard was opened from a job draft and hands the new connection back through route state', async () => {
    const user = userEvent.setup()
    const connection = saved()
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connection, 'Succeeded'))
    mount({ pathname: '/storage/connections/new', state: { returnTo: '/jobs/new', returnState: { draft: 1 } } })
    await fillSftp(user)
    await create()
    await user.click(await screen.findByRole('button', { name: 'Bu depolamayı kullan' }))
    expect(screen.queryByRole('link', { name: 'Depolamaya dön' })).not.toBeInTheDocument()
    expect(screen.getByTestId('where')).toHaveTextContent('/jobs/new {"returnState":{"draft":1},"storageConnectionId":"new"}')
  })

  it('returns to the draft without a connection when cancelled from the first step', async () => {
    const user = userEvent.setup()
    mount({ pathname: '/storage/connections/new', state: { returnTo: '/jobs/new', returnState: { draft: 1 } } })
    await screen.findByRole('radio', { name: /SFTP/ })
    await user.click(screen.getByRole('link', { name: 'Vazgeç' }))
    expect(screen.getByTestId('where')).toHaveTextContent('/jobs/new {"returnState":{"draft":1}}')
  })

  it('returns to the storage list when cancelled from the Depolama page', async () => {
    mount()
    await screen.findByRole('radio', { name: /SFTP/ })
    expect(screen.getByRole('link', { name: 'Vazgeç' })).toHaveAttribute('href', '/tenants/tenant-a/storage')
  })
})

describe('Storage connection wizard: Google Drive', () => {
  const authorizationUrl = `https://accounts.google.com/o/oauth2/v2/auth?redirect_uri=${encodeURIComponent('http://localhost:5252/api/platform/google-drive/oauth/callback')}`
  const drive = () => makeConnection({ id: 'drive-new', name: 'Google Drive', providerType: 'google-drive', providerDisplayName: 'Google Drive', hasSecret: false, permissions: { ...tenantAdminPermissions } })

  async function start(popup: { close: () => void; closed: boolean }) {
    const user = userEvent.setup()
    const connection = drive()
    vi.mocked(api.createTenantConnection).mockResolvedValue(connection)
    vi.mocked(api.getGoogleDriveAuthorizeUrl).mockResolvedValue({ authorizationUrl })
    vi.mocked(api.updateTenantConnection).mockResolvedValue(connection)
    vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window)
    mount()
    await user.click(await screen.findByRole('radio', { name: /Google Drive/ }))
    await next()
    expect(document.querySelector('input[type=password], textarea')).toBeNull()
    expect(screen.queryByRole('button', { name: 'İleri' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Kaydet ve Google ile bağlan' }))
    await waitFor(() => expect(window.open).toHaveBeenCalledWith(authorizationUrl, 'google_oauth', expect.any(String)))
    return { user, connection }
  }

  it('saves first, opens Google at once, tests by itself when consent is given and names the connection after the account', async () => {
    const popup = { close: vi.fn(), closed: false }
    const { connection } = await start(popup)
    expect(api.createTenantConnection).toHaveBeenCalledExactlyOnceWith('tenant-a', { providerType: 'google-drive', name: 'Google Drive', configuration: {} })
    expect(api.testTenantConnection).not.toHaveBeenCalled()
    const connected = { ...connection, hasSecret: true, secretUpdatedAt: '2026-10-30T10:00:00Z', configuration: { accountEmail: 'ofis@firma.com', accountName: 'Ofis', reauthorizationRequired: 'false' } }
    vi.mocked(api.getTenantConnections).mockResolvedValue({ items: [connected] })
    vi.mocked(api.testTenantConnection).mockResolvedValue(tested(connected, 'Succeeded'))
    act(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'GOOGLE_DRIVE_OAUTH_COMPLETED' }, origin: 'http://localhost:5252', source: popup as unknown as Window })))
    expect(await screen.findByText('Bağlantı çalışıyor')).toBeInTheDocument()
    expect(api.testTenantConnection).toHaveBeenCalledExactlyOnceWith('tenant-a', 'drive-new')
    await waitFor(() => expect(api.updateTenantConnection).toHaveBeenCalledWith('tenant-a', 'drive-new', { name: 'Google Drive (ofis@firma.com)', configuration: undefined }))
  })

  it('says the account is not connected yet when the window is closed, and offers "Google ile bağlan"', async () => {
    const popup = { close: vi.fn(), closed: false }
    await start(popup)
    popup.closed = true
    expect(await screen.findByText('Google hesabı henüz bağlanmadı', {}, { timeout: 6_000 })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Google ile bağlan' })).toBeInTheDocument()
    expect(screen.queryByText('Bağlantı çalışıyor')).not.toBeInTheDocument()
    expect(api.testTenantConnection).not.toHaveBeenCalled()
  }, 10_000)
})
