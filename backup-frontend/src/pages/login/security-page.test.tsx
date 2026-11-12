import { screen, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { userEvent } from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { SecurityPage } from './security-page'
import * as client from '@/lib/api/client'

const apiMock = vi.fn()

vi.mock('@/lib/api/client', async (importOriginal) => ({
  ...await importOriginal<typeof client>(),
  apiRequest: (...args: unknown[]) => apiMock(...args),
}))

function mount() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={cache}>
      <MemoryRouter>
        <SecurityPage />
      </MemoryRouter>
    </QueryClientProvider>
  )
}

describe('SecurityPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows enrollment button when TOTP is not active', async () => {
    apiMock.mockImplementation(async (path) => {
      if (path === 'auth/security') return { hasTotp: false, remainingRecoveryCodes: 0 }
      return {}
    })

    mount()
    expect(await screen.findByRole('heading', { name: 'Güvenlik ayarları' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'TOTP Etkinleştir' })).toBeInTheDocument()
    expect(screen.queryByText(/Kalan kullanılabilir kurtarma kodu/)).not.toBeInTheDocument()
  })

  it('offers recovery code management for a passkey without TOTP', async () => {
    apiMock.mockResolvedValue({ hasTotp: false, hasPasskeys: true, remainingRecoveryCodes: 9 })
    mount()
    expect(await screen.findByText(/Kalan kullanılabilir kurtarma kodu: 9/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Kurtarma kodlarını yeniden oluştur' })).toBeInTheDocument()
  })

  it('completes TOTP enrolment and shows single-use recovery codes', async () => {
    apiMock.mockImplementation(async (path, options) => {
      if (path === 'auth/security') return { hasTotp: false, remainingRecoveryCodes: 0 }
      if (path === 'auth/security/totp/setup') {
        return {
          setupToken: 'test-setup-token',
          manualEntryKey: 'JBSWY3DPEHPK3PXP',
          qrUri: 'otpauth://totp/test',
          qrSvg: '<svg data-testid="qr-svg"></svg>',
        }
      }
      if (path === 'auth/security/totp/enroll' && options?.method === 'POST') {
        return {
          recoveryCodes: ['CODE-1111-2222', 'CODE-3333-4444'],
        }
      }
      return {}
    })

    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'TOTP Etkinleştir' }))

    expect(await screen.findByRole('heading', { name: 'İki faktörlü doğrulama kurulumu' })).toBeInTheDocument()
    expect(screen.getByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument()

    const dialog = screen.getByRole('dialog')
    await userEvent.click(within(dialog).getByLabelText('Mevcut şifreniz'))
    await userEvent.paste('my-password')
    await userEvent.click(within(dialog).getByLabelText('Doğrulama kodu'))
    await userEvent.paste('654321')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Doğrula ve Etkinleştir' }))

    expect(apiMock).toHaveBeenCalledWith('auth/security/totp/enroll', {
      method: 'POST',
      json: {
        setupToken: 'test-setup-token',
        code: '654321',
        currentPassword: 'my-password',
      },
    })

    expect(await screen.findByRole('heading', { name: 'Kurtarma kodlarınız' })).toBeInTheDocument()
    expect(screen.getByText('CODE-1111-2222')).toBeInTheDocument()
    expect(screen.getByText('CODE-3333-4444')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Tamam' }))
    expect(screen.queryByRole('heading', { name: 'Kurtarma kodlarınız' })).not.toBeInTheDocument()
  })

  it('removes TOTP with current password or code verification', async () => {
    apiMock.mockImplementation(async (path, options) => {
      if (path === 'auth/security') return { hasTotp: true, remainingRecoveryCodes: 8 }
      if (path === 'auth/security/totp/remove' && options?.method === 'POST') return undefined
      return {}
    })

    mount()
    expect(await screen.findByText('Kalan kullanılabilir kurtarma kodu: 8')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'TOTP’yi Kaldır' }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: 'İki faktörlü doğrulamayı kaldır' })).toBeInTheDocument()

    await userEvent.type(within(dialog).getByLabelText('Mevcut şifre'), 'my-password')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaldır' }))

    expect(apiMock).toHaveBeenCalledWith('auth/security/totp/remove', {
      method: 'POST',
      json: {
        currentPassword: 'my-password',
        code: undefined,
      },
    })
  })

  it('regenerates recovery codes and displays new set', async () => {
    apiMock.mockImplementation(async (path, options) => {
      if (path === 'auth/security') return { hasTotp: true, remainingRecoveryCodes: 5 }
      if (path === 'auth/security/recovery-codes/regenerate' && options?.method === 'POST') {
        return { recoveryCodes: ['NEW-1111-2222', 'NEW-3333-4444'] }
      }
      return {}
    })

    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Kurtarma kodlarını yeniden oluştur' }))

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: 'Kurtarma kodlarını yeniden oluştur' })).toBeInTheDocument()

    await userEvent.type(within(dialog).getByLabelText('Doğrulama kodu (TOTP)'), '112233')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Yeniden oluştur' }))

    expect(apiMock).toHaveBeenCalledWith('auth/security/recovery-codes/regenerate', {
      method: 'POST',
      json: {
        currentPassword: undefined,
        code: '112233',
      },
    })

    expect(await screen.findByRole('heading', { name: 'Kurtarma kodlarınız' })).toBeInTheDocument()
    expect(screen.getByText('NEW-1111-2222')).toBeInTheDocument()
    expect(screen.getByText('NEW-3333-4444')).toBeInTheDocument()
  })
})
