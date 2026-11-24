import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { setViewport } from '@/test/viewport'
import { PlatformAuditPage } from './platform-audit-page'

vi.mock('@/features/platform/api')

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenants).mockResolvedValue(paged([]))
})

describe('platform audit page', () => {
  it('lists plain action labels with the raw codes collapsed', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([{ id: 'audit-1', at: new Date().toISOString(), actorUserId: 'admin-id', actorDisplayName: 'Platform Operatörü', actorKind: 'PlatformAdmin', action: 'tenant_wrap_key.rotated', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', targetType: 'tenant_wrap_key', targetId: 'request-1', details: {} }]))
    renderPlatform(<PlatformAuditPage />)
    expect(screen.getByRole('heading', { level: 1, name: 'Denetim kaydı' })).toBeVisible() // same term as the sidebar link
    expect(await screen.findByText('Şifreleme anahtarı yenilendi')).toBeVisible()
    expect(screen.queryByText('tenant_wrap_key.rotated')).not.toBeInTheDocument()
    expect(screen.queryByText('request-1')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = within(screen.getByRole('region', { name: 'Teknik ayrıntılar' }))
    expect(dialog.getByText('tenant_wrap_key.rotated')).toBeVisible()
    expect(dialog.getByText('request-1')).toBeVisible()
  })

  it('sends search and sorting to the audit list', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([{ id: 'audit-1', at: new Date().toISOString(), actorUserId: 'admin-id', actorDisplayName: 'Admin', actorKind: 'PlatformAdmin', action: 'tenant_wrap_key.rotated', tenantId: null, tenantName: null, targetType: 'tenant', targetId: 'one', details: {} }]))
    setViewport(false)
    const user = userEvent.setup()
    renderPlatform(<PlatformAuditPage />)
    await screen.findByText('Şifreleme anahtarı yenilendi')
    await user.type(screen.getByRole('searchbox', { name: 'Ara' }), 'Admin')
    await waitFor(() => expect(api.getAudit).toHaveBeenCalledWith(expect.objectContaining({ search: 'Admin', page: 1 }), expect.anything()))
    await user.selectOptions(screen.getByLabelText('Sırala'), 'action:asc')
    await waitFor(() => expect(api.getAudit).toHaveBeenCalledWith(expect.objectContaining({ search: 'Admin', sort: 'action', dir: 'asc' }), expect.anything()))
  })

  it('keeps the tenant filter and search while paging', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([{ id: 'audit-1', at: new Date().toISOString(), actorUserId: 'admin-id', actorDisplayName: 'Admin', actorKind: 'PlatformAdmin', action: 'tenant_wrap_key.rotated', tenantId: 'tenant-a', tenantName: 'Acme A.Ş.', targetType: 'tenant', targetId: 'one', details: {} }], { totalCount: 60 }))
    renderPlatform(<PlatformAuditPage />, { path: '/platform/audit?tenantId=tenant-a&search=admin' })
    await screen.findByText('Şifreleme anahtarı yenilendi')
    await userEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(api.getAudit).toHaveBeenLastCalledWith(expect.objectContaining({ tenantId: 'tenant-a', search: 'admin', page: 2 }), expect.anything()))
  })

  it('offers clearing the filters when nothing matches', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([]))
    renderPlatform(<PlatformAuditPage />, { path: '/platform/audit?search=zzz' })
    await screen.findByText('Aramayla eşleşen kayıt yok.')
    await userEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    await waitFor(() => expect(vi.mocked(api.getAudit).mock.lastCall?.[0]).not.toHaveProperty('search'))
  })

  it('says when there is nothing to show', async () => {
    vi.mocked(api.getAudit).mockResolvedValue(paged([]))
    renderPlatform(<PlatformAuditPage />)
    expect(await screen.findByText('Henüz denetim kaydı yok.')).toBeVisible()
  })
})
