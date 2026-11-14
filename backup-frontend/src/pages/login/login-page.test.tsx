import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { LoginPage } from './login-page'
import * as client from '@/lib/api/client'

const loginMock = vi.fn()
const completeSecondFactorMock = vi.fn()
const logoutMock = vi.fn()
const loginWithPasskeyMock = vi.fn()
const verifySecondFactorMock = vi.fn()
const apiMock = vi.fn()

vi.mock('@/lib/api/client', async (importOriginal) => ({
  ...await importOriginal<typeof client>(),
  apiRequest: (...args: unknown[]) => apiMock(...args),
}))

vi.mock('@/features/auth/use-auth', () => ({
  useAuth: () => ({
    login: loginMock,
    completeSecondFactor: completeSecondFactorMock,
    logout: logoutMock,
    loginWithPasskey: loginWithPasskeyMock,
    verifySecondFactor: verifySecondFactorMock,
    isAuthenticated: false,
    sessionExpired: false,
  }),
}))

describe('Sign-in page', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('accepts an email address as the login identifier', async () => {
    loginMock.mockResolvedValueOnce(undefined)
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    const field = screen.getByRole('textbox', { name: 'Kullanıcı adı' })
    expect(field).toHaveAttribute('autocomplete', 'username')
    expect(screen.getByText('E-posta adresinizle de giriş yapabilirsiniz.')).toBeInTheDocument()
    await userEvent.type(field, 'alice@example.com')
    await userEvent.type(screen.getByLabelText('Şifre'), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş Yap' }))
    expect(loginMock).toHaveBeenCalledWith('alice@example.com', 'correct-password', false)
  })

  it('prompts for TOTP code when login indicates second factor is required', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true })
    apiMock.mockResolvedValueOnce(undefined)

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: 'Kullanıcı adı' }), 'alice')
    await userEvent.type(screen.getByLabelText('Şifre'), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş Yap' }))

    expect(await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })).toBeInTheDocument()
    const codeInput = screen.getByRole('textbox', { name: 'Doğrulama kodu' })
    expect(codeInput).toBeInTheDocument()

    await userEvent.type(codeInput, '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Doğrula' }))

    expect(apiMock).toHaveBeenCalledWith('auth/second-factor/totp', {
      method: 'POST',
      json: { code: '123456' },
    })
    expect(completeSecondFactorMock).toHaveBeenCalled()
  })

  it('allows switching to recovery code and signs in with it', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true })
    apiMock.mockResolvedValueOnce(undefined)

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: 'Kullanıcı adı' }), 'alice')
    await userEvent.type(screen.getByLabelText('Şifre'), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş Yap' }))

    await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })
    await userEvent.click(screen.getByRole('button', { name: 'Kurtarma kodu kullan' }))

    const recoveryInput = await screen.findByRole('textbox', { name: 'Kurtarma kodu' })
    expect(recoveryInput).toBeInTheDocument()

    await userEvent.type(recoveryInput, 'ABCD-EFGH-IJKL')
    await userEvent.click(screen.getByRole('button', { name: 'Kurtarma koduyla giriş yap' }))

    expect(apiMock).toHaveBeenCalledWith('auth/second-factor/recovery-code', {
      method: 'POST',
      json: { recoveryCode: 'ABCD-EFGH-IJKL' },
    })
    expect(completeSecondFactorMock).toHaveBeenCalled()
  })

  it('cancelling second factor challenge logs out and returns to password form', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true })
    logoutMock.mockResolvedValueOnce(undefined)

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: 'Kullanıcı adı' }), 'alice')
    await userEvent.type(screen.getByLabelText('Şifre'), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş Yap' }))

    await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })
    await userEvent.click(screen.getByRole('button', { name: 'Girişe dön' }))

    expect(logoutMock).toHaveBeenCalled()
    expect(await screen.findByRole('heading', { name: 'Giriş yapın' })).toBeInTheDocument()
  })

  it('renders passkey sign-in option and calls loginWithPasskey on click', async () => {
    loginWithPasskeyMock.mockResolvedValueOnce(undefined)
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    const passkeyBtn = screen.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' })
    expect(passkeyBtn).toBeInTheDocument()

    await userEvent.click(passkeyBtn)
    await waitFor(() => expect(loginWithPasskeyMock).toHaveBeenCalled())
  })

  it.each([
    ['a cancelled prompt', new DOMException('x', 'NotAllowedError'), 'Geçiş anahtarı işlemi iptal edildi veya zaman aşımına uğradı.'],
    ['an unsupported device', new DOMException('x', 'NotSupportedError'), 'Bu tarayıcı veya cihaz geçiş anahtarlarını desteklemiyor.'],
    ['an unconfigured server', new client.ApiError(503, { error: 'passkeys_not_configured' }), 'Geçiş anahtarları bu sunucuda henüz yapılandırılmamış.'],
  ])('explains %s in plain words instead of a server error', async (_case, failure, message) => {
    loginWithPasskeyMock.mockRejectedValueOnce(failure)
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.click(screen.getByRole('button', { name: 'Geçiş anahtarı ile giriş yap' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(screen.queryByText(/Sunucu isteği tamamlayamadı/)).toBeNull()
  })

  it('switches to second-factor view when password login requires second factor', async () => {
    loginMock.mockResolvedValueOnce({ requiresSecondFactor: true })
    verifySecondFactorMock.mockResolvedValueOnce(undefined)

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByLabelText('Kullanıcı adı'), 'testuser')
    await userEvent.type(screen.getByLabelText('Şifre'), 'password123!')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş Yap' }))

    await waitFor(() => {
      expect(screen.getByText('İkinci faktör doğrulaması')).toBeInTheDocument()
    })

    const verifyBtn = screen.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' })
    expect(verifyBtn).toBeInTheDocument()

    await userEvent.click(verifyBtn)
    await waitFor(() => expect(verifySecondFactorMock).toHaveBeenCalled())
  })
})
