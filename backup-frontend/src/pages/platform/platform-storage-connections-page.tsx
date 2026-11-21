import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { ListEmpty } from '@/features/lists/list-empty'
import { pastLastPageMessage } from '@/features/lists/list-messages'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { Button } from '@/components/ui/button'
import { FilterField } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { CreateManagedTargetDialog } from '@/features/storage-connections/create-dialogs'
import { usePlatformConnectionOps, usePlatformConnections, usePlatformProviders } from '@/features/storage-connections/hooks'
import { platformLockMeaning } from '@/features/storage-connections/labels'
import { PlatformConnectionTable } from '@/features/storage-connections/platform-connection-table'
import { connectionOwnerships, type ConnectionOwnership, type PlatformConnectionFilters } from '@/features/storage-connections/types'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { Select } from '@/components/ui/text-input'

const listConfig = {
  search: true,
  filters: { ownership: [...connectionOwnerships, 'all'], providerType: 'text', enabled: ['true', 'false'], tenantId: 'text' },
  sort: { first: { name: 'asc', provider: 'asc', ownership: 'asc', enabled: 'asc', tenant: 'asc', createdAt: 'desc' }, default: { key: 'createdAt', dir: 'desc' } },
} as const

/** `ownership` absent = the default view (PlatformManaged); `all` is the explicit unfiltered view. */
function ownershipFilter(value: string): ConnectionOwnership | undefined {
  if (value === 'all') return undefined
  return connectionOwnerships.find((ownership) => ownership === value) ?? 'PlatformManaged'
}

export function PlatformStorageConnectionsPage() {
  useLanguage()
  const state = useListState(listConfig)
  const { ownership, providerType, enabled, tenantId } = state.filters
  const filters: PlatformConnectionFilters = {
    ...toServerQuery(state),
    ownership: ownershipFilter(ownership),
    enabled: enabled === 'true' ? true : enabled === 'false' ? false : undefined,
  }
  const query = usePlatformConnections(filters)
  const providers = usePlatformProviders()
  const ops = usePlatformConnectionOps()
  const [adding, setAdding] = useState(false)
  const data = query.data

  return <div className="space-y-6">
    <div className="flex flex-wrap justify-end gap-2">
      <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin motion-reduce:animate-none' : ''}`} aria-hidden="true" />{tx("Yenile")}</Button>
      <Button onClick={() => setAdding(true)}><Plus className="size-4" aria-hidden="true" />{tx("Yönetilen hedef ekle")}</Button>
    </div>
    <Notice tone="info">{tx(platformLockMeaning)}</Notice>
    <ListToolbar state={state} label={tx("Depolama hedefi filtreleri")} total={data?.totalCount}
      search={{ label: tx("Depolama hedefi ara"), placeholder: tx("Ad, sağlayıcı veya çalışma alanı") }}
      footer={tenantId ? <span className="text-sm text-text-muted">{tx("Tek bir çalışma alanına göre filtrelendi.")}</span> : undefined}>
      <FilterField label={tx("Sahiplik")}><Select className="text-sm" value={ownershipFilter(ownership) ?? 'all'} onChange={(event) => state.setFilter('ownership', event.target.value === 'PlatformManaged' ? '' : event.target.value)}>
        <option value="PlatformManaged">{tx("Platform yönetimli")}</option>
        <option value="TenantManaged">{tx("Tenant yönetimli")}</option>
        <option value="all">{tx("Tümü")}</option>
      </Select></FilterField>
      <FilterField label={tx("Depolama sağlayıcısı")}><Select className="text-sm" value={providerType} onChange={(event) => state.setFilter('providerType', event.target.value)}>
        <option value="">{tx("Tümü")}</option>
        {providers.data?.items.map((provider) => <option key={provider.providerType} value={provider.providerType}>{tx(provider.displayName)}</option>)}
      </Select></FilterField>
      <FilterField label={tx("Durum")}><Select className="text-sm" value={enabled} onChange={(event) => state.setFilter('enabled', event.target.value)}>
        <option value="">{tx("Tümü")}</option>
        <option value="true">{tx("Etkin")}</option>
        <option value="false">{tx("Devre dışı")}</option>
      </Select></FilterField>
    </ListToolbar>

    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <>
        {data.totalCount === 0
          ? <ListEmpty message={tx("Kriterlere uygun depolama hedefi bulunamadı.")} filtering={state.filtering} onClear={state.clear} />
          : <PlatformConnectionTable rows={data.items} providers={providers.data?.items ?? []} ops={ops} showTenant sort={sortProps(state)} emptyMessage={pastLastPageMessage()} />}
        <ListPager state={state} total={data.totalCount} disabled={query.isFetching} label={tx("Depolama hedefi sayfaları")} />
      </>}
    </QueryState>

    {adding && <CreateManagedTargetDialog onClose={() => setAdding(false)} />}
  </div>
}
