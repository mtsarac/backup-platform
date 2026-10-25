import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { StorageProvidersTab } from '@/pages/storage/storage-providers-tab'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { catalog, fakeProvider, makeConnection, makeManaged, makeProvider, tenantAdminPermissions } from './test-fixtures'
import { ConnectionList } from './connection-list'
import { ConnectionDetail } from './connection-detail'
import { ConnectionWizard } from './connection-wizard'
import type { ConnectionView } from './types'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('./api')

const SECRET_A = 'ak-12345-secret'
const SECRET_B = 'sk-67890-secret'
const base = '/tenants/tenant-a/storage'
const detail = (id = 'conn-local', tab = 'overview') => `${base}/connections/${id}?tab=${tab}`
const s3 = (overrides: Partial<ConnectionView> = {}) => makeConnection({ id: 'conn-s3', name: 'S3 Arşiv', providerType: 'fake-s3', providerDisplayName: 'Fake S3', isDefault: false, quotaScope: 'tenant', permissions: { ...tenantAdminPermissions }, ...overrides })

function mount(path = base) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><Routes>
    <Route path={base} element={<ConnectionList />} />
    <Route path={`${base}/providers`} element={<StorageProvidersTab />} />
    <Route path={`${base}/connections/new`} element={<ConnectionWizard />} />
    <Route path={`${base}/connections/:connectionId`} element={<ConnectionDetail />} />
  </Routes></MemoryRouter></QueryClientProvider>)
  return { client, ...view }
}

function serve(items: ConnectionView[], providers = [...catalog, fakeProvider]) {
  vi.mocked(api.getTenantConnections).mockResolvedValue({ items })
  vi.mocked(api.getTenantProviders).mockResolvedValue({ items: providers })
}
const article = (name: string) => within(screen.getByRole('article', { name }))
const tab = (label: string) => userEvent.click(screen.getByRole('tab', { name: label }))

beforeEach(() => {
  vi.resetAllMocks()
  access.canAdmin = true
  serve([makeConnection({ permissions: { ...tenantAdminPermissions } }), makeManaged()])
})

describe('Storage connections list and detail', () => {
  it('lists each destination as a card with name, health, last test and provider; statistics stay collapsed', async () => {
    serve([
      makeConnection({ lastTest: { status: 'Succeeded', code: 'ok', message: '', testedAt: new Date(Date.now() - 3 * 3_600_000).toISOString() }, permissions: { ...tenantAdminPermissions } }),
      makeManaged(),
    ])
    mount()
    await screen.findByRole('article', { name: "Local" })
    const local = article('Local')
    expect(local.getByText('Yerel yedekleme bilgisayarı diski')).toBeInTheDocument()
    expect(local.getByText('Varsayılan')).toBeInTheDocument()
    expect(local.getByText('Sağlıklı')).toBeInTheDocument()
    expect(local.getByText('3 saat önce')).toBeInTheDocument()
    expect(local.getByText("Çalışma alanı yönetimli")).toBeInTheDocument()
    expect(article('Managed Storage').getByText('Platform yönetimli')).toBeInTheDocument()
    expect(article('Managed Storage').getByText('Henüz test edilmedi')).toBeInTheDocument()
    // The footprint is a statistic: collapsed until the card's technical details are opened.
    expect(local.getByText('6,2 GB')).not.toBeVisible()
    expect(article('Managed Storage').getByText('123 MB')).not.toBeVisible()
    await userEvent.click(local.getByText('Teknik ayrıntılar'))
    expect(local.getByText('6,2 GB')).toBeVisible()
    expect(local.getByText(/2 Kopya/)).toBeVisible()
    expect(local.getByRole('button', { name: 'Kopyala: Saklanan veri' })).toBeInTheDocument()
  })

  it('keeps the tenant-wide statistics under a collapsed Ayrıntılar section', async () => {
    mount()
    await screen.findByRole('article', { name: "Local" })
    expect(screen.getByText('Dikkat gerektiren')).not.toBeVisible()
    await userEvent.click(screen.getByText('Ayrıntılar'))
    expect(screen.getByText('Dikkat gerektiren')).toBeVisible()
  })

  it('gives an empty list one sentence and one primary action, or asks to consult the administrator', async () => {
    serve([])
    const { unmount } = mount()
    expect(await screen.findByRole('link', { name: 'İlk bağlantıyı ekle' })).toBeInTheDocument()
    unmount()
    access.canAdmin = false
    mount()
    expect(await screen.findByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'İlk bağlantıyı ekle' })).not.toBeInTheDocument()
  })

  it('opens a destination from its card without exposing credentials or configuration; quota scope and capabilities are technical', async () => {
    mount()
    await screen.findByRole('article', { name: "Local" })
    await userEvent.click(article('Local').getByRole('link', { name: 'Local bağlantısını yönet' }))
    await screen.findByRole('heading', { level: 1, name: 'Local' })
    expect(screen.getByRole('list', { name: 'Local yetenekleri' })).not.toBeVisible()
    expect(screen.getByText('tenant')).not.toBeVisible()
    await userEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.getByRole('list', { name: 'Local yetenekleri' })).toHaveTextContent('OkumaSilme')
    expect(screen.getByText('tenant')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: Kota kapsamı' })).toBeInTheDocument()
  })

  it('displays storage capacity metrics and triggers capacity probe', async () => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local',
      totalBytes: 500_000_000_000,
      usedBytes: 300_000_000_000,
      freeBytes: 200_000_000_000,
      status: 'Available',
      reportedAt: '2026-10-21T10:00:00Z',
      historyLimit: 30,
      history: [{
        reportedAt: '2026-10-21T10:00:00Z', totalBytes: 500_000_000_000,
        usedBytes: 300_000_000_000, freeBytes: 200_000_000_000, status: 'Available',
      }],
    })
    vi.mocked(api.probeTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local',
      totalBytes: 500_000_000_000,
      usedBytes: 400_000_000_000,
      freeBytes: 100_000_000_000,
      status: 'Available',
      reportedAt: '2026-10-21T10:05:00Z',
    })

    mount(detail('conn-local'))
    expect(await screen.findByRole('heading', { name: 'Depolama alanı' })).toBeInTheDocument()
    expect(await screen.findByText('Yeterli boş alan var.')).toBeVisible()
    expect(screen.getAllByText('200 GB')[0]).toBeVisible()
    // History and the exact totals are technical: collapsed until opened.
    const history = await screen.findByRole('table', { name: 'Kapasite geçmişi — en son 30 ölçüm' })
    expect(history).not.toBeVisible()
    await userEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(history).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: Toplam Alan' })).toBeInTheDocument()
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    await userEvent.click(within(history).getByRole('button', { name: 'Kopyala: Ölçüm' }))
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"totalBytes":500000000000'))
    await userEvent.click(screen.getByRole('button', { name: 'Kopyala: Yetenekler' }))
    expect(writeText).toHaveBeenCalledWith('storage.read\nstorage.delete')

    const probeBtn = screen.getByRole('button', { name: 'Kapasiteyi Sorgula' })
    await userEvent.click(probeBtn)
    expect(api.probeTenantConnectionCapacity).toHaveBeenCalledWith('tenant-a', 'conn-local')
  })

  it('shows queued capacity without fake metrics and refetches the Agent result', async () => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValueOnce({
      connectionId: 'conn-local', totalBytes: 100_000, usedBytes: 20_000, freeBytes: 80_000,
      status: 'Available', reportedAt: '2026-10-21T10:00:00Z',
    }).mockResolvedValue({
      connectionId: 'conn-local', totalBytes: 100_000, usedBytes: 92_000, freeBytes: 8_000,
      status: 'Critical', reportedAt: '2026-10-21T10:05:00Z',
    })
    vi.mocked(api.probeTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local', taskId: 'task-1', totalBytes: null, usedBytes: null, freeBytes: null,
      status: 'Pending', reportedAt: '2026-10-21T10:04:00Z',
    })
    mount(detail('conn-local'))
    await userEvent.click(await screen.findByRole('button', { name: 'Kapasiteyi Sorgula' }))
    expect(await screen.findByText(/Agent yanıtı bekleniyor/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sorgulanıyor…' })).toBeDisabled()
    expect(screen.queryByText('100 kB')).not.toBeInTheDocument()
    expect(await screen.findByText('Alan dolmak üzere', {}, { timeout: 4_000 })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kapasiteyi Sorgula' })).toBeEnabled()
  })

  it('explains that S3 and SMB capacity cannot be measured instead of asking for an Agent', async () => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local', totalBytes: null, usedBytes: null, freeBytes: null,
      status: 'NotMeasurable', reportedAt: '2026-10-21T10:00:00Z',
    })
    mount(detail('conn-local'))
    expect(await screen.findByText('Kapasite ölçülemiyor')).toBeInTheDocument()
    expect(screen.queryByText('Kapasite bilgisi alınamadı')).not.toBeInTheDocument()
    expect(screen.queryByText('Doluluk Oranı')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kapasiteyi Sorgula' })).toBeDisabled()
  })

  it.each([
    { used: 79_900, status: 'Available' as const, tone: 'bg-primary' },
    { used: 80_000, status: 'Warning' as const, tone: 'bg-warning' },
    { used: 89_900, status: 'Warning' as const, tone: 'bg-warning' },
    { used: 90_000, status: 'Critical' as const, tone: 'bg-error' },
  ])('uses backend $status color at $used bytes without rounded threshold drift', async ({ used, status, tone }) => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local', totalBytes: 100_000, usedBytes: used, freeBytes: 100_000 - used,
      status, reportedAt: '2026-10-21T10:00:00Z',
    })
    mount(detail('conn-local'))
    const label = await screen.findByText('Doluluk Oranı')
    expect(label.parentElement?.nextElementSibling?.firstElementChild).toHaveClass(tone)
  })

  it('says in plain words whether space is running out, on the detail page and the list card', async () => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValueOnce({
      connectionId: 'conn-local', totalBytes: 100_000_000_000, usedBytes: 85_000_000_000, freeBytes: 15_000_000_000,
      status: 'Warning', reportedAt: '2026-10-21T10:00:00Z',
    })
    const first = mount(detail('conn-local'))
    expect(await screen.findByText('Alan azalıyor')).toBeInTheDocument()
    expect(screen.getByText(/Depolama alanı azalıyor/)).toBeVisible()
    first.unmount()

    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValueOnce({
      connectionId: 'conn-local', totalBytes: 100_000_000_000, usedBytes: 95_000_000_000, freeBytes: 5_000_000_000,
      status: 'Critical', reportedAt: '2026-10-21T10:00:00Z',
    })
    const second = mount(detail('conn-local'))
    expect(await screen.findByText('Alan dolmak üzere')).toBeInTheDocument()
    expect(screen.getByText(/Depolama alanı dolmak üzere/)).toBeVisible()
    second.unmount()

    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local', totalBytes: 100_000_000_000, usedBytes: 92_000_000_000, freeBytes: 8_000_000_000,
      status: 'Critical', reportedAt: '2026-10-21T10:00:00Z',
    })
    serve([makeConnection()])
    mount(base)
    expect(await within(await screen.findByRole('article', { name: 'Local' })).findByText('Alan dolmak üzere')).toBeInTheDocument()
  })

  it('shows secret presence, never its value, on the credential tab', async () => {
    serve([s3({ hasSecret: true, secretFieldNames: ['accessKey', 'secretKey'], secretUpdatedAt: '2026-10-05T10:00:00Z' })])
    mount(detail('conn-s3', 'credentials'))
    expect(await screen.findByText('Kimlik bilgisi kayıtlı')).toBeInTheDocument()
    expect(screen.queryByDisplayValue(SECRET_A)).not.toBeInTheDocument()
  })

  it('labels safe configuration with the provider descriptor', async () => {
    serve([s3({ configuration: { bucket: 'archive', port: '9000', useTls: 'true' } })])
    mount(detail('conn-s3', 'configuration'))
    expect(await screen.findByText('archive')).toBeInTheDocument()
    const panel = screen.getByRole('tabpanel')
    expect(panel).toHaveTextContent(/(?:Bucket|Kova)archive/)
    expect(panel).toHaveTextContent(/(?:Port|Bağlantı noktası)9000/)
    expect(panel).toHaveTextContent('TLS kullanEvet')
  })

  it('does not claim health for untestable or failed connections', async () => {
    serve([
      makeConnection({ lastTest: { status: 'NotTestable', code: 'agent_side_storage', message: 'The backend cannot reach agent disks.', testedAt: '2026-10-06T10:00:00Z' } }),
      makeManaged({ lastTest: { status: 'Succeeded', code: 'ok', message: 'Write, verify and delete succeeded.', testedAt: '2026-10-06T10:00:00Z' } }),
      s3({ lastTest: { status: 'Failed', code: 'access_denied', message: 'Access denied.', testedAt: '2026-10-06T10:00:00Z' } }),
    ])
    mount()
    await screen.findByRole('article', { name: "Local" })
    expect(article('Local').getByText("Yedekleme bilgisayarı tarafında denetlenir")).toBeInTheDocument()
    expect(article('Local').queryByText('Sağlıklı')).not.toBeInTheDocument()
    expect(article('Managed Storage').getByText('Sağlıklı')).toBeInTheDocument()
    expect(article('S3 Arşiv').getByText('Bağlantı sorunu')).toBeInTheDocument()
    await userEvent.click(article('Local').getByRole('link', { name: /bağlantısını yönet/ }))
    expect(await screen.findByText(/(?:Backend Agent diskine erişemez|yedekleme bilgisayarı diskine erişemez)/)).toBeInTheDocument()
    expect(screen.queryByText(/cannot reach/)).not.toBeInTheDocument()
  })

  it.each([
    ['read-only', { canEdit: false, canToggle: false, canDelete: false, canManageSecret: false, canTest: false }, false],
    ['edit-only', { canEdit: true, canToggle: false, canDelete: false, canManageSecret: false, canTest: false }, true],
  ])('uses API permissions for %s connections', async (_name, permissions, editable) => {
    serve([makeConnection({ permissions: { ...tenantAdminPermissions, ...permissions } })])
    mount(detail('conn-local', 'configuration'))
    await screen.findByRole('heading', { level: 1, name: "Local" })
    expect(Boolean(screen.queryByRole('button', { name: 'Local bağlantısı: düzenle' }))).toBe(editable)
    expect(screen.queryByRole('button', { name: /test et/ })).not.toBeInTheDocument()
    await tab('Yönetim')
    expect(screen.queryByRole('button', { name: /devre dışı bırak/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Local bağlantısı: sil' })).not.toBeInTheDocument()
  })

  it('does not hide permitted connection actions behind the role hint', async () => {
    access.canAdmin = false
    mount(detail('conn-local', 'manage'))
    expect(await screen.findByRole('button', { name: 'Local bağlantısı: sil' })).toBeInTheDocument()
  })
})

describe('Provider catalog', () => {
  it('does not offer creation for planned, platform-managed or default-only providers', async () => {
    serve([makeConnection(), makeManaged()], catalog)
    mount(`${base}/providers`)
    await screen.findByRole('heading', { name: 'SFTP' })
    for (const name of ['SFTP', 'Google Drive']) {
      const card = screen.getByRole('heading', { name }).closest('li')!
      expect(card).toHaveAttribute('aria-disabled', 'true')
      expect(within(card).getByText(/Yakında \/ henüz kullanılamaz/)).toBeInTheDocument()
      expect(within(card).queryByRole('link')).not.toBeInTheDocument()
    }
    expect(screen.queryByRole('link', { name: /bağlantı ekle/i })).not.toBeInTheDocument()
  })

  it('offers creation for an available provider below its connection limit', async () => {
    mount(`${base}/providers`)
    expect(await screen.findByRole('link', { name: /(?:Fake S3|Sahte S3) için bağlantı ekle/ })).toHaveAttribute('href', `${base}/connections/new?provider=fake-s3`)
  })

  it('does not allow the non-admin role to create, including a direct wizard URL', async () => {
    access.canAdmin = false
    mount(`${base}/connections/new`)
    expect(screen.getByText("Yöneticinize danışın")).toBeInTheDocument()
    expect(api.getTenantConnections).not.toHaveBeenCalled()
    expect(api.createTenantConnection).not.toHaveBeenCalled()
  })

  it('stops offering creation at the provider connection limit', async () => {
    serve([makeConnection(), s3({ id: 's1', name: 'S3 A' }), s3({ id: 's2', name: 'S3 B' })])
    mount(`${base}/providers`)
    expect(await screen.findByText('Bağlantı sınırına ulaşıldı (2).')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /(?:Fake S3|Sahte S3) için bağlantı ekle/ })).not.toBeInTheDocument()
  })
})

async function fillWizard() {
  const user = userEvent.setup()
  await user.click(await screen.findByRole('radio', { name: /Sahte S3/ }))
  await user.click(screen.getByRole('button', { name: 'İleri' }))
  expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
  await user.type(screen.getByRole('textbox', { name: 'Ad' }), ' Arşiv ')
  expect(screen.queryByLabelText("Kova")).not.toBeInTheDocument()
  await user.click(screen.getByRole('button', { name: 'İleri' }))
  expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
  await user.type(screen.getByLabelText("Kova"), 'archive')
  await user.type(screen.getByLabelText("Bağlantı noktası"), '9000')
  await user.click(screen.getByLabelText('TLS kullan'))
  await user.click(screen.getByRole('button', { name: 'İleri' }))
  expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
  await user.type(screen.getByLabelText("Erişim anahtarı"), SECRET_A)
  await user.type(screen.getByLabelText("Gizli anahtar"), SECRET_B)
  expect(screen.getByLabelText("Gizli anahtar")).toHaveAttribute('type', 'password')
  await user.click(screen.getByRole('button', { name: 'İleri' }))
  return user
}

describe('Connection wizard', () => {
  it('validates each step, creates typed settings once, tests, and forgets submitted credentials', async () => {
    const connection = s3({ id: 'new', name: 'Arşiv', hasSecret: true })
    const { promise, resolve: finishCreate } = Promise.withResolvers<ConnectionView>()
    vi.mocked(api.createTenantConnection).mockReturnValue(promise)
    vi.mocked(api.testTenantConnection).mockResolvedValue({ connection, test: { status: 'Succeeded', code: 'ok', message: '', testedAt: '2026-10-06T10:00:00Z', durationMs: 12 } })
    const { client } = mount(`${base}/connections/new`)
    const user = await fillWizard()
    expect(screen.getByRole('listitem', { current: 'step' })).toHaveTextContent(/Özet/)
    const technical = screen.getByText('Teknik ayrıntılar').closest('details')!
    expect(technical).not.toHaveAttribute('open')
    expect(within(technical).getByRole('button', { name: /Kopyala: Kova/ })).toBeInTheDocument()
    expect(document.body.textContent).not.toContain(SECRET_A)
    await user.click(screen.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }))
    expect(screen.getByRole('button', { name: 'Oluşturuluyor…' })).toBeDisabled()
    expect(api.testTenantConnection).not.toHaveBeenCalled()
    expect(document.body.textContent).not.toContain(SECRET_B)
    finishCreate(connection)
    expect(await screen.findByText('Bağlantı çalışıyor')).toBeInTheDocument()
    expect(screen.getByText('Kimlik bilgisi kayıtlı')).toBeInTheDocument()
    expect(api.createTenantConnection).toHaveBeenCalledExactlyOnceWith('tenant-a', { providerType: 'fake-s3', name: 'Arşiv', configuration: { bucket: 'archive', port: '9000', useTls: 'true' }, secret: { accessKey: SECRET_A, secretKey: SECRET_B } })
    expect(api.testTenantConnection).toHaveBeenCalledWith('tenant-a', 'new')
    expect(JSON.stringify(client.getQueryCache().getAll().map(entry => entry.queryKey))).not.toContain(SECRET_A)
    await waitFor(() => expect(JSON.stringify(client.getMutationCache().getAll().map(entry => entry.state.variables))).not.toContain(SECRET_A))
  })

  it('keeps settings after rejection but requires credentials to be re-entered', async () => {
    vi.mocked(api.createTenantConnection).mockRejectedValue(new ApiError(409, { error: 'connection_limit_reached' }))
    mount(`${base}/connections/new`)
    const user = await fillWizard()
    await user.click(screen.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }))
    expect(await screen.findByRole('alert')).toHaveTextContent("Bu depolama sağlayıcısı için bağlantı sınırına ulaşıldı.")
    await user.click(screen.getByRole('button', { name: 'Geri' }))
    expect(screen.getByLabelText("Erişim anahtarı")).toHaveValue('')
    expect(screen.getByLabelText("Gizli anahtar")).toHaveValue('')
    expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Geri' }))
    expect(screen.getByLabelText("Kova")).toHaveValue('archive')
  })

  it('keeps an available provider preselected after loading its catalog', async () => {
    mount(`${base}/connections/new?provider=fake-s3`)
    expect(await screen.findByRole('radio', { name: /Sahte S3/ })).toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'İleri' }))
    expect(screen.getByRole('textbox', { name: 'Ad' })).toBeInTheDocument()
    expect(screen.getByRole('listitem', { current: 'step' })).toHaveTextContent('Ad')
    expect(screen.getAllByText(/Neden soruluyor/)).toHaveLength(1)
  })

  it('does not accept provider preselection for a provider that cannot be created', async () => {
    mount(`${base}/connections/new?provider=local`)
    expect(await screen.findByRole('radio', { name: /Yerel/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
    expect(api.createTenantConnection).not.toHaveBeenCalled()
  })

  it('uses the selected SFTP sign-in method, never renders a pasted key, and wipes it after rejection', async () => {
    const user = userEvent.setup()
    const key = ['test-private-key-header', 'private-key-content', 'test-private-key-footer', ''].join('\n')
    serve([], [makeProvider({
      providerType: 'sftp', displayName: 'SFTP', tenantCreatable: true, maxConnectionsPerTenant: null,
      configFields: [
        { name: 'host', label: 'Host', kind: 'Text', required: true },
        { name: 'port', label: 'Port', kind: 'Number', required: true },
        { name: 'username', label: 'Username', kind: 'Text', required: true },
        { name: 'authenticationType', label: 'Authentication type', kind: 'Text', required: true },
        { name: 'remoteBaseDirectory', label: 'Remote base directory', kind: 'Text', required: true },
        { name: 'hostKeyFingerprint', label: 'SSH host key fingerprint (SHA256)', kind: 'Text', required: true },
      ],
      secretFields: [{ name: 'password', label: 'Password', required: true }, { name: 'privateKey', label: 'SSH private key', required: false }, { name: 'passphrase', label: 'Key passphrase (optional)', required: false }],
    })])
    vi.mocked(api.createTenantConnection).mockRejectedValue(new ApiError(400, { error: 'invalid_request' }))
    mount(`${base}/connections/new?provider=sftp`)
    await user.click(await screen.findByRole('button', { name: 'İleri' }))
    await user.type(screen.getByRole('textbox', { name: 'Ad' }), 'Sunucu arşivi')
    await user.click(screen.getByRole('button', { name: 'İleri' }))
    await user.type(screen.getByLabelText('Sunucu'), 'backup.internal')
    expect(screen.getByLabelText('Bağlantı noktası')).toHaveValue(22)
    await user.type(screen.getByLabelText('Kullanıcı adı'), 'backup-user')
    await user.type(screen.getByLabelText('Yedeklerin saklanacağı klasör'), '/backups')
    await user.type(screen.getByLabelText('Sunucunun güvenlik parmak izi (SHA256)'), 'SHA256:trusted')
    await user.selectOptions(screen.getByRole('combobox'), 'private-key')
    await user.click(screen.getByRole('button', { name: 'İleri' }))
    const input = screen.getByLabelText('SSH özel anahtarı')
    expect(input).toHaveAttribute('type', 'password')
    expect(screen.queryByLabelText('Parola')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
    await user.click(input)
    await user.paste(key)
    expect(document.body.textContent).not.toContain('private-key-content')
    await user.click(screen.getByRole('button', { name: 'İleri' }))
    expect(document.body.textContent).not.toContain('private-key-content')
    await user.click(screen.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }))
    await screen.findByRole('alert')
    expect(api.createTenantConnection).toHaveBeenCalledExactlyOnceWith('tenant-a', {
      providerType: 'sftp', name: 'Sunucu arşivi',
      configuration: { host: 'backup.internal', port: '22', username: 'backup-user', authenticationType: 'private-key', remoteBaseDirectory: '/backups', hostKeyFingerprint: 'SHA256:trusted' },
      secret: { privateKey: key },
    })
    expect(api.testTenantConnection).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Geri' }))
    expect(screen.getByLabelText('SSH özel anahtarı')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'İleri' })).toBeDisabled()
  })
})

describe('Credential replacement', () => {
  const withSecret = () => s3({ hasSecret: true, secretFieldNames: ['accessKey', 'secretKey'] })

  it('opens empty password inputs, replaces credentials, then removes submitted values from cache', async () => {
    const user = userEvent.setup()
    serve([withSecret()])
    vi.mocked(api.setTenantConnectionSecret).mockResolvedValue(withSecret())
    const { client } = mount(detail('conn-s3', 'credentials'))
    await user.click(await screen.findByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisini yenile' }))
    const dialog = await screen.findByRole('dialog')
    for (const name of [/(?:Access key|Erişim anahtarı)/, /(?:Secret key|Gizli anahtar)/]) {
      expect(within(dialog).getByLabelText(name)).toHaveAttribute('type', 'password')
      expect(within(dialog).getByLabelText(name)).toHaveValue('')
    }
    await user.type(within(dialog).getByLabelText("Erişim anahtarı"), SECRET_A)
    await user.type(within(dialog).getByLabelText("Gizli anahtar"), SECRET_B)
    await user.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setTenantConnectionSecret).toHaveBeenCalledWith('tenant-a', 'conn-s3', { accessKey: SECRET_A, secretKey: SECRET_B }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(document.body.textContent).not.toContain(SECRET_A)
    expect(JSON.stringify(client.getQueryCache().getAll())).not.toContain(SECRET_A)
    await waitFor(() => expect(JSON.stringify(client.getMutationCache().getAll().map(entry => entry.state.variables))).not.toContain(SECRET_A))
  })

  it('clears typed secrets after failure and labels the invalid field', async () => {
    const user = userEvent.setup()
    serve([withSecret()])
    vi.mocked(api.setTenantConnectionSecret).mockRejectedValueOnce(new ApiError(400, { error: 'invalid_request', field: 'secret.accessKey', code: 'invalid_value' }))
    mount(detail('conn-s3', 'credentials'))
    await user.click(await screen.findByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisini yenile' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText("Erişim anahtarı"), SECRET_A)
    await user.type(within(dialog).getByLabelText("Gizli anahtar"), SECRET_B)
    await user.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Girilen değer geçersiz.')
    expect(within(dialog).getByLabelText("Erişim anahtarı")).toHaveValue('')
    expect(within(dialog).getByLabelText("Erişim anahtarı")).toHaveAttribute('aria-invalid', 'true')
    expect(within(dialog).getByLabelText("Gizli anahtar")).toHaveValue('')
    expect(within(dialog).getByRole('button', { name: 'Kaydet' })).toBeDisabled()
  })

  it('offers set rather than rotate for an empty credential', async () => {
    serve([s3()])
    mount(detail('conn-s3', 'credentials'))
    expect(await screen.findByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisi ayarla' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /kimlik bilgisini temizle/ })).not.toBeInTheDocument()
  })

  it('requires confirmation before clearing a credential', async () => {
    serve([withSecret()])
    vi.mocked(api.clearTenantConnectionSecret).mockResolvedValue(s3())
    mount(detail('conn-s3', 'credentials'))
    await userEvent.click(await screen.findByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisini temizle' }))
    expect(api.clearTenantConnectionSecret).not.toHaveBeenCalled()
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByRole('textbox')).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kimlik bilgisini temizle' }))
    await waitFor(() => expect(api.clearTenantConnectionSecret).toHaveBeenCalledWith('tenant-a', 'conn-s3'))
  })
})

describe('Enablement, test, deletion and editing', () => {
  it('states disable consequences and preserves existing replicas before confirmation', async () => {
    vi.mocked(api.setTenantConnectionEnabled).mockResolvedValue(makeConnection({ enabled: false }))
    mount(detail('conn-local', 'manage'))
    await userEvent.click(await screen.findByRole('button', { name: 'Local bağlantısı: devre dışı bırak' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByRole('textbox')).not.toBeInTheDocument()
    expect(dialog).toHaveTextContent(/(?:yeni backup'lar \(manuel ve zamanlanmış\) başlatılamaz|yeni yedeklemeler \(elle başlatılan ve zamanlanmış\) başlatılamaz)/)
    expect(dialog).toHaveTextContent(/(?:Mevcut Replica'lar silinmez ve kullanılabilir kalır|Mevcut kopyalar silinmez ve kullanılabilir kalır)/)
    expect(dialog).toHaveTextContent(/(?:indirme, geri yükleme|indirme, restore)/)
    expect(api.setTenantConnectionEnabled).not.toHaveBeenCalled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Devre dışı bırak' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', false))
  })

  it('enables a disabled destination after confirmation', async () => {
    serve([makeConnection({ enabled: false, permissions: { ...tenantAdminPermissions } })])
    vi.mocked(api.setTenantConnectionEnabled).mockResolvedValue(makeConnection())
    mount(detail('conn-local', 'manage'))
    await userEvent.click(await screen.findByRole('button', { name: 'Local bağlantısı: etkinleştir' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Etkinleştir' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', true))
  })

  it('keeps platform lock separate from owner preference, permitting owner disable but not enable', async () => {
    serve([makeConnection({ administrativelyDisabled: true, permissions: { ...tenantAdminPermissions, canDelete: false } })])
    vi.mocked(api.setTenantConnectionEnabled).mockImplementation(async () => {
      const updated = makeConnection({ enabled: false, administrativelyDisabled: true, permissions: { ...tenantAdminPermissions, canDelete: false } })
      serve([updated])
      return updated
    })
    mount(detail('conn-local', 'manage'))
    expect(await screen.findByText('Platform kilidi')).toBeInTheDocument()
    expect(screen.getAllByText(/kilit kalkana kadar yeni yedeklemeler için kullanılamaz/)).toHaveLength(1)
    expect(screen.getByText('Sahip tercihi: Etkin')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Local bağlantısı: sil' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Local bağlantısı: devre dışı bırak' }))
    const lockDialog = await screen.findByRole('dialog')
    expect(within(lockDialog).queryByRole('textbox')).not.toBeInTheDocument()
    await userEvent.click(within(lockDialog).getByRole('button', { name: 'Devre dışı bırak' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', false))
    expect(await screen.findByRole('button', { name: 'Local bağlantısı: etkinleştir' })).toBeDisabled()
  })

  it('refreshes the last test result and translates known failure codes', async () => {
    const tested = makeConnection({ permissions: { ...tenantAdminPermissions }, lastTest: { status: 'Failed', code: 'io_error', message: 'File operation failed.', testedAt: '2026-10-06T10:00:00Z' } })
    vi.mocked(api.getTenantConnections).mockResolvedValueOnce({ items: [makeConnection({ permissions: { ...tenantAdminPermissions } })] }).mockResolvedValue({ items: [tested] })
    vi.mocked(api.testTenantConnection).mockResolvedValue({ connection: tested, test: { ...tested.lastTest!, durationMs: 12 } })
    mount(detail())
    await userEvent.click(await screen.findByRole('button', { name: 'Local bağlantısı: test et' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Depolama dosya işlemi tamamlanamadı.')
    expect(api.testTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local')
  })

  it('blocks deletion until the exact name is typed, then deletes', async () => {
    const user = userEvent.setup()
    vi.mocked(api.deleteTenantConnection).mockResolvedValue(undefined)
    mount(detail('conn-local', 'manage'))
    await user.click(await screen.findByRole('button', { name: 'Local bağlantısı: sil' }))
    const dialog = await screen.findByRole('dialog')
    const confirm = within(dialog).getByRole('button', { name: 'Bağlantıyı sil' })
    expect(confirm).toBeDisabled()
    await user.type(within(dialog).getByRole('textbox'), 'Loca')
    expect(confirm).toBeDisabled()
    await user.type(within(dialog).getByRole('textbox'), 'l')
    expect(confirm).toBeEnabled()
    expect(api.deleteTenantConnection).not.toHaveBeenCalled()
    await user.click(confirm)
    await waitFor(() => expect(api.deleteTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local'))
  })

  it('explains a connection-in-use rejection in three parts and keeps the connection', async () => {
    const user = userEvent.setup()
    vi.mocked(api.deleteTenantConnection).mockRejectedValue(new ApiError(409, { error: 'connection_in_use' }))
    mount(detail('conn-local', 'manage'))
    await user.click(await screen.findByRole('button', { name: 'Local bağlantısı: sil' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByRole('textbox'), 'Local')
    await user.click(within(dialog).getByRole('button', { name: 'Bağlantıyı sil' }))
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent("Bağlantı hâlâ bir kopya veya yedekleme işi tarafından kullanılıyor; silinemez.")
    expect(alert).toHaveTextContent('Bağlantı silinmedi')
    expect(alert).toHaveTextContent('Ne yapmalı')
    expect(api.deleteTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local')
  })

  it('edits only changed settings, never exposing a tenant quota-scope editor', async () => {
    vi.mocked(api.updateTenantConnection).mockResolvedValue(makeConnection({ name: 'Yerel' }))
    mount(detail('conn-local', 'configuration'))
    await userEvent.click(await screen.findByRole('button', { name: 'Local bağlantısı: düzenle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByLabelText('Kota kapsamı')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    await userEvent.clear(within(dialog).getByLabelText('Ad'))
    await userEvent.type(within(dialog).getByLabelText('Ad'), 'Yerel')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.updateTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local', { name: 'Yerel', configuration: undefined }))
  })
})
