import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Radar } from 'lucide-react'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { AccessDenied } from '@/components/auth/access-denied'
import { buttonVariants } from '@/components/ui/button-variants'
import { Button } from '@/components/ui/button'
import { ErrorNotice } from '@/components/ui/notice'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { linkClass } from '@/components/ui/form-styles'
import { PageHeader } from '@/components/ui/page-header'
import { useAgents } from '@/features/agents/hooks'
import { useDiscoveryAgents, useDiscoveryInstances } from '@/features/discovery/hooks'
import { supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryEngine, DiscoveryInstanceSummary } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { discoveryInstancePath } from '@/lib/routes'
import { ComputerScanCard } from './computer-scan-card'

const isForbidden = (error: unknown) => error instanceof ApiError && error.status === 403
const framed = 'rounded-xl border border-border bg-surface'

export function DiscoveryPage() {
  useLanguage()
  const { tenantId, canAdmin, canOperate } = useTenant()
  const agents = useAgents(tenantId)
  const statuses = useDiscoveryAgents()
  const [agentId, setAgentId] = useState('')
  const [engine, setEngine] = useState<'' | DiscoveryEngine>('')
  const [includeMissing, setIncludeMissing] = useState(false)
  const instances = useDiscoveryInstances({ ...(agentId ? { agentId } : {}), ...(engine ? { engine } : {}), ...(includeMissing ? { includeMissing } : {}) })
  const agentList = agents.data ?? []
  const agentName = (id: string) => agentList.find(item => item.agentId === id)?.hostName || tx('Adsız bilgisayar')
  const computersPath = `/tenants/${tenantId}/agents`
  const filtered = Boolean(agentId || engine || includeMissing)
  const columns: Column<DiscoveryInstanceSummary>[] = [
    { key: 'name', card: 'title', header: tx('Veritabanı sunucusu'), wrap: true, render: item => <Link className={linkClass} to={discoveryInstancePath(tenantId, item.id)}>{item.displayName}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: item => <span className="flex flex-wrap gap-1"><StatusPill domain="access" status={item.access} />{item.presence === 'Missing' && <StatusPill level="neutral">{tx("Bulunamadı")}</StatusPill>}</span> },
    { key: 'computer', header: tx('Bilgisayar'), wrap: true, render: item => agentName(item.agentId) },
    { key: 'eligible', header: tx('Yedeklenebilir veritabanı'), render: item => item.access === 'Authenticated' ? item.databaseCounts.backupEligible : '—' },
    { key: 'seen', header: tx('Son görülme'), render: item => <RelativeTime value={item.lastSeenAt} /> },
  ]
  let body
  if (isForbidden(instances.error) || isForbidden(statuses.error) || isForbidden(agents.error)) body = <AccessDenied title={tx("Yetki yok")} what={tx("Veritabanı bulma sonuçlarını görüntüleme yetkiniz yok.")} />
  else if (agents.error || statuses.error) body = <ErrorNotice error={agents.error || statuses.error} />
  else if (agents.isPending) body = <QueryState isPending error={null} />
  else if (agents.isSuccess && agentList.length === 0) body = <div className={framed}><EmptyState icon={Radar} message={tx("Henüz yedekleme bilgisayarı yok; veritabanları bir yedekleme bilgisayarında aranır.")} canAct={canAdmin} action={<Link className={buttonVariants()} to={computersPath}>{tx("Yedekleme bilgisayarı ekle")}</Link>} /></div>
  else if (agents.isSuccess && !agentList.some(item => supportsDiscovery(item.capabilities))) body = <div className={framed}><EmptyState icon={Radar} message={tx("Yedekleme bilgisayarlarınızdaki program veritabanı bulmayı desteklemiyor; programı güncelleyin veya veritabanını yedekleme işinde elle girin.")} canAct={canAdmin} action={<Link className={buttonVariants()} to={computersPath}>{tx("Yedekleme bilgisayarlarına git")}</Link>} /></div>
  else body = <>
    <div className="grid gap-4 lg:grid-cols-2">{agentList.map(agent => <ComputerScanCard key={agent.agentId} agent={agent} status={statuses.data?.items.find(item => item.agentId === agent.agentId)} />)}</div>
    <Filters label={tx("Veritabanı bulma filtreleri")} footer={<label className="flex min-h-11 items-center gap-3 text-sm text-text"><input type="checkbox" className="size-5 accent-primary" checked={includeMissing} onChange={event => setIncludeMissing(event.target.checked)} />{tx("Bulunamayanları da göster")}</label>}>
      <FilterField label={tx("Veritabanı türü")}><select aria-label={tx("Veritabanı türü")} className={filterControlClass} value={engine} onChange={event => setEngine(event.target.value as '' | DiscoveryEngine)}><option value="">{tx("Tümü")}</option><option value="mssql">MSSQL</option><option value="postgres">PostgreSQL</option></select></FilterField>
      {agentList.length > 1 && <FilterField label={tx("Bilgisayar")}><select aria-label={tx("Bilgisayar")} className={filterControlClass} value={agentId} onChange={event => setAgentId(event.target.value)}><option value="">{tx("Tümü")}</option>{agentList.map(item => <option key={item.agentId} value={item.agentId}>{item.hostName || tx('Adsız bilgisayar')}</option>)}</select></FilterField>}
    </Filters>
    <QueryState isPending={instances.isPending} error={instances.error}>
      {instances.data && (instances.data.items.length === 0
        ? <div className={framed}><EmptyState icon={Radar} message={filtered ? tx("Seçilen filtrelere uyan veritabanı sunucusu bulunamadı.") : tx("Bu bilgisayarlarda henüz veritabanı bulunamadı. Bilgisayar kartındaki \"Yeniden tara\" ile yeniden arayabilirsiniz.")} canAct={filtered || canOperate} action={filtered
          ? <Button onClick={() => { setAgentId(''); setEngine(''); setIncludeMissing(false) }}>{tx("Filtreleri temizle")}</Button>
          : <Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-jobs/new`}>{tx("Yeni yedekleme işi")}</Link>} /></div>
        : <DataList rows={instances.data.items} columns={columns} keyExtractor={item => item.id} caption={tx("Bulunan veritabanı sunucuları")} />)}
    </QueryState>
  </>
  return <div className="space-y-6">
    <PageHeader title={tx("Veritabanı bulma")} description={tx("Yedekleme bilgisayarlarınızda bulunan veritabanları ve hangilerinin yedeklenebileceği.")} />
    {body}
  </div>
}
