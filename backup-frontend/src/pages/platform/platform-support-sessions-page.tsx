import { tx, useLanguage } from '@/lib/i18n'
import { CheckField } from '@/components/ui/check-field'
import { Pagination } from '@/components/data/pagination'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { platformPaths } from '@/features/platform/paths'
import { useSupportSessions } from '@/features/platform/support/hooks'
import { supportStatuses } from '@/features/platform/support/presentation'
import { SupportSessionTable } from '@/features/platform/support/session-table'
import { oneOf, LIST_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { statusLabel } from '@/lib/status'
import { QueryState } from '@/components/data/query-state'
import { errorMessage } from '@/lib/api/failure-messages'

export function PlatformSupportSessionsPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const status = oneOf(get('status'), supportStatuses) ?? ''
  const mine = get('mine') === 'true'
  const tenantId = get('tenantId')
  const query = useSupportSessions({ page, pageSize: LIST_PAGE_SIZE, status, mine, tenantId })
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: 'Platform', to: platformPaths.root }]} title={tx("Support Sessions")} description={tx("Tek tenant için süreli, gerekçeli ve izin kapsamlı müşteri erişimi. Yeni bir oturum tenant detayından açılır.")} />
    <Filters label={tx("Destek oturumu filtreleri")} onClear={status || mine || tenantId ? () => update({ status: '', mine: '', tenantId: '' }) : undefined}>
      <TenantPicker value={tenantId} onChange={value => update({ tenantId: value })} />
      <FilterField label={tx("Durum")}><select aria-label={tx("Durum")} className={filterControlClass} value={status} onChange={event => update({ status: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{supportStatuses.map(value => <option key={value} value={value}>{statusLabel('supportSession', value)}</option>)}</select></FilterField>
      <CheckField className="self-end" label={tx("Yalnızca benim oturumlarım")} checked={mine} onChange={event => update({ mine: event.target.checked ? 'true' : 'false' })} />
    </Filters>
    <QueryState forbiddenMessage={errorMessage} isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
      <SupportSessionTable rows={query.data.items} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ page: next })} />
    </div>}</QueryState>
  </div>
}
