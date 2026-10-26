import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { Route } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/artifact-encryption/api'
import { makeBreakGlass } from '@/features/artifact-encryption/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import { PlatformBreakGlassDetailPage } from './platform-break-glass-detail-page'
import { PlatformBreakGlassPage } from './platform-break-glass-page'
import { tx } from '@/lib/i18n'
import * as platformApi from '@/features/platform/api'
import * as supportApi from '@/features/platform/support/api'
import { makeArtifact, makeSession } from '@/features/platform/support/test-fixtures'

vi.mock('@/features/artifact-encryption/api')
vi.mock('@/features/platform/api')
vi.mock('@/features/platform/support/api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getBreakGlassRequests).mockResolvedValue(paged([makeBreakGlass(), makeBreakGlass({ id: 'request-b', tenantName: 'Beta', status: 'Consumed', canApprove: false })]))
  const tenantId = '22222222-2222-2222-2222-222222222222'
  vi.mocked(platformApi.getTenants).mockResolvedValue(paged([{ tenantId, name: 'Acme', memberCount: 1, agentCount: 1, enrollmentEnabled: true }]))
  vi.mocked(platformApi.getTenant).mockResolvedValue({ tenantId, name: 'Acme', memberCount: 1, enabledBackupJobs: 1, agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 }, enrollment: { enabled: true, changedAt: null } })
  vi.mocked(supportApi.getSessions).mockResolvedValue(paged([makeSession({ tenantId })]))
  vi.mocked(supportApi.getSession).mockResolvedValue(makeSession({ tenantId }))
  vi.mocked(supportApi.getSupportArtifacts).mockResolvedValue(paged([makeArtifact({ id: '11111111-1111-1111-1111-111111111111', databaseName: 'Accounts' })]))
})
const list = (search = '') => renderPlatform(<PlatformBreakGlassPage />, { path: `/platform/break-glass${search}`, route: '/platform/break-glass' })

describe('break-glass list', () => {
  it('lists requests with status, links to the detail and requests with the URL filters', async () => {
    list('?status=Pending&mine=true&tenantId=tenant-a')
    const rows = await screen.findAllByRole('row')
    expect(rows).toHaveLength(3)
    expect(within(rows[1]).getByRole('link', { name: 'Acme' })).toHaveAttribute('href', '/platform/break-glass/request-a')
    expect(within(rows[2]).getByText('Kullanıldı')).toBeInTheDocument()
    expect(api.getBreakGlassRequests).toHaveBeenCalledWith({ page: 1, pageSize: 25, status: 'Pending', mine: true, tenantId: 'tenant-a' }, expect.any(AbortSignal))
  })

  it('filters by tenant name through a searchable picker and never shows raw ids in the list', async () => {
    list('?tenantId=22222222-2222-2222-2222-222222222222')
    const picker = await screen.findByRole('combobox', { name: tx('Tenant') })
    await waitFor(() => expect(picker).toHaveValue('Acme'))
    expect(screen.queryByText('11111111-1111-1111-1111-111111111111')).not.toBeInTheDocument()
    expect(screen.queryByText(/^11111111/)).not.toBeInTheDocument()
    expect(screen.queryByText('22222222-2222-2222-2222-222222222222')).not.toBeInTheDocument()
  })

  it('opens the request dialog prefilled from a deep link and clears the link when closed', async () => {
    list('?requestTenantId=22222222-2222-2222-2222-222222222222&requestArtifactId=11111111-1111-1111-1111-111111111111')
    const dialog = await screen.findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: tx('Tenant') })).toHaveValue('Acme'))
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: tx('Yedek dosyası') })).toHaveDisplayValue(/Accounts/))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/break-glass'))
    expect(screen.getByTestId('location').textContent).not.toContain('requestTenantId')
  })

  it('answers 403 with the authorization state, not a generic error', async () => {
    vi.mocked(api.getBreakGlassRequests).mockRejectedValue(new ApiError(403))
    list()
    expect(await screen.findByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
  })
})

describe('break-glass detail page', () => {
  const detail = () => renderPlatform(null, { path: '/platform/break-glass/request-a', routes: <Route path="/platform/break-glass/:requestId" element={<PlatformBreakGlassDetailPage />} /> })

  it('reloads the request after the server refuses a self approval and hides the approve action', async () => {
    vi.mocked(api.getBreakGlassRequest).mockResolvedValueOnce(makeBreakGlass()).mockResolvedValue(makeBreakGlass({ isRequester: true, canApprove: false }))
    vi.mocked(api.approveBreakGlass).mockRejectedValue(new ApiError(403, { error: 'self_approval_forbidden' }))
    detail()
    fireEvent.click(await screen.findByRole('button', { name: 'Onayla' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByRole('textbox', { name: /Acme/ }), { target: { value: 'Acme' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Onayla' }))
    expect(await screen.findByText(/farklı bir PlatformAdmin gerekir/)).toBeInTheDocument()
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Onayla' })).not.toBeInTheDocument())
  })

  it('shows 404 as an error state', async () => {
    vi.mocked(api.getBreakGlassRequest).mockRejectedValue(new ApiError(404))
    detail()
    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })
})
