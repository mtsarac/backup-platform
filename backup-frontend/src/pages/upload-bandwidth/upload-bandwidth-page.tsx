import { useState } from 'react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { Pagination } from '@/components/data/pagination'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { useAgents } from '@/features/agents/hooks'
import type { Agent } from '@/features/agents/types'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { LimitDialog } from '@/features/upload-bandwidth/limit-dialog'
import { useUploadBandwidth } from '@/features/upload-bandwidth/hooks'
import type { BandwidthTarget } from '@/features/upload-bandwidth/types'
import { formatBandwidth, formatExactRate } from '@/features/upload-bandwidth/units'

type Tab = 'tenant' | 'agents' | 'jobs'
const PAGE_SIZE = 25

interface Editing { target: BandwidthTarget; name: string; current: number | null }

function Limit({ value }: { value: number | null | undefined }) {
  if (value == null) return <span className="text-text-muted">Limitsiz</span>
  return <span>{formatBandwidth(value)} <span className="text-xs text-text-muted">({formatExactRate(value)})</span></span>
}

/** Client-side search and paging: both lists come complete from their own endpoints. */
function SearchableTable<T>({ rows, columns, keyExtractor, text, searchLabel, emptyMessage }: {
  rows: readonly T[]; columns: Column<T>[]; keyExtractor: (row: T) => string; text: (row: T) => string; searchLabel: string; emptyMessage: string
}) {
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const needle = query.trim().toLocaleLowerCase('tr-TR')
  const matches = needle ? rows.filter(row => text(row).toLocaleLowerCase('tr-TR').includes(needle)) : rows
  const lastPage = Math.max(1, Math.ceil(matches.length / PAGE_SIZE))
  const current = Math.min(page, lastPage)
  return <div className="space-y-4">
    <Filters label={`${searchLabel} filtreleri`}>
      <FilterField label={searchLabel}><input type="search" aria-label={searchLabel} placeholder={searchLabel} className={filterControlClass} value={query} onChange={event => { setQuery(event.target.value); setPage(1) }} /></FilterField>
    </Filters>
    {matches.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState title={needle ? 'Aramayla eşleşen kayıt yok.' : emptyMessage} /></div>
      : <DataTable rows={matches.slice((current - 1) * PAGE_SIZE, current * PAGE_SIZE)} columns={columns} keyExtractor={keyExtractor} />}
    {matches.length > PAGE_SIZE && <Pagination label={`${searchLabel} sayfaları`} page={current} pageSize={PAGE_SIZE} totalCount={matches.length} onPageChange={setPage} />}
  </div>
}

export function UploadBandwidthPage() {
  const { tenant, tenantId, canAdmin } = useTenant()
  const limits = useUploadBandwidth()
  const agents = useAgents(tenantId)
  const jobs = useBackupJobs()
  const [editing, setEditing] = useState<Editing | null>(null)
  const [tab, setTab] = useState<Tab>('tenant')
  const data = limits.data
  const agentLimits = new Map(data?.agents.map(item => [item.agentId, item.bytesPerSecond]))
  const jobLimits = new Map(data?.jobs.map(item => [item.jobId, item.bytesPerSecond]))
  const edit = (target: BandwidthTarget, name: string, current: number | null | undefined) => canAdmin
    ? <Button variant="outline" size="sm" aria-label={`${name} yükleme hız sınırını düzenle`} onClick={() => setEditing({ target, name, current: current ?? null })}>Düzenle</Button>
    : null
  const agentColumns: Column<Agent>[] = [
    { key: 'name', header: 'Agent', wrap: true, render: agent => agent.hostName || agent.agentId },
    { key: 'limit', header: 'Hız sınırı', wrap: true, render: agent => <Limit value={agentLimits.get(agent.agentId)} /> },
    ...(canAdmin ? [{ key: 'actions', header: 'İşlemler', render: (agent: Agent) => edit({ scope: 'agent', id: agent.agentId }, agent.hostName || agent.agentId, agentLimits.get(agent.agentId)) }] : []),
  ]
  const jobColumns: Column<BackupJob>[] = [
    { key: 'name', header: 'Backup Job', wrap: true, render: job => job.name },
    { key: 'agent', header: 'Agent', wrap: true, render: job => job.agentName || '—' },
    { key: 'limit', header: 'Hız sınırı', wrap: true, render: job => <Limit value={jobLimits.get(job.id)} /> },
    ...(canAdmin ? [{ key: 'actions', header: 'İşlemler', render: (job: BackupJob) => edit({ scope: 'job', id: job.id }, job.name, jobLimits.get(job.id)) }] : []),
  ]
  return <div className="space-y-6">
    <PageHeader title="Upload Bandwidth" description={`${tenant.name} · Yedekleme yüklemeleri için bayt/saniye cinsinden hız sınırları.`} />
    <Notice tone="info">Sınırlar birbirinden bağımsız üst sınırlardır ve birlikte uygulanır; en kısıtlayıcı olan geçerli olur. Değişiklikler çalışan yüklemelere birkaç saniye içinde yansır. Geri yükleme/indirme, checksum ve heartbeat sınırlanmaz.</Notice>
    {!canAdmin && <Notice tone="warning">Hız sınırlarını yalnızca Owner / Admin değiştirebilir.</Notice>}
    <Tabs label="Hız sınırı kapsamı" idPrefix="bandwidth" value={tab} onChange={setTab} items={[{ value: 'tenant', label: 'Tenant limiti' }, { value: 'agents', label: 'Agent\'lar' }, { value: 'jobs', label: 'Backup Job\'lar' }]} />
    <div role="tabpanel" id={`bandwidth-panel-${tab}`} aria-labelledby={`bandwidth-tab-${tab}`}>
      <QueryState isPending={limits.isPending} error={limits.error}>
        {tab === 'tenant' && <Section title="Tenant sınırı" description="Tenant'ın tüm eş zamanlı yüklemelerinin toplamı için üst sınır." actions={edit({ scope: 'tenant' }, tenant.name, data?.tenant.bytesPerSecond)}>
          <p className="text-xl font-semibold text-text-heading" data-testid="tenant-limit"><Limit value={data?.tenant.bytesPerSecond} /></p>
        </Section>}
        {tab === 'agents' && <QueryState isPending={agents.isPending} error={agents.error}>
          <h2 className="mb-4 text-base font-semibold text-text-heading">Agent sınırları</h2>
          <SearchableTable rows={agents.data ?? []} columns={agentColumns} keyExtractor={agent => agent.agentId} text={agent => agent.hostName || agent.agentId} searchLabel="Agent ara" emptyMessage="Agent bulunamadı." />
        </QueryState>}
        {tab === 'jobs' && <QueryState isPending={jobs.isPending} error={jobs.error}>
          <h2 className="mb-4 text-base font-semibold text-text-heading">Backup Job sınırları</h2>
          <SearchableTable rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} text={job => `${job.name} ${job.agentName ?? ''}`} searchLabel="Backup Job ara" emptyMessage="Backup Job bulunamadı." />
        </QueryState>}
      </QueryState>
    </div>
    {editing && data && <LimitDialog {...editing} minimum={data.minimumBytesPerSecond} maximum={data.maximumBytesPerSecond} onClose={() => setEditing(null)} />}
  </div>
}
