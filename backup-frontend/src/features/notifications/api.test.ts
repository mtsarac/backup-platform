import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, resetAntiforgery } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import * as platform from '@/features/platform/notifications/api'
import * as api from './api'

let requests: Request[] = []
let reply: () => Response
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  requests = []
  reply = () => json({ items: [] })
  resetAntiforgery()
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init)
    requests.push(request)
    if (new URL(request.url).pathname === '/api/auth/antiforgery') return json({ requestToken: 'token', headerName: 'X-CSRF-TOKEN' })
    return reply()
  }))
})
afterEach(() => vi.unstubAllGlobals())

const last = () => requests.at(-1)!
const url = () => new URL(last().url)

describe('tenant notification API', () => {
  it('reads policy, channels and filtered deliveries under the tenant route', async () => {
    await api.getPolicy('tenant-a')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/policy')
    await api.getChannels('tenant-a')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels')
    await api.getDeliveries('tenant-a', { page: 2, pageSize: 25, status: 'failed', kind: 'email', eventType: 'backup_failed' })
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/deliveries')
    expect(Object.fromEntries(url().searchParams)).toEqual({ page: '2', pageSize: '25', status: 'failed', kind: 'email', eventType: 'backup_failed' })
    await api.getDeliveries('tenant-a', { page: 1, pageSize: 25 })
    expect(Object.fromEntries(url().searchParams)).toEqual({ page: '1', pageSize: '25' })
  })

  it('puts the whole policy', async () => {
    const policy = { enabled: true, eventTypes: ['backup_failed' as const], minimumSeverity: 'warning' as const, channelKinds: ['email' as const], recoveryNotifications: false, staleAfterHours: 48, agentOfflineAfterMinutes: 15, reminderIntervalHours: 0, maxNotificationsPerHour: 30 }
    await api.updatePolicy('tenant-a', policy)
    expect(last().method).toBe('PUT')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/policy')
    expect(await last().json()).toEqual(policy)
  })

  it('creates, updates and deletes channels with the right verbs and bodies', async () => {
    await api.createChannel('tenant-a', { kind: 'webhook', name: 'Hook', target: 'https://h.example/x', authentication: 'bearer', secret: 's3cret' })
    expect(last().method).toBe('POST')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels')
    expect(await last().json()).toEqual({ kind: 'webhook', name: 'Hook', target: 'https://h.example/x', authentication: 'bearer', secret: 's3cret' })
    await api.updateChannel('tenant-a', 'c 1', { name: 'Yeni' })
    expect(last().method).toBe('PUT')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels/c%201')
    expect(await last().json()).toEqual({ name: 'Yeni' })
    await api.deleteChannel('tenant-a', 'c1')
    expect(last().method).toBe('DELETE')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels/c1')
  })

  it('sends the secret only in the body, never in the URL', async () => {
    await api.setChannelSecret('tenant-a', 'c1', 'topsecret')
    expect(last().method).toBe('PUT')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels/c1/secret')
    expect(url().search).toBe('')
    expect(await last().json()).toEqual({ value: 'topsecret' })
    await api.clearChannelSecret('tenant-a', 'c1')
    expect(last().method).toBe('DELETE')
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels/c1/secret')
  })

  it('enables, disables and tests with POST', async () => {
    reply = () => json({ deliveryId: 'd1' }, 202)
    await api.setChannelEnabled('tenant-a', 'c1', true)
    expect([last().method, url().pathname]).toEqual(['POST', '/api/tenants/tenant-a/notifications/channels/c1/enable'])
    await api.setChannelEnabled('tenant-a', 'c1', false)
    expect(url().pathname).toBe('/api/tenants/tenant-a/notifications/channels/c1/disable')
    expect(await api.testChannel('tenant-a', 'c1')).toEqual({ deliveryId: 'd1' })
    expect([last().method, url().pathname]).toEqual(['POST', '/api/tenants/tenant-a/notifications/channels/c1/test'])
  })

  it.each([
    ['notification_test_rate_limited', 'saatlik test sınırına'],
    ['transport_not_configured', 'platform aktarımı yapılandırılmamış'],
    ['notification_channel_name_taken', 'zaten var'],
    ['notification_channel_limit_reached', 'sınırına ulaşıldı'],
  ])('maps the 409 %s to friendly text', async (code, text) => {
    reply = () => json({ error: code }, 409)
    const error = await api.testChannel('tenant-a', 'c1').catch((caught: unknown) => caught)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).code).toBe(code)
    expect(errorMessage(error)).toContain(text)
  })

  it('maps a missing channel and keeps the validation field', async () => {
    reply = () => json({ error: 'notification_channel_not_found' }, 404)
    expect(errorMessage(await api.deleteChannel('tenant-a', 'gone').catch((caught: unknown) => caught))).toContain('Bildirim kanalı bulunamadı')
    reply = () => json({ error: 'invalid_request', field: 'target', code: 'invalid_email' }, 400)
    const error = await api.createChannel('tenant-a', { kind: 'email', name: 'x', target: 'bad' }).catch((caught: unknown) => caught) as ApiError
    expect(error.field).toBe('target')
  })
})

describe('platform notification transport API', () => {
  it('reads and updates both transports', async () => {
    await platform.getTransports()
    expect([last().method, url().pathname]).toEqual(['GET', '/api/platform/notification-transports'])
    const email = { enabled: true, host: 'smtp.example.com', port: 587, security: 'starttls' as const, username: 'u', fromAddress: 'a@example.com', fromName: null }
    await platform.updateEmailTransport(email)
    expect([last().method, url().pathname]).toEqual(['PUT', '/api/platform/notification-transports/email'])
    expect(await last().json()).toEqual(email)
  })

  it('sets and clears the secrets in the body / by verb only', async () => {
    await platform.setEmailSecret('smtp-pass')
    expect([last().method, url().pathname, url().search]).toEqual(['PUT', '/api/platform/notification-transports/email/secret', ''])
    expect(await last().json()).toEqual({ value: 'smtp-pass' })
    await platform.clearEmailSecret()
    expect([last().method, url().pathname]).toEqual(['DELETE', '/api/platform/notification-transports/email/secret'])
  })
})
