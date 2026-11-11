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
  return <><p>{auth.isLoading ? 'Loading' : auth.user?.displayName ?? 'Anonymous'}</p><p>{auth.tenants.map(item => item.name).join(',')}</p><p>{auth.sessionExpired ? 'Session expired' : ''}</p><button onClick={() => { void auth.login('owner', 'typed-password', true) }}>Login</button><button onClick={() => { void auth.loginWithPasskey?.(true, 'owner') }}>Passkey with username</button><button onClick={() => { void auth.loginWithPasskey?.(false) }}>Passkey without username</button><button onClick={() => { void auth.verifySecondFactor?.() }}>Verify passkey</button><button onClick={() => { void auth.logout() }}>Logout</button></>
}
let authenticated = false
let secondFactorRequired = false
const passkeyOptions = { challenge: 'AQID', rpId: 'example.com', allowCredentials: [{ id: 'BAU', type: 'public-key' }], userVerification: 'required' }
let requests: Request[] = []
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
beforeEach(() => {
  authenticated = false
  secondFactorRequired = false
  requests = []
  resetAntiforgery()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init)
    requests.push(request)
    const path = new URL(request.url).pathname
    if (path === '/api/auth/antiforgery') return json({ requestToken: authenticated ? 'signed-in-token' : 'anonymous-token', headerName: 'X-CSRF-TOKEN' })
    if (path === '/api/auth/login') {
      if (secondFactorRequired) return json({ secondFactorRequired: true })
      authenticated = true
      return new Response(null, { status: 204 })
    }
    if (path === '/api/auth/passkeys/login/options' || path === '/api/auth/passkeys/second-factor/options') return json({ options: passkeyOptions, challengeId: 'challenge-123' })
    if (path === '/api/auth/passkeys/login' || path === '/api/auth/passkeys/second-factor') {
      authenticated = true
      return new Response(null, { status: 204 })
    }
    if (path === '/api/auth/logout') { authenticated = false; return new Response(null, { status: 204 }) }
    if (!authenticated) return json({}, 401)
    if (path === '/api/me/tenants') return json([{ tenantId: 'tenant-a', name: 'Tenant A' }])
    return json({ userId: 'owner', displayName: 'Real Owner', role: 'UserAdmin', isPlatformAdmin: false })
  }))
})
afterEach(() => vi.unstubAllGlobals())
describe('cookie session UI', () => {
  it.each([
    { mode: 'passwordless with username', button: 'Passkey with username', optionsPath: '/api/auth/passkeys/login/options', assertionPath: '/api/auth/passkeys/login', optionsBody: { loginIdentifier: 'owner' }, rememberMe: true, secondFactor: false },
    { mode: 'passwordless without username', button: 'Passkey without username', optionsPath: '/api/auth/passkeys/login/options', assertionPath: '/api/auth/passkeys/login', optionsBody: {}, rememberMe: false, secondFactor: false },
    { mode: 'second-factor', button: 'Verify passkey', optionsPath: '/api/auth/passkeys/second-factor/options', assertionPath: '/api/auth/passkeys/second-factor', optionsBody: undefined, rememberMe: undefined, secondFactor: true },
  ])('establishes a session using $mode passkey authentication', async ({ button, optionsPath, assertionPath, optionsBody, rememberMe, secondFactor }) => {
    secondFactorRequired = secondFactor
    const get = vi.fn().mockResolvedValue({
      id: 'credential-id',
      rawId: new Uint8Array([4, 5]).buffer,
      type: 'public-key',
      response: {
        authenticatorData: new Uint8Array([6]).buffer,
        clientDataJSON: new Uint8Array([7]).buffer,
        signature: new Uint8Array([8]).buffer,
        userHandle: new Uint8Array([9]).buffer,
      },
    })
    const navigatorWithCredentials = Object.create(navigator)
    Object.defineProperty(navigatorWithCredentials, 'credentials', { value: { get } })
    vi.stubGlobal('navigator', navigatorWithCredentials)
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={cache}><AuthProvider><AuthProbe /></AuthProvider></QueryClientProvider>)
    await screen.findByText('Anonymous')

    if (secondFactor) {
      await userEvent.click(screen.getByText('Login'))
      await waitFor(() => expect(requests.some(request => new URL(request.url).pathname === '/api/auth/login')).toBe(true))
      expect(screen.getByText('Anonymous')).toBeInTheDocument()
    }
    await userEvent.click(screen.getByText(button))
    expect(await screen.findByText('Real Owner')).toBeInTheDocument()
    expect(await screen.findByText('Tenant A')).toBeInTheDocument()

    const optionsRequest = requests.find(request => new URL(request.url).pathname === optionsPath)!
    const assertionRequest = requests.find(request => new URL(request.url).pathname === assertionPath)!
    expect(optionsRequest.method).toBe('POST')
    expect(optionsBody === undefined ? await optionsRequest.clone().text() : await optionsRequest.clone().json()).toEqual(optionsBody === undefined ? '' : optionsBody)
    expect(get).toHaveBeenCalledOnce()
    expect(get).toHaveBeenCalledWith({ publicKey: {
      challenge: new Uint8Array([1, 2, 3]).buffer,
      rpId: 'example.com',
      allowCredentials: [{ id: new Uint8Array([4, 5]).buffer, type: 'public-key' }],
      userVerification: 'required',
      timeout: undefined,
    } })
    expect(assertionRequest.method).toBe('POST')
    expect(await assertionRequest.clone().json()).toEqual({
      challengeId: 'challenge-123',
      response: { id: 'credential-id', rawId: 'BAU', type: 'public-key', response: { authenticatorData: 'Bg', clientDataJSON: 'Bw', signature: 'CA', userHandle: 'CQ' } },
      ...(rememberMe === undefined ? {} : { rememberMe }),
    })
    expect(requests.indexOf(optionsRequest)).toBeLessThan(requests.indexOf(assertionRequest))
    expect(requests.slice(requests.indexOf(assertionRequest) + 1).map(request => new URL(request.url).pathname)).toEqual(['/api/me', '/api/me/tenants'])
    cache.clear()
  })
  it('loads real membership after login, refreshes CSRF for logout, and clears resource cache', async () => {
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={cache}><AuthProvider><AuthProbe /></AuthProvider></QueryClientProvider>)
    expect(await screen.findByText('Anonymous')).toBeInTheDocument()
    expect(screen.queryByText('Session expired')).not.toBeInTheDocument()
    await userEvent.click(screen.getByText('Login'))
    expect(await screen.findByText('Real Owner')).toBeInTheDocument()
    expect(await requests.find(request => request.url.endsWith('/auth/login'))?.clone().json()).toEqual({ loginIdentifier: 'owner', password: 'typed-password', rememberMe: true })
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
