import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAgents } from '@/features/platform/hooks'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { SearchForm } from '@/features/platform/search-form'
import { TenantPicker } from '@/features/platform/tenant-picker'
import type { AgentConnectivity, AgentLifecycle, PlatformAgent } from '@/features/platform/types'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'

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
  // OS, versions, capabilities and lifecycle details live on the computer's own page; the list only flags a lifecycle that is not Active.
  const columns: Column<PlatformAgent>[] = [
    { key: 'agent', card: 'title', header: tx('Bilgisayar'), wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.agent(agent.tenantId, agent.agentId)}>{agent.hostName ?? tx('Adsız bilgisayar')}</Link> },
    { key: 'connectivity', card: 'status', header: tx('Durum'), render: (agent) => <div className="flex flex-wrap items-center gap-1.5"><StatusPill domain="agent" status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</div> },
    { key: 'tenant', header: tx('Çalışma alanı'), wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link> },
    { key: 'lastSeenAt', header: tx('Son görülme'), render: (agent) => agent.lastSeenAt ? <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı') },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Yedekleme bilgisayarları")} description={tx("Tüm çalışma alanlarındaki yedekleme bilgisayarları ve çevrimiçi olup olmadıkları. Bu görünüm salt okunurdur.")} />
    <Filters label={tx("Bilgisayar filtreleri")} onClear={filtered ? () => update({ search: '', connectivity: '', status: '', tenantId: '' }) : undefined}>
      <SearchForm className="sm:col-span-2" label={tx("Bilgisayar ara")} value={search} onSearch={(value) => update({ search: value })} placeholder={tx("Bilgisayar adı, platform veya sürüm")} />
      <TenantPicker value={tenantId} onChange={(value) => update({ tenantId: value })} />
      <FilterField label={tx("Bağlantı")}><select className={filterControlClass} value={connectivity ?? ''} onChange={(event) => update({ connectivity: event.target.value })}><option value="">{tx("Tümü")}</option><option value="online">{tx("Çevrimiçi")}</option><option value="offline">{tx("Çevrimdışı")}</option><option value="unknown">{tx("Bilinmiyor")}</option></select></FilterField>
      <FilterField label={tx("Kayıt durumu")}><select className={filterControlClass} value={status ?? ''} onChange={(event) => update({ status: event.target.value })}><option value="">{tx("Tümü")}</option><option value="PendingEnrollment">{tx("Kayıt bekliyor")}</option><option value="Active">{tx("Etkin")}</option><option value="Disabled">{tx("Devre dışı")}</option><option value="Revoked">{tx("İptal edildi")}</option></select></FilterField>
    </Filters>
    <PlatformQueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        <DataList caption={tx("Yedekleme bilgisayarları")} rows={data.items} columns={columns} keyExtractor={(agent) => `${agent.tenantId}:${agent.agentId}`} emptyMessage={filtered ? tx('Filtrelere uygun bilgisayar bulunamadı.') : tx('Henüz hiçbir çalışma alanına yedekleme bilgisayarı eklenmedi.')} />
        {data.totalCount > 0 && <Pagination label={tx("Bilgisayar sayfaları")} page={page} pageSize={data.pageSize} totalCount={data.totalCount} disabled={query.isFetching} onPageChange={(next) => update({ page: next })} />}
      </div>}
    </PlatformQueryState>
  </div>
}
