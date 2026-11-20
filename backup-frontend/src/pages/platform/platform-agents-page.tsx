import { tx, useLanguage } from '@/lib/i18n'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { FilterField } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { platformCrumb } from '@/features/platform/crumbs'
import { usePlatformAgents } from '@/features/platform/hooks'
import { ListEmpty } from '@/features/lists/list-empty'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { platformPaths } from '@/features/platform/paths'
import { TenantPicker } from '@/features/platform/tenant-picker'
import type { PlatformAgent } from '@/features/platform/types'
import { QueryState } from '@/components/data/query-state'
import { platformOnlyMessage } from '@/lib/api/failure-messages'
import { Select } from '@/components/ui/text-input'

const agentListConfig = {
  search: true,
  filters: { tenantId: 'text', connectivity: ['online', 'offline', 'unknown'], status: ['PendingEnrollment', 'Active', 'Disabled', 'Revoked'] },
  sort: { first: { hostName: 'asc', tenantName: 'asc', status: 'asc', connectivity: 'asc', lastSeenAt: 'desc', enrolledAt: 'desc' }, default: { key: 'enrolledAt', dir: 'desc' } },
} as const

export function PlatformAgentsPage() {
  useLanguage()
  const state = useListState(agentListConfig)
  const query = usePlatformAgents(toServerQuery(state))
  const data = query.data
  const { tenantId, connectivity, status } = state.filters
  // OS, versions, capabilities and lifecycle details live on the computer's own page; the list only flags a lifecycle that is not Active.
  const columns: Column<PlatformAgent>[] = [
    { key: 'hostName', sortable: true, card: 'title', header: tx('Bilgisayar'), wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.agent(agent.tenantId, agent.agentId)}>{agent.hostName ?? tx('Adsız bilgisayar')}</Link> },
    { key: 'connectivity', sortable: true, card: 'status', header: tx('Bağlantı'), render: (agent) => <StatusPill domain="agent" status={agent.connectivity} /> },
    { key: 'status', sortable: true, header: tx('Kayıt durumu'), render: (agent) => <StatusPill domain="agent" status={agent.status} /> },
    { key: 'tenantName', sortable: true, header: tx('Çalışma alanı'), wrap: true, render: (agent) => <Link className={linkClass} to={platformPaths.tenant(agent.tenantId)}>{agent.tenantName}</Link> },
    { key: 'lastSeenAt', sortable: true, header: tx('Son görülme'), render: (agent) => agent.lastSeenAt ? <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı') },
    { key: 'enrolledAt', sortable: true, header: tx('Kayıt zamanı'), render: (agent) => agent.enrolledAt ? <RelativeTime value={agent.enrolledAt} /> : '—' },
  ]
  return <div className="space-y-6">
    <PageHeader crumbs={[platformCrumb]} title={tx("Yedekleme bilgisayarları")} description={tx("Tüm çalışma alanlarındaki yedekleme bilgisayarları ve çevrimiçi olup olmadıkları. Bu görünüm salt okunurdur.")} />
    <ListToolbar state={state} label={tx('Bilgisayar filtreleri')} total={data?.totalCount} search={{ label: tx('Bilgisayar ara'), placeholder: tx('Bilgisayar veya çalışma alanı adı') }}>
      <TenantPicker value={tenantId} onChange={(value) => state.setFilter('tenantId', value)} />
      <FilterField label={tx("Bağlantı")}><Select className="text-sm" value={connectivity} onChange={(event) => state.setFilter('connectivity', event.target.value)}><option value="">{tx("Tümü")}</option><option value="online">{tx("Çevrimiçi")}</option><option value="offline">{tx("Çevrimdışı")}</option><option value="unknown">{tx("Bilinmiyor")}</option></Select></FilterField>
      <FilterField label={tx("Kayıt durumu")}><Select className="text-sm" value={status} onChange={(event) => state.setFilter('status', event.target.value)}><option value="">{tx("Tümü")}</option><option value="PendingEnrollment">{tx("Kayıt bekliyor")}</option><option value="Active">{tx("Etkin")}</option><option value="Disabled">{tx("Devre dışı")}</option><option value="Revoked">{tx("İptal edildi")}</option></Select></FilterField>
    </ListToolbar>
    <QueryState forbiddenMessage={platformOnlyMessage} isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-4">
        {data.totalCount === 0 ? <ListEmpty message={tx('Henüz hiçbir çalışma alanına yedekleme bilgisayarı eklenmedi.')} filtering={state.filtering} filteredMessage={tx('Filtrelere uygun bilgisayar bulunamadı.')} onClear={state.clear} />
          : <DataList caption={tx('Yedekleme bilgisayarları')} rows={data.items} columns={columns} sort={sortProps(state)} keyExtractor={(agent) => `${agent.tenantId}:${agent.agentId}`} emptyMessage={tx('Filtrelere uygun bilgisayar bulunamadı.')} onClearFilters={state.filtering ? state.clear : undefined} />}
        <ListPager state={state} total={data.totalCount} label={tx('Bilgisayar sayfaları')} disabled={query.isFetching} />
      </div>}
    </QueryState>
  </div>
}
