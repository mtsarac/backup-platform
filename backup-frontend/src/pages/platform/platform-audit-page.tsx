import { tx, useLanguage } from '@/lib/i18n'
import { ListEmpty } from '@/features/lists/list-empty'
import { pastLastPageMessage } from '@/features/lists/list-messages'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { PageHeader } from '@/components/ui/page-header'
import { AuditTable } from '@/features/platform/audit-table'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAudit } from '@/features/platform/hooks'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

const listConfig = {
  search: true,
  filters: { tenantId: 'text' },
  sort: { first: { at: 'desc', action: 'asc', actor: 'asc', tenant: 'asc' }, default: { key: 'at', dir: 'desc' } },
} as const

export function PlatformAuditPage() {
  useLanguage()
  const state = useListState(listConfig)
  const query = usePlatformAudit(toServerQuery(state))
  const data = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Audit")} description={tx("Kim, ne zaman, hangi çalışma alanında ne yaptı. Ham kodlar ve ayrıntı çiftleri “Teknik ayrıntılar” altındadır.")} />
    <ListToolbar state={state} label={tx("Audit filtreleri")} search={{ label: tx('Ara') }} total={data?.totalCount}>
      <TenantPicker value={state.filters.tenantId} onChange={(value) => state.setFilter('tenantId', value)} />
    </ListToolbar>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.items.length === 0
          ? <ListEmpty message={tx('Henüz audit kaydı yok.')} filtering={state.filtering} onClear={state.clear} />
          : <AuditTable rows={data.items} emptyMessage={pastLastPageMessage()} sort={sortProps(state)} />}
        <ListPager state={state} total={data.totalCount} disabled={query.isFetching} label={tx("Audit sayfaları")} />
      </div>}
    </QueryState>
  </div>
}
