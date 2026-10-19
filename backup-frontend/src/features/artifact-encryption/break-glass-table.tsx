import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { EmptyState } from '@/components/ui/empty-state'
import { artifactDate } from '@/features/artifacts/format'
import { platformPaths } from '@/features/platform/paths'
import { BreakGlassStatusBadge } from './presentation'
import type { BreakGlassRequest } from './types'

export function BreakGlassTable({ rows }: { rows: readonly BreakGlassRequest[] }) {
  useLanguage()
  const columns: Column<BreakGlassRequest>[] = [
    { key: 'tenant', header: tx('Tenant'), wrap: true, render: request => <Link className="font-medium text-primary hover:underline" to={platformPaths.breakGlassDetail(request.id)}>{request.tenantName ?? request.tenantId}</Link> },
    { key: 'status', header: tx('Durum'), render: request => <BreakGlassStatusBadge status={request.status} /> },
    { key: 'artifact', header: tx('Backup'), render: request => <code title={request.artifactId} className="font-mono text-xs">{request.artifactId.slice(0, 8)}…</code> },
    { key: 'requester', header: tx('İsteyen'), wrap: true, render: request => request.requestedByName ?? request.requestedBy },
    { key: 'reason', header: tx('Gerekçe'), wrap: true, render: request => <span className="line-clamp-2" title={request.reason}>{request.reason}</span> },
    { key: 'created', header: tx('Açılış'), render: request => artifactDate(request.createdAt) },
    { key: 'expires', header: tx('Süre sonu'), render: request => artifactDate(request.expiresAt) },
  ]
  if (rows.length === 0) return <div className="rounded-xl border border-border bg-surface"><EmptyState title={tx("Break-glass isteği bulunamadı.")}>{tx("İstek, şifreli bir Backup için gerekçeyle açılır; farklı bir PlatformAdmin onaylayana kadar hiçbir veri çözülmez.")}</EmptyState></div>
  return <DataTable rows={rows} columns={columns} keyExtractor={request => request.id} emptyMessage={tx("Break-glass isteği bulunamadı.")} />
}
