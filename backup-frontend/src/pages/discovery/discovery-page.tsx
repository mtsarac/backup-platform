import { tx, useLanguage } from '@/lib/i18n'
import { Card } from '@/components/ui/card'
import { useEffect, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { Loader2, Radar } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { AccessDenied } from '@/components/auth/access-denied'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice } from '@/components/ui/notice'
import { EmptyState } from '@/components/ui/empty-state'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { useAgents } from '@/features/agents/hooks'
import { useDiscoveryAgents, useDiscoveryInstances } from '@/features/discovery/hooks'
import { AccessPill } from '@/features/discovery/access-pill'
import { supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryInstanceSummary } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { isForbidden } from '@/lib/api/client'
import { discoveryInstancePath } from '@/lib/routes'
import { awaitingCapabilities, CAPABILITY_POLL_MS } from '@/features/first-backup/first-backup-model'
import { AddedComputerGuide } from './added-computer-guide'
import { ComputerScanCard } from './computer-scan-card'


export function DiscoveryPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const agents = useAgents(tenantId, false, list => awaitingCapabilities(list) ? CAPABILITY_POLL_MS : false)
  // Route state from the first-backup flow. Browsers keep history state across a reload, so it is read once and then cleared from the entry.
  const location = useLocation()
  const navigate = useNavigate()
  const [added] = useState(() => (location.state as { addedAgentId?: string } | null)?.addedAgentId ?? '')
  useEffect(() => { if (added) navigate({ pathname: location.pathname, search: location.search }, { replace: true, state: null }) }, []) // oxlint-disable-line react-hooks/exhaustive-deps -- once, on arrival
  const [dismissed, setDismissed] = useState(false)
  const statuses = useDiscoveryAgents()
  const agentList = agents.data ?? []
  const guided = Boolean(added) && !dismissed
  // While the guide is open the list narrows to the computer that was just added.
  const agentId = guided ? agentList.find(item => item.agentId === added)?.agentId ?? '' : ''
  const instances = useDiscoveryInstances(agentId ? { agentId } : {}, agents.isSuccess)
  // Servers of computers missing from the list (revoked ones are hidden) are leftovers and are not shown.
  const servers = instances.data?.items.filter(item => agentList.some(agent => agent.agentId === item.agentId))
  const agentName = (id: string) => agentList.find(item => item.agentId === id)?.hostName || tx('Adsız bilgisayar')
  const computersPath = `/tenants/${tenantId}/agents`
  // Right after a computer was added there is nothing to list yet: say that the first scan is on its way instead of "nothing found".
  const awaiting = guided && awaitingCapabilities(agentList)
  const addedComputer = agentList.find(item => item.agentId === added)
  const firstScanPending = (agentId ? statuses.scanningFor(agentId) : statuses.scanning) || awaiting || (guided && addedComputer?.connectivity === 'online' && supportsDiscovery(addedComputer.capabilities) && !statuses.data?.items.some(item => item.agentId === added && item.lastScanAt))
  const scans = agentList.map(agent => {
    const status = statuses.data?.items.find(item => item.agentId === agent.agentId)
    const needsAttention = !supportsDiscovery(agent.capabilities) || agent.connectivity !== 'online' || !status || !status.enabled || status.scanState !== 'Idle' || status.lastOutcome !== 'Completed'
    return { agent, status, needsAttention }
  })
  const columns: Column<DiscoveryInstanceSummary>[] = [
    { key: 'name', card: 'title', header: tx('Veritabanı sunucusu'), wrap: true, render: item => <Link className={linkClass} to={discoveryInstancePath(tenantId, item.id)}>{item.displayName}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: item => <span className="flex flex-wrap gap-1"><AccessPill status={item.access} reasonCode={item.accessReasonCode} />{item.presence === 'Missing' && <StatusPill level="neutral">{tx("Bulunamadı")}</StatusPill>}</span> },
    { key: 'computer', header: tx('Bilgisayar'), wrap: true, render: item => <Link className={linkClass} to={`${computersPath}/${encodeURIComponent(item.agentId)}`}>{agentName(item.agentId)}</Link> },
    { key: 'eligible', header: tx('Yedeklenebilir veritabanı'), render: item => item.access === 'Authenticated' ? item.databaseCounts.backupEligible : '—' },
    { key: 'seen', header: tx('Son görülme'), render: item => <RelativeTime value={item.lastSeenAt} /> },
  ]
  let body
  if (isForbidden(instances.error) || isForbidden(statuses.error) || isForbidden(agents.error)) body = <AccessDenied title={tx("Yetki yok")} what={tx("Veritabanı bulma sonuçlarını görüntüleme yetkiniz yok.")} />
  else if (agents.error || statuses.error) body = <ErrorNotice error={agents.error || statuses.error} />
  else if (agents.isPending) body = <QueryState isPending error={null} />
  else if (agents.isSuccess && agentList.length === 0) body = <Card padding="none"><EmptyState icon={Radar} message={tx("Henüz yedekleme bilgisayarı yok; veritabanları bir yedekleme bilgisayarında aranır.")} action={<Link className={buttonVariants()} to={computersPath}>{tx("Yedekleme bilgisayarı ekle")}</Link>} /></Card>
  else if (agents.isSuccess && !awaiting && !agentList.some(item => supportsDiscovery(item.capabilities))) body = <Card padding="none"><EmptyState icon={Radar} message={tx("Yedekleme bilgisayarlarınızdaki program veritabanı bulmayı desteklemiyor; programı güncelleyin veya veritabanını yedekleme işinde elle girin.")} action={<Link className={buttonVariants()} to={computersPath}>{tx("Yedekleme bilgisayarlarına git")}</Link>} /></Card>
  else body = <>
    <div className="space-y-4">{scans.filter(scan => scan.needsAttention).map(({ agent, status }) => <ComputerScanCard key={agent.agentId} agent={agent} status={status} />)}</div>
    <QueryState isPending={instances.isPending} error={instances.error}>
      {servers && (servers.length === 0
        ? firstScanPending ? <Card><p role="status" className="flex items-center justify-center gap-2 text-sm text-text-muted"><Loader2 aria-hidden="true" className="size-4 animate-spin text-primary" />{tx("Veritabanları aranıyor…")}</p></Card>
        : <Card padding="none"><EmptyState icon={Radar} message={tx("Bu bilgisayarlarda henüz veritabanı bulunamadı. Bilgisayar kartındaki \"Yeniden tara\" ile yeniden arayabilirsiniz.")} action={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-jobs/new`}>{tx("Yeni yedekleme işi")}</Link>} /></Card>
        : <DataList rows={servers} columns={columns} keyExtractor={item => item.id} caption={tx("Bulunan veritabanı sunucuları")} emptyMessage={tx("Bu bilgisayarlarda henüz veritabanı bulunamadı.")} />)}
    </QueryState>
    <div className="space-y-4">{scans.filter(scan => !scan.needsAttention).map(({ agent, status }) => <ComputerScanCard key={agent.agentId} agent={agent} status={status} />)}</div>
  </>
  return <div className="space-y-6">
    <PageHeader title={tx("Bulunan veritabanları")} description={tx("Yedekleme bilgisayarlarınızda bulunan veritabanları ve hangilerinin yedeklenebileceği.")} />
    <AgentsTabs />
    {guided && !agents.isPending && <AddedComputerGuide agent={agentList.find(item => item.agentId === added)} agentId={added} onDismiss={() => setDismissed(true)} />}
    {body}
  </div>
}
