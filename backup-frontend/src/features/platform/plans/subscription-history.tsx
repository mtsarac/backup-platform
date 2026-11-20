import { tx, useLanguage } from '@/lib/i18n'
import { SortMenu } from '@/components/data/data-list'
import { StatusPill } from '@/components/data/status-pill'
import { Card } from '@/components/ui/card'
import { Fact, Facts, Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { formatDateTime } from '@/lib/format'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { useSubscriptionHistory } from './hooks'
import { formatExactBytes, formatGb } from './storage'
import { assignedByLabel, overrideLabel, sourceLabel } from './subscription-labels'
import type { Subscription } from './types'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

const listConfig = {
  search: true,
  sort: { first: { startsAt: 'desc', plan: 'asc', status: 'asc' }, default: { key: 'startsAt', dir: 'desc' } },
  pageSize: 10,
  pageKey: 'subPage',
} as const

function HistoryCard({ row }: { row: Subscription }) {
  const limit = row.effectiveStorageLimit
  const unknown = tx('Henüz bildirilmedi')
  return <Card as="li" tone="subtle" className="space-y-3">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <h3 className="min-w-0 break-words text-base font-semibold text-text-heading">{row.planName}</h3>
      <StatusPill domain="subscription" status={row.status} />
    </div>
    <Facts>
      <Fact label={tx("Başlangıç")}>{formatDateTime(row.startsAt, unknown)}</Fact>
      <Fact label={tx("Bitiş")}>{row.endsAt ? formatDateTime(row.endsAt, unknown) : tx('Süresiz')}</Fact>
    </Facts>
    <TechnicalDetails items={[
      { label: tx("Etkin limit"), value: formatGb(limit.unlimited ? null : limit.bytes) },
      ...(!limit.unlimited && limit.bytes !== null ? [{ label: tx("Etkin limit (bayt)"), value: formatExactBytes(limit.bytes) }] : []),
      { label: tx("Limit kaynağı"), value: sourceLabel(limit.source) },
      { label: tx("Override"), value: overrideLabel(row.storageLimitOverride) },
      { label: tx("Atayan"), value: assignedByLabel(row) },
      { label: tx("Abonelik kimliği"), value: row.subscriptionId },
    ]} />
  </Card>
}

export function SubscriptionHistory({ tenantId }: { tenantId: string }) {
  useLanguage()
  const state = useListState(listConfig)
  const query = useSubscriptionHistory(tenantId, toServerQuery(state), true)
  const data = query.data
  return <div className="space-y-3">
    <Section title={tx("Abonelik geçmişi")} description={tx("Değiştirilen ve süresi dolan abonelikler korunur.")}>
      <div className="space-y-3">
        <ListToolbar state={state} label={tx("Abonelik geçmişi filtreleri")} search={{ label: tx("Plan ara"), placeholder: tx("Plan adı") }} total={data?.totalCount} />
        <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
          {data && (data.items.length === 0
            ? <ListEmpty message={tx("Abonelik geçmişi yok.")} filtering={state.filtering} onClear={state.clear} />
            : <>
              <SortMenu options={[{ key: 'startsAt', label: tx('Başlangıç') }, { key: 'plan', label: tx('Plan') }, { key: 'status', label: tx('Durum') }]} sort={sortProps(state)} />
              <ul aria-label={tx("Abonelik geçmişi")} className="space-y-3">{data.items.map((row) => <HistoryCard key={row.subscriptionId} row={row} />)}</ul>
            </>)}
        </QueryState>
      </div>
    </Section>
    {data && <ListPager state={state} total={data.totalCount} disabled={query.isFetching} label={tx("Abonelik geçmişi sayfaları")} />}
  </div>
}
