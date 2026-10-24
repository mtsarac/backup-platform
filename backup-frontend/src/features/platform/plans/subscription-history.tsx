import { tx, useLanguage } from '@/lib/i18n'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { Section } from '@/components/ui/section'
import { bareListClass } from '../list-empty'
import { PlatformQueryState } from '../platform-query-state'
import { useListParams } from '../use-list-params'
import { useSubscriptionHistory } from './hooks'
import { StorageLimitText } from './storage-limit-text'
import { assignedByLabel, overrideLabel, sourceLabel } from './subscription-labels'
import type { Subscription } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

const HISTORY_PAGE_SIZE = 10

const columns: Column<Subscription>[] = [
  { key: 'plan', header: 'Plan', wrap: true, render: (row) => <span className="font-medium">{row.planName}</span> },
  { key: 'status', card: 'status', get header() { return tx("Durum") }, render: (row) => <StatusPill domain="subscription" status={row.status} /> },
  { key: 'starts', get header() { return tx("Başlangıç") }, render: (row) => formatDateTime(row.startsAt, tx('Henüz bildirilmedi')) },
  { key: 'ends', get header() { return tx("Bitiş") }, render: (row) => row.endsAt ? formatDateTime(row.endsAt, tx('Henüz bildirilmedi')) : tx('Süresiz') },
  { key: 'limit', get header() { return tx("Etkin limit") }, render: (row) => <div><StorageLimitText bytes={row.effectiveStorageLimit.unlimited ? null : row.effectiveStorageLimit.bytes} /><div className="text-xs text-text-muted">{tx("Kaynak:")}{' '}{sourceLabel(row.effectiveStorageLimit.source)}</div></div> },
  { key: 'override', card: 'hidden', get header() { return tx("Override") }, render: (row) => overrideLabel(row.storageLimitOverride) },
  { key: 'assignedBy', card: 'hidden', get header() { return tx("Atayan") }, wrap: true, render: assignedByLabel },
]

export function SubscriptionHistory({ tenantId }: { tenantId: string }) {
  useLanguage()
  const { page, update } = useListParams('subPage')
  const query = useSubscriptionHistory(tenantId, { page, pageSize: HISTORY_PAGE_SIZE }, true)
  const data = query.data
  return <div className="space-y-3">
    <Section title={tx("Abonelik geçmişi")} description={tx("Değiştirilen ve süresi dolan abonelikler korunur.")} flush className="overflow-hidden">
      <PlatformQueryState isPending={query.isPending} error={query.error}>
        {data && <DataList className={bareListClass} rows={data.items} columns={columns} keyExtractor={(row) => row.subscriptionId} emptyMessage={tx("Abonelik geçmişi yok.")} />}
      </PlatformQueryState>
    </Section>
    {data && data.totalCount > 0 && <Pagination label={tx("Abonelik geçmişi sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ subPage: next })} />}
  </div>
}
