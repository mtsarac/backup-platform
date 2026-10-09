import { useState } from 'react'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { AgentState } from '@/features/agents/presentation'
import { agentDate } from '@/features/agents/format'
import { CapabilitySummary } from '@/features/agents/capabilities'
import { useTenant } from '@/features/tenancy/use-tenant'
import { Button } from '@/components/ui/button'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'

export function ClientsPage() {
  const { tenantId, canAdmin } = useTenant()
  const query = useAgents(tenantId)
  const [search, setSearch] = useState('')
  const [connectivity, setConnectivity] = useState('all')
  const [addingTenant, setAddingTenant] = useState<string | null>(null)
  const term = search.trim().toLocaleLowerCase('tr-TR')
  const rows = (query.data ?? []).filter((agent) =>
    (connectivity === 'all' || agent.connectivity === connectivity) &&
    `${agent.hostName ?? ''} ${agent.platform} ${agent.agentVersion ?? ''}`.toLocaleLowerCase('tr-TR').includes(term))
  const columns: Column<Agent>[] = [
    { key: 'hostName', header: 'Makine adı', render: (agent) => <Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/agents/${agent.agentId}`}>{agent.hostName ?? 'Adsız agent'}</Link> },
    { key: 'platform', header: 'Platform', render: (agent) => agent.platform },
    { key: 'version', header: 'Yazılım / protokol', render: (agent) => <div><div>{agent.agentVersion ?? 'Bildirilmedi'}</div><div className="text-xs text-text-muted">Protokol: {agent.protocolVersion ?? 'Bildirilmedi'}</div></div> },
    { key: 'status', header: 'Yaşam döngüsü', render: (agent) => <AgentState value={agent.status} /> },
    { key: 'operationalStatus', header: 'Çalışma durumu', render: (agent) => <AgentState value={agent.operationalStatus} /> },
    { key: 'connectivity', header: 'Bağlantı', render: (agent) => <AgentState value={agent.connectivity} /> },
    { key: 'lastSeenAt', header: 'Son görülme', render: (agent) => agentDate(agent.lastSeenAt) },
    { key: 'capabilities', header: 'Yetenekler', wrap: true, render: (agent) => <CapabilitySummary capabilities={agent.capabilities} /> },
  ]
  return <div className="space-y-4">
    <div className="flex flex-wrap items-center justify-between gap-3"><div><h1 className="text-2xl font-semibold text-text-heading">Agent'lar</h1><p className="mt-1 text-sm text-text-muted">Yaşam döngüsü ve bağlantı durumu ayrı olarak gösterilir.</p></div>{canAdmin && <div className="flex flex-wrap items-center gap-3"><Link className="text-sm text-primary hover:underline" to={`/tenants/${tenantId}/enrollment`}>Agent kaydı</Link><Button onClick={() => setAddingTenant(tenantId)}>Agent ekle</Button></div>}</div>
    <div className="flex flex-wrap gap-3">
      <label className="min-w-0 flex-1 text-sm text-text-muted">Agent ara<input className="mt-1 block w-full rounded-lg border border-border bg-surface px-3 py-2 text-text" placeholder="Makine adı, platform veya sürüm" value={search} onChange={(event) => setSearch(event.target.value)} /></label>
      <label className="text-sm text-text-muted">Bağlantı<select className="mt-1 block rounded-lg border border-border bg-surface px-3 py-2 text-text" value={connectivity} onChange={(event) => setConnectivity(event.target.value)}><option value="all">Tümü</option><option value="online">Çevrimiçi</option><option value="offline">Çevrimdışı</option><option value="unknown">Bilinmiyor</option></select></label>
    </div>
    <QueryState isPending={query.isPending} error={query.error}><DataTable rows={rows} columns={columns} keyExtractor={(agent) => agent.agentId} emptyMessage={search || connectivity !== 'all' ? 'Filtrelere uygun agent bulunamadı.' : 'Henüz kayıtlı agent yok.'} /></QueryState>
    {canAdmin && addingTenant === tenantId && <AddAgentDialog key={tenantId} tenantId={tenantId} onClose={() => setAddingTenant(null)} />}
  </div>
}
