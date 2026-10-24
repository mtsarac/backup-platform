import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/ui/page-header'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { selectClass } from '@/components/ui/form-styles'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { CreateManagedTargetDialog } from '@/features/storage-connections/create-dialogs'
import { usePlatformConnectionOps, usePlatformConnections, usePlatformProviders } from '@/features/storage-connections/hooks'
import { disableEffects } from '@/features/storage-connections/labels'
import { PlatformConnectionTable } from '@/features/storage-connections/platform-connection-table'
import { connectionOwnerships, type ConnectionOwnership, type PlatformConnectionFilters } from '@/features/storage-connections/types'

/** `ownership` absent = the default view (PlatformManaged); `all` is the explicit unfiltered view. */
function ownershipFilter(value: string): ConnectionOwnership | undefined {
  if (value === 'all') return undefined
  return connectionOwnerships.find((ownership) => ownership === value) ?? 'PlatformManaged'
}

export function PlatformStorageConnectionsPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const ownership = get('ownership')
  const providerType = get('providerType')
  const enabled = get('enabled')
  const search = get('search')
  const tenantId = get('tenantId')
  const filters: PlatformConnectionFilters = {
    ownership: ownershipFilter(ownership),
    providerType: providerType || undefined,
    tenantId: tenantId || undefined,
    enabled: enabled === 'true' ? true : enabled === 'false' ? false : undefined,
    search: search || undefined,
    page,
    pageSize: PLATFORM_PAGE_SIZE,
  }
  const query = usePlatformConnections(filters)
  const providers = usePlatformProviders()
  const ops = usePlatformConnectionOps()
  const [adding, setAdding] = useState(false)
  const data = query.data

  return <div className="space-y-6">
    <PageHeader title={tx("Depolama hedefleri")} crumbs={[{ label: 'Platform', to: '/platform' }, { label: tx('Storage Targets') }]}
      description={tx("Tenant'ların backup hedefleri. Varsayılan görünüm platform yönetimli hedefleri gösterir.")}
      actions={<>
        <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} aria-hidden="true" />{tx("Yenile")}</Button>
        <Button onClick={() => setAdding(true)}><Plus className="size-4" aria-hidden="true" />{tx("Managed target ekle")}</Button>
      </>} />

    <details className="rounded-xl border border-border bg-surface p-4 text-sm text-text-muted shadow-card">
      <summary className="cursor-pointer font-medium text-text-heading">{tx("Platform kilidi ne yapar?")}</summary>
      <p className="mt-2">{tx("Platform yalnızca yönetim kilidini koyar veya kaldırır; tenant'ın etkin / devre dışı tercihini değiştirmez. Durum filtresi bu iki kontrolün birleşimi olan etkin kullanılabilirliğe göre çalışır.")}</p>
      <ul className="mt-2 list-inside list-disc space-y-1">{disableEffects.map((effect) => <li key={effect}>{tx(effect)}</li>)}</ul>
    </details>

    <section aria-label={tx("Depolama hedefi filtreleri")} className="flex flex-wrap items-end gap-3 rounded-xl border border-border bg-surface p-4 shadow-card">
      <SearchForm label={tx("Tenant ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Tenant adı…")} className="min-w-full sm:min-w-60" />
      <label className="text-sm text-text-muted">{tx("Sahiplik")}<select className={selectClass} value={ownershipFilter(ownership) ?? 'all'} onChange={(event) => update({ ownership: event.target.value === 'PlatformManaged' ? undefined : event.target.value })}>
          <option value="PlatformManaged">{tx("Platform yönetimli")}</option>
          <option value="TenantManaged">{tx("Tenant yönetimli")}</option>
          <option value="all">{tx("Tümü")}</option>
        </select>
      </label>
      <label className="text-sm text-text-muted">{tx("Storage Provider")}<select className={selectClass} value={providerType} onChange={(event) => update({ providerType: event.target.value })}>
          <option value="">{tx("Tümü")}</option>
          {providers.data?.items.map((provider) => <option key={provider.providerType} value={provider.providerType}>{tx(provider.displayName)}</option>)}
        </select>
      </label>
      <label className="text-sm text-text-muted">{tx("Durum")}<select className={selectClass} value={enabled} onChange={(event) => update({ enabled: event.target.value })}>
          <option value="">{tx("Tümü")}</option>
          <option value="true">{tx("Etkin")}</option>
          <option value="false">{tx("Devre dışı")}</option>
        </select>
      </label>
    </section>
    {tenantId && <p className="flex flex-wrap items-center gap-2 text-sm text-text-muted">{tx("Tek bir tenant'a göre filtrelendi.")}{' '}<Button variant="outline" size="sm" onClick={() => update({ tenantId: undefined })}>{tx("Tenant filtresini kaldır")}</Button></p>}

    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <PlatformConnectionTable rows={data.items} providers={providers.data?.items ?? []} ops={ops} showTenant emptyMessage={tx("Kriterlere uygun depolama hedefi bulunamadı.")} />
        {data.totalCount > 0 && <Pagination label={tx("Depolama hedefi sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>

    {adding && <CreateManagedTargetDialog onClose={() => setAdding(false)} />}
  </div>
}
