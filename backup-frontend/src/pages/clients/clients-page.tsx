import { tx, useLanguage } from '@/lib/i18n'
import { Select } from '@/components/ui/text-input'
import { CheckField } from '@/components/ui/check-field'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Server } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'
import { linkClass } from '@/components/ui/form-styles'
import { compareDate, compareNumber, compareText } from '@/features/lists/compare'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'

const connectivities = ['online', 'offline', 'unknown'] as const
const listConfig = {
  search: true,
  filters: { connectivity: connectivities, revoked: ['true'] },
  sort: { first: { name: 'asc', status: 'asc', lastSeen: 'desc', enrolled: 'desc' }, default: { key: 'enrolled', dir: 'desc' } },
} as const

const agentName = (agent: Agent) => agent.hostName ?? tx('Adsız bilgisayar')
/** Online first, revoked computers last. */
const statusRank = (agent: Agent) => agent.status === 'Revoked' ? connectivities.length : connectivities.indexOf(agent.connectivity)
const comparators = {
  name: (a: Agent, b: Agent) => compareText(agentName(a), agentName(b)),
  status: (a: Agent, b: Agent) => compareNumber(statusRank(a), statusRank(b)),
  lastSeen: (a: Agent, b: Agent) => compareDate(a.lastSeenAt, b.lastSeenAt),
  enrolled: (a: Agent, b: Agent) => compareDate(a.enrolledAt, b.enrolledAt),
}

export function ClientsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const state = useListState(listConfig)
  const { connectivity, revoked } = state.filters
  const query = useAgents(tenantId, Boolean(revoked))
  const [addingTenant, setAddingTenant] = useState<string | null>(null)
  const all = query.data ?? []
  const list = useClientList(all, state, {
    searchText: agentName,
    filters: { connectivity: agent => agent.connectivity === connectivity },
    comparators, tieBreak: (a, b) => compareText(agentName(a), agentName(b)) || compareText(a.agentId, b.agentId),
  })
  const columns: Column<Agent>[] = [
    { key: 'name', header: tx('Bilgisayar'), sortable: true, card: 'title', wrap: true, render: (agent) => <Link className={linkClass} to={`/tenants/${tenantId}/agents/${agent.agentId}`}>{agentName(agent)}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), sortable: true, render: (agent) => agent.status === 'Revoked' ? <StatusPill domain="agent" status="Revoked" /> : <div className="flex flex-wrap items-center gap-1.5"><StatusPill domain="agent" status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</div> },
    { key: 'lastSeen', header: tx('Son görülme'), sortable: true, render: (agent) => agent.lastSeenAt ? <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı') },
    { key: 'enrolled', header: tx('Eklenme'), sortable: true, render: (agent) => <RelativeTime value={agent.enrolledAt} /> },
  ]
  const empty = query.data?.length === 0
  const addButton = <Button onClick={() => setAddingTenant(tenantId)}>{tx("Yedekleme bilgisayarı ekle")}</Button>
  return <div className="space-y-6">
    <PageHeader title={tx("Bilgisayarlar")} description={tx("Veritabanı yedeklerini alan bilgisayarlar ve açık olup olmadıkları.")} actions={!empty && addButton} />
    <AgentsTabs />
    <CheckField className="w-fit" label={tx("İptal edilenleri göster")} checked={Boolean(revoked)} onChange={(event) => state.setFilter('revoked', event.target.checked ? 'true' : '')} />
    {!empty && <ListToolbar state={state} label={tx("Bilgisayar filtreleri")} total={list.total} search={{ label: tx("Bilgisayar ara"), placeholder: tx("Bilgisayar adı") }}>
      <FilterField label={tx("Durum")}><Select aria-label={tx("Durum")} value={connectivity} onChange={(event) => state.setFilter('connectivity', event.target.value)}><option value="">{tx("Tümü")}</option><option value="online">{tx("Çevrimiçi")}</option><option value="offline">{tx("Çevrimdışı")}</option><option value="unknown">{tx("Bilinmiyor")}</option></Select></FilterField>
    </ListToolbar>}
    <QueryState isPending={query.isPending} error={query.error}>
      {empty ? <ListEmpty icon={Server} message={tx("Henüz yedekleme bilgisayarı eklenmedi.")} action={addButton} />
        : <DataList rows={list.rows} columns={columns} keyExtractor={(agent) => agent.agentId} caption={tx("Yedekleme bilgisayarları")} sort={sortProps(state)} emptyMessage={tx('Filtrelere uygun bilgisayar bulunamadı.')} onClearFilters={state.filtering ? state.clear : undefined} />}
    </QueryState>
    {addingTenant === tenantId && <AddAgentDialog key={tenantId} tenantId={tenantId} onClose={() => setAddingTenant(null)} />}
  </div>
}
