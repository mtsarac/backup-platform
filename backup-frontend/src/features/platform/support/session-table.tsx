import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/ui/empty-state'
import { platformPaths } from '@/features/platform/paths'
import { permissionShortLabels } from './presentation'
import type { SupportPermission, SupportSession } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

export function PermissionBadges({ permissions }: { permissions: readonly SupportPermission[] }) {
  useLanguage()
  return <div className="flex flex-wrap gap-1">{permissions.map(permission => <Badge key={permission} tone="info">{permissionShortLabels[permission]}</Badge>)}</div>
}
export function SupportSessionTable({ rows, showTenant = true }: { rows: readonly SupportSession[]; showTenant?: boolean }) {
  useLanguage()
  const columns: Column<SupportSession>[] = [
    ...(showTenant ? [{ key: 'tenant', header: tx('Tenant'), wrap: true, render: (session: SupportSession) => <Link className="font-medium text-primary hover:underline" to={platformPaths.supportSession(session.id)}>{session.tenantName}</Link> }] : []),
    { key: 'status', card: 'status', header: tx('Durum'), render: session => <StatusPill domain="supportSession" status={session.status} /> },
    { key: 'actor', header: tx('Yapan'), wrap: true, render: session => <div><Link className="text-primary hover:underline" to={platformPaths.supportSession(session.id)}>{session.actorDisplayName ?? tx('Bilinmeyen kullanıcı')}</Link>{!session.actorDisplayName && <p className="break-all font-mono text-xs text-text-muted">{session.platformAdminId}</p>}</div> },
    { key: 'permissions', card: 'hidden', header: tx('İzinler'), wrap: true, render: session => <PermissionBadges permissions={session.permissions} /> },
    { key: 'reason', header: tx('Gerekçe'), wrap: true, render: session => <span className="line-clamp-2" title={session.reason}>{session.reason}</span> },
    { key: 'started', card: 'hidden', header: tx('Başlangıç'), render: session => formatDateTime(session.startedAt) },
    { key: 'expires', header: tx('Süre sonu'), render: session => formatDateTime(session.expiresAt) },
  ]
  if (rows.length === 0) return <div className="rounded-xl border border-border bg-surface"><EmptyState message={tx("Destek oturumu bulunamadı; yeni oturum tenant detayındaki Destek erişimi bölümünden açılır.")} /></div>
  return <DataList rows={rows} columns={columns} keyExtractor={session => session.id} emptyMessage={tx("Destek oturumu bulunamadı.")} />
}
