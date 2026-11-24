import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { DataList, type Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useAgent, agentQueryKeys } from '@/features/agents/hooks'
import { revokeAgent } from '@/features/agents/api'
import { AgentTechnicalDetails } from '@/features/agents/agent-technical-details'
import { AgentLogs } from '@/features/agent-logs/agent-logs'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { backupJobQueryKeys, useBackupJobs } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { notFound } from '@/lib/api/failure-messages'
import { formatDateTime } from '@/lib/format'
import { supportsDiscovery } from '@/features/discovery/status'
import { linkClass } from '@/components/ui/form-styles'
import { compareDate, compareNumber, compareText } from '@/features/lists/compare'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { useClientList } from '@/features/lists/use-client-list'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { AgentDatabases } from './agent-databases'

export function ClientDetailPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const { id = '' } = useParams()
  return <AgentDetailContent key={`${tenantId}:${id}`} tenantId={tenantId} id={id} />
}

function AgentDetailContent({ tenantId, id }: { tenantId: string; id: string }) {
  useLanguage()
  const query = useAgent(tenantId, id)
  const client = useQueryClient()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const revoke = useMutation({ mutationFn: () => revokeAgent(tenantId, id), onSuccess: async () => {
    setConfirmOpen(false)
    await Promise.all([
      client.invalidateQueries({ queryKey: agentQueryKeys.all(tenantId) }),
      client.invalidateQueries({ queryKey: backupJobQueryKeys.all(tenantId) }),
      client.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
    ])
  } })
  const agent = query.data
  const crumbs = [{ label: tx("Yedekleme bilgisayarları"), to: `/tenants/${tenantId}/agents` }, { label: agent?.hostName ?? tx('Adsız bilgisayar') }]
  return <div className="space-y-6">
    {!id ? <><PageHeader title={tx("Yedekleme bilgisayarı bulunamadı")} crumbs={crumbs} /><ErrorNotice {...notFound(tx("Yedekleme bilgisayarı bulunamadı."))} /></> : <QueryState isPending={query.isPending} error={query.error}>
      {agent && <>
        <PageHeader crumbs={crumbs} title={agent.hostName ?? tx('Adsız bilgisayar')}
          description={<>{tx("Son görülme")}: {agent.lastSeenAt ? agent.status === 'Revoked' ? formatDateTime(agent.lastSeenAt) : <RelativeTime value={agent.lastSeenAt} /> : tx('Henüz bağlanmadı')}</>}
          meta={agent.status === 'Revoked' ? <StatusPill domain="agent" status="Revoked" /> : <><StatusPill domain="agent" status={agent.connectivity} />{agent.status !== 'Active' && <StatusPill domain="agent" status={agent.status} />}</>}
          actions={<>
            {agent.status !== 'Revoked' && <Button variant="destructive" onClick={() => { revoke.reset(); setConfirmOpen(true) }}>{tx("Bilgisayar kaydını iptal et")}</Button>}
          </>} />
        {agent.status === 'Active' && agent.connectivity === 'offline' && <Notice tone="warning">{tx("Bu bilgisayar çevrimdışı; yedeklemeler bilgisayar yeniden bağlanana kadar çalışmaz. Bilgisayarın açık olduğunu ve yedekleme programının çalıştığını kontrol edin.")}</Notice>}
        {agent.status !== 'Revoked' && supportsDiscovery(agent.capabilities) && <AgentDatabases agentId={id} />}
        <AgentJobs agentId={id} />
        <AgentTechnicalDetails agent={agent} />
        <AgentLogs tenantId={tenantId} agentId={id} />
      </>}
    </QueryState>}
    <ConfirmDialog open={confirmOpen} onClose={() => setConfirmOpen(false)} title={tx("Bilgisayar kaydı iptal edilsin mi?")}
      consequence={{
        what: tx("Bu yedekleme bilgisayarı artık bu çalışma alanına bağlanamaz."),
        who: tx("Bu bilgisayarda çalışan yedekleme işleri durur."),
        reversible: tx("Hayır. Yeniden kayıt için yeni bir kimlik gerekir."),
      }}
      confirmLabel={tx("İptali onayla")} destructive pending={revoke.isPending} error={revoke.error} onConfirm={() => { revoke.mutate() }} />
  </div>
}

const jobsListConfig = {
  search: true,
  sort: { first: { name: 'asc', enabled: 'asc', lastRun: 'asc', next: 'asc', created: 'desc' }, default: { key: 'created', dir: 'desc' } },
} as const
const jobComparators = {
  name: (a: BackupJob, b: BackupJob) => compareText(a.name, b.name),
  enabled: (a: BackupJob, b: BackupJob) => compareNumber(Number(b.enabled && !b.agentRevoked), Number(a.enabled && !a.agentRevoked)),
  lastRun: (a: BackupJob, b: BackupJob) => compareText(a.lastRun?.status, b.lastRun?.status),
  next: (a: BackupJob, b: BackupJob) => compareDate(a.agentRevoked ? null : a.nextOccurrenceAt, b.agentRevoked ? null : b.nextOccurrenceAt),
  created: (a: BackupJob, b: BackupJob) => compareDate(a.createdAt, b.createdAt),
}

function AgentJobs({ agentId }: { agentId: string }) {
  useLanguage()
  const { tenantId } = useTenant()
  const jobs = useBackupJobs()
  const state = useListState(jobsListConfig)
  const rows = (jobs.data ?? []).filter((job) => job.agentId === agentId)
  const list = useClientList(rows, state, { searchText: job => job.name, comparators: jobComparators, tieBreak: (a, b) => compareText(a.name, b.name) || compareText(a.id, b.id) })
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: tx('Yedekleme işi'), sortable: true, card: 'title', wrap: true, render: (job) => <Link className={`font-medium ${linkClass}`} to={`/tenants/${tenantId}/backup-jobs/${encodeURIComponent(job.id)}`}>{job.name}</Link> },
    { key: 'enabled', card: 'status', header: tx('Durum'), sortable: true, render: (job) => <StatusPill status={job.enabled && !job.agentRevoked ? 'Active' : 'Disabled'} /> },
    { key: 'lastRun', header: tx('Son sonuç'), sortable: true, render: (job) => job.lastRun ? <div className="flex flex-wrap items-center gap-2"><StatusPill status={job.lastRun.status} /><span className="text-sm text-text-muted"><RelativeTime value={job.lastRun.createdAt} /></span></div> : tx('Henüz çalışmadı') },
    { key: 'next', header: tx('Sonraki yedek'), sortable: true, render: (job) => job.nextOccurrenceAt && !job.agentRevoked ? <RelativeTime value={job.nextOccurrenceAt} /> : tx('Zamanlanmadı') },
    { key: 'created', header: tx('Oluşturulma'), sortable: true, render: job => <RelativeTime value={job.createdAt} /> },
  ]
  return <Section title={tx("Bu bilgisayarın yedekleme işleri")} flush>
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      <div className="space-y-4">
        {rows.length > 10 && <ListToolbar state={state} label={tx('İş filtreleri')} total={list.total} search={{ label: tx('Yedekleme işi ara') }} />}
        <DataList rows={list.rows} columns={columns} keyExtractor={(job) => job.id} emptyMessage={state.filtering ? tx('Aramanızla eşleşen iş yok.') : tx("Bu bilgisayar için tanımlı yedekleme işi yok.")} onClearFilters={state.filtering ? state.clear : undefined} sort={sortProps(state)} className="rounded-none border-0" />
      </div>
    </QueryState>
  </Section>
}
