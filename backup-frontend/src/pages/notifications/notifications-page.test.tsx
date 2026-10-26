import { formatDateTime } from '@/lib/format'
import { screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import userEvent from '@testing-library/user-event'
import type { UserEvent } from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/features/notifications/api'
import type { NotificationChannel, NotificationDelivery, NotificationPolicy } from '@/features/notifications/types'
import { ApiError } from '@/lib/api/client'
import { NotificationsPage } from './notifications-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme', role: 'Owner' }, canAdmin: true, canOperate: true }))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ user: { userId: 'owner-a' } }) }))
vi.mock('@/features/notifications/api')

const SECRET = 'whsec-very-secret-123'

const policy = (overrides: Partial<NotificationPolicy> = {}): NotificationPolicy => ({
  enabled: false, eventTypes: ['backup_failed', 'agent_offline'], minimumSeverity: 'warning', channelKinds: ['email', 'webhook'], recoveryNotifications: true,
  staleAfterHours: 48, agentOfflineAfterMinutes: 15, reminderIntervalHours: 24, maxNotificationsPerHour: 30, updatedAt: null,
  transports: { email: { available: true }, sms: { available: false } }, ...overrides,
})
const channel = (overrides: Partial<NotificationChannel> = {}): NotificationChannel => ({
  id: 'c-mail', kind: 'email', name: 'Ops mailbox', targetDisplay: 'ops@example.com', enabled: true, recipientVerified: true, authentication: 'none', hasSecret: false, secretUpdatedAt: null,
  securityOwnerUserId: null,
  createdAt: '2026-10-01T08:00:00Z', updatedAt: '2026-10-01T08:00:00Z', lastDelivery: null, ...overrides,
})
const delivery = (overrides: Partial<NotificationDelivery> = {}): NotificationDelivery => ({
  id: 'd1', eventId: 'e1', eventType: 'agent_offline', phase: 'raised', severity: 'critical', subjectKind: 'agent', subjectName: 'build-01', channelKind: 'email',
  channelName: 'Ops mailbox', targetDisplay: 'ops@example.com', status: 'failed', attempts: 4, errorCode: 'smtp_unavailable', nextAttemptAt: '2026-10-14T12:00:00Z', sentAt: null,
  createdAt: '2026-10-14T10:00:00Z', ...overrides,
})

let client: QueryClient
function mount() {
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><NotificationsPage /></QueryClientProvider></MemoryRouter>)
}

const openTab = async (user: UserEvent, name: string) => user.click(await screen.findByRole('tab', { name }))
const openChannelDetails = async (user: UserEvent, name: string) => {
  const card = within(await screen.findByRole('article', { name }))
  const summary = card.getByText('Teknik ayrıntılar')
  if (!summary.closest('details')?.open) await user.click(summary)
}

beforeEach(() => {
  vi.resetAllMocks()
  access.canAdmin = true
  access.tenant.role = 'Owner'
  vi.mocked(api.getPolicy).mockResolvedValue(policy())
  vi.mocked(api.setOwnerSecurityRecipient).mockResolvedValue(channel({ securityOwnerUserId: 'owner-a' }))
  vi.mocked(api.getChannels).mockResolvedValue({ items: [channel(), channel({ id: 'c-hook', kind: 'webhook', name: 'Pager', recipientVerified: null, targetDisplay: 'https://hooks.example.com/…', authentication: 'bearer', hasSecret: true })] })
  vi.mocked(api.getDeliveries).mockResolvedValue({ items: [delivery()], totalCount: 1, page: 1, pageSize: 25 })
})

describe('role gating', () => {
  it('shows a notice and makes no request for non-admins', () => {
    access.canAdmin = false
    mount()
    expect(screen.getByText('Bildirimleri yönetme yetkiniz yok')).toBeInTheDocument()
    expect(screen.queryByText('Bildirim politikası')).not.toBeInTheDocument()
    expect(api.getPolicy).not.toHaveBeenCalled()
    expect(api.getChannels).not.toHaveBeenCalled()
    expect(api.getDeliveries).not.toHaveBeenCalled()
  })

  it('shows all three sections to admins, one tab at a time', async () => {
    const user = userEvent.setup()
    mount()
    expect(await screen.findByRole('heading', { name: 'Bildirim politikası' })).toBeInTheDocument()
    await screen.findByLabelText('Bildirimler etkin')
    expect(api.getChannels).not.toHaveBeenCalled()
    expect(api.getDeliveries).not.toHaveBeenCalled()
    await openTab(user, 'Kanallar')
    expect(await screen.findByRole('heading', { name: 'Kanallar' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Bildirimler etkin')).not.toBeInTheDocument()
    expect(api.getDeliveries).not.toHaveBeenCalled()
    await openTab(user, 'Gönderim geçmişi')
    expect(await screen.findByRole('heading', { name: 'Gönderim geçmişi' })).toBeInTheDocument()
    expect(screen.queryByRole('article', { name: 'Ops mailbox' })).not.toBeInTheDocument()
  })
})

describe('policy', () => {
  it('warns when a selected email/sms transport is unavailable', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByLabelText('Bildirimler etkin')
    expect(screen.queryByText(/aktarımı platform tarafından yapılandırılmamış/)).not.toBeInTheDocument()
    await user.click(screen.getByLabelText('SMS'))
    expect(screen.getByText(/SMS aktarımı platform tarafından yapılandırılmamış/)).toBeInTheDocument()
  })

  it('keeps numeric thresholds in collapsed copyable technical details', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByLabelText('Bildirimler etkin')
    const details = screen.getByText('Teknik ayrıntılar').closest('details')!
    expect(details).not.toHaveAttribute('open')
    await user.click(screen.getByText('Teknik ayrıntılar'))
    expect(within(details).getByRole('textbox', { name: /Hatırlatma aralığı/ })).toHaveValue('24')
    expect(within(details).getByRole('button', { name: /Kopyala:.*Hatırlatma/ })).toBeInTheDocument()
  })

  it('blocks out-of-range thresholds with a field error and no request', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByText('Teknik ayrıntılar'))
    const stale = screen.getByRole('textbox', { name: /(?:güncel değil|güncelliğini yitirme) eşiği/i })
    await user.clear(stale)
    await user.type(stale, '721')
    await user.click(screen.getByText('Teknik ayrıntılar'))
    await user.click(screen.getByRole('button', { name: 'Politikayı kaydet' }))
    expect(screen.getByText('1 ile 720 arasında bir tam sayı girin.')).toBeVisible()
    expect(api.updatePolicy).not.toHaveBeenCalled()
  })

  it('saves the whole policy body', async () => {
    vi.mocked(api.updatePolicy).mockResolvedValue(policy({ enabled: true, reminderIntervalHours: 0, updatedAt: '2026-10-14T10:00:00Z' }))
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByLabelText('Bildirimler etkin'))
    await user.click(screen.getByText('Teknik ayrıntılar'))
    const reminder = screen.getByRole('textbox', { name: /Hatırlatma aralığı/ })
    await user.clear(reminder)
    await user.type(reminder, '0')
    await user.click(screen.getByRole('button', { name: 'Politikayı kaydet' }))
    await waitFor(() => expect(api.updatePolicy).toHaveBeenCalledWith('tenant-a', {
      enabled: true, eventTypes: ['backup_failed', 'agent_offline'], minimumSeverity: 'warning', channelKinds: ['email', 'webhook'], recoveryNotifications: true,
      staleAfterHours: 48, agentOfflineAfterMinutes: 15, reminderIntervalHours: 0, maxNotificationsPerHour: 30,
    }))
    expect(await screen.findByText('Politika kaydedildi.')).toBeInTheDocument()
  })
})

describe('channels', () => {
  const card = (name: string) => within(screen.getByRole('article', { name }))

  it('shows confirmation and delivery result, with technical codes collapsed', async () => {
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await screen.findByRole('article', { name: 'Pager' })
    expect(card('Ops mailbox').getByText('Adres onaylandı')).toBeInTheDocument()
    expect(card('Pager').getByText('Teknik ayrıntılar').closest('details')).not.toHaveAttribute('open')
    await openChannelDetails(user, 'Pager')
    expect(card('Ops mailbox').getByText(/ops@example.com/)).toBeInTheDocument()
    expect(card('Pager').getByText(/https:\/\/hooks.example.com\/…/)).toBeInTheDocument()
    expect(card('Pager').getByText(/Gizli anahtar kayıtlı/)).toBeInTheDocument()
    expect(card('Pager').getByRole('button', { name: /Pager kanalı: (?:secret|gizli anahtarı) temizle/ })).toBeInTheDocument()
    expect(card('Ops mailbox').queryByRole('button', { name: /secret/i })).not.toBeInTheDocument()
  })

  it('marks an email address that has not confirmed and offers to resend the confirmation instead of a test', async () => {
    vi.mocked(api.getChannels).mockResolvedValue({ items: [channel({ recipientVerified: false })] })
    vi.mocked(api.testChannel).mockResolvedValue({ deliveryId: 'd9' })
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await screen.findByRole('article', { name: 'Ops mailbox' })
    expect(card('Ops mailbox').getByText('Adres onayı bekleniyor')).toBeInTheDocument()
    expect(card('Ops mailbox').queryByRole('button', { name: 'Ops mailbox kanalı: test gönder' })).not.toBeInTheDocument()
    await user.click(card('Ops mailbox').getByRole('button', { name: 'Ops mailbox kanalı: onay isteğini yeniden gönder' }))
    expect(await card('Ops mailbox').findByRole('status')).toHaveTextContent('Onay isteği')
    expect(api.testChannel).toHaveBeenCalledWith('tenant-a', 'c-mail')
  })

  it('shows a friendly error for a rate-limited test', async () => {
    vi.mocked(api.testChannel).mockRejectedValue(new ApiError(409, { error: 'notification_test_rate_limited' }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await user.click(await screen.findByRole('button', { name: 'Ops mailbox kanalı: test gönder' }))
    expect(await card('Ops mailbox').findByRole('alert')).toHaveTextContent('saatlik test sınırına')
  })

  it('shows a friendly error when the transport is not configured and a success note otherwise', async () => {
    vi.mocked(api.testChannel).mockRejectedValueOnce(new ApiError(409, { error: 'transport_not_configured' })).mockResolvedValueOnce({ deliveryId: 'd9' })
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await user.click(await screen.findByRole('button', { name: 'Ops mailbox kanalı: test gönder' }))
    expect(await card('Ops mailbox').findByRole('alert')).toHaveTextContent('platform aktarımı yapılandırılmamış')
    await user.click(card('Ops mailbox').getByRole('button', { name: 'Ops mailbox kanalı: test gönder' }))
    expect(await card('Ops mailbox').findByRole('status')).toHaveTextContent('kuyruğa alındı')
    expect(api.testChannel).toHaveBeenLastCalledWith('tenant-a', 'c-mail')
  })

  it('takes a new webhook secret once and retains it nowhere', async () => {
    vi.mocked(api.createChannel).mockResolvedValue(channel({ id: 'c-new', kind: 'webhook', name: 'Yeni', authentication: 'hmac', hasSecret: true }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await user.click(await screen.findByRole('button', { name: 'Kanal ekle' }))
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/onay e-postası gönderilir/)).toBeInTheDocument()
    expect(dialog.getByText(/Olay ve önem bilgileri/)).toBeInTheDocument()
    await user.selectOptions(dialog.getByLabelText('Tür'), 'webhook')
    expect(dialog.getByText(/JSON/)).toBeInTheDocument()
    await user.type(dialog.getByLabelText('Ad'), 'Yeni')
    await user.type(dialog.getByLabelText("Web kancası adresi"), 'https://hooks.example.com/abc')
    await user.selectOptions(dialog.getByLabelText('Kimlik doğrulama'), 'hmac')
    const input = dialog.getByLabelText(/HMAC imza anahtarı/)
    expect(input).toHaveAttribute('type', 'password')
    await user.type(input, SECRET)
    await user.click(dialog.getByRole('button', { name: 'Kanal ekle' }))
    await waitFor(() => expect(api.createChannel).toHaveBeenCalledWith('tenant-a', { kind: 'webhook', name: 'Yeni', target: 'https://hooks.example.com/abc', authentication: 'hmac', secret: SECRET }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(document.body.innerHTML).not.toContain(SECRET)
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => query.queryKey))).not.toContain(SECRET)
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => query.state.data))).not.toContain(SECRET)
  })

  it('sets a webhook secret from a password input that is cleared on submit', async () => {
    vi.mocked(api.setChannelSecret).mockResolvedValue(channel({ id: 'c-hook', kind: 'webhook', name: 'Pager', authentication: 'bearer', hasSecret: true }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Pager')
    await user.click(await screen.findByRole('button', { name: /Pager kanalı: (?:secret|gizli anahtar) ayarla/ }))
    const dialog = within(await screen.findByRole('dialog'))
    const input = dialog.getByLabelText("Yeni gizli anahtar") as HTMLInputElement
    expect(input.type).toBe('password')
    await user.type(input, SECRET)
    await user.click(dialog.getByRole('button', { name: "Gizli anahtarı kaydet" }))
    await waitFor(() => expect(api.setChannelSecret).toHaveBeenCalledWith('tenant-a', 'c-hook', SECRET))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(document.body.innerHTML).not.toContain(SECRET)
  })

  it('clears a submitted secret after failure and requires fresh entry before retry', async () => {
    vi.mocked(api.setChannelSecret).mockRejectedValue(new ApiError(503, { error: 'service_unavailable' }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Pager')
    await user.click(screen.getByRole('button', { name: /Pager kanalı: gizli anahtar ayarla/ }))
    const dialog = within(await screen.findByRole('dialog'))
    const input = dialog.getByLabelText('Yeni gizli anahtar')
    await user.type(input, SECRET)
    await user.click(dialog.getByRole('button', { name: 'Gizli anahtarı kaydet' }))
    expect(await dialog.findByRole('alert')).toBeInTheDocument()
    expect(input).toHaveValue('')
    expect(dialog.getByRole('button', { name: 'Gizli anahtarı kaydet' })).toBeDisabled()
    expect(document.body.innerHTML).not.toContain(SECRET)
    await waitFor(() => expect(JSON.stringify(client.getMutationCache().getAll().map(mutation => mutation.state.variables))).not.toContain(SECRET))
  })

  it('does not retain a new channel secret after a failed creation', async () => {
    vi.mocked(api.createChannel).mockRejectedValue(new ApiError(503, { error: 'service_unavailable' }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await user.click(await screen.findByRole('button', { name: 'Kanal ekle' }))
    const dialog = within(await screen.findByRole('dialog'))
    await user.selectOptions(dialog.getByLabelText('Tür'), 'webhook')
    await user.type(dialog.getByLabelText('Ad'), 'Yeni')
    await user.type(dialog.getByLabelText('Web kancası adresi'), 'https://hooks.example.com/abc')
    await user.selectOptions(dialog.getByLabelText('Kimlik doğrulama'), 'hmac')
    const input = dialog.getByLabelText(/HMAC imza anahtarı/)
    await user.type(input, SECRET)
    await user.click(dialog.getByRole('button', { name: 'Kanal ekle' }))
    expect(await dialog.findByRole('alert')).toBeInTheDocument()
    expect(input).toHaveValue('')
    expect(document.body.innerHTML).not.toContain(SECRET)
    await waitFor(() => expect(JSON.stringify(client.getMutationCache().getAll().map(mutation => mutation.state.variables))).not.toContain(SECRET))
  })

  it('clears a secret after confirmation and deletes a channel', async () => {
    vi.mocked(api.clearChannelSecret).mockResolvedValue(channel({ id: 'c-hook' }))
    vi.mocked(api.deleteChannel).mockResolvedValue(undefined)
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Pager')
    await user.click(await screen.findByRole('button', { name: /Pager kanalı: (?:secret|gizli anahtarı) temizle/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: "Gizli anahtarı temizle" }))
    await waitFor(() => expect(api.clearChannelSecret).toHaveBeenCalledWith('tenant-a', 'c-hook'))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await openChannelDetails(user, 'Ops mailbox')
    await user.click(screen.getByRole('button', { name: 'Ops mailbox kanalı: sil' }))
    const deletion = within(await screen.findByRole('dialog'))
    const confirm = deletion.getByRole('button', { name: 'Kanalı sil' })
    expect(confirm).toBeDisabled()
    expect(deletion.getByText(/Gönderim geçmişi korunur/)).toBeInTheDocument()
    await user.type(deletion.getByRole('textbox'), 'ops mailbox')
    expect(confirm).toBeDisabled()
    expect(api.deleteChannel).not.toHaveBeenCalled()
    await user.clear(deletion.getByRole('textbox'))
    await user.type(deletion.getByRole('textbox'), 'Ops mailbox')
    await user.click(confirm)
    await waitFor(() => expect(api.deleteChannel).toHaveBeenCalledWith('tenant-a', 'c-mail'))
  })

  it('requires a consequence confirmation before saving webhook authentication off', async () => {
    vi.mocked(api.updateChannel).mockResolvedValue(channel({ id: 'c-hook', kind: 'webhook', authentication: 'none', hasSecret: false }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await user.click(await screen.findByRole('button', { name: 'Pager kanalı: düzenle' }))
    await user.selectOptions(screen.getByLabelText('Kimlik doğrulama'), 'none')
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))
    const confirmation = within(await screen.findByRole('dialog', { name: /Kimlik doğrulama kapatılsın mı/ }))
    expect(confirmation.getByText(/kayıtlı gizli anahtarı siler/)).toBeInTheDocument()
    expect(api.updateChannel).not.toHaveBeenCalled()
    await user.click(confirmation.getByRole('button', { name: 'Vazgeç' }))
    expect(api.updateChannel).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Kaydet' }))
    await user.click(within(await screen.findByRole('dialog', { name: /Kimlik doğrulama kapatılsın mı/ })).getByRole('button', { name: 'Kimlik doğrulamayı kapat' }))
    await waitFor(() => expect(api.updateChannel).toHaveBeenCalledWith('tenant-a', 'c-hook', { authentication: 'none' }))
  })

  it('toggles enablement', async () => {
    vi.mocked(api.setChannelEnabled).mockResolvedValue(channel({ enabled: false }))
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Ops mailbox')
    await user.click(await screen.findByRole('button', { name: 'Ops mailbox kanalı: devre dışı bırak' }))
    await waitFor(() => expect(api.setChannelEnabled).toHaveBeenCalledWith('tenant-a', 'c-mail', false))
  })
})

describe('delivery history', () => {
  it('shows delivery cards with copyable error and attempt data collapsed', async () => {
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Gönderim geçmişi')
    const card = within(await screen.findByRole('article', { name: /build-01/ }))
    expect(card.getByText('Başarısız')).toBeInTheDocument()
    expect(card.getByText('Ops mailbox')).toBeInTheDocument()
    const details = card.getByText('Teknik ayrıntılar').closest('details')!
    expect(details).not.toHaveAttribute('open')
    await user.click(card.getByText('Teknik ayrıntılar'))
    expect(within(details).getByText('smtp_unavailable')).toBeVisible()
    expect(within(details).getByText('4')).toBeVisible()
    expect(within(details).getByText(formatDateTime('2026-10-14T12:00:00Z'))).toBeVisible()
    expect(within(details).getByRole('button', { name: /Kopyala:.*Hata/ })).toBeInTheDocument()
  })

  it('sends status, kind and eventType filters and resets to page 1', async () => {
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Gönderim geçmişi')
    await screen.findByText(/build-01/)
    expect(api.getDeliveries).toHaveBeenLastCalledWith('tenant-a', { page: 1, pageSize: 25, status: undefined, kind: undefined, eventType: undefined }, expect.anything())
    await user.selectOptions(screen.getByLabelText('Durum'), 'failed')
    await user.selectOptions(screen.getByLabelText('Kanal türü'), 'webhook')
    await user.selectOptions(screen.getByLabelText('Olay türü'), 'test')
    await waitFor(() => expect(api.getDeliveries).toHaveBeenLastCalledWith('tenant-a', { page: 1, pageSize: 25, status: 'failed', kind: 'webhook', eventType: 'test' }, expect.anything()))
  })

  it('pages through results', async () => {
    vi.mocked(api.getDeliveries).mockResolvedValue({ items: [delivery()], totalCount: 60, page: 1, pageSize: 25 })
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Gönderim geçmişi')
    await screen.findByText(/build-01/)
    await user.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(api.getDeliveries).toHaveBeenLastCalledWith('tenant-a', expect.objectContaining({ page: 2 }), expect.anything()))
  })
})

describe('mandatory Owner security recipient', () => {
  it('requires Owner confirmation and refreshes the designated channel independently of disabled general policy', async () => {
    const user = userEvent.setup()
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Ops mailbox')
    await user.click(await screen.findByRole('button', { name: 'Güvenlik alıcım olarak kaydet' }))
    expect(api.setOwnerSecurityRecipient).not.toHaveBeenCalled()
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setOwnerSecurityRecipient).toHaveBeenCalledWith('tenant-a', 'c-mail', true))
    await waitFor(() => expect(api.getChannels).toHaveBeenCalledTimes(2))
  })

  it('offers no designation to a tenant Admin and disables unconfirmed email designation', async () => {
    const user = userEvent.setup()
    access.tenant.role = 'Admin'
    const first = mount()
    await openTab(user, 'Kanallar')
    await screen.findByRole('heading', { name: 'Ops mailbox' })
    await openChannelDetails(user, 'Ops mailbox')
    expect(screen.queryByRole('button', { name: 'Güvenlik alıcım olarak kaydet' })).not.toBeInTheDocument()
    first.unmount()
    access.tenant.role = 'Owner'
    vi.mocked(api.getChannels).mockResolvedValue({ items: [channel({ recipientVerified: false })] })
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Ops mailbox')
    expect(await screen.findByRole('button', { name: 'Güvenlik alıcım olarak kaydet' })).toBeDisabled()
  })

  it('lets the designated Owner remove the binding after confirmation', async () => {
    const user = userEvent.setup()
    vi.mocked(api.getChannels).mockResolvedValue({ items: [channel({ securityOwnerUserId: 'owner-a' })] })
    mount()
    await openTab(user, 'Kanallar')
    await openChannelDetails(user, 'Ops mailbox')
    await user.click(await screen.findByRole('button', { name: 'Güvenlik alıcımı kaldır' }))
    expect(api.setOwnerSecurityRecipient).not.toHaveBeenCalled()
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kaldır' }))
    await waitFor(() => expect(api.setOwnerSecurityRecipient).toHaveBeenCalledWith('tenant-a', 'c-mail', false))
  })
})
