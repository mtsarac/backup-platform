import { useState } from 'react'
import { Activity } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useAgents } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useBackups } from '@/features/backups/hooks'
import { dateFilterInstant, formatDuration, formatTimestamp, runStatuses, runStatusLabels, triggerLabel } from '@/features/backups/presentation'
import { ReplicationBadge } from '@/features/backups/replication'
import type { BackupRun, BackupRunFilters, BackupRunStatus, BackupTrigger } from '@/features/backups/types'
import { linkClass } from '@/features/platform/styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage } from '@/lib/api/failure-messages'

export function BackupsPage() {
  const { tenantId } = useTenant()
  const [filters, setFilters] = useState<BackupRunFilters>({ page: 1, pageSize: 25 })
  const [fromDay, setFromDay] = useState('')
  const [toDay, setToDay] = useState('')
  const backups = useBackups(filters)
  const jobs = useBackupJobs()
  const agents = useAgents(tenantId)
  const page = filters.page ?? 1
  const pageSize = filters.pageSize ?? 25
  const invalidDateRange = Boolean(fromDay && toDay && fromDay > toDay)
  const updateFilters = (patch: Partial<BackupRunFilters>) => setFilters((current) => ({ ...current, ...patch, page: 1 }))
  const clearFilters = () => { setFilters({ page: 1, pageSize: 25 }); setFromDay(''); setToDay('') }
  const columns: Column<BackupRun>[] = [
    { key: 'job', header: 'İş', wrap: true, render: (run) => <div className="space-y-0.5">
      <Link className={linkClass} to={`/tenants/${tenantId}/backup-runs/${run.id}`}>{run.jobName ?? jobs.data?.find((job) => job.id === run.jobId)?.name ?? 'Backup Job'}</Link>
      <p className="text-sm text-text-muted">{run.configuration.database} · {run.agentName ?? agents.data?.find((agent) => agent.agentId === run.agentId)?.hostName ?? 'Agent'}</p>
    </div> },
    { key: 'status', header: 'Durum', render: (run) => <div className="flex flex-col items-start gap-1">
      <StatusPill status={run.status} /><ReplicationBadge replication={run.replication} />
      <span className="text-sm text-text-muted">Deneme {run.currentAttemptNumber} / {run.retry.maxAttempts}</span>
    </div> },
    { key: 'trigger', header: 'Tetikleyici', wrap: true, render: (run) => <div>{triggerLabel(run.triggerType)}<p className="text-sm text-text-muted">Planlanan: {formatTimestamp(run.scheduledFor)}</p></div> },
    { key: 'started', header: 'Başlangıç', render: (run) => formatTimestamp(run.startedAt) },
    { key: 'duration', header: 'Süre', render: (run) => formatDuration(run.startedAt, run.completedAt) },
  ]
  const filterError = jobs.error ?? agents.error

  return (
    <div className="space-y-6">
      <PageHeader title="Yedekleme geçmişi" description="Backup Job çalıştırmaları, denemeleri ve sonuçları. Aktif kayıtlar görünür sekmede güncellenir." />
      <Filters label="Yedekleme filtreleri" onClear={clearFilters}
        footer={<span className="text-sm text-text-muted">Tarihler yerel saat diliminizdeki oluşturulma günlerini kapsar.</span>}>
        <FilterField label="Durum">
          <select aria-label="Durum" className={filterControlClass} value={filters.status ?? ''} onChange={(event) => updateFilters({ status: event.target.value as BackupRunStatus | '' })}>
            <option value="">Tüm durumlar</option>
            {runStatuses.map((status) => <option key={status} value={status}>{runStatusLabels[status]}</option>)}
          </select>
        </FilterField>
        <FilterField label="İş">
          <select aria-label="İş" className={filterControlClass} value={filters.jobId ?? ''} onChange={(event) => updateFilters({ jobId: event.target.value })} disabled={jobs.isPending || Boolean(jobs.error)}>
            <option value="">Tüm işler</option>
            {jobs.data?.map((job) => <option key={job.id} value={job.id}>{job.name}</option>)}
          </select>
        </FilterField>
        <FilterField label="Ajan">
          <select aria-label="Ajan" className={filterControlClass} value={filters.agentId ?? ''} onChange={(event) => updateFilters({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}>
            <option value="">Tüm ajanlar</option>
            {agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? 'Adsız ajan'}</option>)}
          </select>
        </FilterField>
        <FilterField label="Tetikleyici">
          <select aria-label="Tetikleyici" className={filterControlClass} value={filters.trigger ?? ''} onChange={(event) => updateFilters({ trigger: event.target.value as BackupTrigger | '' })}>
            <option value="">Tüm tetikleyiciler</option><option value="Manual">Elle başlatıldı</option><option value="Scheduled">Zamanlanmış</option><option value="Retry">Yeniden denenmiş çalıştırmalar</option>
          </select>
        </FilterField>
        <FilterField label="Oluşturulma başlangıcı">
          <input aria-label="Oluşturulma başlangıcı" type="date" max={toDay || undefined} className={filterControlClass} value={fromDay} onChange={(event) => { setFromDay(event.target.value); updateFilters({ from: dateFilterInstant(event.target.value) }) }} />
        </FilterField>
        <FilterField label="Oluşturulma sonu">
          <input aria-label="Oluşturulma sonu" type="date" min={fromDay || undefined} className={filterControlClass} value={toDay} onChange={(event) => { setToDay(event.target.value); updateFilters({ to: dateFilterInstant(event.target.value, true) }) }} />
        </FilterField>
      </Filters>
      {invalidDateRange && <Notice tone="error">Başlangıç tarihi bitiş tarihinden sonra olamaz.</Notice>}
      {filterError && <Notice tone="error">Filtre seçenekleri yüklenemedi: {errorMessage(filterError)}</Notice>}
      <QueryState isPending={backups.isPending} error={backups.error}>
        {backups.data && (backups.data.totalCount === 0
          ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={Activity} title="Bu filtrelerde Backup Run bulunamadı." action={<Button variant="outline" onClick={clearFilters}>Filtreleri temizle</Button>}>Filtreleri değiştirin veya temizleyin. Yeni çalıştırmalar Backup Job&apos;lardan başlatılır.</EmptyState></div>
          : <>
            <DataTable rows={backups.data.items} columns={columns} keyExtractor={(run) => run.id} />
            <Pagination label="Yedekleme sayfaları" page={page} pageSize={pageSize} totalCount={backups.data.totalCount} disabled={backups.isFetching} onPageChange={(next) => setFilters((current) => ({ ...current, page: next }))} />
          </>)}
      </QueryState>
    </div>
  )
}
