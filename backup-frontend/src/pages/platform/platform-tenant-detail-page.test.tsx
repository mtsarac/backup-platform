import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import * as supportApi from '@/features/platform/support/api'
import * as encryptionApi from '@/features/artifact-encryption/api'
import * as storageApi from '@/features/platform/storage/api'
import { makeTenantEncryption } from '@/features/artifact-encryption/test-fixtures'
import * as connectionApi from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged, noPermissions } from '@/features/storage-connections/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformAuditEntry, PlatformMembership, PlatformTenantDetail, PlatformUser } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { PlatformTenantDetailPage } from './platform-tenant-detail-page'

vi.mock('@/features/platform/api')
vi.mock('@/features/platform/support/api')
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/artifact-encryption/api')
vi.mock('@/features/platform/storage/api')

const tenant: PlatformTenantDetail = { tenantId: 'tenant-a', name: 'Acme A.Ş.', memberCount: 2, enabledBackupJobs: 5, agents: { total: 4, online: 2, offline: 1, unknown: 0, disabled: 1, revoked: 0 }, enrollment: { enabled: true, changedAt: '2026-10-05T09:00:00Z' } }
const ayse: PlatformMembership = { userId: 'user-1', displayName: 'Ayşe Yılmaz', loginIdentifier: 'ayse', role: 'Owner' }
const can: PlatformMembership = { userId: 'user-2', displayName: 'Can Demir', loginIdentifier: null, role: 'Viewer' }
const users: PlatformUser[] = [
  { userId: 'user-3', displayName: 'Deniz Kaya', loginIdentifier: 'deniz', isPlatformAdmin: false, membershipCount: 0 },
  { userId: 'user-4', displayName: 'Root', loginIdentifier: 'root', isPlatformAdmin: true, membershipCount: 0 },
]
const audit: PlatformAuditEntry = { id: 'audit-1', at: '2026-10-06T08:00:00Z', actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', actorKind: 'PlatformAdmin', action: 'membership.role_changed', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', targetType: 'Membership', targetId: 'user-1', details: { role: 'Admin', changed: true, note: null } }

function mount(tab = '') {
  return renderPlatform(<PlatformTenantDetailPage />, { path: `/platform/tenants/tenant-a${tab ? `?tab=${tab}` : ''}`, route: '/platform/tenants/:tenantId' })
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenant).mockResolvedValue(tenant)
  vi.mocked(api.getMemberships).mockResolvedValue(paged([ayse, can]))
  vi.mocked(api.getUsers).mockResolvedValue(paged(users))
  vi.mocked(api.getAudit).mockResolvedValue(paged([audit]))
  vi.mocked(api.getTenantSubscription).mockResolvedValue({ tenantId: 'tenant-a', subscription: null })
  vi.mocked(supportApi.getSessions).mockResolvedValue(paged([]))
  vi.mocked(connectionApi.getPlatformTenantConnections).mockResolvedValue({ items: [] })
  vi.mocked(connectionApi.getPlatformProviders).mockResolvedValue({ items: catalog })
  vi.mocked(encryptionApi.getWrapKeys).mockResolvedValue({ items: [] })
  vi.mocked(encryptionApi.getTenantEncryption).mockRejectedValue(new ApiError(403))
  vi.mocked(encryptionApi.getBreakGlassRequests).mockResolvedValue(paged([]))
  vi.mocked(storageApi.getPlatformTenantQuota).mockRejectedValue(new ApiError(503))
})

describe('platform tenant detail', () => {
  it('keeps active keys non-retirable and recovery removal name-confirmed in security', async () => {
    vi.mocked(encryptionApi.getTenantEncryption).mockResolvedValue(makeTenantEncryption({
      tenantRecovery: { keyId: 'tenant-recovery-id', keyVersion: 2, createdAt: '2026-10-19T10:00:00Z' },
    }))
    vi.mocked(encryptionApi.getWrapKeys).mockResolvedValue({ items: [
      { version: 2, keyId: 'active-wrap-id', state: 'Active', artifactCount: 3, createdAt: '2026-10-20T10:00:00Z' },
    ] })
    vi.mocked(encryptionApi.removeTenantRecoveryKey).mockResolvedValue(undefined)
    mount('security')
    const field = await screen.findByRole('textbox', { name: 'Genel anahtar (PEM)' })
    expect(screen.getByRole('button', { name: 'Emekli et' })).toBeDisabled()
    expect(api.getMemberships).not.toHaveBeenCalled()
    expect(api.getAudit).not.toHaveBeenCalled()
    expect(connectionApi.getPlatformTenantConnections).not.toHaveBeenCalled()
    fireEvent.change(field, { target: { value: 'accidental PRIVATE KEY input' } })
    await userEvent.click(screen.getByRole('button', { name: 'Anahtarı değiştir' }))
    expect(encryptionApi.setTenantRecoveryKey).not.toHaveBeenCalled()
    expect(field).toHaveValue('')
    expect(await screen.findByText(/Özel anahtar kabul edilmez/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Anahtarı kaldır' }))
    const dialog = within(await screen.findByRole('dialog'))
    const confirm = dialog.getByRole('button', { name: 'Kaldır' })
    expect(confirm).toBeDisabled()
    expect(encryptionApi.removeTenantRecoveryKey).not.toHaveBeenCalled()
    await userEvent.type(dialog.getByRole('textbox'), tenant.name)
    await userEvent.click(confirm)
    await waitFor(() => expect(encryptionApi.removeTenantRecoveryKey).toHaveBeenCalledWith('tenant-a'))
  })

  it('exposes four URL-addressable sections and loads only the active section', async () => {
    vi.mocked(encryptionApi.getWrapKeys).mockResolvedValue({ items: [] })
    vi.mocked(encryptionApi.getTenantEncryption).mockRejectedValue(new ApiError(403))
    vi.mocked(encryptionApi.getBreakGlassRequests).mockResolvedValue(paged([]))
    mount()
    await screen.findByRole('heading', { name: tenant.name })
    expect(screen.getAllByRole('tab').map(tab => tab.textContent)).toEqual(['Özet', 'Kişiler', 'Depolama ve bilgisayarlar', 'Güvenlik'])
    expect(api.getMemberships).not.toHaveBeenCalled()
    expect(connectionApi.getPlatformTenantConnections).not.toHaveBeenCalled()
    expect(storageApi.getPlatformTenantQuota).not.toHaveBeenCalled()
    expect(supportApi.getSessions).not.toHaveBeenCalled()
    expect(encryptionApi.getWrapKeys).not.toHaveBeenCalled()
    expect(encryptionApi.getTenantEncryption).not.toHaveBeenCalled()
    expect(encryptionApi.getBreakGlassRequests).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('tab', { name: 'Kişiler' }))
    expect(await screen.findByText('Ayşe Yılmaz')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('?tab=people')
    expect(encryptionApi.getWrapKeys).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('tab', { name: 'Depolama ve bilgisayarlar' }))
    expect(await screen.findByRole('switch')).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('?tab=storage-computers')
    expect(connectionApi.getPlatformTenantConnections).toHaveBeenCalled()
    expect(supportApi.getSessions).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('tab', { name: 'Güvenlik' }))
    expect(await screen.findByRole('heading', { name: 'Destek erişimi' })).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent('?tab=security')
    await waitFor(() => expect(encryptionApi.getTenantEncryption).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal)))
    expect(encryptionApi.getWrapKeys).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal))
    expect(encryptionApi.getBreakGlassRequests).toHaveBeenCalledWith({ tenantId: 'tenant-a', page: 1, pageSize: 25 }, expect.any(AbortSignal))
    expect(await screen.findByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Anahtarı kaydet' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('tab', { name: 'Özet' }))
    expect(screen.getByTestId('location')).toHaveTextContent('?tab=summary')
    expect(screen.queryByRole('heading', { name: 'Destek erişimi' })).not.toBeInTheDocument()
  })

  it('manages the plan and quota only on the storage tab', async () => {
    mount()
    expect(await screen.findByRole('heading', { name: 'Acme A.Ş.' })).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'Plan ve kota' })).not.toBeInTheDocument()
    expect(api.getTenantSubscription).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('tab', { name: 'Depolama ve bilgisayarlar' }))
    const plan = within(await screen.findByRole('region', { name: 'Plan ve kota' }))
    expect(await plan.findByText('Bu çalışma alanı için henüz bir plan atanmadı.')).toBeInTheDocument()
    expect(plan.getByRole('button', { name: 'Plan ata' })).toBeInTheDocument()
    expect(api.getTenantSubscription).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal))
  })

  it('shows counts, filtered links, memberships and recent tenant audit', async () => {
    mount()
    expect(await screen.findByRole('heading', { name: 'Acme A.Ş.' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: "Yedekleme bilgisayarlarını görüntüle" })).toHaveAttribute('href', '/platform/agents?tenantId=tenant-a')
    expect(screen.getByRole('link', { name: "Tüm denetim kayıtları" })).toHaveAttribute('href', '/platform/audit?tenantId=tenant-a')
    expect(await screen.findByText('Üyelik rolü değiştirildi')).toBeInTheDocument()
    expect(screen.getByText('Evet')).toBeInTheDocument()
    expect(api.getAudit).toHaveBeenCalledWith({ tenantId: 'tenant-a', page: 1, pageSize: 10 }, expect.any(AbortSignal))
    expect(supportApi.getSessions).not.toHaveBeenCalled()
    expect(api.getMemberships).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('tab', { name: 'Güvenlik' }))
    expect(await screen.findByRole('heading', { name: 'Destek erişimi' })).toBeInTheDocument()
    expect(supportApi.getSessions).toHaveBeenCalledWith({ tenantId: 'tenant-a', page: 1, pageSize: 5 }, expect.any(AbortSignal))
    await userEvent.click(screen.getByRole('tab', { name: 'Kişiler' }))
    expect(await screen.findByText('Ayşe Yılmaz')).toBeInTheDocument()
    expect(screen.getByText('Can Demir')).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Ayşe Yılmaz rolü' })).toHaveValue('Owner')
  })

  it('renames the tenant', async () => {
    vi.mocked(api.renameTenant).mockResolvedValue({ tenantId: 'tenant-a', name: 'Acme Corp', memberCount: 2, agentCount: 4, enrollmentEnabled: true })
    mount()
    const input = await screen.findByLabelText('Ad')
    await userEvent.clear(input)
    await userEvent.type(input, 'Acme Corp')
    vi.mocked(api.getTenant).mockResolvedValue({ ...tenant, name: 'Acme Corp' })
    await userEvent.click(screen.getByRole('button', { name: 'Yeniden adlandır' }))
    expect(api.renameTenant).toHaveBeenCalledWith('tenant-a', 'Acme Corp')
    expect(await screen.findByRole('heading', { name: 'Acme Corp' })).toBeInTheDocument()
  })

  it('asks for confirmation before changing the enrollment policy', async () => {
    vi.mocked(api.setTenantEnrollment).mockResolvedValue({ tenantId: 'tenant-a', enabled: false, changed: true })
    mount('storage-computers')
    await userEvent.click(await screen.findByRole('switch', { name: "Yeni yedekleme bilgisayarı kayıtlarına izin ver" }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent("Bilgisayar kaydı kapatılsın mı?")
    expect(api.setTenantEnrollment).not.toHaveBeenCalled()
    vi.mocked(api.getTenant).mockResolvedValue({ ...tenant, enrollment: { enabled: false, changedAt: '2026-10-07T09:00:00Z' } })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydı kapat' }))
    expect(api.setTenantEnrollment).toHaveBeenCalledWith('tenant-a', false)
    await waitFor(() => expect(screen.getByRole('switch', { name: "Yeni yedekleme bilgisayarı kayıtlarına izin ver" })).toHaveAttribute('aria-checked', 'false'))
  })

  it('adds a member chosen from the searchable user list', async () => {
    vi.mocked(api.addMembership).mockResolvedValue({ userId: 'user-3', displayName: 'Deniz Kaya', loginIdentifier: 'deniz', role: 'Operator' })
    mount('people')
    await screen.findByText('Ayşe Yılmaz')
    await userEvent.type(screen.getByRole('searchbox', { name: 'Kullanıcı ara' }), 'den')
    await userEvent.click(screen.getByRole('button', { name: 'Ara' }))
    await waitFor(() => expect(api.getUsers).toHaveBeenLastCalledWith({ search: 'den', page: 1, pageSize: 20 }, expect.any(AbortSignal)))
    expect(screen.getByRole('option', { name: /Root \(root\) · (?:PlatformAdmin|Platform Yöneticisi)/ })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Kullanıcı' }), 'user-3')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Rol' }), 'Operator')
    vi.mocked(api.getMemberships).mockResolvedValue(paged([ayse, can, { userId: 'user-3', displayName: 'Deniz Kaya', loginIdentifier: 'deniz', role: 'Operator' }]))
    await userEvent.click(screen.getByRole('button', { name: 'Üye ekle' }))
    expect(api.addMembership).toHaveBeenCalledWith('tenant-a', 'user-3', 'Operator')
    expect(await screen.findByRole('combobox', { name: 'Deniz Kaya rolü' })).toHaveValue('Operator')
  })

  it('reports an existing member (409) and a missing user (404) with safe messages', async () => {
    vi.mocked(api.addMembership).mockRejectedValueOnce(new ApiError(409, { error: 'already_member' })).mockRejectedValueOnce(new ApiError(404, { error: 'user_not_found' }))
    mount('people')
    await screen.findByText('Ayşe Yılmaz')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Kullanıcı' }), 'user-3')
    await userEvent.click(screen.getByRole('button', { name: 'Üye ekle' }))
    expect(await screen.findByText("Bu kullanıcı zaten çalışma alanı üyesi.")).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Üye ekle' }))
    expect(await screen.findByText('Kullanıcı bulunamadı.')).toBeInTheDocument()
  })

  it('changes a member role', async () => {
    vi.mocked(api.changeMembershipRole).mockResolvedValue({ ...can, role: 'Admin' })
    mount('people')
    await screen.findByText('Can Demir')
    vi.mocked(api.getMemberships).mockResolvedValue(paged([ayse, { ...can, role: 'Admin' }]))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Can Demir rolü' }), 'Admin')
    expect(api.changeMembershipRole).toHaveBeenCalledWith('tenant-a', 'user-2', 'Admin')
    await waitFor(() => expect(api.getMemberships).toHaveBeenCalledTimes(2))
    expect(screen.getByRole('combobox', { name: 'Can Demir rolü' })).toHaveValue('Admin')
  })

  it('removes a member only after confirmation', async () => {
    vi.mocked(api.removeMembership).mockResolvedValue(undefined)
    mount('people')
    await screen.findByText('Can Demir')
    await userEvent.click(screen.getByRole('button', { name: 'Can Demir üyeliğini kaldır' }))
    const dialog = await screen.findByRole('dialog')
    expect(api.removeMembership).not.toHaveBeenCalled()
    vi.mocked(api.getMemberships).mockResolvedValue(paged([ayse]))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Üyeliği kaldır' }))
    expect(api.removeMembership).toHaveBeenCalledWith('tenant-a', 'user-2')
    await waitFor(() => expect(screen.queryByText('Can Demir')).not.toBeInTheDocument())
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('shows a not-found message for an unknown tenant', async () => {
    vi.mocked(api.getTenant).mockRejectedValue(new ApiError(404, { error: 'tenant_not_found' }))
    mount()
    expect(await screen.findByText("Çalışma alanı bulunamadı.")).toBeInTheDocument()
  })

  it('paginates memberships through the memberPage URL parameter', async () => {
    vi.mocked(api.getMemberships).mockResolvedValue(paged([ayse], { totalCount: 40 }))
    mount('people')
    await userEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('?tab=people&memberPage=2'))
    expect(api.getMemberships).toHaveBeenLastCalledWith('tenant-a', { page: 2, pageSize: 25 }, expect.any(AbortSignal))
  })

  it('lists the tenant\'s storage connections with permission-gated actions and adds a managed target for this tenant', async () => {
    const full = { canEdit: true, canSetQuotaScope: true, canToggle: true, canDelete: true, canManageSecret: true, canTest: true }
    vi.mocked(connectionApi.getPlatformTenantConnections).mockResolvedValue({ items: [
      makeManaged({ permissions: full }),
      makeConnection({ permissions: { ...noPermissions, canSetQuotaScope: true, canToggle: true } }),
    ] })
    vi.mocked(connectionApi.createPlatformConnection).mockResolvedValue(makeManaged())
    mount('storage-computers')
    expect(await screen.findByRole('heading', { name: 'Depolama bağlantıları' })).toBeInTheDocument()
    expect(connectionApi.getPlatformTenantConnections).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal))
    const managedHeading = (await screen.findAllByText('Managed Storage')).find(el => el.tagName === 'SPAN')!
    const managed = within(managedHeading.closest('tr') as HTMLElement)
    expect(managed.getAllByRole('button').map((button) => button.textContent).filter((label) => label !== 'Kopyala')).toEqual(['Düzenle', 'Platform kilidi koy', 'Test et', 'Sil'])
    const localHeading = screen.getByText('Local')
    const local = within(localHeading.closest('tr') as HTMLElement)
    expect(local.getAllByRole('button').map((button) => button.textContent).filter((label) => label !== 'Kopyala')).toEqual(['Düzenle', 'Platform kilidi koy'])
    expect(screen.queryByRole('columnheader', { name: "Çalışma Alanı" })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: "Yönetilen hedef ekle" }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: /(?:Managed target|Yönetilen hedef) ekle: Acme A.Ş./ })).toBeInTheDocument()
    expect(within(dialog).queryByLabelText("Çalışma alanı ara")).not.toBeInTheDocument()
    await userEvent.selectOptions(await within(dialog).findByLabelText("Depolama sağlayıcısı"), 'central')
    await userEvent.type(within(dialog).getByLabelText('Hedef adı'), 'İkinci hedef')
    await userEvent.click(within(dialog).getByRole('button', { name: "Yönetilen hedef ekle" }))
    await waitFor(() => expect(connectionApi.createPlatformConnection).toHaveBeenCalledWith('tenant-a', { providerType: 'central', name: 'İkinci hedef', ownership: 'PlatformManaged' }))
  })

  it('shows the authorization state for the connections section on 403', async () => {
    vi.mocked(connectionApi.getPlatformTenantConnections).mockRejectedValue(new ApiError(403))
    mount('storage-computers')
    expect(await screen.findByRole('heading', { name: 'Depolama bağlantıları' })).toBeInTheDocument()
    expect(await screen.findByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
  })
})
