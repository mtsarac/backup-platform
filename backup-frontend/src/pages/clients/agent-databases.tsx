import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { linkClass } from '@/components/ui/form-styles'
import { Section } from '@/components/ui/section'
import { DatabaseTable } from '@/features/discovery/database-table'
import { useDiscoveryInstance, useDiscoveryInstances } from '@/features/discovery/hooks'
import type { DiscoveryInstanceSummary } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { discoveryInstancePath } from '@/lib/routes'

/** Databases the agent found on this computer, grouped by database server; servers without credentials link to their page instead. */
export function AgentDatabases({ agentId }: { agentId: string }) {
  useLanguage()
  const instances = useDiscoveryInstances({ agentId })
  return <Section title={tx("Bu bilgisayarda bulunan veritabanları")}>
    <QueryState isPending={instances.isPending} error={instances.error}>
      {instances.data?.items.length === 0 && <p className="text-sm text-text-muted">{tx("Bu bilgisayarda henüz veritabanı bulunamadı.")}</p>}
      <div className="space-y-5">{instances.data?.items.map(item => <InstanceDatabases key={item.id} instance={item} />)}</div>
    </QueryState>
  </Section>
}

function InstanceDatabases({ instance }: { instance: DiscoveryInstanceSummary }) {
  const { tenantId } = useTenant()
  const authenticated = instance.access === 'Authenticated'
  const detail = useDiscoveryInstance(authenticated ? instance.id : '')
  return <div className="space-y-2">
    <div className="flex flex-wrap items-center gap-2">
      <Link className={`font-medium ${linkClass}`} to={discoveryInstancePath(tenantId, instance.id)}>{instance.displayName}</Link>
      <StatusPill domain="access" status={instance.access} />
    </div>
    {authenticated && <QueryState isPending={detail.isPending} error={detail.error}>{detail.data && <DatabaseTable instance={detail.data} />}</QueryState>}
  </div>
}
