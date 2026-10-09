import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { useSupportSessions } from '@/features/platform/support/hooks'
import { supportStatuses, statusLabels } from '@/features/platform/support/presentation'
import { SupportSessionTable } from '@/features/platform/support/session-table'
import { SupportQueryState } from '@/features/platform/support/support-query-state'
import type { SupportStatus } from '@/features/platform/support/types'
import { selectClass } from '@/features/platform/styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'

export function PlatformSupportSessionsPage() {
  const { page, get, update } = useListParams()
  const rawStatus = get('status')
  const status = supportStatuses.includes(rawStatus as SupportStatus) ? rawStatus as SupportStatus : ''
  const mine = get('mine') === 'true'
  const tenantId = get('tenantId')
  const query = useSupportSessions({ page, pageSize: PLATFORM_PAGE_SIZE, status, mine, tenantId })
  return <div className="space-y-5">
    <header><h1 className="text-2xl font-semibold text-text-heading">Support Sessions</h1><p className="mt-1 text-sm text-text-muted">Tek tenant için süreli, gerekçeli ve izin kapsamlı müşteri erişimi. Yeni bir oturum tenant detayından açılır.</p></header>
    <section aria-label="Destek oturumu filtreleri" className="flex flex-wrap items-end gap-4 rounded-lg border border-border bg-surface p-4">
      <label className="text-sm">Durum<select className={selectClass} value={status} onChange={event => update({ status: event.target.value })}><option value="">Tüm durumlar</option>{supportStatuses.map(value => <option key={value} value={value}>{statusLabels[value]}</option>)}</select></label>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={mine} onChange={event => update({ mine: event.target.checked ? 'true' : 'false' })} />Yalnızca benim oturumlarım</label>
      {tenantId && <div className="flex items-center gap-2 text-sm">Tenant: {tenantId}<Button size="sm" variant="outline" onClick={() => update({ tenantId: '' })}>Tenant filtresini kaldır</Button></div>}
    </section>
    <SupportQueryState isPending={query.isPending} error={query.error}>{query.data && <>
      <SupportSessionTable rows={query.data.items} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ page: next })} />
    </>}</SupportQueryState>
  </div>
}
