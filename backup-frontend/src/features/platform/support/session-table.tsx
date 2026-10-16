import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/ui/empty-state'
import { artifactDate } from '@/features/artifacts/format'
import { platformPaths } from '@/features/platform/paths'
import { permissionShortLabels, statusLabels } from './presentation'
import type { SupportPermission, SupportSession, SupportStatus } from './types'

export function PermissionBadges({ permissions }: { permissions: readonly SupportPermission[] }) {
  return <div className="flex flex-wrap gap-1">{permissions.map(permission => <Badge key={permission} tone="info">{permissionShortLabels[permission]}</Badge>)}</div>
}
export function SupportStatusPill({ status }: { status: SupportStatus }) {
  return <Badge dot tone={status === 'Active' ? 'success' : status === 'Revoked' ? 'error' : status === 'Expired' ? 'warning' : 'neutral'}>{statusLabels[status]}</Badge>
}
export function SupportSessionTable({ rows, showTenant = true }: { rows: readonly SupportSession[]; showTenant?: boolean }) {
  const columns: Column<SupportSession>[] = [
    ...(showTenant ? [{ key: 'tenant', header: 'Tenant', wrap: true, render: (session: SupportSession) => <Link className="font-medium text-primary hover:underline" to={platformPaths.supportSession(session.id)}>{session.tenantName}</Link> }] : []),
    { key: 'status', header: 'Durum', render: session => <SupportStatusPill status={session.status} /> },
    { key: 'actor', header: 'Yapan', wrap: true, render: session => <div><Link className="text-primary hover:underline" to={platformPaths.supportSession(session.id)}>{session.actorDisplayName ?? 'Bilinmeyen kullanıcı'}</Link>{!session.actorDisplayName && <p className="break-all font-mono text-xs text-text-muted">{session.platformAdminId}</p>}</div> },
    { key: 'permissions', header: 'İzinler', wrap: true, render: session => <PermissionBadges permissions={session.permissions} /> },
    { key: 'reason', header: 'Gerekçe', wrap: true, render: session => <span className="line-clamp-2" title={session.reason}>{session.reason}</span> },
    { key: 'started', header: 'Başlangıç', render: session => artifactDate(session.startedAt) },
    { key: 'expires', header: 'Süre sonu', render: session => artifactDate(session.expiresAt) },
  ]
  if (rows.length === 0) return <div className="rounded-xl border border-border bg-surface"><EmptyState title="Destek oturumu bulunamadı.">Yeni bir destek oturumu tenant detayındaki Destek erişimi bölümünden açılır.</EmptyState></div>
  return <DataTable rows={rows} columns={columns} keyExtractor={session => session.id} emptyMessage="Destek oturumu bulunamadı." />
}
