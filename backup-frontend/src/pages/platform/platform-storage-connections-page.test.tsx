import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as platformApi from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformTenant } from '@/features/platform/types'
import * as api from '@/features/storage-connections/api'
import { catalog, fakeProvider, makeConnection, makeManaged, noPermissions } from '@/features/storage-connections/test-fixtures'
import type { ConnectionView } from '@/features/storage-connections/types'
import { ApiError } from '@/lib/api/client'
import { PlatformStorageConnectionsPage } from './platform-storage-connections-page'

vi.mock('@/features/platform/api')
vi.mock('@/features/storage-connections/api')

const platformFull = { canEdit: true, canSetQuotaScope: true, canToggle: true, canDelete: true, canManageSecret: true, canTest: true }
const managed = makeManaged({ id: 'conn-central', tenantName: 'Acme A.Ş.', permissions: { ...platformFull }, lastTest: { status: 'Succeeded', code: null, message: 'Yazma ve doğrulama başarılı.', testedAt: '2026-10-06T10:00:00Z' } })
const tenantManaged = makeConnection({ id: 'conn-local', tenantName: 'Beta Ltd.', tenantId: 'tenant-b', permissions: { ...noPermissions, canSetQuotaScope: true, canToggle: true } })
const acme: PlatformTenant = { tenantId: 'tenant-a', name: 'Acme A.Ş.', memberCount: 2, agentCount: 1, enrollmentEnabled: true }

function mount(path = '/platform/storage-connections') {
  return renderPlatform(<PlatformStorageConnectionsPage />, { path, route: '/platform/storage-connections' })
}
const rowOf = (name: string) => within(screen.getByText(name).closest('tr') as HTMLElement)
const rowButtons = (name: string) => rowOf(name).queryAllByRole('button').map((button) => button.textContent)
const location = () => screen.getByTestId('location').textContent

function serve(items: ConnectionView[], overrides: { totalCount?: number } = {}) {
  vi.mocked(api.getPlatformConnections).mockResolvedValue(paged(items, overrides))
}

beforeEach(() => {
  vi.resetAllMocks()
  serve([managed, tenantManaged])
  vi.mocked(api.getPlatformProviders).mockResolvedValue({ items: [...catalog, fakeProvider] })
  vi.mocked(platformApi.getTenants).mockResolvedValue(paged([acme]))
})

describe('platform storage targets list', () => {
  it('highlights PlatformManaged by default and renders the row facts', async () => {
    mount()
    expect(await screen.findByText('Managed Storage')).toBeInTheDocument()
    expect(api.getPlatformConnections).toHaveBeenCalledWith(expect.objectContaining({ ownership: 'PlatformManaged', page: 1, pageSize: 25 }), expect.any(AbortSignal))
    expect(screen.getByLabelText('Sahiplik')).toHaveValue('PlatformManaged')
    expect(screen.getByRole('link', { name: 'Acme A.Ş.' })).toHaveAttribute('href', '/platform/tenants/tenant-a')
    const row = rowOf('Managed Storage')
    expect(row.getByText('Platform yönetimli')).toBeInTheDocument()
    expect(row.getByText('managed')).toBeInTheDocument()
    expect(row.getByText('Etkin')).toBeInTheDocument()
    expect(row.getByText('Başarılı')).toBeInTheDocument()
    expect(row.getByText(/3 (?:Replica|Kopya)/)).toBeInTheDocument()
    expect(row.getByText('123 MB')).toBeInTheDocument()
  })

  it('explains the platform lock in one always-visible sentence instead of a collapsed section', async () => {
    mount()
    await screen.findByText('Managed Storage')
    const explanation = screen.getByText(/^Platform kilidi, .*[.]$/)
    expect(explanation.closest('details')).toBeNull()
    expect(explanation.textContent?.match(/[.]/g)).toHaveLength(1)
  })

  it('wires ownership, provider, enabled and tenant search into the URL and the request, resetting the page', async () => {
    const user = userEvent.setup()
    mount('/platform/storage-connections?page=3')
    await screen.findByText('Managed Storage')
    await user.selectOptions(screen.getByLabelText('Sahiplik'), 'all')
    await waitFor(() => expect(location()).toBe('/platform/storage-connections?ownership=all'))
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenLastCalledWith(expect.objectContaining({ ownership: undefined, page: 1 }), expect.any(AbortSignal)))
    await user.selectOptions(screen.getByLabelText("Depolama Sağlayıcısı"), 'central')
    await user.selectOptions(screen.getByLabelText('Durum'), 'false')
    await waitFor(() => expect(location()).toBe('/platform/storage-connections?ownership=all&providerType=central&enabled=false'))
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenLastCalledWith(expect.objectContaining({ ownership: undefined, providerType: 'central', enabled: false }), expect.any(AbortSignal)))
    await user.type(screen.getByRole('searchbox', { name: "Çalışma alanı ara" }), 'acme')
    await user.click(screen.getByRole('button', { name: 'Ara' }))
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'acme', providerType: 'central', enabled: false }), expect.any(AbortSignal)))
    await user.selectOptions(screen.getByLabelText('Sahiplik'), 'PlatformManaged')
    await waitFor(() => expect(location()).toBe('/platform/storage-connections?providerType=central&enabled=false&search=acme'))
  })

  it('pages through the list via the URL', async () => {
    const user = userEvent.setup()
    serve([managed], { totalCount: 60 })
    mount()
    await screen.findByText('Managed Storage')
    expect(screen.getByText('60 kayıt · Sayfa 1 / 3')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(location()).toBe('/platform/storage-connections?page=2'))
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, pageSize: 25 }), expect.any(AbortSignal)))
  })

  it('shows the authorization state on 403 and an empty message', async () => {
    vi.mocked(api.getPlatformConnections).mockRejectedValue(new ApiError(403))
    const first = mount()
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu alan için yetkiniz yok')
    first.unmount()
    serve([])
    mount()
    expect(await screen.findByText('Kriterlere uygun depolama hedefi bulunamadı.')).toBeInTheDocument()
  })

  it('offers each row only the actions its permissions allow', async () => {
    serve([managed, tenantManaged, makeConnection({ id: 'conn-ro', name: 'Salt okunur', tenantName: 'Gama' })])
    mount()
    await screen.findByText('Managed Storage')
    // central declares no secret fields, so no secret control even with canManageSecret
    expect(rowButtons('Managed Storage')).toEqual(['Düzenle', 'Platform kilidi koy', 'Test et', 'Sil'])
    expect(rowButtons('Local')).toEqual(['Düzenle', 'Platform kilidi koy'])
    expect(rowButtons('Salt okunur')).toEqual([])
  })

  it('shows secret controls only for a PlatformManaged connection whose provider takes secrets', async () => {
    serve([
      makeManaged({ id: 'fake-managed', name: 'S3 Managed', providerType: 'fake-s3', providerDisplayName: 'Fake S3', permissions: { ...platformFull }, hasSecret: true, secretFieldNames: ['accessKey', 'secretKey'] }),
      makeConnection({ id: 'fake-tenant', name: 'S3 Tenant', providerType: 'fake-s3', providerDisplayName: 'Fake S3', hasSecret: true, permissions: { ...noPermissions, canSetQuotaScope: true, canToggle: true } }),
    ])
    mount()
    await screen.findByText('S3 Managed')
    expect(rowButtons('S3 Managed')).toEqual(['Düzenle', 'Platform kilidi koy', 'Test et', 'Kimlik bilgisini yenile', 'Kimlik bilgisini temizle', 'Sil'])
    expect(rowButtons('S3 Tenant')).toEqual(['Düzenle', 'Platform kilidi koy'])
    expect(rowOf('S3 Tenant').getByText('Kimlik bilgisi kayıtlı')).toBeInTheDocument()
  })

  it('lets a PlatformAdmin set only the quota scope of a tenant-managed connection, with validation', async () => {
    const user = userEvent.setup()
    vi.mocked(api.updatePlatformConnection).mockResolvedValue({ ...tenantManaged, quotaScope: 'managed' })
    mount('/platform/storage-connections?ownership=all')
    await screen.findByText('Local')
    await user.click(screen.getByRole('button', { name: 'Local bağlantısı: düzenle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Ad')).toBeDisabled()
    const scope = within(dialog).getByLabelText(/Kota kapsamı/)
    const save = within(dialog).getByRole('button', { name: 'Kaydet' })
    expect(save).toBeDisabled()
    await user.clear(scope)
    await user.type(scope, 'Managed')
    expect(within(dialog).getByRole('alert')).toHaveTextContent('küçük harf')
    expect(save).toBeDisabled()
    await user.clear(scope)
    await user.type(scope, 'managed')
    await user.click(save)
    await waitFor(() => expect(api.updatePlatformConnection).toHaveBeenCalledWith('conn-local', { quotaScope: 'managed' }))
  })

  it('states the disable semantics and disables by connection id', async () => {
    const user = userEvent.setup()
    vi.mocked(api.setPlatformConnectionEnabled).mockResolvedValue(makeManaged({ administrativelyDisabled: true }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'Managed Storage bağlantısı: platform kilidi koy' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/(?:yeni backup'lar \(manuel ve zamanlanmış\) başlatılamaz|yeni yedeklemeler \(elle başlatılan ve zamanlanmış\) başlatılamaz)/)
    expect(dialog).toHaveTextContent(/(?:Mevcut Replica'lar silinmez|Mevcut kopyalar silinmez)/)
    expect(dialog).toHaveTextContent('sahibin etkin / devre dışı tercihi korunur')
    await user.click(within(dialog).getByRole('button', { name: 'Platform kilidi koy' }))
    await waitFor(() => expect(api.setPlatformConnectionEnabled).toHaveBeenCalledWith('conn-central', false))
  })

  it('unlocks a tenant-owned target without enabling its owner preference', async () => {
    const user = userEvent.setup()
    const locked = makeConnection({ enabled: false, administrativelyDisabled: true, permissions: { ...noPermissions, canToggle: true } })
    serve([locked])
    vi.mocked(api.setPlatformConnectionEnabled).mockResolvedValue(makeConnection({ enabled: false, administrativelyDisabled: false }))
    mount('/platform/storage-connections?ownership=all')
    await screen.findByText('Local')
    expect(rowOf('Local').getByText('Sahip tercihi: Devre dışı')).toBeInTheDocument()
    expect(rowOf('Local').getByText('Platform kilidi')).toBeInTheDocument()
    expect(rowOf('Local').getByText(/kilit kalkana kadar yeni yedeklemeler için kullanılamaz/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Local bağlantısı: platform kilidini kaldır' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/(?:Kilit kalksa da sahip tercihi|Kilit kaldırılsa da sahibin tercihi)/)
    await user.click(within(dialog).getByRole('button', { name: 'Platform kilidini kaldır' }))
    await waitFor(() => expect(api.setPlatformConnectionEnabled).toHaveBeenCalledWith('conn-local', true))
  })

  it('tests a PlatformManaged connection and reports a failure to start the test', async () => {
    const user = userEvent.setup()
    vi.mocked(api.testPlatformConnection).mockRejectedValue(new ApiError(409, { error: 'tenant_managed' }))
    mount()
    await user.click(await screen.findByRole('button', { name: 'Managed Storage bağlantısı: test et' }))
    await waitFor(() => expect(api.testPlatformConnection).toHaveBeenCalledWith('conn-central'))
    expect(await screen.findByText("Çalışma alanı tarafından yönetilen bağlantı için bu işlemi yalnızca çalışma alanı yöneticisi yapabilir.")).toBeInTheDocument()
  })
})

describe('Managed target ekle', () => {
  it('creates a PlatformManaged connection for a chosen tenant from the platform-creatable providers', async () => {
    const user = userEvent.setup()
    vi.mocked(api.createPlatformConnection).mockResolvedValue(managed)
    mount()
    await screen.findByText('Managed Storage')
    await user.click(screen.getByRole('button', { name: "Yönetilen hedef ekle" }))
    const dialog = await screen.findByRole('dialog')
    const providerSelect = within(dialog).getByLabelText("Depolama Sağlayıcısı")
    const options = within(providerSelect).getAllByRole('option').map((option) => option.textContent)
    expect(options).toEqual(['Sağlayıcı seçin', 'Managed Storage (Central)', 'Fake S3'])
    const submit = within(dialog).getByRole('button', { name: "Yönetilen hedef ekle" })
    expect(submit).toBeDisabled()
    await user.selectOptions(await within(dialog).findByRole('option', { name: 'Acme A.Ş.' }).then(() => within(dialog).getByLabelText("Çalışma Alanı")), 'tenant-a')
    await user.selectOptions(providerSelect, 'central')
    await user.type(within(dialog).getByLabelText('Ad'), 'Arşiv hedefi')
    await user.click(submit)
    await waitFor(() => expect(api.createPlatformConnection).toHaveBeenCalledWith('tenant-a', { providerType: 'central', name: 'Arşiv hedefi', ownership: 'PlatformManaged' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('renders provider fields and sends the quota scope and secret for a provider that needs them', async () => {
    const user = userEvent.setup()
    vi.mocked(api.createPlatformConnection).mockResolvedValue(managed)
    mount()
    await screen.findByText('Managed Storage')
    await user.click(screen.getByRole('button', { name: "Yönetilen hedef ekle" }))
    const dialog = await screen.findByRole('dialog')
    await user.selectOptions(await within(dialog).findByRole('option', { name: 'Acme A.Ş.' }).then(() => within(dialog).getByLabelText("Çalışma Alanı")), 'tenant-a')
    await user.selectOptions(within(dialog).getByLabelText("Depolama Sağlayıcısı"), 'fake-s3')
    await user.type(within(dialog).getByLabelText('Ad'), 'S3')
    await user.type(within(dialog).getByLabelText(/Kota kapsamı/), 'Bad Scope')
    expect(within(dialog).getByRole('button', { name: "Yönetilen hedef ekle" })).toBeDisabled()
    await user.clear(within(dialog).getByLabelText(/Kota kapsamı/))
    await user.type(within(dialog).getByLabelText(/Kota kapsamı/), 'managed-s3')
    await user.type(within(dialog).getByLabelText("Kova"), 'b1')
    expect(within(dialog).getByLabelText("Erişim anahtarı")).toHaveAttribute('type', 'password')
    await user.type(within(dialog).getByLabelText("Erişim anahtarı"), 'ak-1')
    await user.type(within(dialog).getByLabelText("Gizli anahtar"), 'sk-1')
    await user.click(within(dialog).getByRole('button', { name: "Yönetilen hedef ekle" }))
    await waitFor(() => expect(api.createPlatformConnection).toHaveBeenCalledWith('tenant-a', {
      providerType: 'fake-s3', name: 'S3', configuration: { bucket: 'b1', useTls: 'false' }, secret: { accessKey: 'ak-1', secretKey: 'sk-1' }, ownership: 'PlatformManaged', quotaScope: 'managed-s3',
    }))
  })

  it('shows the server error code in Turkish', async () => {
    const user = userEvent.setup()
    vi.mocked(api.createPlatformConnection).mockRejectedValue(new ApiError(409, { error: 'connection_limit_reached' }))
    mount()
    await screen.findByText('Managed Storage')
    await user.click(screen.getByRole('button', { name: "Yönetilen hedef ekle" }))
    const dialog = await screen.findByRole('dialog')
    await user.selectOptions(await within(dialog).findByRole('option', { name: 'Acme A.Ş.' }).then(() => within(dialog).getByLabelText("Çalışma Alanı")), 'tenant-a')
    await user.selectOptions(within(dialog).getByLabelText("Depolama Sağlayıcısı"), 'central')
    await user.type(within(dialog).getByLabelText('Ad'), 'İkinci')
    await user.click(within(dialog).getByRole('button', { name: "Yönetilen hedef ekle" }))
    expect(await within(dialog).findByText("Bu depolama sağlayıcısı için bağlantı sınırına ulaşıldı.")).toBeInTheDocument()
  })
})
