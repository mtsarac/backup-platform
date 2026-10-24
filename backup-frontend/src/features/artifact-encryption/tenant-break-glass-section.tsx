import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { Section } from '@/components/ui/section'
import { linkClass } from '@/components/ui/form-styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { useTenantBreakGlass } from './hooks'
import type { TenantBreakGlassEvent } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

/** What the tenant is owed under break-glass transparency: who asked for which Backup, why, who approved, and whether it was used. */
export function TenantBreakGlassSection({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, update } = useListParams('breakGlassPage')
  const query = useTenantBreakGlass(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  const columns: Column<TenantBreakGlassEvent>[] = [
    { key: 'status', card: 'status', header: tx('Durum'), render: event => <StatusPill domain="breakGlass" status={event.status} /> },
    { key: 'artifact', header: tx('Backup'), render: event => <Link className={linkClass} to={`/tenants/${tenantId}/artifacts/${event.artifactId}`}>{event.artifactId.slice(0, 8)}…</Link> },
    { key: 'reason', header: tx('Gerekçe'), wrap: true, render: event => event.reason },
    { key: 'requester', header: tx('İsteyen'), wrap: true, render: event => event.requestedByName ?? tx('Bilinmiyor') },
    { key: 'approver', card: 'hidden', header: tx('Karar veren'), wrap: true, render: event => event.decidedByName ? <>{event.decidedByName}<span className="block text-sm text-text-muted">{formatDateTime(event.decidedAt)}</span></> : '—' },
    { key: 'used', card: 'hidden', header: tx('Kullanıldı'), render: event => event.consumedAt ? formatDateTime(event.consumedAt) : tx('Hayır') },
    { key: 'created', card: 'hidden', header: tx('Açılış'), render: event => formatDateTime(event.createdAt) },
    { key: 'expires', header: tx('Süre sonu'), render: event => formatDateTime(event.expiresAt) },
  ]
  return <Section title={tx("Break-glass erişimleri")} description={tx("Platform yöneticilerinin bu tenant'ın şifreli Backup'larını çözme istekleri. Her istek gerekçeli, çift onaylı, süreli ve tek kullanımlıktır. Bu liste salt okunurdur.")}>
    <QueryState isPending={query.isPending} error={query.error}>
      {query.data && <div className="space-y-4">
        <DataList rows={query.data.items} columns={columns} keyExtractor={event => event.id} emptyMessage={tx("Bu tenant için break-glass isteği yok.")} />
        {query.data.totalCount > 0 && <Pagination label={tx("Break-glass sayfaları")} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ breakGlassPage: next })} />}
      </div>}
    </QueryState>
  </Section>
}
