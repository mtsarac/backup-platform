import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Server } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { AgentState } from '@/features/agents/presentation'
import { agentDate } from '@/features/agents/format'
import { CapabilitySummary } from '@/features/agents/capabilities'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'
import { linkClass } from '@/features/platform/styles'

export function ClientsPage() {
  const { tenantId, canAdmin } = useTenant()
  const query = useAgents(tenantId)
  const [search, setSearch] = useState('')
  const [connectivity, setConnectivity] = useState('all')
  const [addingTenant, setAddingTenant] = useState<string | null>(null)
  const term = search.trim().toLocaleLowerCase('tr-TR')
  const filtered = search !== '' || connectivity !== 'all'
  const rows = (query.data ?? []).filter((agent) =>
    (connectivity === 'all' || agent.connectivity === connectivity) &&
    `${agent.hostName ?? ''} ${agent.platform} ${agent.agentVersion ?? ''}`.toLocaleLowerCase('tr-TR').includes(term))
  const columns: Column<Agent>[] = [
    { key: 'hostName', header: 'Agent', wrap: true, render: (agent) => <div><Link className={linkClass} to={`/tenants/${tenantId}/agents/${agent.agentId}`}>{agent.hostName ?? 'Adsız agent'}</Link><div className="break-all font-mono text-xs text-text-muted">{agent.agentId}</div></div> },
    { key: 'platform', header: 'Platform', wrap: true, render: (agent) => <div><div>{agent.platform}</div><div className="text-sm text-text-muted">Sürüm {agent.agentVersion ?? 'bildirilmedi'}</div></div> },
    { key: 'connectivity', header: 'Durum', render: (agent) => <div className="flex flex-wrap items-center gap-1.5"><StatusPill status={agent.connectivity} />{agent.status !== 'Active' && <AgentState value={agent.status} />}{agent.operationalStatus.toLowerCase() !== agent.connectivity.toLowerCase() && <AgentState value={agent.operationalStatus} />}</div> },
    { key: 'lastSeenAt', header: 'Son görülme', render: (agent) => agentDate(agent.lastSeenAt) },
    { key: 'capabilities', header: 'Yetenekler', render: (agent) => <CapabilitySummary capabilities={agent.capabilities} /> },
  ]
  const addButton = canAdmin && <Button onClick={() => setAddingTenant(tenantId)}>Agent ekle</Button>
  return <div className="space-y-6">
    <PageHeader title="Agent'lar" description="Yedekleme işlerini çalıştıran makineler ve bağlantı durumları." actions={addButton} />
    <AgentsTabs />
    <Filters label="Agent filtreleri" onClear={filtered ? () => { setSearch(''); setConnectivity('all') } : undefined}>
      <FilterField label="Agent ara" className="sm:col-span-2"><input className={filterControlClass} placeholder="Makine adı, platform veya sürüm" value={search} onChange={(event) => setSearch(event.target.value)} /></FilterField>
      <FilterField label="Bağlantı"><select className={filterControlClass} value={connectivity} onChange={(event) => setConnectivity(event.target.value)}><option value="all">Tümü</option><option value="online">Çevrimiçi</option><option value="offline">Çevrimdışı</option><option value="unknown">Bilinmiyor</option></select></FilterField>
    </Filters>
    <QueryState isPending={query.isPending} error={query.error}>
      {query.data?.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Server} title="Henüz kayıtlı Agent yok." action={addButton}>{canAdmin ? 'Yedekleme işlerini çalıştırmak için bir makineye Agent kurun ve bu çalışma alanına kaydedin.' : 'Bir çalışma alanı yöneticisi Agent ekleyebilir.'}</EmptyState></div>
        : <DataTable rows={rows} columns={columns} keyExtractor={(agent) => agent.agentId} emptyMessage={filtered ? 'Filtrelere uygun agent bulunamadı.' : 'Henüz kayıtlı agent yok.'} />}
    </QueryState>
    {canAdmin && addingTenant === tenantId && <AddAgentDialog key={tenantId} tenantId={tenantId} onClose={() => setAddingTenant(null)} />}
  </div>
}
