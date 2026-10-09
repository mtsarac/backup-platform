import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { agentDate } from '@/features/agents/format'
import { AgentState } from '@/features/agents/presentation'
import { CapabilityReporting } from '@/features/platform/capability-reporting'
import { usePlatformAgents } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { linkClass, selectClass } from '@/features/platform/styles'
import type { AgentConnectivity, AgentLifecycle, PlatformAgent } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'

const connectivityValues: AgentConnectivity[] = ['online', 'offline', 'unknown']
const lifecycleValues: AgentLifecycle[] = ['PendingEnrollment', 'Active', 'Disabled', 'Revoked']

function oneOf<T extends string>(value: string, allowed: readonly T[]): T | undefined {
  return allowed.find((item) => item === value)
}

export function PlatformAgentsPage() {
  const { page, get, update } = useListParams()
  const search = get('search')
  const tenantId = get('tenantId')
  const connectivity = oneOf(get('connectivity'), connectivityValues)
  const status = oneOf(get('status'), lifecycleValues)
  const query = usePlatformAgents({ tenantId, connectivity, status, search, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  const filtered = Boolean(search || tenantId || connectivity || status)
  const columns: Column<PlatformAgent>[] = [
    { key: 'tenant', header: 'Tenant', wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link> },
    { key: 'agent', header: 'Agent', wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.agent(agent.tenantId, agent.agentId)}>{agent.hostName ?? 'Adsız agent'}</Link> },
    { key: 'status', header: 'Yaşam döngüsü', render: (agent) => <AgentState value={agent.status} /> },
    { key: 'connectivity', header: 'Bağlantı', render: (agent) => <AgentState value={agent.connectivity} /> },
    { key: 'os', header: 'İşletim sistemi', wrap: true, render: (agent) => <div><div>{agent.platform}</div><div className="text-xs text-text-muted">{agent.osDescription ?? 'Bildirilmedi'}</div></div> },
    { key: 'version', header: 'Yazılım / protokol', render: (agent) => <div><div>{agent.agentVersion ?? 'Bildirilmedi'}</div><div className="text-xs text-text-muted">Protokol: {agent.protocolVersion ?? 'Bildirilmedi'}</div></div> },
    { key: 'capabilities', header: 'Yetenekler', render: (agent) => <CapabilityReporting agent={agent} /> },
    { key: 'lastSeenAt', header: 'Son görülme', render: (agent) => agentDate(agent.lastSeenAt) },
  ]
  return <div className="space-y-4">
    <div><h1 className="text-2xl font-semibold text-text-heading">Agents</h1><p className="mt-1 text-sm text-text-muted">Tüm tenant'lardaki Agent'lar. Yaşam döngüsü ve bağlantı durumu ayrı gösterilir; bu görünüm salt okunurdur.</p></div>
    <div className="flex flex-wrap items-end gap-3">
      <SearchForm label="Agent ara" value={search} onSearch={(value) => update({ search: value })} placeholder="Makine adı, platform veya sürüm" />
      <label className="text-sm text-text-muted">Bağlantı<select className={selectClass} value={connectivity ?? ''} onChange={(event) => update({ connectivity: event.target.value })}><option value="">Tümü</option><option value="online">Çevrimiçi</option><option value="offline">Çevrimdışı</option><option value="unknown">Bilinmiyor</option></select></label>
      <label className="text-sm text-text-muted">Yaşam döngüsü<select className={selectClass} value={status ?? ''} onChange={(event) => update({ status: event.target.value })}><option value="">Tümü</option><option value="PendingEnrollment">Kayıt bekliyor</option><option value="Active">Etkin</option><option value="Disabled">Devre dışı</option><option value="Revoked">İptal edildi</option></select></label>
    </div>
    {tenantId && <div className="flex flex-wrap items-center gap-2 rounded-lg border border-border bg-surface p-3 text-sm"><span>Tek bir tenant'a göre filtreleniyor.</span><Button variant="outline" size="sm" onClick={() => update({ tenantId: '' })}>Tenant filtresini kaldır</Button></div>}
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <DataTable rows={data.items} columns={columns} keyExtractor={(agent) => `${agent.tenantId}:${agent.agentId}`} emptyMessage={filtered ? 'Filtrelere uygun Agent bulunamadı.' : 'Henüz kayıtlı Agent yok.'} />
        {data.totalCount > 0 && <Pagination label="Agent sayfaları" page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </>}
    </PlatformQueryState>
  </div>
}
