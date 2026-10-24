import { tx, useLanguage } from '@/lib/i18n'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { Filters } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { AuditTable } from '@/features/platform/audit-table'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAudit } from '@/features/platform/hooks'
import { bareListClass } from '@/features/platform/list-empty'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'

export function PlatformAuditPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const tenantId = get('tenantId')
  const query = usePlatformAudit({ tenantId, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Audit")} description={tx("Kullanıcı, Agent ve sistem işlemlerinin denetim kayıtları.")} />
    <Filters label={tx("Audit filtreleri")} footer={tenantId && <div className="flex flex-wrap items-center gap-2 text-sm"><span>{tx("Tek bir tenant'a göre filtreleniyor.")}</span><Button variant="outline" size="sm" onClick={() => update({ tenantId: '' })}>{tx("Tenant filtresini kaldır")}</Button></div>}>
      <TenantPicker value={tenantId} onChange={(value) => update({ tenantId: value })} />
    </Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        <Section title={tx("Denetim geçmişi")} description={tx("İşlemi yapan, hedef ve güvenli olay ayrıntıları.")} flush className="overflow-hidden"><AuditTable className={bareListClass} rows={data.items} emptyMessage={tenantId ? tx('Bu tenant için audit kaydı yok.') : tx('Henüz audit kaydı yok.')} /></Section>
        {data.totalCount > 0 && <Pagination label={tx("Audit sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
  </div>
}
