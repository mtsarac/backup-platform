import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { QueryState } from '@/components/data/query-state'
import { AccessPill } from '@/features/discovery/access-pill'
import { StatusPill } from '@/components/data/status-pill'
import { linkClass } from '@/components/ui/form-styles'
import { Section } from '@/components/ui/section'
import { databaseAvailability, isStaleList, staleListNote, staleListTitle } from '@/features/discovery/status'
import { useDiscoveryInstance, useDiscoveryInstances } from '@/features/discovery/hooks'
import type { DiscoveryDatabase, DiscoveryInstanceDetail, DiscoveryInstanceSummary } from '@/features/discovery/types'
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
  const listed = instance.access !== 'CredentialsRequired'
  const detail = useDiscoveryInstance(listed ? instance.id : '')
  return <div className="space-y-2">
    <div className="flex flex-wrap items-center gap-2">
      <Link className={`font-medium ${linkClass}`} to={discoveryInstancePath(tenantId, instance.id)}>{instance.displayName}</Link>
      <AccessPill status={instance.access} reasonCode={instance.accessReasonCode} />
    </div>
    {listed && <QueryState isPending={detail.isPending} error={detail.error}>{detail.data && (detail.data.databases.length === 0
      ? <p className="text-sm text-text-muted">{tx("Bu sunucuda listelenmiş veritabanı yok.")}</p>
      : <>
        {isStaleList(detail.data) && <div className="space-y-0.5 text-sm text-text-muted"><p className="font-medium">{staleListTitle()}</p><p>{staleListNote()}</p></div>}
        <ul className={`divide-y divide-border rounded-lg border border-border${isStaleList(detail.data) ? ' opacity-60' : ''}`}>{detail.data.databases.map(db => <DatabaseItem key={db.name} db={db} instance={detail.data} />)}</ul></>)}</QueryState>}
  </div>
}

function DatabaseItem({ db, instance }: { db: DiscoveryDatabase; instance: DiscoveryInstanceDetail }) {
  if (isStaleList(instance)) return <li className="px-3 py-2"><span className="font-medium text-text-heading">{db.name}</span></li>
  const { kind, reason } = databaseAvailability(db, instance)
  return <li className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-3 py-2">
    <div className="min-w-0"><span className="font-medium text-text-heading">{db.name}</span>{reason && <p className="text-sm text-text-muted">{reason}</p>}</div>
    {kind === 'available' ? <StatusPill level="safe">{tx("Yedeklenebilir")}</StatusPill>
      : kind === 'covered' ? <StatusPill level="info">{tx("Zaten yedekleniyor")}</StatusPill>
      : <StatusPill level={db.isSystem ? 'neutral' : 'attention'}>{tx("Yedeklenemez")}</StatusPill>}
  </li>
}
