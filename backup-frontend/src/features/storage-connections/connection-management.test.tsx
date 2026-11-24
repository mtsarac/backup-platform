import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { catalog, fakeProvider, makeConnection, makeManaged, tenantAdminPermissions } from './test-fixtures'
import { ConnectionList } from './connection-list'
import { ConnectionDetail } from './connection-detail'
import { ConnectionWizard } from './connection-wizard'
import type { ConnectionView } from './types'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a' }))
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
  serve([makeConnection({ permissions: { ...tenantAdminPermissions } }), makeManaged()])
})

describe('Storage connections list and detail', () => {
  it('lists each destination as a card with name, health, last test and provider; statistics stay collapsed', async () => {
    serve([
      makeConnection({ lastTest: { status: 'Succeeded', code: 'ok', message: '', testedAt: new Date(Date.now() - 3 * 3_600_000).toISOString() }, permissions: { ...tenantAdminPermissions } }),
      makeManaged(),
    ])
    mount()
    await screen.findByRole('article', { name: 'Yerel disk' })
    const local = article('Yerel disk')
    expect(local.getByText('Yerel yedekleme bilgisayarı diski')).toBeInTheDocument()
    expect(local.getByText('Varsayılan')).toBeInTheDocument()
    // The backend marks a default per provider type; only the local disk (used when nothing is chosen) shows the badge.
    expect(article('Merkezi depolama').queryByText('Varsayılan')).not.toBeInTheDocument()
    expect(screen.getAllByText('Varsayılan')).toHaveLength(1)
    expect(local.getByText('Sağlıklı')).toBeInTheDocument()
    expect(local.getByText('3 saat önce')).toBeInTheDocument()
    expect(local.getByText("Çalışma alanı yönetimli")).toBeInTheDocument()
    expect(article('Merkezi depolama').getByText('Platform yönetimli')).toBeInTheDocument()
    expect(article('Merkezi depolama').getByText('Test platform tarafından yapılır')).toBeInTheDocument()
    expect(article('Merkezi depolama').queryByText('Test edilmedi')).not.toBeInTheDocument()
    expect(article('Merkezi depolama').queryByText('Henüz test edilmedi')).not.toBeInTheDocument()
    // The footprint is a statistic: collapsed until the card's technical details are opened.
    expect(local.queryByText('6,2 GB')).not.toBeInTheDocument()
    expect(article('Merkezi depolama').queryByText('123 MB')).not.toBeInTheDocument()
    await userEvent.click(local.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = within(screen.getByRole('region', { name: 'Teknik ayrıntılar' }))
    expect(dialog.getByText('6,2 GB')).toBeVisible()
    expect(dialog.getByText(/2 Kopya/)).toBeVisible()
    expect(dialog.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
  })

  it('retains untested information when another health state takes precedence', async () => {
    serve([makeConnection({ enabled: false, effectiveEnabled: false, lastTest: null })])
    mount()
    const local = within(await screen.findByRole('article', { name: 'Yerel disk' }))
    expect(local.getByText('Devre dışı')).toBeInTheDocument()
    expect(local.getByText('Henüz test edilmedi')).toBeInTheDocument()
  })

  it('gives each destination one link to its page without repeating health totals', async () => {
    serve([makeConnection({ lastTest: { status: 'Succeeded', code: 'ok', message: '', testedAt: new Date().toISOString() } }), s3({ lastTest: { status: 'Failed', code: 'access_denied', message: 'Access denied.', testedAt: new Date().toISOString() } }), makeManaged()])
    mount()
    await screen.findByRole('article', { name: 'Yerel disk' })
    expect(screen.queryByText(/bağlantı · .* sağlıklı/)).not.toBeInTheDocument()
    expect(screen.queryByText('Ayrıntılar')).not.toBeInTheDocument()
    expect(article('Yerel disk').getAllByRole('link')).toHaveLength(1)
    expect(article('Yerel disk').getByRole('link', { name: 'Yerel disk' })).toHaveAttribute('href', `${base}/connections/conn-local`)
  })

  it('puts free space and the platform lock into the card once', async () => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValue({ connectionId: 'conn-local', totalBytes: 500_000_000_000, usedBytes: 300_000_000_000, freeBytes: 200_000_000_000, status: 'Available', reportedAt: '2026-10-21T10:00:00Z' })
    serve([makeConnection({ administrativelyDisabled: true })])
    mount()
    await screen.findByRole('article', { name: 'Yerel disk' })
    const local = article('Yerel disk')
    expect(await local.findByText('200 GB')).toBeVisible()
    expect(local.queryByText('Platform kilidi')).not.toBeInTheDocument()
    expect(local.getAllByText(/kilit kalkana kadar yeni yedeklemeler için kullanılamaz/)).toHaveLength(1)
  })

  it('offers the add connection action on an empty list', async () => {
    serve([])
    mount()
    expect(await screen.findByText(/Henüz depolama bağlantısı yok/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Depolama bağlantısı ekle' })).toHaveAttribute('href', '/tenants/tenant-a/storage/connections/new')
  })

  it('opens a destination from its card without exposing credentials or configuration; quota scope and capabilities are technical', async () => {
    mount()
    await screen.findByRole('article', { name: 'Yerel disk' })
    await userEvent.click(article('Yerel disk').getByRole('link', { name: 'Yerel disk' }))
    await screen.findByRole('heading', { level: 1, name: 'Yerel disk' })
    expect(screen.queryByRole('list', { name: 'Yerel disk yetenekleri' })).not.toBeInTheDocument()
    expect(screen.queryByText('tenant')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = within(screen.getByRole('region', { name: 'Teknik ayrıntılar' }))
    expect(dialog.getByRole('list', { name: 'Yerel disk yetenekleri' })).toHaveTextContent('Okuma ·Silme')
    expect(dialog.getByText('tenant')).toBeVisible()
    expect(dialog.getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
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
    expect(screen.queryByRole('table', { name: 'Kapasite geçmişi — en son 30 ölçüm' })).not.toBeInTheDocument()
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = within(screen.getByRole('region', { name: 'Teknik ayrıntılar' }))
    const history = dialog.getByRole('table', { name: 'Kapasite geçmişi — en son 30 ölçüm' })
    expect(history).toBeVisible()
    await userEvent.click(within(history).getByRole('button', { name: 'Kopyala: Ölçüm' }))
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"totalBytes":500000000000'))
    await userEvent.click(dialog.getByRole('button', { name: 'Kopyala' }))
    expect(writeText).toHaveBeenLastCalledWith(expect.stringContaining('Toplam alan: 500 GB\nKullanılan alan: 300 GB'))

    const probeBtn = screen.getByRole('button', { name: 'Kapasiteyi sorgula' })
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
    await userEvent.click(await screen.findByRole('button', { name: 'Kapasiteyi sorgula' }))
    expect(await screen.findByText(/yedekleme bilgisayarı yanıtı bekleniyor/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sorgulanıyor…' })).toBeDisabled()
    expect(screen.queryByText('100 kB')).not.toBeInTheDocument()
    expect(await screen.findByText('Alan dolmak üzere', {}, { timeout: 4_000 })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kapasiteyi sorgula' })).toBeEnabled()
  })

  it('explains that S3 and SMB capacity cannot be measured instead of asking for an Agent', async () => {
    vi.mocked(api.getTenantConnectionCapacity).mockResolvedValue({
      connectionId: 'conn-local', totalBytes: null, usedBytes: null, freeBytes: null,
      status: 'NotMeasurable', reportedAt: '2026-10-21T10:00:00Z',
    })
    mount(detail('conn-local'))
    expect(await screen.findByText('Kapasite ölçülemiyor')).toBeInTheDocument()
    expect(screen.queryByText('Kapasite bilgisi alınamadı')).not.toBeInTheDocument()
    expect(screen.queryByText('Doluluk oranı')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kapasiteyi sorgula' })).toBeDisabled()
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
    const label = await screen.findByText('Doluluk oranı')
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
    expect(await within(await screen.findByRole('article', { name: 'Yerel disk' })).findByText('Alan dolmak üzere')).toBeInTheDocument()
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
    await screen.findByRole('article', { name: 'Yerel disk' })
    expect(article('Yerel disk').getByText('Test yedekleme bilgisayarında yapılır')).toBeInTheDocument()
    expect(article('Yerel disk').queryByText('Test edilmedi')).not.toBeInTheDocument()
    expect(article('Yerel disk').queryByText('Sağlıklı')).not.toBeInTheDocument()
    expect(article('Merkezi depolama').getByText('Sağlıklı')).toBeInTheDocument()
    expect(article('S3 Arşiv').getByText('Bağlantı sorunu')).toBeInTheDocument()
    await userEvent.click(article('Yerel disk').getByRole('link', { name: 'Yerel disk' }))
    expect(await screen.findByText(/(?:Backend Agent diskine erişemez|yedekleme bilgisayarı diskine erişemez)/)).toBeInTheDocument()
    expect(screen.queryByText(/cannot reach/)).not.toBeInTheDocument()
  })

  it.each([
    ['read-only', { canEdit: false, canToggle: false, canDelete: false, canManageSecret: false, canTest: false }, false],
    ['edit-only', { canEdit: true, canToggle: false, canDelete: false, canManageSecret: false, canTest: false }, true],
  ])('uses API permissions for %s connections', async (_name, permissions, editable) => {
    serve([makeConnection({ permissions: { ...tenantAdminPermissions, ...permissions } })])
    mount(detail('conn-local', 'configuration'))
    await screen.findByRole('heading', { level: 1, name: 'Yerel disk' })
    expect(Boolean(screen.queryByRole('button', { name: 'Yerel disk bağlantısı: düzenle' }))).toBe(editable)
    expect(screen.queryByRole('button', { name: /test et/ })).not.toBeInTheDocument()
    await tab('Yönetim')
    expect(screen.queryByRole('button', { name: /devre dışı bırak/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Yerel disk bağlantısı: sil' })).not.toBeInTheDocument()
  })

  it('states a healthy connection once in the header with its last test time, and keeps a single breadcrumb back to Depolama', async () => {
    serve([makeConnection({ lastTest: { status: 'Succeeded', code: 'ok', message: '', testedAt: new Date(Date.now() - 3 * 3_600_000).toISOString() }, permissions: { ...tenantAdminPermissions } })])
    mount(detail('conn-local'))
    const heading = await screen.findByRole('heading', { level: 1, name: 'Yerel disk' })
    expect(within(heading.closest('header')!).getByText('Sağlıklı')).toBeInTheDocument()
    expect(within(heading.closest('header')!).getByText('3 saat önce')).toBeInTheDocument()
    expect(screen.getAllByText('Sağlıklı')).toHaveLength(1)
    expect(screen.queryByText('Bağlantı sağlıklı')).not.toBeInTheDocument()
    const crumbs = within(screen.getByRole('navigation', { name: 'Sayfa yolu' }))
    expect(crumbs.getAllByRole('link')).toHaveLength(1)
    expect(crumbs.getByRole('link', { name: 'Depolama' })).toHaveAttribute('href', base)
  })

  it('offers credential correction and keeps the backend message in technical details', async () => {
    serve([s3({ lastTest: { status: 'Failed', code: 'access_denied', message: 'Access denied.', testedAt: new Date().toISOString() } })])
    mount(detail('conn-s3'))
    const alert = within((await screen.findByText('Son test başarısız')).closest('[role="alert"]')!)
    expect(screen.getByText('Bağlantı sorunu')).toBeInTheDocument()
    expect(alert.getByRole('link', { name: 'Bilgileri düzelt' })).toHaveAttribute('href', `${base}/connections/conn-s3?tab=credentials`)
    expect(screen.queryByText('Access denied.')).not.toBeInTheDocument()
    await userEvent.click(alert.getByText('Teknik ayrıntılar'))
    const dialog = screen.getByRole('region', { name: 'Teknik ayrıntılar' })
    expect(within(dialog).getByText('Access denied.')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Kopyala' })).toBeInTheDocument()
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
    await userEvent.click(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: devre dışı bırak' }))
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
    await userEvent.click(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: etkinleştir' }))
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
    expect(screen.queryByRole('button', { name: 'Yerel disk bağlantısı: sil' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Yerel disk bağlantısı: devre dışı bırak' }))
    const lockDialog = await screen.findByRole('dialog')
    expect(within(lockDialog).queryByRole('textbox')).not.toBeInTheDocument()
    await userEvent.click(within(lockDialog).getByRole('button', { name: 'Devre dışı bırak' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', false))
    expect(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: etkinleştir' })).toBeDisabled()
  })

  it('refreshes the last test result and translates known failure codes', async () => {
    const tested = makeConnection({ permissions: { ...tenantAdminPermissions }, lastTest: { status: 'Failed', code: 'io_error', message: 'File operation failed.', testedAt: '2026-10-06T10:00:00Z' } })
    vi.mocked(api.getTenantConnections).mockResolvedValueOnce({ items: [makeConnection({ permissions: { ...tenantAdminPermissions } })] }).mockResolvedValue({ items: [tested] })
    vi.mocked(api.testTenantConnection).mockResolvedValue({ connection: tested, test: { ...tested.lastTest!, durationMs: 12 } })
    mount(detail())
    await userEvent.click(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: test et' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Depolama dosya işlemi tamamlanamadı.')
    expect(api.testTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local')
  })

  it('shows the agent-side wording on the card after "Test et" on the local disk', async () => {
    const tested = makeConnection({ permissions: { ...tenantAdminPermissions }, lastTest: { status: 'NotTestable', code: 'agent_side_storage', message: '', testedAt: new Date().toISOString() } })
    vi.mocked(api.getTenantConnections).mockResolvedValueOnce({ items: [makeConnection({ permissions: { ...tenantAdminPermissions } })] }).mockResolvedValue({ items: [tested] })
    vi.mocked(api.testTenantConnection).mockResolvedValue({ connection: tested, test: { ...tested.lastTest!, durationMs: 12 } })
    mount()
    const local = await screen.findByRole('article', { name: 'Yerel disk' })
    expect(within(local).getByText('Test edilmedi')).toBeInTheDocument()
    await userEvent.click(within(local).getByRole('button', { name: 'Yerel disk bağlantısı: test et' }))
    expect(await within(screen.getByRole('article', { name: 'Yerel disk' })).findByText('Test yedekleme bilgisayarında yapılır')).toBeInTheDocument()
    expect(within(screen.getByRole('article', { name: 'Yerel disk' })).queryByText('Test edilmedi')).not.toBeInTheDocument()
  })

  it('blocks deletion until the exact name is typed, then deletes', async () => {
    const user = userEvent.setup()
    vi.mocked(api.deleteTenantConnection).mockResolvedValue(undefined)
    mount(detail('conn-local', 'manage'))
    await user.click(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: sil' }))
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

  it('explains a connection-in-use rejection with its consequence and keeps the connection', async () => {
    const user = userEvent.setup()
    vi.mocked(api.deleteTenantConnection).mockRejectedValue(new ApiError(409, { error: 'connection_in_use' }))
    mount(detail('conn-local', 'manage'))
    await user.click(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: sil' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByRole('textbox'), 'Local')
    await user.click(within(dialog).getByRole('button', { name: 'Bağlantıyı sil' }))
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent("Bağlantı hâlâ bir kopya veya yedekleme işi tarafından kullanılıyor; silinemez.")
    expect(alert).toHaveTextContent('Önce bu bağlantıyı kullanan işleri başka bir depolamaya taşıyın.')
    expect(api.deleteTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local')
  })

  it('edits only changed settings, never exposing a tenant quota-scope editor', async () => {
    vi.mocked(api.updateTenantConnection).mockResolvedValue(makeConnection({ name: 'Yerel' }))
    mount(detail('conn-local', 'configuration'))
    await userEvent.click(await screen.findByRole('button', { name: 'Yerel disk bağlantısı: düzenle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByLabelText('Kota kapsamı')).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    await userEvent.clear(within(dialog).getByLabelText('Ad'))
    await userEvent.type(within(dialog).getByLabelText('Ad'), 'Yerel')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.updateTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local', { name: 'Yerel', configuration: undefined }))
  })
})
