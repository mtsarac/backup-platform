import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { tx } from '@/lib/i18n'
import * as api from '@/features/platform/support/api'
import * as platformApi from '@/features/platform/api'
import { makeSession } from '@/features/platform/support/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { setViewport } from '@/test/viewport'
import { PlatformSupportSessionsPage } from './platform-support-sessions-page'

vi.mock('@/features/platform/support/api')
vi.mock('@/features/platform/api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getSessions).mockResolvedValue(paged([makeSession()], { totalCount: 75 }))
  vi.mocked(platformApi.getTenants).mockResolvedValue(paged([]))
  vi.mocked(platformApi.getTenant).mockResolvedValue({ tenantId: 'tenant-a', name: 'Acme', memberCount: 1, enabledBackupJobs: 1, agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 }, enrollment: { enabled: true, changedAt: null } })
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
  it('leads with tenant, status, owner and expiry; reason and permissions wait under collapsed technical details', async () => {
    const inTwoHours = new Date(Date.now() + 2 * 3_600_000 + 60_000).toISOString()
    vi.mocked(api.getSessions).mockResolvedValue(paged([makeSession({ expiresAt: inTwoHours })]))
    renderPlatform(<PlatformSupportSessionsPage />)
    const row = (await screen.findByRole('link', { name: 'Acme A.Ş.' })).closest('tr')!
    expect(within(row).getByText('Etkin')).toBeVisible()
    expect(within(row).getByRole('link', { name: 'Platform Operatörü' })).toBeVisible()
    expect(within(row).getByText('2 saat sonra')).toHaveAttribute('title', expect.stringContaining('2026'))
    expect(within(row).getByText('Müşteri talebi: backup doğrulama')).not.toBeVisible()
    expect(within(row).getByText('ViewBackupMetadata, DownloadBackup, RestoreBackup')).not.toBeVisible()
    fireEvent.click(within(row).getByText('Teknik ayrıntılar'))
    expect(within(row).getByText('Müşteri talebi: backup doğrulama')).toBeVisible()
    expect(within(row).getByRole('button', { name: 'Kopyala: Gerekçe' })).toBeVisible()
  })
  it('becomes cards on a phone with the same plain fields', async () => {
    setViewport(false)
    renderPlatform(<PlatformSupportSessionsPage />)
    const card = (await screen.findByRole('link', { name: 'Acme A.Ş.' })).closest('li')!
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(within(card).getByText('Etkin')).toBeVisible()
    expect(within(card).getByText('Platform Operatörü')).toBeVisible()
    expect(within(card).getByText('Süre sonu')).toBeVisible()
    expect(within(card).getByText('Müşteri talebi: backup doğrulama')).not.toBeVisible()
  })
  it('names the tenant filter in a picker instead of showing its raw identifier', async () => {
    renderPlatform(<PlatformSupportSessionsPage />, { path: '/platform/support-sessions?tenantId=tenant-a' })
    await screen.findByRole('link', { name: 'Acme A.Ş.' })
    await waitFor(() => expect(screen.getByRole('combobox', { name: tx('Tenant') })).toHaveValue('Acme'))
    expect(screen.queryByText('tenant-a')).not.toBeInTheDocument()
  })
  it('clears tenant, status and mine filters with one action and offers no clear action without filters', async () => {
    renderPlatform(<PlatformSupportSessionsPage />, { path: '/platform/support-sessions?tenantId=tenant-a&status=Active&mine=true&page=2' })
    fireEvent.click(await screen.findByRole('button', { name: 'Filtreleri temizle' }))
    await waitFor(() => expect(api.getSessions).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, tenantId: '', status: '', mine: false }, expect.any(AbortSignal)))
    expect(screen.getByTestId('location').textContent).toBe('/platform/support-sessions')
    expect(screen.queryByRole('button', { name: 'Filtreleri temizle' })).not.toBeInTheDocument()
  })
})
