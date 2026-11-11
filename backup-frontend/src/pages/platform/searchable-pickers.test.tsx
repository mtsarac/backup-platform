import { fireEvent, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { PlatformAgentsPage } from './platform-agents-page'
import { PlatformAuditPage } from './platform-audit-page'
import { tx } from '@/lib/i18n'
import { CreateManagedTargetDialog } from '@/features/storage-connections/create-dialogs'
import * as storageApi from '@/features/storage-connections/api'
import { catalog, makeManaged } from '@/features/storage-connections/test-fixtures'

vi.mock('@/features/platform/api')
vi.mock('@/features/storage-connections/api')
const tenant = { tenantId: 'tenant-acme', name: 'Acme', memberCount: 1, agentCount: 1, enrollmentEnabled: true }
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenants).mockResolvedValue(paged([tenant]))
  vi.mocked(api.getTenant).mockResolvedValue({ ...tenant, enabledBackupJobs: 1, agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 }, enrollment: { enabled: true, changedAt: null } })
  vi.mocked(api.getPlatformAgents).mockResolvedValue(paged([]))
  vi.mocked(api.getAudit).mockResolvedValue(paged([]))
  vi.mocked(storageApi.getPlatformProviders).mockResolvedValue({ items: catalog })
  vi.mocked(storageApi.createPlatformConnection).mockResolvedValue(makeManaged())
})

describe('platform name filters', () => {
  it.each([
    { Page: PlatformAuditPage, path: '/platform/audit', request: api.getAudit },
    { Page: PlatformAgentsPage, path: '/platform/agents', request: api.getPlatformAgents },
  ])('searches tenants by name and applies and clears their ID at $path', async ({ Page, path, request }) => {
    renderPlatform(<Page />, { path: `${path}?page=3&search=sql&connectivity=offline` })
    const picker = await screen.findByRole('combobox', { name: tx('Tenant') })
    fireEvent.change(picker, { target: { value: 'Acme' } })
    await waitFor(() => expect(api.getTenants).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'Acme' }), expect.any(AbortSignal)))
    fireEvent.click(await screen.findByRole('option', { name: 'Acme' }))
    await waitFor(() => expect(request).toHaveBeenLastCalledWith(expect.objectContaining({ tenantId: 'tenant-acme', page: 1 }), expect.any(AbortSignal)))
    expect(picker).toHaveValue('Acme')
    expect(screen.getByTestId('location')).toHaveTextContent('search=sql&connectivity=offline&tenantId=tenant-acme')
    fireEvent.click(screen.getByRole('button', { name: 'Seçimi temizle' }))
    await waitFor(() => expect(request).toHaveBeenLastCalledWith(expect.objectContaining({ tenantId: '' }), expect.any(AbortSignal)))
  })
})

describe('setup name pickers', () => {
  it('creates a managed target for the tenant selected by name', async () => {
    renderPlatform(<CreateManagedTargetDialog onClose={vi.fn()} />)
    fireEvent.change(await screen.findByRole('combobox', { name: tx('Tenant') }), { target: { value: 'Acme' } })
    fireEvent.click(await screen.findByRole('option', { name: 'Acme' }))
    fireEvent.change(screen.getByRole('combobox', { name: tx('Depolama sağlayıcısı') }), { target: { value: 'central' } })
    fireEvent.change(screen.getByRole('textbox', { name: tx('Hedef adı') }), { target: { value: 'Offsite' } })
    fireEvent.click(screen.getByRole('button', { name: tx('Yönetilen hedef ekle') }))
    await waitFor(() => expect(storageApi.createPlatformConnection).toHaveBeenCalledWith('tenant-acme', expect.objectContaining({ name: 'Offsite', providerType: 'central' })))
  })

})
