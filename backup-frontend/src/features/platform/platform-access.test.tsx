import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import { Route } from 'react-router-dom'
import { PlatformAdminRoute } from '@/components/auth/platform-admin-route'
import { PlatformShell } from '@/components/layout/platform-shell'
import { Sidebar } from '@/components/layout/sidebar'
import { ThemeProvider } from '@/components/theme-provider'
import { TenantSelectPage } from '@/pages/tenancy/tenant-select-page'
import { PlatformDashboardPage } from '@/pages/platform/platform-dashboard-page'
import * as api from './api'
import { platformAdmin, renderPlatform, tenantOwner } from './test-utils'

vi.mock('./api')
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a', canAdmin: true, canOperate: true, tenant: { tenantId: 'tenant-a', name: 'Acme', role: 'Owner' } }) }))

const tree = <Route path="/platform" element={<PlatformAdminRoute />}>
  <Route element={<ThemeProvider><PlatformShell /></ThemeProvider>}><Route index element={<PlatformDashboardPage />} /></Route>
</Route>

beforeEach(() => {
  vi.resetAllMocks()
  localStorage.setItem('backup-theme', 'light')
  vi.mocked(api.getSummary).mockResolvedValue({ tenants: 3, users: 7, agents: { total: 10, online: 6, offline: 2, unknown: 1, disabled: 1, revoked: 0 }, enabledBackupJobs: 9, failedRunsLast24Hours: 2 })
})

describe('Platform Administration entry points', () => {
  it('tenant sidebar shows the Platform Administration link to a PlatformAdmin', () => {
    renderPlatform(<Sidebar />, { user: platformAdmin })
    for (const link of screen.getAllByText('Platform Administration')) expect(link.closest('a')).toHaveAttribute('href', '/platform')
  })

  it('tenant sidebar hides it from a user who is only a tenant Owner', () => {
    renderPlatform(<Sidebar />, { user: tenantOwner })
    expect(screen.queryByText('Platform Administration')).not.toBeInTheDocument()
    expect(screen.getAllByText('Backup Jobs').length).toBeGreaterThan(0)
  })

  it('tenant picker links PlatformAdmin users and hides it for others', () => {
    const tenants = [{ tenantId: 't1', name: 'Bir', role: 'Owner' as const }, { tenantId: 't2', name: 'İki', role: 'Viewer' as const }]
    const { unmount } = renderPlatform(<TenantSelectPage />, { user: platformAdmin, tenants })
    expect(screen.getByRole('link', { name: 'Platform Administration' })).toHaveAttribute('href', '/platform')
    unmount()
    renderPlatform(<TenantSelectPage />, { user: tenantOwner, tenants })
    expect(screen.queryByRole('link', { name: 'Platform Administration' })).not.toBeInTheDocument()
  })
})

describe('PlatformAdminRoute guard', () => {
  it('renders the authorization state for a non-PlatformAdmin and never calls platform endpoints', async () => {
    renderPlatform(null, { routes: tree, user: tenantOwner })
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Bu alan için yetkiniz yok')
    expect(within(alert).getByRole('link', { name: 'Ana sayfaya dön' })).toHaveAttribute('href', '/')
    expect(screen.queryByRole('navigation', { name: 'Platform navigation' })).not.toBeInTheDocument()
    expect(screen.queryByText('Platform Dashboard')).not.toBeInTheDocument()
    for (const fn of Object.values(api)) if (typeof fn === 'function') expect(fn).not.toHaveBeenCalled()
  })

  it('renders the platform shell and dashboard for a PlatformAdmin without tenant selector', async () => {
    renderPlatform(null, { routes: tree, user: platformAdmin })
    expect(await screen.findByRole('heading', { name: 'Platform Dashboard' })).toBeInTheDocument()
    const nav = screen.getAllByRole('navigation', { name: 'Platform navigation' })[0]
    for (const name of ['Dashboard', 'Tenants', 'Users', 'Agents', 'Audit']) expect(within(nav).getByRole('link', { name })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Storage Targets' })).toHaveAttribute('href', '/platform/storage-connections')
    expect(within(nav).getByRole('link', { name: 'Tenant arayüzüne dön' })).toHaveAttribute('href', '/')
    expect(screen.getAllByText('Platform Administration').length).toBeGreaterThan(0)
    expect(screen.getByText('PlatformAdmin')).toBeInTheDocument()
    expect(screen.getByText('Platform Operatörü')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Tema değiştir' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Çıkış' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Tenant')).not.toBeInTheDocument()
    expect(api.getSummary).toHaveBeenCalled()
  })
})

describe('platform dashboard', () => {
  it('shows KPI totals and the agent breakdown', async () => {
    renderPlatform(<PlatformDashboardPage />)
    const card = (label: string | RegExp) => screen.getByText(label).parentElement!.parentElement!
    await screen.findByText('Tenants')
    expect(card('Tenants')).toHaveTextContent('3')
    expect(card('Users')).toHaveTextContent('7')
    expect(card('Etkin Backup Jobs')).toHaveTextContent('9')
    expect(card(/Başarısız Backup Runs/)).toHaveTextContent('2')
    expect(screen.getByText('Çevrimiçi').nextSibling).toHaveTextContent('6')
    expect(screen.getByText('Çevrimdışı').nextSibling).toHaveTextContent('2')
    expect(screen.getByText('Devre dışı').nextSibling).toHaveTextContent('1')
    expect(screen.getByText('İptal edilmiş').nextSibling).toHaveTextContent('0')
  })
})
