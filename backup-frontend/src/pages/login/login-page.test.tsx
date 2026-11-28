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
    const field = screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ })
    expect(field).toHaveAttribute('autocomplete', 'username')
    expect(screen.getByText('E-posta adresinizle de giriş yapabilirsiniz.')).toBeInTheDocument()
    await userEvent.type(field, 'alice@example.com')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))
    expect(loginMock).toHaveBeenCalledWith('alice@example.com', 'correct-password', false)
  })

  it('prompts for TOTP code when login indicates second factor is required', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true })
    apiMock.mockResolvedValueOnce(undefined)

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    expect(await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })).toBeInTheDocument()
    const codeInput = screen.getByRole('textbox', { name: /^Doğrulama kodu( \*)?$/ })
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
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })
    await userEvent.click(screen.getByRole('button', { name: 'Kurtarma kodu kullan' }))

    const recoveryInput = await screen.findByRole('textbox', { name: /^Kurtarma kodu( \*)?$/ })
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
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

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
    await userEvent.type(screen.getByLabelText(/^Kullanıcı adı( \*)?$/), 'testuser')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'password123!')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    await waitFor(() => {
      expect(screen.getByText('İkinci faktör doğrulaması')).toBeInTheDocument()
    })

    const verifyBtn = screen.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' })
    expect(verifyBtn).toBeInTheDocument()

    await userEvent.click(verifyBtn)
    await waitFor(() => expect(verifySecondFactorMock).toHaveBeenCalled())
  })

  it('shows lockout message and Retry-After wait time on 429 instead of unexpected error (#243)', async () => {
    loginMock.mockRejectedValueOnce(new client.ApiError(429, { error: 'Too many failed login attempts.' }, 45))
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'wrong-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Çok fazla başarısız deneme.')
    expect(alert).toHaveTextContent('Giriş geçici olarak kilitlendi.')
    expect(alert).toHaveTextContent('45 saniye sonra tekrar deneyin.')
    expect(alert).not.toHaveTextContent('Beklenmeyen bir sorun oluştu.')
  })

  it('opens the passkey step instead of TOTP prompt for a passkey-only user (#260)', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true, requiresSecondFactor: true, methods: ['passkey', 'recovery'] })
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'passkey-only')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'İkinci faktör doğrulaması' })).toBeInTheDocument()
    expect(screen.queryByLabelText(/^Doğrulama kodu( \*)?$/)).toBeNull()
    expect(screen.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' })).toBeInTheDocument()
  })

  it('shows an error alert when passkey second factor verification fails (#250)', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true, requiresSecondFactor: true, methods: ['totp', 'passkey', 'recovery'] })
    verifySecondFactorMock.mockRejectedValueOnce(new DOMException('cancelled', 'NotAllowedError'))

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })
    await userEvent.click(screen.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }))
    await userEvent.click(screen.getByRole('button', { name: 'Geçiş anahtarı ile doğrula' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Geçiş anahtarı işlemi iptal edildi veya zaman aşımına uğradı.')
  })

  it('moves focus to the active field on 2FA, recovery code, and forced password change steps (#245)', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true, requiresSecondFactor: true, methods: ['totp', 'recovery'] })
    logoutMock.mockResolvedValueOnce(undefined)

    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    const totpInput = await screen.findByRole('textbox', { name: /^Doğrulama kodu( \*)?$/ })
    await waitFor(() => expect(totpInput).toHaveFocus())

    await userEvent.click(screen.getByRole('button', { name: 'Kurtarma kodu kullan' }))
    const recoveryInput = await screen.findByRole('textbox', { name: /^Kurtarma kodu( \*)?$/ })
    await waitFor(() => expect(recoveryInput).toHaveFocus())

    await userEvent.click(screen.getByRole('button', { name: 'Girişe dön' }))
    await screen.findByRole('heading', { name: 'Giriş yapın' })

    loginMock.mockResolvedValueOnce({ passwordChangeRequired: true, requiresPasswordChange: true })
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'temp-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    const firstPwdField = await screen.findByLabelText(/^Mevcut şifre( \*)?$/)
    await waitFor(() => expect(firstPwdField).toHaveFocus())
  })

  it('offers only the second-factor methods the account has', async () => {
    loginMock.mockResolvedValueOnce({ secondFactorRequired: true, requiresSecondFactor: true, methods: ['totp', 'recovery'] })
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    await userEvent.type(screen.getByRole('textbox', { name: /^Kullanıcı adı( \*)?$/ }), 'alice')
    await userEvent.type(screen.getByLabelText(/^Şifre( \*)?$/), 'correct-password')
    await userEvent.click(screen.getByRole('button', { name: 'Giriş yap' }))

    await screen.findByRole('heading', { name: 'İki faktörlü doğrulama' })
    expect(screen.getByRole('button', { name: 'Kurtarma kodu kullan' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Geçiş anahtarı ile doğrula' })).toBeNull()
  })
})
