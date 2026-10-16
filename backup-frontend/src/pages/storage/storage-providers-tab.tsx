import { QueryState } from '@/components/data/query-state'
import { ProvidersPanel } from '@/features/storage-connections/providers-panel'
import { useTenantConnections, useTenantProviders } from '@/features/storage-connections/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'

export function StorageProvidersTab() {
  const { tenantId, canAdmin } = useTenant()
  const providers = useTenantProviders()
  const connections = useTenantConnections()
  return <QueryState isPending={providers.isPending || connections.isPending} error={providers.error ?? connections.error} empty={providers.data?.items.length === 0}>
    <ProvidersPanel providers={providers.data?.items ?? []} connections={connections.data?.items ?? []} addPath={canAdmin ? `/tenants/${tenantId}/storage/connections/new` : undefined} />
  </QueryState>
}
