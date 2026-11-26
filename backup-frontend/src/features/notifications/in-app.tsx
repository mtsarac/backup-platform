import { useContext, useEffect, useState } from 'react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { QueryClientContext, useMutation, useQuery, type QueryClient } from '@tanstack/react-query'
import { Popover } from '@base-ui/react/popover'
import { Bell } from 'lucide-react'
import { Link, useInRouterContext } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { linkClass } from '@/components/ui/form-styles'
import { ErrorNotice } from '@/components/ui/notice'
import { EmptyState } from '@/components/ui/empty-state'
import { QueryState } from '@/components/data/query-state'
import { AuthContext } from '@/features/auth/auth-context'
import { TenantContext } from '@/features/tenancy/tenant-context'
import { API_BASE_URL, apiRequest } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import { tx, useLanguage } from '@/lib/i18n'
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

function shareHref(item: InAppNotification, role?: string, currentTenantId?: string): string | null {
  if (item.subjectKind !== 'backup_share' && !shareEventTypes[item.type]) return null
  if (role === 'PlatformAdmin') return '/platform/shares'
  const tenantId = item.tenantId || currentTenantId
  return tenantId ? `/tenants/${encodeURIComponent(tenantId)}/backup-shares` : '/platform/shares'
}

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
  const markRead = useMutation({
    mutationFn: (id: string) => apiRequest<void>(`notifications/in-app/${encodeURIComponent(id)}/read`, { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: key }),
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

  const unread = list.data?.filter((item) => !item.readAt).length ?? 0
  return <Popover.Root open={open} onOpenChange={setOpen}>
    <Popover.Trigger render={<Button variant="outline" size="sm" />}>
      <Bell aria-hidden="true" className="size-4" />{tx('Bildirimler')} {unread > 0 && <span>({unread})</span>}
    </Popover.Trigger>
    <Popover.Portal><Popover.Positioner align="end" sideOffset={8} className="z-50"><Popover.Popup aria-label={tx('Bildirimler')} className="max-h-[min(70vh,32rem)] w-80 max-w-[calc(100vw-2rem)] overflow-y-auto rounded-lg border border-border bg-surface-raised p-4 shadow-overlay outline-none">
      <QueryState isPending={list.isPending} error={list.error} empty={list.data?.length === 0} emptyState={<EmptyState message={tx("Henüz bildiriminiz yok.")} action={<Button disabled={list.isFetching} onClick={() => void list.refetch()}>{tx('Yenile')}</Button>} />}>
        {markRead.error && <ErrorNotice error={markRead.error} />}
        <ul className="space-y-3">{list.data?.map((item) => {
          const href = shareHref(item, auth?.user?.role, tenant?.tenantId)
          return <li key={item.id} className="space-y-1 border-b border-border pb-3 last:border-0">
            <p className="font-medium">
              {href ? (
                inRouter
                  ? <Link className={linkClass} to={href} onClick={() => setOpen(false)}>{item.subjectName}</Link>
                  : <a className={linkClass} href={href} onClick={() => setOpen(false)}>{item.subjectName}</a>
              ) : item.type.startsWith('security_') ? tx('Hesap güvenliği') : item.subjectName}
            </p>
            {item.type.startsWith('security_') && <p className="text-xs text-text-muted">{tx('Zorunlu güvenlik bildirimi')}</p>}
            <p className="text-sm text-text-muted">{eventTypeLabels[item.type as keyof typeof eventTypeLabels] ?? item.type} · {formatDateTime(item.occurredAt)}</p>
            {item.readAt ? <span className="text-sm text-text-muted">{tx('Okundu')}</span> : <Button variant="outline" size="sm" disabled={markRead.isPending} onClick={() => markRead.mutate(item.id)}>{tx('Okundu olarak işaretle')}</Button>}
          </li>
        })}</ul>
      </QueryState>
    </Popover.Popup></Popover.Positioner></Popover.Portal>
  </Popover.Root>
}
