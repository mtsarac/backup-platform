import { tx, useLanguage } from '@/lib/i18n'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { platformPaths } from '@/features/platform/paths'
import { useSupportSessions } from '@/features/platform/support/hooks'
import { supportStatuses } from '@/features/platform/support/presentation'
import { SupportSessionTable } from '@/features/platform/support/session-table'
import { SupportQueryState } from '@/features/platform/support/support-query-state'
import type { SupportStatus } from '@/features/platform/support/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { statusLabel } from '@/lib/status'

export function PlatformSupportSessionsPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const rawStatus = get('status')
  const status = supportStatuses.includes(rawStatus as SupportStatus) ? rawStatus as SupportStatus : ''
  const mine = get('mine') === 'true'
  const tenantId = get('tenantId')
  const query = useSupportSessions({ page, pageSize: PLATFORM_PAGE_SIZE, status, mine, tenantId })
  return <div className="space-y-6">
    <PageHeader crumbs={[{ label: 'Platform', to: platformPaths.root }]} title={tx("Support Sessions")} description={tx("Tek tenant için süreli, gerekçeli ve izin kapsamlı müşteri erişimi. Yeni bir oturum tenant detayından açılır.")} />
    <Filters label={tx("Destek oturumu filtreleri")} footer={tenantId ? <>
      <span className="text-sm text-text-muted">{tx("Tenant filtresi:")}{' '}<code className="break-all rounded bg-surface-2 px-1.5 py-0.5 font-mono text-xs text-text">{tenantId}</code></span>
      <Button size="sm" variant="outline" onClick={() => update({ tenantId: '' })}>{tx("Tenant filtresini kaldır")}</Button>
    </> : undefined}>
      <FilterField label={tx("Durum")}><select aria-label={tx("Durum")} className={filterControlClass} value={status} onChange={event => update({ status: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{supportStatuses.map(value => <option key={value} value={value}>{statusLabel('supportSession', value)}</option>)}</select></FilterField>
      <label className="flex min-h-10 items-center gap-2 self-end text-sm font-medium text-text-heading"><input type="checkbox" className="size-4 accent-primary" checked={mine} onChange={event => update({ mine: event.target.checked ? 'true' : 'false' })} />{tx("Yalnızca benim oturumlarım")}</label>
    </Filters>
    <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
      <SupportSessionTable rows={query.data.items} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ page: next })} />
    </div>}</SupportQueryState>
  </div>
}
