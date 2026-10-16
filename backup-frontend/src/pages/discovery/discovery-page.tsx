import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Radar } from 'lucide-react'
import { DataTable, type Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Badge } from '@/components/ui/badge'
import { useAgents } from '@/features/agents/hooks'
import { agentDate } from '@/features/agents/format'
import { AccessPill, FullBackupPill, LastChangePill, PresencePill, ReachabilityPill } from '@/features/discovery/presentation'
import { useDiscoveryAgents, useDiscoveryInstances } from '@/features/discovery/hooks'
import { engineLabels, sourceLabels, supportsDiscovery } from '@/features/discovery/status'
import type { DiscoveryEngine, DiscoveryInstanceSummary } from '@/features/discovery/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { ApiError } from '@/lib/api/client'
import { discoveryInstancePath } from '@/lib/routes'
import { AgentStatusBar } from './agent-status-bar'

const isForbidden = (error: unknown) => error instanceof ApiError && error.status === 403

export function DiscoveryPage() {
  const { tenant, tenantId } = useTenant()
  const agents = useAgents(tenantId)
  const statuses = useDiscoveryAgents()
  const [agentId, setAgentId] = useState('')
  const [engine, setEngine] = useState<'' | DiscoveryEngine>('')
  const [includeMissing, setIncludeMissing] = useState(false)
  const instances = useDiscoveryInstances({ ...(agentId ? { agentId } : {}), ...(engine ? { engine } : {}), ...(includeMissing ? { includeMissing } : {}) })
  const agentList = agents.data ?? []
  const agentName = (id: string) => agentList.find(item => item.agentId === id)?.hostName || id
  const columns: Column<DiscoveryInstanceSummary>[] = [
    { key: 'name', header: 'Instance', wrap: true, render: item => <Link className="font-medium text-primary underline-offset-4 hover:underline" to={discoveryInstancePath(tenantId, item.id)}>{item.displayName}</Link> },
    { key: 'agent', header: 'Agent', wrap: true, render: item => agentName(item.agentId) },
    { key: 'engine', header: 'Motor', render: item => engineLabels[item.engine] ?? item.engine },
    { key: 'source', header: 'Kaynak', render: item => sourceLabels[item.source] ?? item.source },
    { key: 'address', header: 'Adres', render: item => `${item.host}${item.port != null ? `:${item.port}` : ''}` },
    { key: 'version', header: 'Sürüm / edition', wrap: true, render: item => [item.version, item.edition].filter(Boolean).join(' · ') || '—' },
    { key: 'reach', header: 'Erişilebilirlik', render: item => <ReachabilityPill value={item.reachability} /> },
    { key: 'access', header: 'Erişim', wrap: true, render: item => <AccessPill value={item.access} /> },
    { key: 'backup', header: 'Full Backup', wrap: true, render: item => <FullBackupPill value={item.fullBackup} /> },
    { key: 'seen', header: 'Son görülme', render: item => agentDate(item.lastSeenAt) },
    { key: 'change', header: 'Değişiklik', render: item => <span className="flex flex-wrap gap-1">{item.presence === 'Missing' && <PresencePill value="Missing" />}{item.lastChange ? <LastChangePill value={item.lastChange.kind} /> : null}</span> },
  ]
  let body
  if (isForbidden(instances.error) || isForbidden(statuses.error)) body = <Notice tone="error" title="Yetki yok">Keşfedilen veritabanlarını görüntüleme yetkiniz yok.</Notice>
  else if (agents.isSuccess && agentList.length === 0) body = <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Radar} title="Henüz Agent yok.">Veritabanları, kayıtlı bir Agent'ın bulunduğu makinede keşfedilir. Önce bir Agent kaydedin.</EmptyState></div>
  else if (agents.isSuccess && !agentList.some(item => supportsDiscovery(item.capabilities))) body = <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Radar} title="Agent sürümü keşfi desteklemiyor.">Hiçbir Agent veritabanı keşfi yeteneğini bildirmedi. Agent'ları güncelleyin; bu arada veritabanlarını Backup Job sihirbazında elle girebilirsiniz.</EmptyState></div>
  else body = <>
    <div className="grid gap-4 lg:grid-cols-2">{agentList.map(agent => <AgentStatusBar key={agent.agentId} agent={agent} status={statuses.data?.items.find(item => item.agentId === agent.agentId)} />)}</div>
    <Filters label="Keşif filtreleri" footer={<label className="flex min-h-11 items-center gap-3 text-sm text-text"><input type="checkbox" className="size-5 accent-primary" checked={includeMissing} onChange={event => setIncludeMissing(event.target.checked)} />Kaybolanları göster</label>}>
      <FilterField label="Motor"><select aria-label="Motor" className={filterControlClass} value={engine} onChange={event => setEngine(event.target.value as '' | DiscoveryEngine)}><option value="">Tümü</option><option value="mssql">MSSQL</option><option value="postgres">PostgreSQL</option></select></FilterField>
      <FilterField label="Agent"><select aria-label="Agent filtresi" className={filterControlClass} value={agentId} onChange={event => setAgentId(event.target.value)}><option value="">Tümü</option>{agentList.map(item => <option key={item.agentId} value={item.agentId}>{item.hostName || item.agentId}</option>)}</select></FilterField>
    </Filters>
    <QueryState isPending={instances.isPending} error={instances.error}>
      {instances.data && (instances.data.items.length === 0
        ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Radar} title="Keşfedilen Instance yok.">Henüz bir tarama sonuç vermedi veya filtreler eşleşmedi. 'Yeniden Tara' ile yeni bir tarama isteyebilirsiniz.</EmptyState></div>
        : <DataTable rows={instances.data.items} columns={columns} keyExtractor={item => item.id} />)}
    </QueryState>
  </>
  return <div className="space-y-6">
    <PageHeader title="Keşfedilen Veritabanları" description={`${tenant.name} · Agent makinelerinde bulunan MSSQL ve PostgreSQL Instance'ları.`} meta={<Badge tone="info">Keşif hiçbir Backup Job oluşturmaz</Badge>} />
    {body}
  </div>
}
