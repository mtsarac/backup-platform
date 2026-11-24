import { screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import * as client from '@/lib/api/client'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { AuthContext } from '@/features/auth/auth-context'
import { SessionsPage } from './sessions-page'
import { LoginPage } from './login-page'

const api = vi.hoisted(() => vi.fn())
vi.mock('@/lib/api/client', async importOriginal => ({ ...await importOriginal<typeof client>(), apiRequest: api }))
const clearAuth = vi.fn(async () => {})
const login = vi.fn(async () => {})
const auth = { user: null, tenants: [], isAuthenticated: false, isLoading: false, error: null, sessionExpired: false, login, completeSecondFactor: vi.fn(), logout: vi.fn(), clearAuth, refresh: vi.fn() }
const FF = 'Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0'
const CH = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36'
const IOS = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1'
const records = [
  { id: 'current', device: FF, ipAddress: '127.0.0.1', lastActivityAt: '2026-01-01T12:00:00Z', isCurrent: true },
  { id: 'other', device: CH, ipAddress: '192.0.2.1', lastActivityAt: '2026-01-02T12:00:00Z', isCurrent: false },
]
function mount(ui: ReactNode) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={cache}><AuthContext.Provider value={auth}><MemoryRouter>{ui}</MemoryRouter></AuthContext.Provider></QueryClientProvider>)
}
beforeEach(() => { vi.clearAllMocks(); api.mockImplementation(async (_path, options) => options?.method === 'DELETE' ? undefined : records) })
afterEach(() => vi.restoreAllMocks())

describe('session management', () => {
  it('shows a friendly device name and keeps the raw user agent in collapsed technical details', async () => {
    api.mockResolvedValue([...records, { id: 'ios', device: IOS, ipAddress: null, lastActivityAt: '2026-01-03T12:00:00Z', isCurrent: false }, { id: 'none', device: null, ipAddress: null, lastActivityAt: '2026-01-04T12:00:00Z', isCurrent: false }])
    mount(<SessionsPage />)
    expect(await screen.findByText('Chrome · Windows')).toBeInTheDocument()
    expect(screen.getByText('Mobil Safari · iOS')).toBeInTheDocument()
    expect(screen.getByText('Bilinmeyen cihaz')).toBeInTheDocument()
    expect(screen.queryByText(CH)).not.toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'Teknik ayrıntılar' })[1])
    expect(screen.getByText(CH, { exact: false })).toBeInTheDocument()
  })

  it('lists devices and revokes another session without clearing authentication', async () => {
    mount(<SessionsPage />)
    expect(await screen.findByText(/Firefox · Linux/)).toBeInTheDocument()
    expect(screen.getByText('127.0.0.1', { exact: false })).toBeInTheDocument()
    expect(screen.getByText(/Bu oturum/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Chrome · Windows oturumunu sonlandır' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Oturumu sonlandır' }))
    await waitFor(() => expect(api).toHaveBeenCalledWith('auth/sessions/other', { method: 'DELETE' }))
    expect(clearAuth).not.toHaveBeenCalled()
  })
  it.each([['current', 'Firefox · Linux oturumunu sonlandır', 'auth/sessions/current'], ['all', 'Tüm oturumları sonlandır', 'auth/sessions']])('clears authentication when revoking %s', async (_kind, button, path) => {
    mount(<SessionsPage />)
    await screen.findByText(/Firefox · Linux/)
    await userEvent.click(screen.getByRole('button', { name: button }))
    const dialog = screen.getByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: button === 'Tüm oturumları sonlandır' ? button : 'Oturumu sonlandır' }))
    await waitFor(() => expect(clearAuth).toHaveBeenCalledOnce())
    expect(api).toHaveBeenCalledWith(path, { method: 'DELETE' })
  })
  it('keeps authentication on a failed revoke', async () => {
    api.mockImplementation(async (_path, options) => { if (options?.method === 'DELETE') throw new Error('failure'); return records })
    mount(<SessionsPage />)
    await screen.findByText(/Firefox · Linux/)
    await userEvent.click(screen.getByRole('button', { name: 'Firefox · Linux oturumunu sonlandır' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Oturumu sonlandır' }))
    await waitFor(() => expect(api).toHaveBeenCalledWith('auth/sessions/current', { method: 'DELETE' }))
    expect(clearAuth).not.toHaveBeenCalled()
  })
  it('passes the remember-me choice to login', async () => {
    mount(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Kullanıcı adı'), 'owner')
    await userEvent.type(screen.getByLabelText('Şifre'), 'password')
    await userEvent.click(screen.getByLabelText('Beni hatırla'))
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))
    await waitFor(() => expect(login).toHaveBeenCalledWith('owner', 'password', true))
  })
})
