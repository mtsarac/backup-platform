import { AuthContext, type AuthContextValue } from '@/features/auth/auth-context'
import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render } from '@/test/render'
import { apiRequest } from '@/lib/api/client'
import * as clientModule from '@/lib/api/client'
import { InAppNotifications, type InAppNotification } from './in-app'

const hub = vi.hoisted(() => ({
  received: undefined as ((notification: InAppNotification) => void) | undefined,
  reconnected: undefined as (() => void) | undefined,
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
}))
vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    build() { return { start: hub.start, stop: hub.stop, on: (_: string, callback: typeof hub.received) => { hub.received = callback }, onreconnected: (callback: typeof hub.reconnected) => { hub.reconnected = callback }, onclose: vi.fn() } }
  },
}))
vi.mock('@/lib/api/client', async (importOriginal) => ({ ...(await importOriginal<typeof clientModule>()), apiRequest: vi.fn() }))

const first: InAppNotification = { id: '1', eventId: 'event-1', type: 'backup_failed', subjectName: 'Sunucu A', occurredAt: '2026-01-01T12:00:00Z', readAt: null }
const second: InAppNotification = { ...first, id: '2', eventId: 'event-2', subjectName: 'Sunucu B' }
let records: InAppNotification[]

beforeEach(() => {
  records = [first]
  hub.received = undefined
  hub.reconnected = undefined
  vi.mocked(apiRequest).mockReset().mockImplementation(async (path) => {
    if (path === 'notifications/in-app') return [...records] as never
    if (path === 'notifications/in-app/1/read') { records = [{ ...first, readAt: '2026-01-02T00:00:00Z' }]; return undefined as never }
    throw new Error('Unexpected request')
  })
})

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><InAppNotifications userId="user-a" /></QueryClientProvider>)
}

describe('in-app notifications', () => {
  it('loads the durable list and marks a notification as read', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    expect(await screen.findByText('Sunucu A')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Okundu olarak işaretle' }))
    expect(await screen.findByText('Okundu')).toBeInTheDocument()
    expect(apiRequest).toHaveBeenCalledWith('notifications/in-app/1/read', { method: 'POST' })
    expect(screen.queryByRole('button', { name: 'Okundu olarak işaretle' })).not.toBeInTheDocument()
  })

  it('shows real-time arrivals once and reloads the durable list on reconnect', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    await screen.findByText('Sunucu A')
    records = [second, first]
    act(() => { hub.received?.(second); hub.received?.(second) })
    expect(await screen.findByText('Sunucu B')).toBeInTheDocument()
    expect(screen.getAllByText('Sunucu B')).toHaveLength(1)
    records = [first]
    act(() => hub.reconnected?.())
    await waitFor(() => expect(screen.queryByText('Sunucu B')).not.toBeInTheDocument())
  })

  it('keeps unread state when marking read fails', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    await screen.findByText('Sunucu A')
    vi.mocked(apiRequest).mockImplementation(async (path) => {
      if (path === 'notifications/in-app') return [...records] as never
      throw new Error('Network error')
    })
    await user.click(screen.getByRole('button', { name: 'Okundu olarak işaretle' }))
    expect(await within(screen.getByRole('region', { name: 'Bildirimler' })).findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Okundu olarak işaretle' })).toBeInTheDocument()
  })

  it('displays share notification labels and links to the related share screen for UserAdmin and PlatformAdmin', async () => {
    const user = userEvent.setup()
    records = [
      { id: 'share-n1', eventId: 'ev-1', type: 'backup_share_created', subjectName: 'ProductionDb', occurredAt: '2026-01-01T12:00:00Z', readAt: null, tenantId: 'tenant-a', subjectKind: 'backup_share', subjectId: 'share-1' },
      { id: 'share-n2', eventId: 'ev-2', type: 'backup_share_extended', subjectName: 'AnalyticsDb', occurredAt: '2026-01-01T13:00:00Z', readAt: null, tenantId: 'tenant-a', subjectKind: 'backup_share', subjectId: 'share-2' },
      { id: 'share-n3', eventId: 'ev-3', type: 'backup_share_revoked', subjectName: 'ArchiveDb', occurredAt: '2026-01-01T14:00:00Z', readAt: null, tenantId: 'tenant-a', subjectKind: 'backup_share', subjectId: 'share-3' },
    ]
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    const { unmount } = render(
      <AuthContext.Provider value={{ user: { userId: 'user-a', displayName: 'Alice', role: 'UserAdmin', isPlatformAdmin: false } } as AuthContextValue}>
        <QueryClientProvider client={client}><InAppNotifications userId="user-a" /></QueryClientProvider>
      </AuthContext.Provider>
    )
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    expect(await screen.findByText(/Paylaşım oluşturuldu/)).toBeInTheDocument()
    expect(screen.getByText(/Paylaşım süresi uzatıldı/)).toBeInTheDocument()
    expect(screen.getByText(/Paylaşım iptal edildi/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'ProductionDb' })).toHaveAttribute('href', '/tenants/tenant-a/shares')
    unmount()

    const platformClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
    render(
      <AuthContext.Provider value={{ user: { userId: 'pa-1', displayName: 'Peter', role: 'PlatformAdmin', isPlatformAdmin: true } } as AuthContextValue}>
        <QueryClientProvider client={platformClient}><InAppNotifications userId="pa-1" /></QueryClientProvider>
      </AuthContext.Provider>
    )
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    expect(await screen.findByRole('link', { name: 'ProductionDb' })).toHaveAttribute('href', '/platform/shares')
  })
})
