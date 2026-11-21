import { currentLocale, tx, useLanguage } from '@/lib/i18n'
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
import { FilterField } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { Select } from '@/components/ui/text-input'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { useAgents } from '@/features/agents/hooks'
import { useDiscoveryAgents, useDiscoveryInstances } from '@/features/discovery/hooks'
import { AccessPill } from '@/features/discovery/access-pill'
import { engineLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryInstanceSummary } from '@/features/discovery/types'
import { compareDate, compareNumber, compareText } from '@/features/lists/compare'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { useTenant } from '@/features/tenancy/use-tenant'
import { isForbidden } from '@/lib/api/client'
import { statusLabel } from '@/lib/status'
import { discoveryInstancePath } from '@/lib/routes'
import { awaitingCapabilities, CAPABILITY_POLL_MS } from '@/features/first-backup/first-backup-model'
import { AddedComputerGuide } from './added-computer-guide'
import { ComputerScanCard } from './computer-scan-card'

const accessStates = ['CredentialsRequired', 'Authenticated', 'AuthenticationFailed', 'Unreachable', 'Error', 'Unknown'] as const
/** Newest discovered server first; a count or a date a server has none of goes last. */
const listConfig = {
  search: true,
  filters: { access: accessStates, engine: 'text', computer: 'text' },
  sort: { first: { name: 'asc', status: 'asc', computer: 'asc', eligible: 'desc', seen: 'desc', first: 'desc' }, default: { key: 'first', dir: 'desc' } },
} as const

/** Only a server with a working login knows how many of its databases can be backed up. */
const eligibleDatabases = (item: DiscoveryInstanceSummary) => item.access === 'Authenticated' ? item.databaseCounts.backupEligible : null

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
  const state = useListState(listConfig)
  const all = servers ?? []
  const computers = [...new Map(all.map(item => [item.agentId, agentName(item.agentId)])).entries()].toSorted((a, b) => a[1].localeCompare(b[1], currentLocale()))
  const engines = [...new Set(all.map(item => item.engine))]
  const accessPresent = accessStates.filter(access => all.some(item => item.access === access))
  // A value the list no longer offers (a computer that is gone, a hand-edited address) filters nothing.
  const computer = computers.find(([id]) => id === state.filters.computer && computers.length > 1)?.[0]
  const engine = engines.find(item => item === state.filters.engine && engines.length > 1)
  const comparators: Record<keyof typeof listConfig.sort.first, (a: DiscoveryInstanceSummary, b: DiscoveryInstanceSummary) => number> = {
    name: (a, b) => compareText(a.displayName, b.displayName),
    status: (a, b) => compareText(statusLabel('access', a.access), statusLabel('access', b.access)),
    computer: (a, b) => compareText(agentName(a.agentId), agentName(b.agentId)),
    eligible: (a, b) => compareNumber(eligibleDatabases(a), eligibleDatabases(b)),
    seen: (a, b) => compareDate(a.lastSeenAt, b.lastSeenAt),
    first: (a, b) => compareDate(a.firstSeenAt, b.firstSeenAt),
  }
  const list = useClientList(all, state, {
    searchText: item => `${item.displayName} ${agentName(item.agentId)}`,
    filters: { access: item => item.access === state.filters.access, computer: item => !computer || item.agentId === computer, engine: item => !engine || item.engine === engine },
    comparators,
    tieBreak: (a, b) => compareText(a.displayName, b.displayName) || compareText(a.id, b.id),
  })
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
    { key: 'name', card: 'title', header: tx('Veritabanı sunucusu'), sortable: true, wrap: true, render: item => <Link className={linkClass} to={discoveryInstancePath(tenantId, item.id)}>{item.displayName}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), sortable: true, render: item => <span className="flex flex-wrap gap-1"><AccessPill status={item.access} reasonCode={item.accessReasonCode} />{item.presence === 'Missing' && <StatusPill level="neutral">{tx("Bulunamadı")}</StatusPill>}</span> },
    { key: 'computer', header: tx('Bilgisayar'), sortable: true, wrap: true, render: item => <Link className={linkClass} to={`${computersPath}/${encodeURIComponent(item.agentId)}`}>{agentName(item.agentId)}</Link> },
    { key: 'eligible', header: tx('Yedeklenebilir veritabanı'), sortable: true, render: item => eligibleDatabases(item) ?? '—' },
    { key: 'seen', header: tx('Son görülme'), sortable: true, render: item => <RelativeTime value={item.lastSeenAt} /> },
    { key: 'first', header: tx('İlk bulunma'), sortable: true, render: item => <RelativeTime value={item.firstSeenAt} /> },
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
        : <div className="space-y-4">
          <ListToolbar state={state} label={tx("Veritabanı bulma filtreleri")} total={list.total} search={{ label: tx("Sunucu veya bilgisayar ara"), placeholder: tx("Sunucu veya bilgisayar adı") }}>
            <FilterField label={tx("Durum")}>
              <Select aria-label={tx("Durum")} value={state.filters.access} onChange={event => state.setFilter('access', event.target.value)}>
                <option value="">{tx("Tümü")}</option>{accessPresent.map(access => <option key={access} value={access}>{statusLabel('access', access)}</option>)}
              </Select>
            </FilterField>
            {engines.length > 1 && <FilterField label={tx("Veritabanı türü")}>
              <Select aria-label={tx("Veritabanı türü")} value={engine ?? ''} onChange={event => state.setFilter('engine', event.target.value)}>
                <option value="">{tx("Tümü")}</option>{engines.map(item => <option key={item} value={item}>{engineLabels[item]}</option>)}
              </Select>
            </FilterField>}
            {computers.length > 1 && <FilterField label={tx("Bilgisayar")}>
              <Select aria-label={tx("Bilgisayar")} value={computer ?? ''} onChange={event => state.setFilter('computer', event.target.value)}>
                <option value="">{tx("Tümü")}</option>{computers.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
              </Select>
            </FilterField>}
          </ListToolbar>
          <DataList rows={list.rows} columns={columns} keyExtractor={item => item.id} caption={tx("Bulunan veritabanı sunucuları")} sort={sortProps(state)} emptyMessage={tx("Aramanızla eşleşen sunucu yok.")} onClearFilters={state.filtering ? state.clear : undefined} />
        </div>)}
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
