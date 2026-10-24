import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/platform/notifications/api'
import type { EmailTransport, NotificationTransports, SmsTransport } from '@/features/platform/notifications/types'
import { renderPlatform } from '@/features/platform/test-utils'
import { PlatformNotificationTransportsPage } from './platform-notification-transports-page'
import { tx } from '@/lib/i18n'

vi.mock('@/features/platform/notifications/api')

const SECRET = 'smtp-pass-123'
const email = (overrides: Partial<EmailTransport> = {}): EmailTransport => ({
  enabled: false, configured: false, host: null, port: null, security: 'starttls', username: null, fromAddress: null, fromName: null, hasSecret: false, secretUpdatedAt: null, updatedAt: null, ...overrides,
})
const sms: SmsTransport = { enabled: false, configured: false, provider: 'generic-http', endpoint: null, authentication: 'none', username: null, headerName: null, sender: null, hasSecret: false, secretUpdatedAt: null, updatedAt: null }
const serve = (value: EmailTransport) => vi.mocked(api.getTransports).mockResolvedValue({ email: value, sms } satisfies NotificationTransports)

beforeEach(() => { vi.resetAllMocks(); serve(email()) })

describe('platform notification transports page', () => {
  it.each([
    ['enabled and complete', { enabled: true, configured: true, host: 'smtp.example.com', port: 465 }, 'Çalışıyor'],
    ['complete but switched off', { enabled: false, configured: true, host: 'smtp.example.com', port: 465 }, 'Kapalı'],
    ['switched on with settings missing', { enabled: true, configured: false }, 'Ayarlar eksik'],
    ['never set up', {}, 'Kurulmadı'],
  ])('tells at a glance whether email delivery works when it is %s', async (_state, transport, verdict) => {
    serve(email(transport))
    renderPlatform(<PlatformNotificationTransportsPage />)
    const card = within(await screen.findByRole('region', { name: tx('E-posta (SMTP)') }))
    expect(card.getByText(verdict)).toBeVisible()
  })

  it('keeps host, port and secret dates under collapsed copyable technical details and still says SMS is unverified', async () => {
    serve(email({ enabled: true, configured: true, host: 'smtp.example.com', port: 465, security: 'implicit', fromAddress: 'noreply@example.com', hasSecret: true, secretUpdatedAt: '2026-10-14T10:00:00Z' }))
    const user = userEvent.setup()
    renderPlatform(<PlatformNotificationTransportsPage />)
    const card = within(await screen.findByRole('region', { name: tx('E-posta (SMTP)') }))
    expect(await card.findByDisplayValue('smtp.example.com')).not.toBeVisible()
    expect(card.getByDisplayValue('465')).not.toBeVisible()
    expect(card.getByRole('button', { name: /Gizli değerin ayarlandığı zaman/ })).not.toBeVisible()
    expect(card.getByDisplayValue('noreply@example.com')).toBeVisible()
    await user.click(card.getByText('Teknik ayrıntılar'))
    expect(card.getByDisplayValue('smtp.example.com')).toBeVisible()
    expect(card.getByRole('button', { name: /Kayıtlı sunucu/ })).toBeVisible()
    expect(card.getByRole('button', { name: /Gizli değerin ayarlandığı zaman/ })).toBeVisible()
    expect(screen.getByText('UYGULANDI — DOĞRULANMADI')).toBeInTheDocument()
    expect(screen.getByText(/genel bir HTTP JSON bağdaştırıcısı/)).toBeInTheDocument()
  })

  it('validates the port and saves the email transport', async () => {
    vi.mocked(api.updateEmailTransport).mockResolvedValue(email())
    const user = userEvent.setup()
    renderPlatform(<PlatformNotificationTransportsPage />)
    await user.click((await screen.findAllByText('Teknik ayrıntılar'))[0])
    await user.type(await screen.findByLabelText('Sunucu'), 'smtp.example.com')
    await user.type(screen.getByLabelText('Gönderen adresi'), 'noreply@example.com')
    await user.clear(screen.getByLabelText("Bağlantı noktası"))
    await user.type(screen.getByLabelText("Bağlantı noktası"), '70000')
    expect(screen.getByRole('button', { name: 'E-posta ayarlarını kaydet' })).toBeDisabled()
    await user.clear(screen.getByLabelText("Bağlantı noktası"))
    await user.type(screen.getByLabelText("Bağlantı noktası"), '587')
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
    const dialog = await screen.findByRole('dialog')
    for (const part of ['Ne olacak?', 'Kimleri etkiler?', 'Geri alınabilir mi?']) expect(dialog).toHaveTextContent(part)
    await user.click(screen.getByRole('button', { name: "Gizli değeri temizle" }))
    await waitFor(() => expect(api.clearEmailSecret).toHaveBeenCalled())
  })
})
