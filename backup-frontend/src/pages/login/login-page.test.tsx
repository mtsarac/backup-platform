import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { userEvent } from '@testing-library/user-event'
import { LoginPage } from './login-page'
import * as client from '@/lib/api/client'

const loginMock = vi.fn()
const completeSecondFactorMock = vi.fn()
const logoutMock = vi.fn()
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
})
