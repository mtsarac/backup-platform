import { screen, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import * as client from '@/lib/api/client'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { AuthContext } from '@/features/auth/auth-context'
import { PasskeysPage, type PasskeyItem } from './passkeys-page'

const api = vi.hoisted(() => vi.fn())
vi.mock('@/lib/api/client', async importOriginal => ({ ...await importOriginal<typeof client>(), apiRequest: api }))

const webauthnMock = vi.hoisted(() => ({
  createCredential: vi.fn(),
  getAssertion: vi.fn(),
  isWebAuthnSupported: vi.fn(() => true)
}))
vi.mock('@/features/auth/webauthn', () => webauthnMock)

const auth = {
  user: { userId: '11111111-1111-1111-1111-111111111111', displayName: 'Test User', isPlatformAdmin: false },
  tenants: [],
  isAuthenticated: true,
  isLoading: false,
  error: null,
  sessionExpired: false,
  login: vi.fn(),
  completeSecondFactor: vi.fn(),
  loginWithPasskey: vi.fn(),
  verifySecondFactor: vi.fn(),
  logout: vi.fn(),
  clearAuth: vi.fn(),
  refresh: vi.fn()
}

const mockPasskeys: PasskeyItem[] = [
  { id: 'cred-1', name: 'Work YubiKey', createdAt: '2026-01-01T10:00:00Z', lastUsedAt: '2026-01-02T10:00:00Z' },
  { id: 'cred-2', name: 'MacBook Touch ID', createdAt: '2026-01-03T10:00:00Z', lastUsedAt: null }
]

function mount(ui: ReactNode) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={cache}>
      <AuthContext.Provider value={auth}>
        <MemoryRouter>{ui}</MemoryRouter>
      </AuthContext.Provider>
    </QueryClientProvider>
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  api.mockImplementation(async (path: string, options?: { method?: string; json?: unknown }) => {
    if (path === 'auth/passkeys' && (!options || !options.method || options.method === 'GET')) {
      return mockPasskeys
    }
    if (path === 'auth/passkeys/register/options') {
      return { options: { challenge: 'test-challenge' }, challengeId: 'test-cid' }
    }
    if (path === 'auth/passkeys/register') {
      return { id: 'cred-new', name: 'New Key', createdAt: '2026-01-04T10:00:00Z' }
    }
    if (path.startsWith('auth/passkeys/') && options?.method === 'DELETE') {
      return undefined
    }
    return undefined
  })

  webauthnMock.createCredential.mockResolvedValue({
    id: 'mock-id',
    rawId: 'mock-raw-id',
    type: 'public-key',
    response: { attestationObject: 'mock-att', clientDataJSON: 'mock-client-data' }
  })
})

afterEach(() => vi.restoreAllMocks())

describe('Passkeys management page', () => {
  it('lists registered passkeys', async () => {
    mount(<PasskeysPage />)
    expect(await screen.findByText('Work YubiKey')).toBeInTheDocument()
    expect(screen.getByText('MacBook Touch ID')).toBeInTheDocument()
  })

  it('adds a new passkey with password verification', async () => {
    mount(<PasskeysPage />)
    expect(await screen.findByText('Work YubiKey')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Geçiş anahtarı ekle' }))

    const nameInput = screen.getByLabelText('Geçiş anahtarı adı')
    const pwdInput = screen.getByLabelText('Mevcut şifreniz')

    await userEvent.type(nameInput, 'Home Key')
    await userEvent.type(pwdInput, 'Correct-password-123!')

    await userEvent.click(screen.getByRole('button', { name: 'Kaydet' }))

    await waitFor(() => {
      expect(api).toHaveBeenCalledWith('auth/passkeys/register/options', { method: 'POST' })
      expect(webauthnMock.createCredential).toHaveBeenCalled()
      expect(api).toHaveBeenCalledWith('auth/passkeys/register', expect.objectContaining({
        method: 'POST',
        json: expect.objectContaining({
          name: 'Home Key',
          currentPassword: 'Correct-password-123!'
        })
      }))
    })
  })

  it('removes a passkey with password verification', async () => {
    mount(<PasskeysPage />)
    expect(await screen.findByText('Work YubiKey')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Work YubiKey geçiş anahtarını kaldır' }))

    expect(screen.getByText(/Bu geçiş anahtarını kaldırmak istediğinizden emin misiniz\?/)).toBeInTheDocument()

    const pwdInput = screen.getByLabelText('Mevcut şifreniz')
    await userEvent.type(pwdInput, 'Correct-password-123!')

    await userEvent.click(screen.getByRole('button', { name: 'Kaldır' }))

    await waitFor(() => {
      expect(api).toHaveBeenCalledWith('auth/passkeys/cred-1', {
        method: 'DELETE',
        json: { currentPassword: 'Correct-password-123!' }
      })
    })
  })
})
