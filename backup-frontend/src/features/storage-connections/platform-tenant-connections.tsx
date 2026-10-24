import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { CreateManagedTargetDialog } from './create-dialogs'
import { usePlatformConnectionOps, usePlatformProviders, usePlatformTenantConnections } from './hooks'
import { PlatformConnectionTable } from './platform-connection-table'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/components/auth/access-denied'

/** 'Depolama bağlantıları' on the platform tenant detail page: every destination of one tenant, with the same row actions as the global list. */
export function PlatformTenantConnections({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const connections = usePlatformTenantConnections(tenantId)
  const providers = usePlatformProviders()
  const ops = usePlatformConnectionOps()
  const [adding, setAdding] = useState(false)
  return <section className="space-y-3 rounded-lg border border-border bg-surface p-4" aria-labelledby="tenant-connections-heading">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div><h2 id="tenant-connections-heading" className="font-medium text-text-heading">{tx("Depolama bağlantıları")}</h2>
        <p className="mt-1 text-sm text-text-muted">{tx("Tenant'ın backup hedefleri. Devre dışı bırakmak yalnızca yeni backup'ları engeller; mevcut Replica'lar silinmez.")}</p></div>
      <Button variant="outline" size="sm" onClick={() => setAdding(true)}><Plus className="size-3.5" aria-hidden="true" />{tx("Managed target ekle")}</Button>
    </div>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={connections.isPending} error={connections.error}>
      {connections.data && <PlatformConnectionTable rows={connections.data.items} providers={providers.data?.items ?? []} ops={ops} showTenant={false} emptyMessage={tx("Bu tenant için depolama bağlantısı yok.")} />}
    </QueryState>
    {adding && <CreateManagedTargetDialog tenant={{ tenantId, name: tenantName }} onClose={() => setAdding(false)} />}
  </section>
}
