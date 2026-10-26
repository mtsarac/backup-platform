import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { EmptyState } from '@/components/ui/empty-state'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { linkClass } from '@/components/ui/form-styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { formatDateTime } from '@/lib/format'
import { useTenantBreakGlass } from './hooks'

export function TenantBreakGlassSection({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, update } = useListParams('breakGlassPage')
  const query = useTenantBreakGlass(tenantId, { page, pageSize: PLATFORM_PAGE_SIZE })
  return <Section title={tx('Acil destek erişimi')} description={tx('Platform çalışanlarının şifreli yedeklerinizi açma istekleri. Her istek gerekçeli, iki kişi tarafından onaylanan, süreli ve tek kullanımlıktır.')}>
    <QueryState isPending={query.isPending} error={query.error}>
      {query.data && <div className="space-y-4">
        {query.data.items.length === 0 ? <EmptyState message={tx('Henüz acil destek erişimi isteği yok.')} /> : <ul aria-label={tx('Acil destek erişimi geçmişi')} className="space-y-3">
          {query.data.items.map(event => <li key={event.id} className="space-y-4 rounded-xl border border-border p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 className="break-words font-semibold">{event.requestedByName ?? tx('Bilinmiyor')}</h3>
              <StatusPill domain="breakGlass" status={event.status} />
            </div>
            <p className="break-words">{event.reason}</p>
            <Facts>
              <Fact label={tx('İstek zamanı')}><RelativeTime value={event.createdAt} /></Fact>
              <Fact label={tx('Karar veren')}>{event.decidedByName ?? tx('Henüz karar verilmedi')}</Fact>
              <Fact label={tx('Kullanıldı')}>{event.consumedAt ? <RelativeTime value={event.consumedAt} /> : tx('Hayır')}</Fact>
            </Facts>
            <Link className={`${linkClass} inline-flex min-h-11 items-center`} to={`/tenants/${tenantId}/artifacts/${event.artifactId}`}>{tx('Yedek dosyasını görüntüle')}</Link>
            <TechnicalDetails items={[
              { label: tx('İstek kimliği'), value: event.id },
              { label: tx('Yedek dosyası kimliği'), value: event.artifactId },
              { label: tx('Ham durum'), value: event.status },
              { label: tx('Açılış'), value: formatDateTime(event.createdAt) },
              { label: tx('Süre sonu'), value: formatDateTime(event.expiresAt) },
              { label: tx('Karar zamanı'), value: formatDateTime(event.decidedAt) },
              { label: tx('Kullanılma zamanı'), value: formatDateTime(event.consumedAt) },
            ]} />
          </li>)}
        </ul>}
        {query.data.totalCount > 0 && <Pagination label={tx('Acil destek erişimi sayfaları')} page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ breakGlassPage: next })} />}
      </div>}
    </QueryState>
  </Section>
}
