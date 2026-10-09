import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { QueryState } from '@/components/data/query-state'
import { Fact } from '@/features/storage/quota-summary'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ConnectionActions } from './connection-actions'
import { CapabilityChips, ConnectionState, Footprint, LastTest, OwnershipBadge, SecretStatus } from './connection-parts'
import { CreateConnectionDialog } from './create-dialogs'
import { type ConnectionOps, useTenantConnectionOps, useTenantConnections, useTenantProviders } from './hooks'
import { configurationSummary } from './labels'
import { ProvidersPanel } from './providers-panel'
import type { ConnectionView, ProviderDescriptor } from './types'

function ConnectionCard({ connection, descriptor, ops }: { connection: ConnectionView; descriptor?: ProviderDescriptor; ops: ConnectionOps }) {
  const summary = configurationSummary(connection, descriptor)
  return <li><article aria-label={connection.name} className="space-y-3 rounded-lg border border-border bg-surface-2 p-4">
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div className="min-w-0"><h3 className="break-words font-medium text-text-heading">{connection.name}</h3><p className="text-xs text-text-muted">{connection.providerDisplayName} · <code>{connection.providerType}</code></p></div>
      <div className="flex flex-wrap items-center gap-1.5">
        <OwnershipBadge ownership={connection.ownership} /><ConnectionState connection={connection} />
        {connection.isDefault && <Badge tone="accent">Varsayılan</Badge>}
      </div>
    </div>
    <dl className="grid gap-3 sm:grid-cols-2">
      <Fact label="Kota kapsamı"><code>{connection.quotaScope}</code></Fact>
      <Fact label="Yapılandırma">{summary.length === 0 ? <span className="text-text-muted">Ek yapılandırma yok</span> : <ul>{summary.map((item) => <li key={item.name}>{item.label}: {item.value}</li>)}</ul>}</Fact>
      <Fact label="Kimlik bilgisi"><SecretStatus connection={connection} /></Fact>
      <Fact label="Son test"><LastTest test={connection.lastTest} /></Fact>
      <Fact label="Depolanan Replica"><Footprint connection={connection} /></Fact>
    </dl>
    <CapabilityChips capabilities={connection.capabilities} label={`${connection.name} yetenekleri`} />
    {!connection.effectiveEnabled && <p className="text-xs text-warning">Bu hedef devre dışı: yeni backup başlatılamaz. Mevcut backup'lar indirilebilir ve geri yüklenebilir kalır.</p>}
    <ConnectionActions connection={connection} descriptor={descriptor} ops={ops} />
  </article></li>
}

/** Tenant Storage page: the tenant's real destinations, then what each provider can do. Actions come from the API's `permissions`; `canAdmin` only decides whether the add affordance is shown. */
export function TenantDestinations() {
  const { canAdmin } = useTenant()
  const connections = useTenantConnections()
  const providers = useTenantProviders()
  const ops = useTenantConnectionOps()
  const [adding, setAdding] = useState<ProviderDescriptor | null>(null)
  const list = connections.data?.items ?? []
  const catalog = providers.data?.items ?? []

  return <>
    <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="storage-destinations-heading">
      <div><h2 id="storage-destinations-heading" className="font-medium text-text-heading">Depolama hedefleri</h2>
        <p className="mt-1 text-sm text-text-muted">Backup'ların yazıldığı hedefler. Bir hedefi devre dışı bırakmak yalnızca yeni backup'ları engeller; mevcut backup'lar silinmez.</p></div>
      <QueryState isPending={connections.isPending} error={connections.error} empty={connections.data?.items.length === 0}>
        <ul className="grid gap-3 lg:grid-cols-2">{list.map((connection) => <ConnectionCard key={connection.id} connection={connection} descriptor={catalog.find((item) => item.providerType === connection.providerType)} ops={ops} />)}</ul>
      </QueryState>
    </section>
    <section className="space-y-4 rounded-lg border border-border bg-surface p-4" aria-labelledby="storage-providers-heading">
      <h2 id="storage-providers-heading" className="font-medium text-text-heading">Sağlayıcılar ve yetenekler</h2>
      <QueryState isPending={providers.isPending} error={providers.error} empty={providers.data?.items.length === 0}>
        <ProvidersPanel providers={catalog} connections={list} canAdd={canAdmin} onAdd={setAdding} />
      </QueryState>
    </section>
    {adding && <CreateConnectionDialog provider={adding} onClose={() => setAdding(null)} />}
  </>
}
