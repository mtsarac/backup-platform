import { useContext, useEffect, useState, type ReactNode } from 'react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { QueryClientContext, useMutation, useQuery, type QueryClient } from '@tanstack/react-query'
import { Popover } from '@base-ui/react/popover'
import { AlertTriangle, Bell, Check, Info, Server, Share2, ShieldAlert, type LucideIcon } from 'lucide-react'
import { Link, useInRouterContext } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { ErrorNotice } from '@/components/ui/notice'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { AuthContext } from '@/features/auth/auth-context'
import { TenantContext } from '@/features/tenancy/tenant-context'
import { API_BASE_URL, apiRequest } from '@/lib/api/client'
import { formatDateTime, formatRelativeTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
import { cn } from '@/lib/utils'
import { eventTypeLabels } from './labels'

const shareEventTypes: Record<string, true> = { backup_share_created: true, backup_share_extended: true, backup_share_revoked: true }

export interface InAppNotification {
  id: string
  eventId: string
  type: string
  subjectName: string
  occurredAt: string
  readAt: string | null
  tenantId?: string | null
  subjectKind?: string | null
  subjectId?: string | null
}

/** The screen an item is about: share, job, storage connection or agent. Null when the subject has no page this user can open. */
function subjectHref(item: InAppNotification, role?: string, currentTenantId?: string): string | null {
  const tenantId = item.tenantId || currentTenantId
  const platform = role === 'PlatformAdmin'
  if (item.subjectKind === 'backup_share' || shareEventTypes[item.type]) return platform ? '/platform/shares' : tenantId ? `/tenants/${encodeURIComponent(tenantId)}/backup-shares` : '/platform/shares'
  if (!item.subjectId || !tenantId) return null
  const t = encodeURIComponent(tenantId), id = encodeURIComponent(item.subjectId)
  if (item.subjectKind === 'agent') return platform ? `/platform/tenants/${t}/agents/${id}` : `/tenants/${t}/agents/${id}`
  if (platform) return null
  if (item.subjectKind === 'backup_job') return `/tenants/${t}/backup-jobs/${id}`
  if (item.subjectKind === 'storage_connection') return `/tenants/${t}/storage/connections/${id}`
  return null
}

type Tone = 'error' | 'warning' | 'neutral'
const failureTypes = new Set(['backup_failed', 'agent_offline', 'quota_critical', 'storage_capacity_critical', 'replica_degraded'])
const warningTypes = new Set(['backup_stale', 'backup_size_anomaly'])
const toneRank: Record<Tone, number> = { error: 0, warning: 1, neutral: 2 }

function describeType(type: string): { tone: Tone; Icon: LucideIcon } {
  if (type.startsWith('security_')) return { tone: 'warning', Icon: ShieldAlert }
  if (failureTypes.has(type)) return { tone: 'error', Icon: type === 'agent_offline' ? Server : AlertTriangle }
  if (warningTypes.has(type)) return { tone: 'warning', Icon: AlertTriangle }
  if (shareEventTypes[type]) return { tone: 'neutral', Icon: Share2 }
  return { tone: 'neutral', Icon: Info }
}
const toneClass: Record<Tone, string> = { error: 'bg-error-subtle text-error', warning: 'bg-warning-subtle text-warning', neutral: 'bg-neutral-subtle text-neutral' }

export function InAppNotifications({ userId }: { userId: string }) {
  const client = useContext(QueryClientContext)
  if (!client) return null
  return <InAppNotificationsConnected userId={userId} client={client} />
}

function InAppNotificationsConnected({ userId, client }: { userId: string; client: QueryClient }) {
  useLanguage()
  const auth = useContext(AuthContext)
  const tenant = useContext(TenantContext)
  const inRouter = useInRouterContext()
  const [open, setOpen] = useState(false)
  const key = ['in-app-notifications', userId] as const
  const list = useQuery({ queryKey: key, queryFn: ({ signal }) => apiRequest<InAppNotification[]>('notifications/in-app', { signal }) })
  // There is no bulk endpoint: "mark all" posts one read per unread item and reloads whatever the outcome.
  const markRead = useMutation({
    mutationFn: (ids: string[]) => Promise.all(ids.map((id) => apiRequest<void>(`notifications/in-app/${encodeURIComponent(id)}/read`, { method: 'POST' }))),
    onSettled: () => client.invalidateQueries({ queryKey: key }),
  })

  useEffect(() => {
    let stopped = false
    let retry: number | undefined
    const hubUrl = new URL('/hubs/notifications', new URL(API_BASE_URL, window.location.origin)).toString()
    const connection = new HubConnectionBuilder().withUrl(hubUrl, { withCredentials: true }).withAutomaticReconnect().build()
    const reload = () => { void client.invalidateQueries({ queryKey: ['in-app-notifications', userId] }) }
    connection.on('notificationReceived', (notification: InAppNotification) => {
      client.setQueryData<InAppNotification[]>(['in-app-notifications', userId], (current) => {
        if (!current) return current
        const existing = current.find((item) => item.id === notification.id)
        return [existing ? { ...notification, readAt: existing.readAt ?? notification.readAt } : notification, ...current.filter((item) => item.id !== notification.id)]
      })
      reload()
    })
    connection.onreconnected(reload)
    const start = () => {
      void connection.start().then(() => { if (!stopped) reload() }).catch(() => {
        if (!stopped) retry = window.setTimeout(start, 5000)
      })
    }
    connection.onclose(() => { if (!stopped) retry = window.setTimeout(start, 5000) })
    start()
    return () => { stopped = true; clearTimeout(retry); void connection.stop() }
  }, [client, userId])

  const items = list.data ?? []
  const unreadItems = items.filter((item) => !item.readAt).sort((a, b) => toneRank[describeType(a.type).tone] - toneRank[describeType(b.type).tone])
  const earlierItems = items.filter((item) => item.readAt)
  const unread = unreadItems.length
  const role = auth?.user?.role
  const settingsHref = role === 'PlatformAdmin' ? '/platform/notification-transports' : tenant?.tenantId ? `/tenants/${encodeURIComponent(tenant.tenantId)}/settings` : null
  const openLink = (href: string, className: string, children: ReactNode, onClick?: () => void) => inRouter
    ? <Link className={className} to={href} onClick={onClick}>{children}</Link>
    : <a className={className} href={href} onClick={onClick}>{children}</a>

  const row = (item: InAppNotification) => {
    const href = subjectHref(item, role, tenant?.tenantId)
    const { tone, Icon } = describeType(item.type)
    const security = item.type.startsWith('security_')
    const title = security ? tx('Hesap güvenliği') : item.subjectName
    const isUnread = !item.readAt
    const titleClass = cn('block truncate font-medium text-text-heading', href && 'min-h-0 rounded-sm after:absolute after:inset-0 hover:underline')
    return <li key={item.id} className={cn('relative flex items-start gap-3 px-4 py-3 hover:bg-surface-2', !isUnread && 'text-text-muted')}>
      <span aria-hidden="true" className={cn('mt-0.5 flex size-8 shrink-0 items-center justify-center rounded-full', isUnread ? toneClass[tone] : 'bg-surface-2 text-text-muted')}><Icon className="size-4" /></span>
      <div className="min-w-0 flex-1 space-y-0.5">
        {href ? openLink(href, titleClass, title, () => { setOpen(false); if (isUnread) markRead.mutate([item.id]) }) : <p className={titleClass}>{title}</p>}
        <p className="text-sm text-text-muted">{eventTypeLabels[item.type as keyof typeof eventTypeLabels] ?? item.type}</p>
        {security && <p className="text-xs text-text-muted">{tx('Zorunlu güvenlik bildirimi')}</p>}
        <p className="text-xs text-text-muted"><time dateTime={item.occurredAt} title={formatDateTime(item.occurredAt)}>{formatRelativeTime(item.occurredAt)}</time></p>
      </div>
      {isUnread && <Button variant="ghost" size="icon" className="relative z-10 -mr-2 shrink-0" disabled={markRead.isPending} aria-label={tx('Okundu olarak işaretle: {0}', { '0': title })} onClick={() => markRead.mutate([item.id])}><Check aria-hidden="true" /></Button>}
    </li>
  }
  const group = (label: string, group: InAppNotification[]) => group.length > 0 && <section aria-label={label}>
    <h3 className="bg-canvas px-4 py-1.5 text-xs font-medium text-text-muted">{label}</h3>
    <ul className="divide-y divide-border">{group.map(row)}</ul>
  </section>

  return <Popover.Root open={open} onOpenChange={setOpen}>
    <Popover.Trigger render={<Button variant="outline" size="sm" aria-label={tx('Bildirimler, {0} okunmamış', { '0': unread })} />}>
      <Bell aria-hidden="true" className="size-4" /><span className="max-sm:sr-only">{tx('Bildirimler')}</span>
      {unread > 0 && <Badge tone="accent" className="tabular-nums">{unread > 99 ? '99+' : unread}</Badge>}
    </Popover.Trigger>
    <Popover.Portal><Popover.Positioner align="end" sideOffset={8} collisionPadding={8} className="z-50"><Popover.Popup aria-label={tx('Bildirimler')} className="flex max-h-[min(80dvh,34rem)] w-96 max-w-[calc(100vw-1rem)] flex-col overflow-hidden rounded-lg border border-border bg-surface-raised shadow-overlay outline-none">
      <div className="flex items-center justify-between gap-2 border-b border-border px-4 py-2">
        <h2 className="font-medium text-text-heading">{tx('Bildirimler')}</h2>
        {unread > 0 && <Button variant="ghost" size="sm" disabled={markRead.isPending} onClick={() => markRead.mutate(unreadItems.map((item) => item.id))}>{tx('Tümünü okundu işaretle')}</Button>}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
        {markRead.error && <ErrorNotice className="m-3" error={markRead.error} />}
        {list.isPending ? <div role="status" aria-label={tx('Yükleniyor')} className="space-y-3 p-4"><Skeleton className="h-10 w-full" /><Skeleton className="h-10 w-full" /><Skeleton className="h-10 w-4/5" /><span className="sr-only">{tx('Yükleniyor…')}</span></div>
          : list.error && !list.data ? <ErrorNotice className="m-3" title={tx('Veriler alınamadı')} error={list.error} onRetry={() => void list.refetch()} />
          : items.length === 0 ? <EmptyState icon={Bell} message={tx('Henüz bildiriminiz yok.')} action={<Button variant="outline" disabled={list.isFetching} onClick={() => void list.refetch()}>{tx('Yenile')}</Button>} />
          : <>{group(tx('Okunmamış'), unreadItems)}{group(tx('Daha eski'), earlierItems)}</>}
      </div>
      {settingsHref && <div className="border-t border-border px-4 py-2">{openLink(settingsHref, 'text-sm font-medium text-primary underline-offset-4 hover:underline', tx('Bildirim ayarları'), () => setOpen(false))}</div>}
    </Popover.Popup></Popover.Positioner></Popover.Portal>
  </Popover.Root>
}
