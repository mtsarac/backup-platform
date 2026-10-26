import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { RelativeTime } from '@/components/data/relative-time'
import { Badge } from '@/components/ui/badge'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { platformPaths } from './paths'
import { linkClass } from '@/components/ui/form-styles'
import type { PlatformAuditEntry } from './types'
import { formatDateTime } from '@/lib/format'
import { auditActionLabel } from '@/features/audit/action-labels'
import { auditDetailItems } from '@/features/audit/audit-details'

const actorLabels: Record<PlatformAuditEntry['actorKind'], string> = {
  get PlatformAdmin() { return tx("Platform Yöneticisi") },
  get TenantAdministrator() { return tx("Tenant yöneticisi") },
  get System() { return tx("Sistem") },
  get User() { return tx("Kullanıcı") },
  get Anonymous() { return tx("Anonim") },
}

/**
 * Visible: when (everyday time), what happened (plain label), who, which tenant.
 * Collapsed and copyable: action code, target type and id, entry and actor ids, exact time, actor kind, and the raw detail pairs.
 */
export function AuditTable({ rows, emptyMessage, showTenant = true, className }: { rows: readonly PlatformAuditEntry[]; emptyMessage: string; showTenant?: boolean; className?: string }) {
  useLanguage()
  const columns: Column<PlatformAuditEntry>[] = [
    { key: 'at', header: tx('Zaman'), render: (entry) => <RelativeTime value={entry.at} /> },
    { key: 'action', card: 'title', header: tx('İşlem'), wrap: true, render: (entry) => <span className="font-medium">{auditActionLabel(entry.action) ?? tx('Bilinmeyen işlem')}</span> },
    { key: 'actor', header: tx('Yapan'), wrap: true, render: (entry) => <div className="flex flex-wrap items-center gap-1.5"><span>{entry.actorDisplayName ?? tx('Bilinmeyen kullanıcı')}</span><Badge tone={entry.actorKind === 'PlatformAdmin' ? 'warning' : entry.actorKind === 'System' || entry.actorKind === 'Anonymous' ? 'neutral' : 'info'}>{actorLabels[entry.actorKind]}</Badge></div> },
    ...(showTenant ? [{ key: 'tenant', header: tx('Tenant'), wrap: true, render: (entry: PlatformAuditEntry) => entry.tenantId ? <Link className={`${linkClass} inline-flex min-h-11 items-center`} to={platformPaths.tenant(entry.tenantId)}>{entry.tenantName ?? tx('Tenant')}</Link> : <span className="text-text-muted">—</span> }] : []),
    { key: 'technical', card: 'actions', header: tx('Ayrıntılar'), wrap: true, render: (entry) => <TechnicalDetails items={[
      { label: tx('İşlem kodu'), value: entry.action },
      { label: tx('Hedef türü'), value: entry.targetType },
      { label: tx('Hedef kimliği'), value: entry.targetId },
      { label: tx('Kayıt kimliği'), value: entry.id },
      { label: tx('Yapan kimliği'), value: entry.actorUserId },
      { label: tx('Yapan türü'), value: entry.actorKind },
      { label: tx('Tam zaman'), value: formatDateTime(entry.at) },
      ...auditDetailItems(entry.details),
    ]} /> },
  ]
  return <DataList caption={tx('Denetim kayıtları')} rows={rows} columns={columns} keyExtractor={(entry) => entry.id} emptyMessage={emptyMessage} className={className} />
}
