import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { EmptyState } from '@/components/ui/empty-state'
import { platformPaths } from '@/features/platform/paths'
import type { BreakGlassRequest } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

export function BreakGlassTable({ rows }: { rows: readonly BreakGlassRequest[] }) {
  useLanguage()
  const columns: Column<BreakGlassRequest>[] = [
    { key: 'tenant', header: tx('Tenant'), wrap: true, render: request => <Link className="font-medium text-primary hover:underline" to={platformPaths.breakGlassDetail(request.id)}>{request.tenantName ?? request.tenantId}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: request => <StatusPill domain="breakGlass" status={request.status} /> },
    { key: 'artifact', card: 'hidden', header: tx('Backup'), render: request => <code title={request.artifactId} className="font-mono text-xs">{request.artifactId.slice(0, 8)}…</code> },
    { key: 'requester', header: tx('İsteyen'), wrap: true, render: request => request.requestedByName ?? request.requestedBy },
    { key: 'reason', header: tx('Gerekçe'), wrap: true, render: request => <span className="line-clamp-2" title={request.reason}>{request.reason}</span> },
    { key: 'created', card: 'hidden', header: tx('Açılış'), render: request => formatDateTime(request.createdAt) },
    { key: 'expires', header: tx('Süre sonu'), render: request => formatDateTime(request.expiresAt) },
  ]
  if (rows.length === 0) return <div className="rounded-xl border border-border bg-surface"><EmptyState message={tx("Break-glass isteği bulunamadı.")} /></div>
  return <DataList rows={rows} columns={columns} keyExtractor={request => request.id} emptyMessage={tx("Break-glass isteği bulunamadı.")} />
}
