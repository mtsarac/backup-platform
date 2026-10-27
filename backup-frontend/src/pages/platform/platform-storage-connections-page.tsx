import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { SearchForm } from '@/features/platform/search-form'
import { LIST_PAGE_SIZE, oneOf, useListParams } from '@/features/platform/use-list-params'
import { CreateManagedTargetDialog } from '@/features/storage-connections/create-dialogs'
import { usePlatformConnectionOps, usePlatformConnections, usePlatformProviders } from '@/features/storage-connections/hooks'
import { platformLockMeaning } from '@/features/storage-connections/labels'
import { PlatformConnectionTable } from '@/features/storage-connections/platform-connection-table'
import { connectionOwnerships, type ConnectionOwnership, type PlatformConnectionFilters } from '@/features/storage-connections/types'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'

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
  const enabled = oneOf(get('enabled'), ['true', 'false'] as const) ?? ''
  const search = get('search')
  const tenantId = get('tenantId')
  const filters: PlatformConnectionFilters = {
    ownership: ownershipFilter(ownership),
    providerType: providerType || undefined,
    tenantId: tenantId || undefined,
    enabled: enabled === 'true' ? true : enabled === 'false' ? false : undefined,
    search: search || undefined,
    page,
    pageSize: LIST_PAGE_SIZE,
  }
  const query = usePlatformConnections(filters)
  const providers = usePlatformProviders()
  const ops = usePlatformConnectionOps()
  const [adding, setAdding] = useState(false)
  const data = query.data

  const filtered = Boolean(search || tenantId || providerType || enabled || ownership)
  return <div className="space-y-6">
    <div className="flex flex-wrap justify-end gap-2">
      <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} aria-hidden="true" />{tx("Yenile")}</Button>
      <Button onClick={() => setAdding(true)}><Plus className="size-4" aria-hidden="true" />{tx("Yönetilen hedef ekle")}</Button>
    </div>
    <Notice tone="info">{tx(platformLockMeaning)}</Notice>
    <Filters label={tx("Depolama hedefi filtreleri")} onClear={filtered ? () => update({ search: '', ownership: '', providerType: '', enabled: '', tenantId: '' }) : undefined}
      footer={tenantId ? <span className="text-sm text-text-muted">{tx("Tek bir çalışma alanına göre filtrelendi.")}</span> : undefined}>
      <SearchForm className="sm:col-span-2" label={tx("Çalışma alanı ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Çalışma alanı adı")} />
      <FilterField label={tx("Sahiplik")}><select className={filterControlClass} value={ownershipFilter(ownership) ?? 'all'} onChange={(event) => update({ ownership: event.target.value === 'PlatformManaged' ? undefined : event.target.value })}>
        <option value="PlatformManaged">{tx("Platform yönetimli")}</option>
        <option value="TenantManaged">{tx("Tenant yönetimli")}</option>
        <option value="all">{tx("Tümü")}</option>
      </select></FilterField>
      <FilterField label={tx("Depolama sağlayıcısı")}><select className={filterControlClass} value={providerType} onChange={(event) => update({ providerType: event.target.value })}>
        <option value="">{tx("Tümü")}</option>
        {providers.data?.items.map((provider) => <option key={provider.providerType} value={provider.providerType}>{tx(provider.displayName)}</option>)}
      </select></FilterField>
      <FilterField label={tx("Durum")}><select className={filterControlClass} value={enabled} onChange={(event) => update({ enabled: event.target.value })}>
        <option value="">{tx("Tümü")}</option>
        <option value="true">{tx("Etkin")}</option>
        <option value="false">{tx("Devre dışı")}</option>
      </select></FilterField>
    </Filters>

    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <>
        <PlatformConnectionTable rows={data.items} providers={providers.data?.items ?? []} ops={ops} showTenant emptyMessage={tx("Kriterlere uygun depolama hedefi bulunamadı.")} />
        {data.totalCount > 0 && <Pagination label={tx("Depolama hedefi sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </QueryState>

    {adding && <CreateManagedTargetDialog onClose={() => setAdding(false)} />}
  </div>
}
