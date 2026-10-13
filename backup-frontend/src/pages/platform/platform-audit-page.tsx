import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { AuditTable } from '@/features/platform/audit-table'
import { usePlatformAudit } from '@/features/platform/hooks'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'

export function PlatformAuditPage() {
  const { page, get, update } = useListParams()
  const tenantId = get('tenantId')
  const query = usePlatformAudit({ tenantId, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  return <div className="space-y-4">
    <div><h1 className="text-2xl font-semibold text-text-heading">Audit</h1><p className="mt-1 text-sm text-text-muted">Kullanıcı, agent ve sistem işlemlerinin denetim kayıtları.</p></div>
    {tenantId && <div className="flex flex-wrap items-center gap-2 rounded-lg border border-border bg-surface p-3 text-sm"><span>Tek bir tenant'a göre filtreleniyor.</span><Button variant="outline" size="sm" onClick={() => update({ tenantId: '' })}>Tenant filtresini kaldır</Button></div>}
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <AuditTable rows={data.items} emptyMessage={tenantId ? 'Bu tenant için audit kaydı yok.' : 'Henüz audit kaydı yok.'} />
        {data.totalCount > 0 && <Pagination label="Audit sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>
  </div>
}
