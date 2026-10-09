import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { artifactDate } from '@/features/artifacts/format'
import { platformPaths } from '@/features/platform/paths'
import { permissionLabels, statusLabels } from './presentation'
import type { SupportPermission, SupportSession, SupportStatus } from './types'

export function PermissionBadges({ permissions }: { permissions: readonly SupportPermission[] }) {
  return <div className="flex flex-wrap gap-1">{permissions.map(permission => <Badge key={permission} tone="info">{permissionLabels[permission]}</Badge>)}</div>
}
export function SupportStatusPill({ status }: { status: SupportStatus }) {
  return <Badge tone={status === 'Active' ? 'success' : status === 'Revoked' ? 'error' : 'neutral'}>{statusLabels[status]}</Badge>
}
export function SupportSessionTable({ rows, showTenant = true }: { rows: readonly SupportSession[]; showTenant?: boolean }) {
  const columns: Column<SupportSession>[] = [
    ...(showTenant ? [{ key: 'tenant', header: 'Tenant', wrap: true, render: (session: SupportSession) => <Link className="font-medium text-primary hover:underline" to={platformPaths.supportSession(session.id)}>{session.tenantName}</Link> }] : []),
    { key: 'actor', header: 'Yapan', wrap: true, render: session => <Link className="text-primary hover:underline" to={platformPaths.supportSession(session.id)}>{session.actorDisplayName ?? session.platformAdminId}</Link> },
    { key: 'reason', header: 'Gerekçe', render: session => <p className="max-w-52 truncate" title={session.reason}>{session.reason}</p> },
    { key: 'permissions', header: 'İzinler', wrap: true, render: session => <PermissionBadges permissions={session.permissions} /> },
    { key: 'started', header: 'Başlangıç', render: session => artifactDate(session.startedAt) },
    { key: 'expires', header: 'Süre sonu', render: session => artifactDate(session.expiresAt) },
    { key: 'status', header: 'Durum', render: session => <SupportStatusPill status={session.status} /> },
  ]
  return <DataTable rows={rows} columns={columns} keyExtractor={session => session.id} emptyMessage="Destek oturumu bulunamadı." />
}
