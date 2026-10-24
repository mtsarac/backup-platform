import { tx, useLanguage } from '@/lib/i18n'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { EmptyState } from '@/components/ui/empty-state'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { formatDateTime } from '@/lib/format'
import { PlatformQueryState } from '../platform-query-state'
import { useListParams } from '../use-list-params'
import { useSubscriptionHistory } from './hooks'
import { formatBytes, formatGb } from './storage'
import { assignedByLabel, overrideLabel, sourceLabel } from './subscription-labels'
import type { Subscription } from './types'

const HISTORY_PAGE_SIZE = 10

function HistoryCard({ row }: { row: Subscription }) {
  const limit = row.effectiveStorageLimit
  const unknown = tx('Henüz bildirilmedi')
  return <li className="space-y-3 rounded-xl border border-border bg-surface-2 p-4">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <h3 className="min-w-0 break-words text-base font-semibold text-text-heading">{row.planName}</h3>
      <StatusPill domain="subscription" status={row.status} />
    </div>
    <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-2">
      <div><dt className="text-sm text-text-muted">{tx("Başlangıç")}</dt><dd className="text-[0.9375rem] text-text">{formatDateTime(row.startsAt, unknown)}</dd></div>
      <div><dt className="text-sm text-text-muted">{tx("Bitiş")}</dt><dd className="text-[0.9375rem] text-text">{row.endsAt ? formatDateTime(row.endsAt, unknown) : tx('Süresiz')}</dd></div>
    </dl>
    <TechnicalDetails items={[
      { label: tx("Etkin limit"), value: formatGb(limit.unlimited ? null : limit.bytes) },
      ...(!limit.unlimited && limit.bytes !== null ? [{ label: tx("Etkin limit (bayt)"), value: formatBytes(limit.bytes) }] : []),
      { label: tx("Limit kaynağı"), value: sourceLabel(limit.source) },
      { label: tx("Override"), value: overrideLabel(row.storageLimitOverride) },
      { label: tx("Atayan"), value: assignedByLabel(row) },
      { label: tx("Abonelik kimliği"), value: row.subscriptionId },
    ]} />
  </li>
}

export function SubscriptionHistory({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, update } = useListParams('subPage')
  const query = useSubscriptionHistory(tenantId, { page, pageSize: HISTORY_PAGE_SIZE }, true)
  const data = query.data
  return <div className="space-y-3">
    <Section title={tx("Abonelik geçmişi")} description={tx("Değiştirilen ve süresi dolan abonelikler korunur.")}>
      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {data && (data.items.length === 0
          ? <EmptyState message={tx("Abonelik geçmişi yok.")} />
          : <ul aria-label={tx("Abonelik geçmişi")} className="space-y-3">{data.items.map((row) => <HistoryCard key={row.subscriptionId} row={row} />)}</ul>)}
      </PlatformQueryState>
    </Section>
    {data && data.totalCount > 0 && <Pagination label={tx("Abonelik geçmişi sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ subPage: next })} />}
  </div>
}
