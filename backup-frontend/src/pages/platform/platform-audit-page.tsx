import { tx, useLanguage } from '@/lib/i18n'
import { Pagination } from '@/components/data/pagination'
import { Filters } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { AuditTable } from '@/features/platform/audit-table'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAudit } from '@/features/platform/hooks'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { LIST_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

export function PlatformAuditPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const tenantId = get('tenantId')
  const query = usePlatformAudit({ tenantId, page, pageSize: LIST_PAGE_SIZE })
  const data = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Audit")} description={tx("Kim, ne zaman, hangi çalışma alanında ne yaptı. Ham kodlar ve ayrıntı çiftleri “Teknik ayrıntılar” altındadır.")} />
    <Filters label={tx("Audit filtreleri")} onClear={tenantId ? () => update({ tenantId: '' }) : undefined}>
      <TenantPicker value={tenantId} onChange={(value) => update({ tenantId: value })} />
    </Filters>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        <AuditTable rows={data.items} emptyMessage={tenantId ? tx('Bu tenant için audit kaydı yok.') : tx('Henüz audit kaydı yok.')} />
        {data.totalCount > 0 && <Pagination label={tx("Audit sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </QueryState>
  </div>
}
