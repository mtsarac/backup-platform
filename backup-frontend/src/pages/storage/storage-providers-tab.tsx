import { QueryState } from '@/components/data/query-state'
import { EmptyState } from '@/components/ui/empty-state'
import { Button } from '@/components/ui/button'
import { tx } from '@/lib/i18n'
import { ProvidersPanel } from '@/features/storage-connections/providers-panel'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'

export function StorageProvidersTab() {
  const { tenantId, canAdmin } = useTenant()
  const providers = useTenantProviders()
  const connections = useTenantConnections()
  return <QueryState isPending={providers.isPending || connections.isPending} error={providers.error ?? connections.error} empty={providers.data?.items.length === 0} emptyState={<EmptyState message={tx("Henüz kullanılabilir depolama türü yok.")} action={<Button disabled={providers.isFetching} onClick={() => void providers.refetch()}>{tx('Yenile')}</Button>} />}>
    <ProvidersPanel providers={providers.data?.items ?? []} connections={connections.data?.items ?? []} addPath={canAdmin ? `/tenants/${tenantId}/storage/connections/new` : undefined} />
  </QueryState>
}
