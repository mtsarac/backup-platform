import { useState } from 'react'
import { Plus, RefreshCw } from 'lucide-react'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/ui/page-header'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { selectClass } from '@/features/platform/styles'
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
    <PageHeader title="Depolama hedefleri" crumbs={[{ label: 'Platform', to: '/platform' }, { label: 'Storage Targets' }]}
      description="Tenant'ların backup hedefleri. Varsayılan görünüm platform yönetimli hedefleri gösterir."
      actions={<>
        <Button variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw className={`size-4 ${query.isFetching ? 'animate-spin' : ''}`} aria-hidden="true" />Yenile</Button>
        <Button onClick={() => setAdding(true)}><Plus className="size-4" aria-hidden="true" />Managed target ekle</Button>
      </>} />

    <details className="rounded-xl border border-border bg-surface p-4 text-sm text-text-muted shadow-card">
      <summary className="cursor-pointer font-medium text-text-heading">Platform kilidi ne yapar?</summary>
      <p className="mt-2">Platform yalnızca yönetim kilidini koyar veya kaldırır; tenant'ın etkin / devre dışı tercihini değiştirmez. Durum filtresi bu iki kontrolün birleşimi olan etkin kullanılabilirliğe göre çalışır.</p>
      <ul className="mt-2 list-inside list-disc space-y-1">{disableEffects.map((effect) => <li key={effect}>{effect}</li>)}</ul>
    </details>

    <section aria-label="Depolama hedefi filtreleri" className="flex flex-wrap items-end gap-3 rounded-xl border border-border bg-surface p-4 shadow-card">
      <SearchForm label="Tenant ara" value={search} onSearch={(value) => update({ search: value })} placeholder="Tenant adı…" className="min-w-full sm:min-w-60" />
      <label className="text-sm text-text-muted">Sahiplik
        <select className={selectClass} value={ownershipFilter(ownership) ?? 'all'} onChange={(event) => update({ ownership: event.target.value === 'PlatformManaged' ? undefined : event.target.value })}>
          <option value="PlatformManaged">Platform yönetimli</option>
          <option value="TenantManaged">Tenant yönetimli</option>
          <option value="all">Tümü</option>
        </select>
      </label>
      <label className="text-sm text-text-muted">Storage Provider
        <select className={selectClass} value={providerType} onChange={(event) => update({ providerType: event.target.value })}>
          <option value="">Tümü</option>
          {providers.data?.items.map((provider) => <option key={provider.providerType} value={provider.providerType}>{provider.displayName}</option>)}
        </select>
      </label>
      <label className="text-sm text-text-muted">Durum
        <select className={selectClass} value={enabled} onChange={(event) => update({ enabled: event.target.value })}>
          <option value="">Tümü</option>
          <option value="true">Etkin</option>
          <option value="false">Devre dışı</option>
        </select>
      </label>
    </section>
    {tenantId && <p className="flex flex-wrap items-center gap-2 text-sm text-text-muted">Tek bir tenant'a göre filtrelendi. <Button variant="outline" size="sm" onClick={() => update({ tenantId: undefined })}>Tenant filtresini kaldır</Button></p>}

    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <PlatformConnectionTable rows={data.items} providers={providers.data?.items ?? []} ops={ops} showTenant emptyMessage="Kriterlere uygun depolama hedefi bulunamadı." />
        {data.totalCount > 0 && <Pagination label="Depolama hedefi sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>

    {adding && <CreateManagedTargetDialog onClose={() => setAdding(false)} />}
  </div>
}
