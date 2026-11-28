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

let unmountAll = () => undefined as void
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(<QueryClientProvider client={client}><InAppNotifications userId="user-a" /></QueryClientProvider>)
  unmountAll = view.unmount
  return view
}

describe('in-app notifications', () => {
  it('closes on Escape and on an outside click, returns focus to the button, and is not a live region', async () => {
    const user = userEvent.setup()
    render(<QueryClientProvider client={new QueryClient()}><button>dışarısı</button><InAppNotifications userId="user-a" /></QueryClientProvider>)
    const trigger = await screen.findByRole('button', { name: /Bildirimler/ })
    await user.click(trigger)
    const panel = await screen.findByRole('dialog', { name: 'Bildirimler' })
    expect(panel.querySelector('[aria-live]')).toBeNull()
    await user.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(trigger).toHaveFocus()
    await user.click(trigger)
    await screen.findByRole('dialog', { name: 'Bildirimler' })
    await user.click(screen.getByRole('button', { name: 'dışarısı' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('shows a security notification as mandatory, with no control to switch it off', async () => {
    records = [{ ...first, type: 'security_suspicious_login', subjectName: 'Account security' }]
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    const panel = await screen.findByRole('dialog', { name: 'Bildirimler' })
    expect(within(panel).getByText('Hesap güvenliği')).toBeInTheDocument()
    expect(within(panel).getByText(/Şüpheli giriş etkinliği/)).toBeInTheDocument()
    expect(within(panel).getByText('Zorunlu güvenlik bildirimi')).toBeInTheDocument()
    expect(within(panel).queryByRole('checkbox')).not.toBeInTheDocument()
    expect(within(panel).queryByRole('switch')).not.toBeInTheDocument()
    expect(within(panel).getAllByRole('button').map((button) => button.getAttribute('aria-label') ?? button.textContent)).toEqual(['Tümünü okundu işaretle', 'Okundu olarak işaretle: Hesap güvenliği'])
  })

  it('loads the durable list and marks a notification as read', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    expect(await screen.findByText('Sunucu A')).toBeInTheDocument()
    records = [{ ...first, readAt: '2026-01-01T12:05:00Z' }]
    await user.click(screen.getByRole('button', { name: 'Okundu olarak işaretle: Sunucu A' }))
    await waitFor(() => expect(screen.queryByText('Okunmamış')).not.toBeInTheDocument())
    expect(apiRequest).toHaveBeenCalledWith('notifications/in-app/1/read', { method: 'POST' })
    expect(screen.queryByRole('button', { name: /Okundu olarak işaretle/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Tümünü okundu işaretle' })).not.toBeInTheDocument()
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
    await user.click(screen.getByRole('button', { name: 'Okundu olarak işaretle: Sunucu A' }))
    expect(await within(screen.getByRole('dialog', { name: 'Bildirimler' })).findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Okundu olarak işaretle: Sunucu A' })).toBeInTheDocument()
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
    expect(screen.getByRole('link', { name: 'ProductionDb' })).toHaveAttribute('href', '/tenants/tenant-a/backup-shares')
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

  it('lists every notification newest first whatever its type or read state, marks all as read, and shows the unread count', async () => {
    const user = userEvent.setup()
    const at = (hour: number) => `2026-01-01T${String(hour).padStart(2, '0')}:00:00Z`
    records = [
      { ...first, id: 'a', type: 'backup_share_created', subjectName: 'Paylasim', occurredAt: at(11) },
      { ...first, id: 'b', type: 'backup_failed', subjectName: 'Okunmus yeni', occurredAt: at(14), readAt: at(15) },
      { ...first, id: 'c', type: 'backup_stale', subjectName: 'Eski uyari', occurredAt: at(9) },
      { ...first, id: 'd', type: 'agent_offline', subjectName: 'Kritik', occurredAt: at(12) },
    ]
    mount()
    expect(await screen.findByRole('button', { name: 'Bildirimler 3 okunmamış' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /Bildirimler/ }))
    const panel = await screen.findByRole('dialog', { name: 'Bildirimler' })
    const order = () => within(panel).getAllByRole('listitem').map((li) => ['Okunmus yeni', 'Kritik', 'Paylasim', 'Eski uyari'].find((name) => li.textContent?.includes(name)))
    expect(order()).toEqual(['Okunmus yeni', 'Kritik', 'Paylasim', 'Eski uyari'])
    expect(within(panel).getAllByText('Okunmamış')).toHaveLength(3)
    records = records.map((item) => ({ ...item, readAt: item.readAt ?? at(16) }))
    await user.click(within(panel).getByRole('button', { name: 'Tümünü okundu işaretle' }))
    await waitFor(() => expect(within(panel).queryByText('Okunmamış')).not.toBeInTheDocument())
    expect(order()).toEqual(['Okunmus yeni', 'Kritik', 'Paylasim', 'Eski uyari'])
    for (const id of ['a', 'c', 'd']) expect(apiRequest).toHaveBeenCalledWith(`notifications/in-app/${id}/read`, { method: 'POST' })
    expect(apiRequest).not.toHaveBeenCalledWith('notifications/in-app/b/read', { method: 'POST' })
  })

  it('links job, connection and agent notifications to their screens and marks the item read on click', async () => {
    const user = userEvent.setup()
    const base = { ...first, tenantId: 'tenant-a' }
    records = [
      { ...base, id: 'j', subjectName: 'Gece yedegi', subjectKind: 'backup_job', subjectId: 'job-1' },
      { ...base, id: 'c', type: 'quota_critical', subjectName: 'Depo', subjectKind: 'storage_connection', subjectId: 'conn-1' },
      { ...base, id: 'g', type: 'agent_offline', subjectName: 'host-1', subjectKind: 'agent', subjectId: 'agent-1' },
    ]
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    expect(await screen.findByRole('link', { name: 'Gece yedegi' })).toHaveAttribute('href', '/tenants/tenant-a/backup-jobs/job-1')
    expect(screen.getByRole('link', { name: 'Depo' })).toHaveAttribute('href', '/tenants/tenant-a/storage/connections/conn-1')
    expect(screen.getByRole('link', { name: 'host-1' })).toHaveAttribute('href', '/tenants/tenant-a/agents/agent-1')
    await user.click(screen.getByRole('link', { name: 'Gece yedegi' }))
    await waitFor(() => expect(apiRequest).toHaveBeenCalledWith('notifications/in-app/j/read', { method: 'POST' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('shows an empty state, and an error state with a retry that recovers', async () => {
    const user = userEvent.setup()
    records = []
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    expect(await screen.findByText('Henüz bildiriminiz yok.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Tümünü okundu işaretle' })).not.toBeInTheDocument()
    unmountAll()

    vi.mocked(apiRequest).mockImplementation(async () => { throw new Error('Network error') })
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    const panel = await screen.findByRole('dialog', { name: 'Bildirimler' })
    expect(await within(panel).findByRole('alert')).toBeInTheDocument()
    records = [first]
    vi.mocked(apiRequest).mockImplementation(async (path) => (path === 'notifications/in-app' ? [...records] : undefined) as never)
    await user.click(within(panel).getByRole('button', { name: 'Yeniden dene' }))
    expect(await within(panel).findByText('Sunucu A')).toBeInTheDocument()
  })

  it('shows relative time with the exact time in a tooltip', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Bildirimler/ }))
    const time = (await screen.findByText('Sunucu A')).closest('li')!.querySelector('time')!
    expect(time).toHaveAttribute('datetime', first.occurredAt)
    expect(time.textContent).toMatch(/önce/)
    expect(time.getAttribute('title')).toBeTruthy()
  })
})
