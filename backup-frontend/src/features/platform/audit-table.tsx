import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Badge } from '@/components/ui/badge'
import { agentDate } from '@/features/agents/format'
import { platformPaths } from './paths'
import { linkClass } from './styles'
import type { AuditDetailValue, PlatformAuditEntry } from './types'

const actionLabels: Record<string, string> = {
  'tenant.created': 'Tenant oluşturuldu',
  'tenant.renamed': 'Tenant yeniden adlandırıldı',
  'membership.added': 'Üyelik eklendi',
  'membership.role_changed': 'Üyelik rolü değiştirildi',
  'membership.removed': 'Üyelik kaldırıldı',
  'agent_enrollment.policy_changed': 'Agent kayıt politikası değiştirildi',
  'user.created': 'Kullanıcı oluşturuldu',
}

function auditActionLabel(action: string) {
  return Object.hasOwn(actionLabels, action) ? actionLabels[action] : action
}

function detailValue(value: AuditDetailValue) {
  if (value === null) return '—'
  if (typeof value === 'boolean') return value ? 'Evet' : 'Hayır'
  return String(value)
}

function Details({ details }: { details: PlatformAuditEntry['details'] }) {
  const entries = Object.entries(details ?? {})
  if (entries.length === 0) return <span className="text-text-muted">—</span>
  return <dl className="space-y-0.5 text-xs">{entries.map(([key, value]) => <div key={key} className="flex flex-wrap gap-1"><dt className="text-text-muted">{key}:</dt><dd className="break-all">{detailValue(value)}</dd></div>)}</dl>
}

export function AuditTable({ rows, emptyMessage, showTenant = true }: { rows: readonly PlatformAuditEntry[]; emptyMessage: string; showTenant?: boolean }) {
  const columns: Column<PlatformAuditEntry>[] = [
    { key: 'at', header: 'Zaman', render: (entry) => agentDate(entry.at) },
    { key: 'action', header: 'İşlem', wrap: true, render: (entry) => <span className="font-medium">{auditActionLabel(entry.action)}</span> },
    { key: 'actor', header: 'Yapan', wrap: true, render: (entry) => <div className="flex flex-wrap items-center gap-1.5"><span>{entry.actorDisplayName ?? 'Bilinmeyen kullanıcı'}</span>{entry.actorKind === 'PlatformAdmin' ? <Badge tone="warning">PlatformAdmin</Badge> : <Badge tone="info">Tenant yöneticisi</Badge>}</div> },
    ...(showTenant ? [{ key: 'tenant', header: 'Tenant', wrap: true, render: (entry: PlatformAuditEntry) => entry.tenantId ? <Link className={linkClass} to={platformPaths.tenant(entry.tenantId)}>{entry.tenantName ?? 'Tenant'}</Link> : <span className="text-text-muted">—</span> }] : []),
    { key: 'target', header: 'Hedef', wrap: true, render: (entry) => entry.targetType },
    { key: 'details', header: 'Ayrıntılar', wrap: true, render: (entry) => <Details details={entry.details} /> },
  ]
  return <DataTable rows={rows} columns={columns} keyExtractor={(entry) => entry.id} emptyMessage={emptyMessage} />
}
