import { screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/features/auth/auth-context'
import { TenantProvider } from './tenant-provider'
import { useTenant } from './use-tenant'
import { apiRequest } from '@/lib/api/client'
import { TopBar } from '@/components/layout/top-bar'
import { ThemeProvider } from '@/components/theme-provider'
import type * as ApiClient from '@/lib/api/client'

vi.mock('@/lib/api/client', async (importOriginal) => ({ ...await importOriginal<typeof ApiClient>(), apiRequest: vi.fn() }))

const tenants = [{ tenantId: 'tenant-a', name: 'Tenant A' }, { tenantId: 'tenant-b', name: 'Tenant B' }]
function TenantProbe() {
  const { tenantId, switchTenant, canOperate } = useTenant()
  const navigate = useNavigate()
  const query = useQuery({ queryKey: ['tenant', tenantId, 'backup-runs'], queryFn: () => Promise.resolve(tenantId === 'tenant-a' ? ['Private A'] : ['Public B']) })
  return <><p>{tenantId}</p><p>{query.data?.join(',') ?? 'Loading tenant data'}</p>{canOperate && <button>Run now</button>}<button onClick={() => { void switchTenant('tenant-b') }}>Switch tenant</button><button onClick={() => navigate('/tenants/tenant-b')}>Direct navigation</button></>
}
function setup(cache: QueryClient, role: 'PlatformAdmin' | 'UserAdmin' = 'PlatformAdmin', initialPath = '/tenants/tenant-a') {
  return render(<QueryClientProvider client={cache}><AuthContext.Provider value={{ user: { userId: 'user', displayName: 'User', role, isPlatformAdmin: role === 'PlatformAdmin' }, tenants, isAuthenticated: true, isLoading: false, error: null, sessionExpired: false, login: vi.fn(), completeSecondFactor: vi.fn(), logout: vi.fn(), clearAuth: vi.fn(), refresh: vi.fn() }}><MemoryRouter initialEntries={[initialPath]}><Routes><Route path="/tenants/:tenantId" element={<TenantProvider><TenantProbe /></TenantProvider>} /></Routes></MemoryRouter></AuthContext.Provider></QueryClientProvider>)
}
describe('tenant switching and permissions', () => {
  it('loads tenant context for a platform administrator without membership', async () => {
    vi.mocked(apiRequest).mockResolvedValueOnce({ tenantId: 'tenant-c', name: 'Tenant C' })
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    setup(cache, 'PlatformAdmin', '/tenants/tenant-c')
    expect(await screen.findByText('tenant-c')).toBeInTheDocument()
    expect(apiRequest).toHaveBeenCalledWith('tenants/tenant-c/context', expect.objectContaining({ signal: expect.any(AbortSignal) }))
    cache.clear()
  })
  it('displays not-found state for a user administrator deep-linking to foreign tenant without requesting context', () => {
    vi.mocked(apiRequest).mockClear()
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    setup(cache, 'UserAdmin', '/tenants/tenant-c')
    expect(screen.getByText('Sayfa bulunamadı.')).toBeInTheDocument()
    expect(apiRequest).not.toHaveBeenCalled()
    cache.clear()
  })
  it('displays not-found state for a platform administrator deep-linking to a nonexistent tenant', async () => {
    vi.mocked(apiRequest).mockRejectedValueOnce(new Error('tenant_not_found'))
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    setup(cache, 'PlatformAdmin', '/tenants/nonexistent-tenant')
    expect(await screen.findByText('Sayfa bulunamadı.')).toBeInTheDocument()
    cache.clear()
  })
  it.each(['Switch tenant', 'Direct navigation'])('clears previous tenant cache with %s and does not keep its state on screen', async action => {
    const cache = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity, retry: false } } })
    cache.setQueryData(['tenant', 'tenant-a', 'backup-runs'], ['Private A'])
    setup(cache)
    expect(await screen.findByText('Private A')).toBeInTheDocument()
    await userEvent.click(screen.getByText(action))
    expect(await screen.findByText('Public B')).toBeInTheDocument()
    expect(screen.queryByText('Private A')).not.toBeInTheDocument()
    expect(screen.queryByText("Şimdi çalıştır")).not.toBeInTheDocument()
    await waitFor(() => expect(cache.getQueryData(['tenant', 'tenant-a', 'backup-runs'])).toBeUndefined())
    cache.clear()
  })
})

describe('top-bar tenant selector and role navigation', () => {
  it('allows user administrator to switch tenants from the top-bar selector', async () => {
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={cache}>
        <AuthContext.Provider value={{ user: { userId: 'user', displayName: 'User', role: 'UserAdmin', isPlatformAdmin: false }, tenants, isAuthenticated: true, isLoading: false, error: null, sessionExpired: false, login: vi.fn(), completeSecondFactor: vi.fn(), logout: vi.fn(), clearAuth: vi.fn(), refresh: vi.fn() }}>
          <MemoryRouter initialEntries={['/tenants/tenant-a']}>
            <Routes>
              <Route path="/tenants/:tenantId" element={<ThemeProvider><TenantProvider><TopBar onMenuToggle={() => undefined} /><TenantProbe /></TenantProvider></ThemeProvider>} />
            </Routes>
          </MemoryRouter>
        </AuthContext.Provider>
      </QueryClientProvider>
    )
    const select = screen.getByLabelText('Çalışma Alanı')
    expect(select).toHaveValue('tenant-a')
    await userEvent.selectOptions(select, 'tenant-b')
    expect(await screen.findByText('Public B')).toBeInTheDocument()
    expect(screen.queryByText('Private A')).not.toBeInTheDocument()
    cache.clear()
  })
  it('provides role navigation link to platform management for platform administrator in the top bar', async () => {
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={cache}>
        <AuthContext.Provider value={{ user: { userId: 'admin', displayName: 'Platform Admin', role: 'PlatformAdmin', isPlatformAdmin: true }, tenants, isAuthenticated: true, isLoading: false, error: null, sessionExpired: false, login: vi.fn(), completeSecondFactor: vi.fn(), logout: vi.fn(), clearAuth: vi.fn(), refresh: vi.fn() }}>
          <MemoryRouter initialEntries={['/tenants/tenant-a']}>
            <Routes>
              <Route path="/tenants/:tenantId" element={<ThemeProvider><TenantProvider><TopBar onMenuToggle={() => undefined} /><TenantProbe /></TenantProvider></ThemeProvider>} />
            </Routes>
          </MemoryRouter>
        </AuthContext.Provider>
      </QueryClientProvider>
    )
    expect(screen.getByRole('link', { name: 'Platform Yönetimi' })).toHaveAttribute('href', '/platform')
    cache.clear()
  })
})
