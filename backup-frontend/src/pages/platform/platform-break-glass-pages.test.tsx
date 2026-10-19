import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { Route } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/artifact-encryption/api'
import { makeBreakGlass } from '@/features/artifact-encryption/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import { PlatformBreakGlassDetailPage } from './platform-break-glass-detail-page'
import { PlatformBreakGlassPage } from './platform-break-glass-page'

vi.mock('@/features/artifact-encryption/api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getBreakGlassRequests).mockResolvedValue(paged([makeBreakGlass(), makeBreakGlass({ id: 'request-b', tenantName: 'Beta', status: 'Consumed', canApprove: false })]))
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

  it('opens the request dialog prefilled from a deep link and clears the link when closed', async () => {
    list('?requestTenantId=22222222-2222-2222-2222-222222222222&requestArtifactId=11111111-1111-1111-1111-111111111111')
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Tenant kimliği')).toHaveValue('22222222-2222-2222-2222-222222222222')
    expect(within(dialog).getByLabelText('Backup kimliği')).toHaveValue('11111111-1111-1111-1111-111111111111')
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
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Onayla' }))
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
