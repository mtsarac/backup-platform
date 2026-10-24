import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { CapabilityReporting } from '@/features/platform/capability-reporting'
import { usePlatformAgents } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { linkClass } from '@/features/platform/styles'
import type { AgentConnectivity, AgentLifecycle, PlatformAgent } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

const connectivityValues: AgentConnectivity[] = ['online', 'offline', 'unknown']
const lifecycleValues: AgentLifecycle[] = ['PendingEnrollment', 'Active', 'Disabled', 'Revoked']

function oneOf<T extends string>(value: string, allowed: readonly T[]): T | undefined {
  return allowed.find((item) => item === value)
}

export function PlatformAgentsPage() {
  useLanguage()
  const { page, get, update } = useListParams()
  const search = get('search')
  const tenantId = get('tenantId')
  const connectivity = oneOf(get('connectivity'), connectivityValues)
  const status = oneOf(get('status'), lifecycleValues)
  const query = usePlatformAgents({ tenantId, connectivity, status, search, page, pageSize: PLATFORM_PAGE_SIZE })
  const data = query.data
  const filtered = Boolean(search || tenantId || connectivity || status)
  const columns: Column<PlatformAgent>[] = [
    { key: 'tenant', header: tx('Tenant'), wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link> },
    { key: 'agent', header: tx('Agent'), wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.agent(agent.tenantId, agent.agentId)}>{agent.hostName ?? tx('Adsız agent')}</Link> },
    { key: 'status', header: tx('Yaşam döngüsü'), render: (agent) => <StatusPill domain="agent" status={agent.status} /> },
    { key: 'connectivity', header: tx('Bağlantı'), render: (agent) => <StatusPill domain="agent" status={agent.connectivity} /> },
    { key: 'os', header: tx('İşletim sistemi'), wrap: true, render: (agent) => <div><div>{agent.platform}</div><div className="text-xs text-text-muted">{agent.osDescription ?? tx('Bildirilmedi')}</div></div> },
    { key: 'version', header: tx('Yazılım / protokol'), render: (agent) => <div><div>{agent.agentVersion ?? tx('Bildirilmedi')}</div><div className="text-xs text-text-muted">{tx("Protokol:")}{' '}{agent.protocolVersion ?? tx('Bildirilmedi')}</div></div> },
    { key: 'capabilities', header: tx('Yetenekler'), render: (agent) => <CapabilityReporting agent={agent} /> },
    { key: 'lastSeenAt', header: tx('Son görülme'), render: (agent) => formatDateTime(agent.lastSeenAt, tx('Henüz bildirilmedi')) },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Agents")} description={tx("Tüm tenant'lardaki Agent'lar. Yaşam döngüsü ve bağlantı durumu ayrı gösterilir; bu görünüm salt okunurdur.")} />
    <Filters label={tx("Agent filtreleri")} onClear={filtered ? () => update({ search: '', connectivity: '', status: '', tenantId: '' }) : undefined}
      footer={tenantId && <div className="flex flex-wrap items-center gap-2 text-sm"><span>{tx("Tek bir tenant'a göre filtreleniyor.")}</span><Button variant="outline" size="sm" onClick={() => update({ tenantId: '' })}>{tx("Tenant filtresini kaldır")}</Button></div>}>
      <SearchForm className="sm:col-span-2" label={tx("Agent ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Makine adı, platform veya sürüm")} />
      <FilterField label={tx("Bağlantı")}><select className={filterControlClass} value={connectivity ?? ''} onChange={(event) => update({ connectivity: event.target.value })}><option value="">{tx("Tümü")}</option><option value="online">{tx("Çevrimiçi")}</option><option value="offline">{tx("Çevrimdışı")}</option><option value="unknown">{tx("Bilinmiyor")}</option></select></FilterField>
      <FilterField label={tx("Yaşam döngüsü")}><select className={filterControlClass} value={status ?? ''} onChange={(event) => update({ status: event.target.value })}><option value="">{tx("Tümü")}</option><option value="PendingEnrollment">{tx("Kayıt bekliyor")}</option><option value="Active">{tx("Etkin")}</option><option value="Disabled">{tx("Devre dışı")}</option><option value="Revoked">{tx("İptal edildi")}</option></select></FilterField>
    </Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        <DataTable rows={data.items} columns={columns} keyExtractor={(agent) => `${agent.tenantId}:${agent.agentId}`} emptyMessage={filtered ? tx('Filtrelere uygun Agent bulunamadı.') : tx('Henüz kayıtlı Agent yok.')} />
        {data.totalCount > 0 && <Pagination label={tx("Agent sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
  </div>
}
