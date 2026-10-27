import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { CheckField } from '@/components/ui/check-field'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Server } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { PageHeader } from '@/components/ui/page-header'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { AgentsTabs } from '@/features/agents/agents-tabs'
import { AddAgentDialog } from '@/features/agents/add-agent-dialog'
import { linkClass } from '@/components/ui/form-styles'
import { oneOf, useListParams } from '@/features/platform/use-list-params'

export function ClientsPage() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const { get, update } = useListParams()
  const showRevoked = get('revoked') === 'true'
  const query = useAgents(tenantId, showRevoked)
  const search = get('search')
  const connectivity = oneOf(get('connectivity'), ['online', 'offline', 'unknown'] as const)
  const [addingTenant, setAddingTenant] = useState<string | null>(null)
  const term = search.trim().toLocaleLowerCase(currentLocale())
  const filtered = search !== '' || connectivity !== undefined
  const rows = (query.data ?? []).filter((agent) =>
    (!connectivity || agent.connectivity === connectivity) &&
    (agent.hostName ?? tx('Adsız bilgisayar')).toLocaleLowerCase(currentLocale()).includes(term))
  const columns: Column<Agent>[] = [
    { key: 'hostName', header: tx('Bilgisayar'), card: 'title', wrap: true, render: (agent) => <Link className={linkClass} to={`/tenants/${tenantId}/agents/${agent.agentId}`}>{agent.hostName ?? tx('Adsız bilgisayar')}</Link> },
    { key: 'connectivity', card: 'status', header: tx('Durum'), render: (agent) => <div className="flex flex-wrap items-center gap-1.5"><StatusPill domain="agent" status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</div> },
    { key: 'lastSeenAt', header: tx('Son görülme'), render: (agent) => agent.lastSeenAt ? <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı') },
  ]
  const empty = query.data?.length === 0
  const addButton = canAdmin && <Button onClick={() => setAddingTenant(tenantId)}>{tx("Yedekleme bilgisayarı ekle")}</Button>
  return <div className="space-y-6">
    <PageHeader title={tx("Yedekleme bilgisayarları")} description={tx("Veritabanı yedeklerini alan bilgisayarlar ve açık olup olmadıkları.")} actions={!empty && addButton} />
    <AgentsTabs />
    <CheckField className="w-fit" label={tx("İptal edilenleri göster")} checked={showRevoked} onChange={(event) => update({ revoked: event.target.checked ? 'true' : '' })} />
    {!empty && <Filters label={tx("Bilgisayar filtreleri")} onClear={filtered ? () => update({ search: '', connectivity: '' }) : undefined}>
      <FilterField label={tx("Bilgisayar ara")} className="sm:col-span-2"><input type="search" aria-label={tx("Bilgisayar ara")} className={filterControlClass} placeholder={tx("Bilgisayar adı")} value={search} onChange={(event) => update({ search: event.target.value }, { replace: true })} /></FilterField>
      <FilterField label={tx("Durum")}><select className={filterControlClass} value={connectivity ?? 'all'} onChange={(event) => update({ connectivity: event.target.value === 'all' ? '' : event.target.value })}><option value="all">{tx("Tümü")}</option><option value="online">{tx("Çevrimiçi")}</option><option value="offline">{tx("Çevrimdışı")}</option><option value="unknown">{tx("Bilinmiyor")}</option></select></FilterField>
    </Filters>}
    <QueryState isPending={query.isPending} error={query.error}>
      {empty ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Server} message={tx("Henüz yedekleme bilgisayarı eklenmedi.")} canAct={canAdmin} action={addButton} /></div>
        : <DataList rows={rows} columns={columns} keyExtractor={(agent) => agent.agentId} caption={tx("Yedekleme bilgisayarları")} emptyMessage={filtered ? tx('Filtrelere uygun bilgisayar bulunamadı.') : tx('Henüz yedekleme bilgisayarı eklenmedi.')} />}
    </QueryState>
    {canAdmin && addingTenant === tenantId && <AddAgentDialog key={tenantId} tenantId={tenantId} onClose={() => setAddingTenant(null)} />}
  </div>
}
