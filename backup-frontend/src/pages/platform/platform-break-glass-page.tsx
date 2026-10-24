import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Pagination } from '@/components/data/pagination'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { BreakGlassRequestDialog } from '@/features/artifact-encryption/break-glass-request-dialog'
import { BreakGlassTable } from '@/features/artifact-encryption/break-glass-table'
import { useBreakGlassRequests } from '@/features/artifact-encryption/hooks'
import { breakGlassStatuses, type BreakGlassStatus } from '@/features/artifact-encryption/types'
import { TenantPicker } from '@/features/platform/tenant-picker'
import { platformCrumb } from '@/features/platform/crumbs'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { statusLabel } from '@/lib/status'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/components/auth/access-denied'

export function PlatformBreakGlassPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const rawStatus = get('status')
  const status = breakGlassStatuses.includes(rawStatus as BreakGlassStatus) ? rawStatus as BreakGlassStatus : ''
  const mine = get('mine') === 'true'
  const tenantId = get('tenantId')
  // A deep link from support or artifact views arrives with the target already filled in.
  const prefillTenant = get('requestTenantId')
  const prefillArtifact = get('requestArtifactId')
  const [dialogOpen, setDialogOpen] = useState(Boolean(prefillTenant && prefillArtifact))
  const query = useBreakGlassRequests({ page, pageSize: PLATFORM_PAGE_SIZE, status, mine, tenantId })
  function closeDialog() {
    setDialogOpen(false)
    update({ requestTenantId: '', requestArtifactId: '' })
  }
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Acil destek erişimi")} description={tx("Şifreli yedek dosyalarını tenant anahtarıyla çözme istekleri. İki kişi onayı, süre sınırı ve tek kullanım zorunludur; platform kurtarma anahtarı çevrimiçi kullanılmaz.")}
      actions={<Button onClick={() => setDialogOpen(true)}>{tx("Yeni istek")}</Button>} />
    <Filters label={tx("Acil destek erişimi filtreleri")} footer={tenantId ? <div className="flex flex-wrap items-center gap-2 text-sm"><span>{tx("Tek bir tenant'a göre filtreleniyor.")}</span><Button size="sm" variant="outline" onClick={() => update({ tenantId: '' })}>{tx("Tenant filtresini kaldır")}</Button></div> : undefined}>
      <TenantPicker value={tenantId} onChange={value => update({ tenantId: value })} />
      <FilterField label={tx("Durum")}><select aria-label={tx("Durum")} className={filterControlClass} value={status} onChange={event => update({ status: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{breakGlassStatuses.map(value => <option key={value} value={value}>{statusLabel('breakGlass', value)}</option>)}</select></FilterField>
      <label className="flex min-h-11 items-center gap-2 self-end text-sm font-medium text-text-heading"><input type="checkbox" className="size-4 accent-primary" checked={mine} onChange={event => update({ mine: event.target.checked ? 'true' : 'false' })} />{tx("Yalnızca benim isteklerim")}</label>
    </Filters>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>{query.data && <div className="space-y-4">
      <BreakGlassTable rows={query.data.items} />
      <Pagination page={page} pageSize={query.data.pageSize} totalCount={query.data.totalCount} disabled={query.isFetching} onPageChange={next => update({ page: next })} />
    </div>}</QueryState>
    {dialogOpen && <BreakGlassRequestDialog initialTenantId={prefillTenant} initialArtifactId={prefillArtifact} onClose={closeDialog} />}
  </div>
}
