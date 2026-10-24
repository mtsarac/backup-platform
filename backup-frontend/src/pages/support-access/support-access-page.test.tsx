import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { Sidebar } from '@/components/layout/sidebar'
import { TenantContext } from '@/features/tenancy/tenant-context'
import type { MembershipRole } from '@/features/platform/types'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import * as api from '@/features/support-access/api'
import { supportAccessKeys } from '@/features/support-access/hooks'
import type { SupportAccessSession } from '@/features/support-access/types'
import { ApiError } from '@/lib/api/client'
import { SupportAccessPage } from './support-access-page'

vi.mock('@/features/support-access/api')

const session: SupportAccessSession = {
  id: 'session-a', actorDisplayName: 'Destek Uzmanı', reason: 'Backup incelemesi',
  permissions: ['ViewBackupMetadata'], startedAt: '2026-10-09T10:00:00Z', expiresAt: '2026-10-09T11:00:00Z',
  status: 'Closed', closedAt: '2026-10-09T10:30:00Z', closeReason: 'İnceleme tamamlandı', createdAt: '2026-10-09T10:00:00Z',
  summary: { artifactsViewed: 2, downloadsStarted: 3, downloadsCompleted: 4, downloadsFailed: 5, restoresRequested: 6, restoresCancelled: 7 },
}

function renderAccess(role: MembershipRole = 'Owner', path = '/tenants/tenant-a/support-access', ui: ReactNode = <SupportAccessPage />) {
  const tenantId = 'tenant-a'
  return renderPlatform(<TenantContext.Provider value={{ tenantId, tenant: { tenantId, name: 'Acme', role }, canAdmin: role === 'Owner' || role === 'Admin', canOperate: role !== 'Viewer', switchTenant: vi.fn() }}>{ui}</TenantContext.Provider>, { path })
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getSupportAccessSessions).mockResolvedValue(paged([session], { totalCount: 30 }))
  vi.mocked(api.getSupportAccessSession).mockResolvedValue(session)
  vi.mocked(api.getSupportAccessActivity).mockResolvedValue(paged([{ id: 'event-a', at: session.startedAt, action: 'support.artifact.viewed', targetType: 'Artifact', targetId: 'artifact-a', result: 'Success' }], { totalCount: 30 }))
})

describe('tenant support transparency', () => {
  it.each(['Owner', 'Admin'] as const)('shows the tenant support navigation for %s', (role) => {
    renderAccess(role, '/tenants/tenant-a/support-access', <Sidebar />)
    const links = screen.getAllByRole('link', { name: /Support access|Destek erişimi/, hidden: true })
    expect(links.length).toBeGreaterThan(0)
    for (const link of links) expect(link).toHaveAttribute('href', '/tenants/tenant-a/support-access')
  })

  it.each(['Operator', 'Viewer'] as const)('hides the tenant support navigation for %s', (role) => {
    renderAccess(role, '/tenants/tenant-a/support-access', <Sidebar />)
    expect(screen.queryByRole('link', { name: /Support access|Destek erişimi/, hidden: true })).not.toBeInTheDocument()
  })

  it.each(['Owner', 'Admin'] as const)('renders history, all six counters and read-only activity for %s', async (role) => {
    renderAccess(role)
    expect(await screen.findByText('Destek Uzmanı')).toBeInTheDocument()
    for (const [label, count] of [['Görüntülenen yedek dosyası', 2], ['Başlatılan indirme', 3], ['Tamamlanan indirme', 4], ['Başarısız indirme', 5], ['İstenen geri yükleme', 6], ['İptal edilen geri yükleme', 7]] as const) {
      expect(screen.getByText(label).parentElement).toHaveTextContent(String(count))
    }
    fireEvent.click(screen.getByRole('button', { name: 'Etkinliği görüntüle' }))
    expect(await screen.findByText('support.artifact.viewed')).toBeInTheDocument()
    const detail = screen.getByRole('region', { name: 'Destek oturumu ayrıntısı' })
    expect(within(detail).getByText('İnceleme tamamlandı')).toBeInTheDocument()
    expect(within(detail).getByText('artifact-a')).toBeInTheDocument()
    const actionButtons = within(detail).getAllByRole('button').filter(button => !button.getAttribute('aria-label')?.startsWith('Kopyala:'))
    expect(actionButtons.some(button => /Geri Yükleme başlat|indir|iptal et/i.test(button.textContent ?? ''))).toBe(false)
    fireEvent.click(within(detail).getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(api.getSupportAccessActivity).toHaveBeenLastCalledWith('tenant-a', 'session-a', { page: 2, pageSize: 25 }, expect.any(AbortSignal)))
  })

  it('keeps staff, reason and readable activity on cards and raw targets in copyable collapsed details', async () => {
    renderAccess()
    const history = await screen.findByRole('list', { name: 'Destek erişimi geçmişi' })
    expect(within(history).getByText(session.actorDisplayName!)).toBeInTheDocument()
    expect(within(history).getByText(session.reason)).toBeInTheDocument()
    fireEvent.click(within(history).getByRole('button', { name: 'Etkinliği görüntüle' }))
    const activity = await screen.findByRole('list', { name: 'Etkinlik geçmişi' })
    expect(within(activity).getByText('Yedek dosyası görüntülendi')).toBeInTheDocument()
    const details = within(activity).getByText('artifact-a').closest('details')!
    expect(details).not.toHaveAttribute('open')
    expect(within(details).getByRole('button', { name: 'Kopyala: Hedef kimliği', hidden: true })).toBeInTheDocument()
    expect(within(details).getByText('support.artifact.viewed')).toBeInTheDocument()
  })

  it.each(['Operator', 'Viewer'] as const)('blocks %s before any query mounts', (role) => {
    renderAccess(role, '/tenants/tenant-a/support-access?sessionId=session-a')
    expect(screen.getByRole('alert')).toHaveTextContent(/(?:Owner \/ Admin|Sahibi \/ Yöneticisi)/)
    expect(screen.getByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(api.getSupportAccessSessions).not.toHaveBeenCalled()
    expect(api.getSupportAccessSession).not.toHaveBeenCalled()
    expect(api.getSupportAccessActivity).not.toHaveBeenCalled()
  })

  it('paginates the history using URL parameters', async () => {
    renderAccess()
    await screen.findByText('Destek Uzmanı')
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(api.getSupportAccessSessions).toHaveBeenLastCalledWith('tenant-a', { page: 2, pageSize: 25 }, expect.any(AbortSignal)))
    expect(screen.getByTestId('location')).toHaveTextContent('page=2')
  })

  it('renders a tenant-specific authorization state for server 403', async () => {
    vi.mocked(api.getSupportAccessSessions).mockRejectedValue(new ApiError(403))
    renderAccess()
    expect(await screen.findByRole('alert')).toHaveTextContent(/(?:Owner \/ Admin|Sahibi \/ Yöneticisi)/)
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('renders missing or cross-tenant session 404 without requesting activity', async () => {
    vi.mocked(api.getSupportAccessSession).mockRejectedValue(new ApiError(404, { error: 'support_session_not_found' }))
    renderAccess('Admin', '/tenants/tenant-a/support-access?sessionId=session-b')
    expect(await screen.findByRole('alert')).toHaveTextContent(/bu (?:tenant|çalışma alanın)a ait değil/)
    expect(api.getSupportAccessActivity).not.toHaveBeenCalled()
  })

  it('renders an authorization state when activity returns 403', async () => {
    vi.mocked(api.getSupportAccessActivity).mockRejectedValue(new ApiError(403))
    renderAccess('Admin', '/tenants/tenant-a/support-access?sessionId=session-a')
    expect(await screen.findByRole('alert')).toHaveTextContent(/(?:Owner \/ Admin|Sahibi \/ Yöneticisi)/)
    expect(screen.queryByText('support.artifact.viewed')).not.toBeInTheDocument()
  })

  it('scopes query keys by tenant and session and caches detail independently', async () => {
    const params = { page: 1, pageSize: 25 }
    expect(supportAccessKeys.sessions('tenant-a', params)).not.toEqual(supportAccessKeys.sessions('tenant-b', params))
    expect(supportAccessKeys.session('tenant-a', 'session-a')).not.toEqual(supportAccessKeys.session('tenant-b', 'session-a'))
    expect(supportAccessKeys.activity('tenant-a', 'session-a', params)).not.toEqual(supportAccessKeys.activity('tenant-a', 'session-b', params))
    const { client } = renderAccess('Owner', '/tenants/tenant-a/support-access?sessionId=session-a')
    await screen.findByText('support.artifact.viewed')
    expect(client.getQueryData(supportAccessKeys.session('tenant-a', 'session-a'))).toEqual(session)
    expect(client.getQueryData(supportAccessKeys.session('tenant-b', 'session-a'))).toBeUndefined()
  })
})
