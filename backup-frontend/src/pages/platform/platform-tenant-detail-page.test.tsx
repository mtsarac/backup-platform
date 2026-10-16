import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import * as supportApi from '@/features/platform/support/api'
import * as connectionApi from '@/features/storage-connections/api'
import { catalog, makeConnection, makeManaged, noPermissions } from '@/features/storage-connections/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformAuditEntry, PlatformMembership, PlatformTenantDetail, PlatformUser } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { PlatformTenantDetailPage } from './platform-tenant-detail-page'

vi.mock('@/features/platform/api')
vi.mock('@/features/platform/support/api')
vi.mock('@/features/storage-connections/api')

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
})

describe('platform tenant detail', () => {
  it('shows counts, filtered links, memberships and recent tenant audit', async () => {
    mount()
    expect(await screen.findByRole('heading', { name: 'Acme A.Ş.' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: "Bu çalışma alanının ajanları" })).toHaveAttribute('href', '/platform/agents?tenantId=tenant-a')
    expect(screen.getByRole('link', { name: "Tüm denetim kayıtları" })).toHaveAttribute('href', '/platform/audit?tenantId=tenant-a')
    expect(await screen.findByText('Üyelik rolü değiştirildi')).toBeInTheDocument()
    expect(screen.getByText('Evet')).toBeInTheDocument()
    expect(api.getAudit).toHaveBeenCalledWith({ tenantId: 'tenant-a', page: 1, pageSize: 10 }, expect.any(AbortSignal))
    expect(supportApi.getSessions).not.toHaveBeenCalled()
    expect(api.getMemberships).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('tab', { name: 'Destek erişimi' }))
    expect(await screen.findByRole('heading', { name: 'Destek erişimi' })).toBeInTheDocument()
    expect(supportApi.getSessions).toHaveBeenCalledWith({ tenantId: 'tenant-a', page: 1, pageSize: 5 }, expect.any(AbortSignal))
    await userEvent.click(screen.getByRole('tab', { name: 'Üyeler' }))
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
    mount('agents')
    await userEvent.click(await screen.findByRole('switch', { name: "Yeni ajan kayıtlarına izin ver" }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent("Ajan kaydı kapatılsın mı?")
    expect(api.setTenantEnrollment).not.toHaveBeenCalled()
    vi.mocked(api.getTenant).mockResolvedValue({ ...tenant, enrollment: { enabled: false, changedAt: '2026-10-07T09:00:00Z' } })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydı kapat' }))
    expect(api.setTenantEnrollment).toHaveBeenCalledWith('tenant-a', false)
    await waitFor(() => expect(screen.getByRole('switch', { name: "Yeni ajan kayıtlarına izin ver" })).toHaveAttribute('aria-checked', 'false'))
  })

  it('adds a member chosen from the searchable user list', async () => {
    vi.mocked(api.addMembership).mockResolvedValue({ userId: 'user-3', displayName: 'Deniz Kaya', loginIdentifier: 'deniz', role: 'Operator' })
    mount('members')
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
    mount('members')
    await screen.findByText('Ayşe Yılmaz')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Kullanıcı' }), 'user-3')
    await userEvent.click(screen.getByRole('button', { name: 'Üye ekle' }))
    expect(await screen.findByText("Bu kullanıcı zaten çalışma alanı üyesi.")).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Üye ekle' }))
    expect(await screen.findByText('Kullanıcı bulunamadı.')).toBeInTheDocument()
  })

  it('changes a member role', async () => {
    vi.mocked(api.changeMembershipRole).mockResolvedValue({ ...can, role: 'Admin' })
    mount('members')
    await screen.findByText('Can Demir')
    vi.mocked(api.getMemberships).mockResolvedValue(paged([ayse, { ...can, role: 'Admin' }]))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Can Demir rolü' }), 'Admin')
    expect(api.changeMembershipRole).toHaveBeenCalledWith('tenant-a', 'user-2', 'Admin')
    await waitFor(() => expect(api.getMemberships).toHaveBeenCalledTimes(2))
    expect(screen.getByRole('combobox', { name: 'Can Demir rolü' })).toHaveValue('Admin')
  })

  it('removes a member only after confirmation', async () => {
    vi.mocked(api.removeMembership).mockResolvedValue(undefined)
    mount('members')
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
    mount('members')
    await userEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('?tab=members&memberPage=2'))
    expect(api.getMemberships).toHaveBeenLastCalledWith('tenant-a', { page: 2, pageSize: 25 }, expect.any(AbortSignal))
  })

  it('lists the tenant\'s storage connections with permission-gated actions and adds a managed target for this tenant', async () => {
    const full = { canEdit: true, canSetQuotaScope: true, canToggle: true, canDelete: true, canManageSecret: true, canTest: true }
    vi.mocked(connectionApi.getPlatformTenantConnections).mockResolvedValue({ items: [
      makeManaged({ permissions: full }),
      makeConnection({ permissions: { ...noPermissions, canSetQuotaScope: true, canToggle: true } }),
    ] })
    vi.mocked(connectionApi.createPlatformConnection).mockResolvedValue(makeManaged())
    mount('storage')
    expect(await screen.findByRole('heading', { name: 'Depolama bağlantıları' })).toBeInTheDocument()
    expect(connectionApi.getPlatformTenantConnections).toHaveBeenCalledWith('tenant-a', expect.any(AbortSignal))
    const managedHeading = (await screen.findAllByText('Managed Storage')).find(el => el.tagName === 'SPAN')!
    const managed = within(managedHeading.closest('tr') as HTMLElement)
    expect(managed.getAllByRole('button').map((button) => button.textContent)).toEqual(['Düzenle', 'Platform kilidi koy', 'Test et', 'Sil'])
    const localHeading = screen.getByText('Local')
    const local = within(localHeading.closest('tr') as HTMLElement)
    expect(local.getAllByRole('button').map((button) => button.textContent)).toEqual(['Düzenle', 'Platform kilidi koy'])
    expect(screen.queryByRole('columnheader', { name: "Çalışma Alanı" })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: "Yönetilen hedef ekle" }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: /(?:Managed target|Yönetilen hedef) ekle: Acme A.Ş./ })).toBeInTheDocument()
    expect(within(dialog).queryByLabelText("Çalışma alanı ara")).not.toBeInTheDocument()
    await userEvent.selectOptions(await within(dialog).findByLabelText("Depolama Sağlayıcısı"), 'central')
    await userEvent.type(within(dialog).getByLabelText('Ad'), 'İkinci hedef')
    await userEvent.click(within(dialog).getByRole('button', { name: "Yönetilen hedef ekle" }))
    await waitFor(() => expect(connectionApi.createPlatformConnection).toHaveBeenCalledWith('tenant-a', { providerType: 'central', name: 'İkinci hedef', ownership: 'PlatformManaged' }))
  })

  it('shows the authorization state for the connections section on 403', async () => {
    vi.mocked(connectionApi.getPlatformTenantConnections).mockRejectedValue(new ApiError(403))
    mount('storage')
    expect(await screen.findByRole('heading', { name: 'Depolama bağlantıları' })).toBeInTheDocument()
    expect(await screen.findByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
  })
})
