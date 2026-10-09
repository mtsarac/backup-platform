import { fireEvent, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/support/api'
import { makeSession } from '@/features/platform/support/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { PlatformSupportSessionsPage } from './platform-support-sessions-page'

vi.mock('@/features/platform/support/api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getSessions).mockResolvedValue(paged([makeSession()], { totalCount: 75 }))
})
describe('Support Sessions list', () => {
  it('links tenant/actor to the detail and persists status/mine/page filters in the URL', async () => {
    renderPlatform(<PlatformSupportSessionsPage />, { path: '/platform/support-sessions?page=2&tenantId=tenant-a&status=Active&mine=true' })
    expect(await screen.findByRole('link', { name: 'Acme A.Ş.' })).toHaveAttribute('href', '/platform/support-sessions/support-a')
    expect(screen.getByRole('link', { name: 'Platform Operatörü' })).toHaveAttribute('href', '/platform/support-sessions/support-a')
    expect(api.getSessions).toHaveBeenCalledWith({ page: 2, pageSize: 25, tenantId: 'tenant-a', status: 'Active', mine: true }, expect.any(AbortSignal))
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Closed' } })
    await waitFor(() => expect(api.getSessions).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, tenantId: 'tenant-a', status: 'Closed', mine: true }, expect.any(AbortSignal)))
    expect(screen.getByTestId('location')).not.toHaveTextContent('page=2')
    fireEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(api.getSessions).toHaveBeenLastCalledWith({ page: 2, pageSize: 25, tenantId: 'tenant-a', status: 'Closed', mine: true }, expect.any(AbortSignal)))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Yalnızca benim oturumlarım' }))
    await waitFor(() => expect(api.getSessions).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, tenantId: 'tenant-a', status: 'Closed', mine: false }, expect.any(AbortSignal)))
  })
})
