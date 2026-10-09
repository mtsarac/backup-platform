import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { catalog, fakeProvider, makeConnection, makeManaged, tenantAdminPermissions } from './test-fixtures'
import { TenantDestinations } from './tenant-destinations'
import type { ConnectionView } from './types'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('./api')

const SECRET_A = 'ak-12345-secret'
const SECRET_B = 'sk-67890-secret'

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(<QueryClientProvider client={client}><TenantDestinations /></QueryClientProvider>)
  return { client, ...view }
}

function serve(items: ConnectionView[], providers = [...catalog, fakeProvider]) {
  vi.mocked(api.getTenantConnections).mockResolvedValue({ items })
  vi.mocked(api.getTenantProviders).mockResolvedValue({ items: providers })
}

const article = (name: string) => within(screen.getByRole('article', { name }))
const buttonNames = (name: string) => article(name).queryAllByRole('button').map((button) => button.textContent)
const s3 = (overrides: Partial<ConnectionView> = {}) => makeConnection({ id: 'conn-s3', name: 'S3 Arşiv', providerType: 'fake-s3', providerDisplayName: 'Fake S3', isDefault: false, quotaScope: 'tenant', permissions: { ...tenantAdminPermissions }, ...overrides })

beforeEach(() => {
  vi.resetAllMocks()
  access.canAdmin = true
  serve([makeConnection({ permissions: { ...tenantAdminPermissions } }), makeManaged()])
})

describe('Depolama hedefleri', () => {
  it('lists real destinations with ownership, scope, state, secret status, footprint and capability chips', async () => {
    mount()
    await screen.findByRole('article', { name: 'Local' })
    const local = article('Local')
    expect(local.getByText('Tenant yönetimli')).toBeInTheDocument()
    expect(local.getByText('Etkin')).toBeInTheDocument()
    expect(local.getByText('Varsayılan')).toBeInTheDocument()
    expect(local.getByText('tenant')).toBeInTheDocument()
    expect(local.getByText('Yok')).toBeInTheDocument()
    expect(local.getByText('2 Replica')).toBeInTheDocument()
    expect(local.getByText('6,2 GB')).toBeInTheDocument()
    expect(local.getByRole('list', { name: 'Local yetenekleri' })).toHaveTextContent('OkumaSilme')
    expect(local.getByText('Henüz test edilmedi')).toBeInTheDocument()
    const managed = article('Managed Storage')
    expect(managed.getByText('Platform yönetimli')).toBeInTheDocument()
    expect(managed.getByText('managed')).toBeInTheDocument()
    expect(managed.getByText('3 Replica')).toBeInTheDocument()
    expect(managed.getByText('123 MB')).toBeInTheDocument()
  })

  it('shows the secret status without any value', async () => {
    serve([s3({ hasSecret: true, secretFieldNames: ['accessKey', 'secretKey'], secretUpdatedAt: '2026-10-05T10:00:00Z' })])
    mount()
    await screen.findByRole('article', { name: 'S3 Arşiv' })
    expect(article('S3 Arşiv').getByText('Kimlik bilgisi kayıtlı')).toBeInTheDocument()
  })

  it('renders the safe configuration summary with descriptor labels', async () => {
    serve([s3({ configuration: { bucket: 'archive', port: 9000, useTls: true } })])
    mount()
    await screen.findByRole('article', { name: 'S3 Arşiv' })
    const card = article('S3 Arşiv')
    expect(card.getByText('Bucket: archive')).toBeInTheDocument()
    expect(card.getByText('Port: 9000')).toBeInTheDocument()
    expect(card.getByText('TLS kullan: Evet')).toBeInTheDocument()
  })

  it('shows the three test outcomes honestly: NotTestable is a warning, never success', async () => {
    serve([
      makeConnection({ id: 'a', name: 'Local', lastTest: { status: 'NotTestable', code: 'agent_side_storage', message: 'The backend cannot reach agent disks.', testedAt: '2026-10-06T10:00:00Z' } }),
      makeManaged({ id: 'b', name: 'Central', lastTest: { status: 'Succeeded', code: 'ok', message: 'Write, verify and delete succeeded.', testedAt: '2026-10-06T10:00:00Z' } }),
      s3({ id: 'c', permissions: { ...tenantAdminPermissions }, lastTest: { status: 'Failed', code: 'access_denied', message: 'Access denied.', testedAt: '2026-10-06T10:00:00Z' } }),
    ])
    mount()
    await screen.findByRole('article', { name: 'Local' })
    const local = article('Local')
    expect(local.getByText('Test edilemez')).toBeInTheDocument()
    expect(local.getByText(/Backend Agent diskine erişemez/)).toBeInTheDocument()
    expect(local.queryByText(/cannot reach/)).not.toBeInTheDocument()
    expect(local.queryByText('Başarılı')).not.toBeInTheDocument()
    expect(article('Central').getByText('Başarılı')).toBeInTheDocument()
    expect(article('S3 Arşiv').getByText('Başarısız')).toBeInTheDocument()
    expect(article('S3 Arşiv').getByText('Depolama erişim izni reddedildi.')).toBeInTheDocument()
  })

  it('gates every action by the API permissions, not by the role', async () => {
    serve([
      makeConnection({ id: 'v', name: 'Salt okunur' }),
      makeManaged({ id: 'm', name: 'Platform hedefi' }),
      makeConnection({ id: 'e', name: 'Sadece düzenle', permissions: { ...tenantAdminPermissions, canToggle: false, canDelete: false, canManageSecret: false, canTest: false } }),
      makeConnection({ id: 'all', name: 'Tam yetki', permissions: { ...tenantAdminPermissions } }),
    ])
    mount()
    await screen.findByRole('article', { name: 'Salt okunur' })
    expect(buttonNames('Salt okunur')).toEqual([])
    expect(buttonNames('Platform hedefi')).toEqual([])
    expect(buttonNames('Sadece düzenle')).toEqual(['Düzenle'])
    // the provider has no secret fields, so even canManageSecret offers no secret control
    expect(buttonNames('Tam yetki')).toEqual(['Düzenle', 'Devre dışı bırak', 'Test et', 'Sil'])
  })

  it('does not hide permitted actions behind the tenant role hint', async () => {
    access.canAdmin = false
    serve([makeConnection({ permissions: { ...tenantAdminPermissions } })])
    mount()
    await screen.findByRole('article', { name: 'Local' })
    expect(buttonNames('Local')).toContain('Sil')
  })
})

describe('Sağlayıcılar ve yetenekler', () => {
  it('lists Planned providers as unavailable with no create control, and offers nothing creatable in the current catalog', async () => {
    serve([makeConnection(), makeManaged()], catalog)
    mount()
    expect(await screen.findByRole('heading', { name: 'SFTP' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Sağlayıcılar ve yetenekler' })).toBeInTheDocument()
    for (const name of ['SFTP', 'Google Drive']) {
      const card = screen.getByRole('heading', { name }).closest('li')
      expect(card).toHaveAttribute('aria-disabled', 'true')
      expect(within(card as HTMLElement).getByText(/Yakında \/ henüz kullanılamaz/)).toBeInTheDocument()
      expect(within(card as HTMLElement).queryAllByRole('button')).toHaveLength(0)
    }
    expect(screen.queryByRole('button', { name: /bağlantı ekle/i })).not.toBeInTheDocument()
    expect(screen.getAllByText(/Platform tarafından yönetilir; tenant bağlantı ekleyemez/)).toHaveLength(2)
  })

  it('offers Bağlantı ekle only for an Available, tenant-creatable provider below its limit and for Owner/Admin', async () => {
    mount()
    expect(await screen.findByRole('button', { name: 'Fake S3 için bağlantı ekle' })).toBeInTheDocument()
  })

  it('hides the add button without the Owner/Admin hint', async () => {
    access.canAdmin = false
    mount()
    await screen.findByRole('heading', { name: 'Fake S3' })
    expect(screen.queryByRole('button', { name: 'Fake S3 için bağlantı ekle' })).not.toBeInTheDocument()
    expect(screen.getByText('Bağlantı eklemek için Owner / Admin rolü gerekir.')).toBeInTheDocument()
  })

  it('stops offering the dialog once maxConnectionsPerTenant is reached', async () => {
    serve([makeConnection(), s3({ id: 's1', name: 'S3 A' }), s3({ id: 's2', name: 'S3 B' })])
    mount()
    await screen.findByRole('heading', { name: 'Fake S3' })
    expect(screen.queryByRole('button', { name: 'Fake S3 için bağlantı ekle' })).not.toBeInTheDocument()
    expect(screen.getByText('Bağlantı sınırına ulaşıldı (2).')).toBeInTheDocument()
  })
})

describe('Bağlantı ekle dialog (generic over the descriptor)', () => {
  it('renders the descriptor fields and sends typed configuration and secret, then forgets the secret', async () => {
    const user = userEvent.setup()
    vi.mocked(api.createTenantConnection).mockResolvedValue(s3({ id: 'new', name: 'Arşiv' }))
    const { client } = mount()
    await user.click(await screen.findByRole('button', { name: 'Fake S3 için bağlantı ekle' }))
    const dialog = await screen.findByRole('dialog')
    const submit = within(dialog).getByRole('button', { name: 'Bağlantı ekle' })
    expect(submit).toBeDisabled()
    await user.type(within(dialog).getByLabelText('Ad'), ' Arşiv ')
    await user.type(within(dialog).getByLabelText('Bucket'), 'archive')
    await user.type(within(dialog).getByLabelText('Port'), '9000')
    await user.click(within(dialog).getByLabelText('TLS kullan'))
    expect(submit).toBeDisabled()
    await user.type(within(dialog).getByLabelText('Access key'), SECRET_A)
    await user.type(within(dialog).getByLabelText('Secret key'), SECRET_B)
    expect(within(dialog).getByLabelText('Secret key')).toHaveAttribute('type', 'password')
    await user.click(submit)
    await waitFor(() => expect(api.createTenantConnection).toHaveBeenCalledWith('tenant-a', {
      providerType: 'fake-s3', name: 'Arşiv', configuration: { bucket: 'archive', port: 9000, useTls: true }, secret: { accessKey: SECRET_A, secretKey: SECRET_B },
    }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(JSON.stringify(client.getQueryCache().getAll().map((entry) => entry.queryKey))).not.toContain('secret')
    await waitFor(() => expect(JSON.stringify(client.getMutationCache().getAll().map((entry) => entry.state.variables))).not.toContain(SECRET_A))
  })

  it('surfaces a server rejection on the right field and clears the secret inputs', async () => {
    const user = userEvent.setup()
    vi.mocked(api.createTenantConnection).mockRejectedValue(new ApiError(409, { error: 'connection_limit_reached' }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'Fake S3 için bağlantı ekle' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('Ad'), 'Arşiv')
    await user.type(within(dialog).getByLabelText('Bucket'), 'archive')
    await user.type(within(dialog).getByLabelText('Access key'), SECRET_A)
    await user.type(within(dialog).getByLabelText('Secret key'), SECRET_B)
    await user.click(within(dialog).getByRole('button', { name: 'Bağlantı ekle' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Bu Storage Provider için bağlantı sınırına ulaşıldı.')
    expect(within(dialog).getByLabelText('Access key')).toHaveValue('')
    expect(within(dialog).getByLabelText('Secret key')).toHaveValue('')
    expect(within(dialog).getByLabelText('Bucket')).toHaveValue('archive')
  })
})

describe('Kimlik bilgisi dialog', () => {
  const withSecret = () => s3({ hasSecret: true, secretFieldNames: ['accessKey', 'secretKey'] })

  it('renders password inputs from the descriptor, never prefilled, sends only the values and closes', async () => {
    const user = userEvent.setup()
    serve([withSecret()])
    vi.mocked(api.setTenantConnectionSecret).mockResolvedValue(withSecret())
    const { client } = mount()
    await user.click(await screen.findByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisini yenile' }))
    const dialog = await screen.findByRole('dialog')
    const access = within(dialog).getByLabelText('Access key')
    const secret = within(dialog).getByLabelText('Secret key')
    expect(access).toHaveAttribute('type', 'password')
    expect(secret).toHaveAttribute('type', 'password')
    expect(access).toHaveValue('')
    expect(secret).toHaveValue('')
    await user.type(access, SECRET_A)
    await user.type(secret, SECRET_B)
    await user.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setTenantConnectionSecret).toHaveBeenCalledWith('tenant-a', 'conn-s3', { accessKey: SECRET_A, secretKey: SECRET_B }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(document.body.textContent).not.toContain(SECRET_A)
    expect(JSON.stringify(client.getQueryCache().getAll().map((entry) => entry.queryKey))).not.toContain(SECRET_A)
    await waitFor(() => expect(JSON.stringify(client.getMutationCache().getAll().map((entry) => entry.state.variables))).not.toContain(SECRET_A))
  })

  it('wipes typed values at submit even when the request fails, and keeps the dialog for a retry', async () => {
    const user = userEvent.setup()
    serve([withSecret()])
    vi.mocked(api.setTenantConnectionSecret).mockRejectedValueOnce(new ApiError(400, { error: 'invalid_request', field: 'secret.accessKey', code: 'invalid_value' }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisini yenile' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('Access key'), SECRET_A)
    await user.type(within(dialog).getByLabelText('Secret key'), SECRET_B)
    await user.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Girilen değer geçersiz.')
    expect(within(dialog).getByLabelText('Access key')).toHaveValue('')
    expect(within(dialog).getByLabelText('Access key')).toHaveAttribute('aria-invalid', 'true')
    expect(within(dialog).getByLabelText('Secret key')).toHaveValue('')
    expect(within(dialog).getByRole('button', { name: 'Kaydet' })).toBeDisabled()
  })

  it('offers set (not rotate) when no secret is stored and clears a stored one after confirmation', async () => {
    const user = userEvent.setup()
    serve([s3({ id: 'none', name: 'Boş' }), withSecret()])
    vi.mocked(api.clearTenantConnectionSecret).mockResolvedValue(s3())
    mount()
    await screen.findByRole('article', { name: 'S3 Arşiv' })
    expect(buttonNames('Boş')).toContain('Kimlik bilgisi ayarla')
    expect(buttonNames('Boş')).not.toContain('Kimlik bilgisini temizle')
    await user.click(article('S3 Arşiv').getByRole('button', { name: 'S3 Arşiv bağlantısı: kimlik bilgisini temizle' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kimlik bilgisini temizle' }))
    await waitFor(() => expect(api.clearTenantConnectionSecret).toHaveBeenCalledWith('tenant-a', 'conn-s3'))
  })
})

describe('Etkinleştirme, test ve silme', () => {
  it('states the disable semantics before disabling and then disables', async () => {
    const user = userEvent.setup()
    vi.mocked(api.setTenantConnectionEnabled).mockResolvedValue(makeConnection({ enabled: false }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'Local bağlantısı: devre dışı bırak' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: '“Local” devre dışı bırakılsın mı?' })).toBeInTheDocument()
    expect(dialog).toHaveTextContent('yeni backup\'lar (manuel ve zamanlanmış) başlatılamaz')
    expect(dialog).toHaveTextContent('Mevcut Replica\'lar silinmez ve kullanılabilir kalır')
    expect(dialog).toHaveTextContent('indirme, geri yükleme')
    await user.click(within(dialog).getByRole('button', { name: 'Devre dışı bırak' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', false))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('enables a disabled connection after confirmation', async () => {
    const user = userEvent.setup()
    serve([makeConnection({ enabled: false, permissions: { ...tenantAdminPermissions } })])
    vi.mocked(api.setTenantConnectionEnabled).mockResolvedValue(makeConnection())
    mount()
    await screen.findByRole('article', { name: 'Local' })
    expect(article('Local').getByText('Devre dışı')).toBeInTheDocument()
    await user.click(article('Local').getByRole('button', { name: 'Local bağlantısı: etkinleştir' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Etkinleştir' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', true))
  })

  it('shows effective disabled state and platform lock separately while allowing owner disabling', async () => {
    const user = userEvent.setup()
    serve([makeConnection({ administrativelyDisabled: true, permissions: { ...tenantAdminPermissions, canDelete: false } })])
    vi.mocked(api.setTenantConnectionEnabled).mockResolvedValue(makeConnection({ enabled: false, administrativelyDisabled: true }))
    mount()
    await screen.findByRole('article', { name: 'Local' })
    expect(article('Local').getByText('Devre dışı')).toBeInTheDocument()
    expect(article('Local').getByText('Sahip tercihi: Etkin')).toBeInTheDocument()
    expect(article('Local').getByText('Platform kilidi')).toBeInTheDocument()
    expect(article('Local').queryByRole('button', { name: 'Local bağlantısı: sil' })).not.toBeInTheDocument()
    await user.click(article('Local').getByRole('button', { name: 'Local bağlantısı: devre dışı bırak' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Devre dışı bırak' }))
    await waitFor(() => expect(api.setTenantConnectionEnabled).toHaveBeenCalledWith('tenant-a', 'conn-local', false))
  })

  it('does not let a tenant enable its disabled preference while the platform lock remains', async () => {
    serve([makeConnection({ enabled: false, administrativelyDisabled: true, permissions: { ...tenantAdminPermissions, canDelete: false } })])
    mount()
    await screen.findByRole('article', { name: 'Local' })
    expect(article('Local').getByRole('button', { name: 'Local bağlantısı: etkinleştir' })).toBeDisabled()
    expect(article('Local').queryByRole('button', { name: 'Local bağlantısı: sil' })).not.toBeInTheDocument()
    expect(api.setTenantConnectionEnabled).not.toHaveBeenCalled()
  })

  it('runs the test and shows the refreshed last result', async () => {
    const user = userEvent.setup()
    const tested = makeConnection({ permissions: { ...tenantAdminPermissions }, lastTest: { status: 'Failed', code: 'io_error', message: 'File operation failed.', testedAt: '2026-10-06T10:00:00Z' } })
    vi.mocked(api.getTenantConnections).mockResolvedValueOnce({ items: [makeConnection({ permissions: { ...tenantAdminPermissions } })] }).mockResolvedValue({ items: [tested] })
    vi.mocked(api.testTenantConnection).mockResolvedValue({ connection: tested, test: { ...tested.lastTest!, durationMs: 12 } })
    mount()
    await user.click(await screen.findByRole('button', { name: 'Local bağlantısı: test et' }))
    await waitFor(() => expect(api.testTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local'))
    expect(await article('Local').findByText('Başarısız')).toBeInTheDocument()
    expect(article('Local').getByText('Depolama dosya işlemi tamamlanamadı.')).toBeInTheDocument()
  })

  it('surfaces connection_in_use when deleting', async () => {
    const user = userEvent.setup()
    vi.mocked(api.deleteTenantConnection).mockRejectedValue(new ApiError(409, { error: 'connection_in_use' }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'Local bağlantısı: sil' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Bağlantıyı sil' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Bağlantı hâlâ Replica veya Backup Job tarafından kullanılıyor; silinemez.')
    expect(api.deleteTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local')
  })

  it('edits the name and sends only what changed', async () => {
    const user = userEvent.setup()
    vi.mocked(api.updateTenantConnection).mockResolvedValue(makeConnection({ name: 'Yerel' }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'Local bağlantısı: düzenle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByLabelText('Kota kapsamı')).not.toBeInTheDocument()
    const save = within(dialog).getByRole('button', { name: 'Kaydet' })
    expect(save).toBeDisabled()
    await user.clear(within(dialog).getByLabelText('Ad'))
    await user.type(within(dialog).getByLabelText('Ad'), 'Yerel')
    await user.click(save)
    await waitFor(() => expect(api.updateTenantConnection).toHaveBeenCalledWith('tenant-a', 'conn-local', { name: 'Yerel', configuration: undefined }))
  })
})
