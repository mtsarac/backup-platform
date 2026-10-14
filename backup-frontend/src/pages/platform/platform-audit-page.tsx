import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { Filters } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { AuditTable } from '@/features/platform/audit-table'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAudit } from '@/features/platform/hooks'
import { bareTableClass } from '@/features/platform/list-empty'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'

export function PlatformAuditPage() {
  const { page, get, update } = useListParams()
  const tenantId = get('tenantId')
  const query = usePlatformAudit({ tenantId, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title="Audit" description="Kullanıcı, Agent ve sistem işlemlerinin denetim kayıtları." />
    <Filters label="Audit filtreleri" footer={tenantId && <div className="flex flex-wrap items-center gap-2 text-sm"><span>Tek bir tenant'a göre filtreleniyor.</span><Button variant="outline" size="sm" onClick={() => update({ tenantId: '' })}>Tenant filtresini kaldır</Button></div>}>
      <SearchForm className="sm:col-span-2" label="Tenant kimliğiyle filtrele" value={tenantId} onSearch={(value) => update({ tenantId: value })} placeholder="Tenant kimliği (isteğe bağlı)" />
    </Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        <Section title="Denetim geçmişi" description="İşlemi yapan, hedef ve güvenli olay ayrıntıları." flush className="overflow-hidden"><AuditTable className={bareTableClass} rows={data.items} emptyMessage={tenantId ? 'Bu tenant için audit kaydı yok.' : 'Henüz audit kaydı yok.'} /></Section>
        {data.totalCount > 0 && <Pagination label="Audit sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
  </div>
}
