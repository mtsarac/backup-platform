import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/features/auth/auth-context'
import type { TenantRole } from '@/features/auth/types'
import { TenantProvider } from './tenant-provider'
import { useTenant } from './use-tenant'
import { tenantPermissions } from './permissions'

const tenants = [{ tenantId: 'tenant-a', name: 'Tenant A', role: 'Administrator' as const, membershipRole: 'Owner' as const }, { tenantId: 'tenant-b', name: 'Tenant B', role: 'Viewer' as const }]
function TenantProbe() {
  const { tenantId, switchTenant, canOperate } = useTenant()
  const navigate = useNavigate()
  const query = useQuery({ queryKey: ['tenant', tenantId, 'backup-runs'], queryFn: () => Promise.resolve(tenantId === 'tenant-a' ? ['Private A'] : ['Public B']) })
  return <><p>{tenantId}</p><p>{query.data?.join(',') ?? 'Loading tenant data'}</p>{canOperate && <button>Run now</button>}<button onClick={() => { void switchTenant('tenant-b') }}>Switch tenant</button><button onClick={() => navigate('/tenants/tenant-b')}>Direct navigation</button></>
}
function setup(cache: QueryClient) {
  return render(<QueryClientProvider client={cache}><AuthContext.Provider value={{ user: { userId: 'user', displayName: 'User', isPlatformAdmin: true }, tenants, isAuthenticated: true, isLoading: false, error: null, sessionExpired: false, login: vi.fn(), logout: vi.fn(), refresh: vi.fn() }}><MemoryRouter initialEntries={['/tenants/tenant-a']}><Routes><Route path="/tenants/:tenantId" element={<TenantProvider><TenantProbe /></TenantProvider>} /></Routes></MemoryRouter></AuthContext.Provider></QueryClientProvider>)
}
describe('tenant switching and permissions', () => {
  it.each(['Switch tenant', 'Direct navigation'])('clears previous tenant cache with %s and does not keep its state on screen', async action => {
    const cache = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity, retry: false } } })
    cache.setQueryData(['tenant', 'tenant-a', 'backup-runs'], ['Private A'])
    setup(cache)
    expect(await screen.findByText('Private A')).toBeInTheDocument()
    await userEvent.click(screen.getByText(action))
    expect(await screen.findByText('Public B')).toBeInTheDocument()
    expect(screen.queryByText('Private A')).not.toBeInTheDocument()
    expect(screen.queryByText('Run now')).not.toBeInTheDocument()
    await waitFor(() => expect(cache.getQueryData(['tenant', 'tenant-a', 'backup-runs'])).toBeUndefined())
    cache.clear()
  })
  it.each<[TenantRole, boolean, boolean]>([['Owner', true, true], ['Admin', true, true], ['Administrator', true, true], ['Operator', false, true], ['Viewer', false, false]])('uses tenant %s independently of platform role', (role, canAdmin, canOperate) => {
    expect(tenantPermissions(role)).toEqual({ canAdmin, canOperate })
  })
})
