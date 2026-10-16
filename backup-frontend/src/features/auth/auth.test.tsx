import { act, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from './auth-provider'
import { useAuth } from './use-auth'
import { ApiError, apiRequest, resetAntiforgery } from '@/lib/api/client'

function AuthProbe() {
  const auth = useAuth()
  return <><p>{auth.isLoading ? 'Loading' : auth.user?.displayName ?? 'Anonymous'}</p><p>{auth.tenants.map(item => item.name).join(',')}</p><p>{auth.sessionExpired ? 'Session expired' : ''}</p><button onClick={() => { void auth.login('owner', 'typed-password') }}>Login</button><button onClick={() => { void auth.logout() }}>Logout</button></>
}
let authenticated = false
let requests: Request[] = []
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
beforeEach(() => {
  authenticated = false
  requests = []
  resetAntiforgery()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init)
    requests.push(request)
    const path = new URL(request.url).pathname
    if (path === '/api/auth/antiforgery') return json({ requestToken: authenticated ? 'signed-in-token' : 'anonymous-token', headerName: 'X-CSRF-TOKEN' })
    if (path === '/api/auth/login') { authenticated = true; return new Response(null, { status: 204 }) }
    if (path === '/api/auth/logout') { authenticated = false; return new Response(null, { status: 204 }) }
    if (!authenticated) return json({}, 401)
    if (path === '/api/me/tenants') return json([{ tenantId: 'tenant-a', name: 'Tenant A', role: 'Administrator', membershipRole: 'Owner' }])
    return json({ userId: 'owner', displayName: 'Real Owner', isPlatformAdmin: false })
  }))
})
afterEach(() => vi.unstubAllGlobals())
describe('cookie session UI', () => {
  it('loads real membership after login, refreshes CSRF for logout, and clears resource cache', async () => {
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={cache}><AuthProvider><AuthProbe /></AuthProvider></QueryClientProvider>)
    expect(await screen.findByText('Anonymous')).toBeInTheDocument()
    expect(screen.queryByText('Session expired')).not.toBeInTheDocument()
    await userEvent.click(screen.getByText('Login'))
    expect(await screen.findByText('Real Owner')).toBeInTheDocument()
    expect(screen.getByText('Tenant A')).toBeInTheDocument()
    cache.setQueryData(['tenant', 'tenant-a', 'backup-runs'], ['private run'])
    cache.setQueryData(['platform', 'summary'], { tenants: 1 })
    await userEvent.click(screen.getByText('Logout'))
    expect(await screen.findByText('Anonymous')).toBeInTheDocument()
    expect(cache.getQueryData(['tenant', 'tenant-a', 'backup-runs'])).toBeUndefined()
    expect(cache.getQueryData(['platform', 'summary'])).toBeUndefined()
    expect(requests.find(request => request.url.endsWith('/auth/logout'))?.headers.get('X-CSRF-TOKEN')).toBe('signed-in-token')
    expect(localStorage.getItem('token')).toBeNull()
    cache.clear()
  })
  it('restores existing cookie session and expires it on resource401, not403', async () => {
    authenticated = true
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={cache}><AuthProvider><AuthProbe /></AuthProvider></QueryClientProvider>)
    await screen.findByText('Real Owner')
    await screen.findByText('Tenant A')
    const fetch = vi.mocked(globalThis.fetch)
    fetch.mockResolvedValueOnce(json({}, 403))
    await expect(apiRequest('forbidden')).rejects.toBeInstanceOf(ApiError)
    expect(screen.getByText('Real Owner')).toBeInTheDocument()
    cache.setQueryData(['tenant', 'tenant-a', 'backup-runs'], ['private run'])
    cache.setQueryData(['platform', 'summary'], { tenants: 1 })
    fetch.mockResolvedValueOnce(json({}, 401))
    await act(async () => { await apiRequest('expired').catch(() => undefined) })
    await waitFor(() => expect(screen.getByText('Anonymous')).toBeInTheDocument())
    expect(screen.getByText('Session expired')).toBeInTheDocument()
    expect(cache.getQueryData(['tenant', 'tenant-a', 'backup-runs'])).toBeUndefined()
    expect(cache.getQueryData(['platform', 'summary'])).toBeUndefined()
    cache.clear()
  })
})
