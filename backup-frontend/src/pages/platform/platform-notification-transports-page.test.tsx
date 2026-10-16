import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/notifications/api'
import type { EmailTransport, NotificationTransports, SmsTransport } from '@/features/platform/notifications/types'
import { renderPlatform } from '@/features/platform/test-utils'
import { PlatformNotificationTransportsPage } from './platform-notification-transports-page'

vi.mock('@/features/platform/notifications/api')

const SECRET = 'smtp-pass-123'
const email = (overrides: Partial<EmailTransport> = {}): EmailTransport => ({
  enabled: false, configured: false, host: null, port: null, security: 'starttls', username: null, fromAddress: null, fromName: null, hasSecret: false, secretUpdatedAt: null, updatedAt: null, ...overrides,
})
const sms: SmsTransport = { enabled: false, configured: false, provider: 'generic-http', endpoint: null, authentication: 'none', username: null, headerName: null, sender: null, hasSecret: false, secretUpdatedAt: null, updatedAt: null }
const serve = (value: EmailTransport) => vi.mocked(api.getTransports).mockResolvedValue({ email: value, sms } satisfies NotificationTransports)

beforeEach(() => { vi.resetAllMocks(); serve(email()) })

describe('platform notification transports page', () => {
  it('shows configured/enabled state and that SMS is implemented but not verified', async () => {
    serve(email({ enabled: true, configured: true, host: 'smtp.example.com', port: 465, security: 'implicit', fromAddress: 'noreply@example.com' }))
    renderPlatform(<PlatformNotificationTransportsPage />)
    expect(await screen.findByDisplayValue('smtp.example.com')).toBeInTheDocument()
    expect(screen.getByText('Yapılandırıldı')).toBeInTheDocument()
    expect(screen.getByText('UYGULANDI — DOĞRULANMADI')).toBeInTheDocument()
    expect(screen.getByText(/genel bir HTTP JSON bağdaştırıcısı/)).toBeInTheDocument()
  })

  it('validates the port and saves the email transport', async () => {
    vi.mocked(api.updateEmailTransport).mockResolvedValue(email())
    const user = userEvent.setup()
    renderPlatform(<PlatformNotificationTransportsPage />)
    await user.type(await screen.findByLabelText('Sunucu'), 'smtp.example.com')
    await user.type(screen.getByLabelText('Gönderen adresi'), 'noreply@example.com')
    await user.clear(screen.getByLabelText('Port'))
    await user.type(screen.getByLabelText('Port'), '70000')
    expect(screen.getByRole('button', { name: 'E-posta ayarlarını kaydet' })).toBeDisabled()
    await user.clear(screen.getByLabelText('Port'))
    await user.type(screen.getByLabelText('Port'), '587')
    await user.click(screen.getByRole('button', { name: 'E-posta ayarlarını kaydet' }))
    await waitFor(() => expect(api.updateEmailTransport).toHaveBeenCalledWith({ enabled: false, host: 'smtp.example.com', port: 587, security: 'starttls', username: null, fromAddress: 'noreply@example.com', fromName: null }))
  })

  it('sends the SMTP password once from a password input and never renders it', async () => {
    vi.mocked(api.setEmailSecret).mockResolvedValue(email({ hasSecret: true, secretUpdatedAt: '2026-10-14T10:00:00Z' }))
    const user = userEvent.setup()
    const { client } = renderPlatform(<PlatformNotificationTransportsPage />)
    const input = await screen.findByLabelText('Yeni smtp parolası') as HTMLInputElement
    expect(input.type).toBe('password')
    await user.type(input, SECRET)
    await user.click(screen.getAllByRole('button', { name: 'Ayarla' })[0])
    await waitFor(() => expect(api.setEmailSecret).toHaveBeenCalledWith(SECRET))
    await waitFor(() => expect(input.value).toBe(''))
    expect(document.body.innerHTML).not.toContain(SECRET)
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => [query.queryKey, query.state.data]))).not.toContain(SECRET)
  })

  it('clears the stored secret', async () => {
    serve(email({ hasSecret: true, secretUpdatedAt: '2026-10-14T10:00:00Z' }))
    vi.mocked(api.clearEmailSecret).mockResolvedValue(email())
    const user = userEvent.setup()
    renderPlatform(<PlatformNotificationTransportsPage />)
    await user.click(await screen.findByRole('button', { name: 'Temizle' }))
    expect(api.clearEmailSecret).not.toHaveBeenCalled()
    expect(await screen.findByRole('dialog')).toHaveTextContent('Kimlik doğrulaması kullanan bildirim gönderimleri')
    await user.click(screen.getByRole('button', { name: 'Secret değerini temizle' }))
    await waitFor(() => expect(api.clearEmailSecret).toHaveBeenCalled())
  })
})
