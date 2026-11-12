import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { userEvent } from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ForgotPasswordPage } from './forgot-password-page'
import { ResetPasswordPage } from './reset-password-page'
import { VerifyEmailPage } from './verify-email-page'
import { EmailVerificationCard } from './email-verification-card'
import * as client from '@/lib/api/client'

const apiMock = vi.fn()

vi.mock('@/lib/api/client', async (importOriginal) => ({
  ...await importOriginal<typeof client>(),
  apiRequest: (...args: unknown[]) => apiMock(...args),
}))

function mount(ui: React.ReactElement, url = '/') {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={cache}><MemoryRouter initialEntries={[url]}>{ui}</MemoryRouter></QueryClientProvider>)
}

describe('email recovery screens', () => {
  beforeEach(() => vi.clearAllMocks())

  it('reports email recovery as unavailable without offering the reset form', async () => {
    apiMock.mockResolvedValue({ available: false })
    mount(<ForgotPasswordPage />)
    expect(await screen.findByText(/E-posta ile kurtarma bu platformda kullanılamıyor/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sıfırlama bağlantısı gönder' })).not.toBeInTheDocument()
  })

  it('shows the same confirmation after a reset request whatever the account', async () => {
    apiMock.mockImplementation(async (path: string) => (path === 'auth/email-recovery' ? { available: true } : { accepted: true }))
    mount(<ForgotPasswordPage />)
    await userEvent.type(await screen.findByLabelText(/Kullanıcı adı/), 'someone@example.com')
    await userEvent.click(screen.getByRole('button', { name: 'Sıfırlama bağlantısı gönder' }))
    expect(await screen.findByText(/eşleşiyorsa şifre sıfırlama bağlantısı gönderildi/)).toBeInTheDocument()
    expect(apiMock).toHaveBeenCalledWith('auth/password-reset/request', { method: 'POST', json: { loginIdentifier: 'someone@example.com' } })
  })

  it('completes a reset with the token from the link and rejects mismatched passwords locally', async () => {
    apiMock.mockResolvedValue(undefined)
    mount(<ResetPasswordPage />, '/reset-password?token=abc')
    await userEvent.type(screen.getByLabelText('Yeni şifre'), 'a-long-passphrase-1')
    await userEvent.type(screen.getByLabelText('Yeni şifre (tekrar)'), 'different-passphrase-2')
    await userEvent.click(screen.getByRole('button', { name: 'Şifreyi sıfırla' }))
    expect(await screen.findByText('Yeni şifreler eşleşmiyor.')).toBeInTheDocument()
    expect(apiMock).not.toHaveBeenCalled()

    await userEvent.clear(screen.getByLabelText('Yeni şifre (tekrar)'))
    await userEvent.type(screen.getByLabelText('Yeni şifre (tekrar)'), 'a-long-passphrase-1')
    await userEvent.click(screen.getByRole('button', { name: 'Şifreyi sıfırla' }))
    expect(await screen.findByText(/Tüm oturumlarınız sonlandırıldı/)).toBeInTheDocument()
    expect(apiMock).toHaveBeenCalledWith('auth/password-reset/complete', { method: 'POST', json: { token: 'abc', newPassword: 'a-long-passphrase-1' } })
  })

  it('explains an expired reset link', async () => {
    apiMock.mockRejectedValue(new client.ApiError(400, { error: 'invalid_token' }))
    mount(<ResetPasswordPage />, '/reset-password?token=old')
    await userEvent.type(screen.getByLabelText('Yeni şifre'), 'a-long-passphrase-1')
    await userEvent.type(screen.getByLabelText('Yeni şifre (tekrar)'), 'a-long-passphrase-1')
    await userEvent.click(screen.getByRole('button', { name: 'Şifreyi sıfırla' }))
    expect(await screen.findByText('Sıfırlama bağlantısı geçersiz veya süresi dolmuş.')).toBeInTheDocument()
  })

  it('confirms an address only after the user presses the button', async () => {
    apiMock.mockResolvedValue(undefined)
    mount(<VerifyEmailPage />, '/verify-email?token=xyz')
    expect(apiMock).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'E-posta adresini doğrula' }))
    expect(await screen.findByText('E-posta adresiniz doğrulandı.')).toBeInTheDocument()
    expect(apiMock).toHaveBeenCalledWith('auth/email-verification/confirm', { method: 'POST', json: { token: 'xyz' } })
  })

  it('offers verification only while unverified and email recovery is available', async () => {
    apiMock.mockResolvedValue({ available: true })
    const { unmount } = mount(<EmailVerificationCard emailVerified={false} />)
    expect(await screen.findByRole('button', { name: 'Doğrulama e-postası gönder' })).toBeInTheDocument()
    unmount()
    mount(<EmailVerificationCard emailVerified />)
    expect(await screen.findByText(/E-posta adresiniz doğrulandı;/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Doğrulama e-postası gönder' })).not.toBeInTheDocument()
  })

  it('says so when the platform cannot send verification mail', async () => {
    apiMock.mockResolvedValue({ available: false })
    mount(<EmailVerificationCard emailVerified={false} />)
    expect(await screen.findByText('E-posta ile kurtarma bu platformda kullanılamıyor.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Doğrulama e-postası gönder' })).not.toBeInTheDocument()
  })
})
