import { useState } from 'react'
import { Link } from 'react-router-dom'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { useAgents } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useBackups } from '@/features/backups/hooks'
import { dateFilterInstant, formatDuration, formatTimestamp, runStatuses, runStatusLabels, triggerLabel } from '@/features/backups/presentation'
import type { BackupRun, BackupRunFilters, BackupRunStatus, BackupTrigger } from '@/features/backups/types'
import { useTenant } from '@/features/tenancy/use-tenant'
import { errorMessage } from '@/lib/api/failure-messages'

const controlClass = 'h-9 w-full rounded-lg border border-border bg-surface px-3 text-sm text-text focus-visible:outline-2 focus-visible:outline-primary'

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
  const columns: Column<BackupRun>[] = [
    { key: 'job', header: 'İş', render: (run) => <Link className="font-medium text-primary hover:underline" to={`/tenants/${tenantId}/backup-runs/${run.id}`}>{run.jobName ?? jobs.data?.find((job) => job.id === run.jobId)?.name ?? 'Yedekleme işi'}</Link> },
    { key: 'database', header: 'Veritabanı', render: (run) => run.configuration.database },
    { key: 'agent', header: 'Ajan', render: (run) => run.agentName ?? agents.data?.find((agent) => agent.agentId === run.agentId)?.hostName ?? 'Ajan' },
    { key: 'trigger', header: 'Tetikleyici', render: (run) => triggerLabel(run.triggerType) },
    { key: 'scheduled', header: 'Planlanan zaman', render: (run) => formatTimestamp(run.scheduledFor) },
    { key: 'started', header: 'Başlangıç', render: (run) => formatTimestamp(run.startedAt) },
    { key: 'duration', header: 'Süre', render: (run) => formatDuration(run.startedAt, run.completedAt) },
    { key: 'status', header: 'Durum', render: (run) => <StatusPill status={run.status} /> },
    { key: 'attempts', header: 'Deneme sayısı', render: (run) => `${run.currentAttemptNumber} / ${run.retry.maxAttempts}` },
  ]

  return (
    <div className="space-y-5">
      <header>
        <h1 className="text-2xl font-semibold text-text-heading">Yedekleme geçmişi</h1>
        <p className="mt-1 text-sm text-text-muted">İş çalıştırmaları, denemeler ve sonuçları. Aktif kayıtlar görünür sekmede güncellenir.</p>
      </header>
      <section aria-label="Yedekleme filtreleri" className="grid gap-3 rounded-lg border border-border bg-surface p-4 sm:grid-cols-2 xl:grid-cols-3">
        <label className="space-y-1 text-sm">Durum
          <select aria-label="Durum" className={controlClass} value={filters.status ?? ''} onChange={(event) => updateFilters({ status: event.target.value as BackupRunStatus | '' })}>
            <option value="">Tüm durumlar</option>
            {runStatuses.map((status) => <option key={status} value={status}>{runStatusLabels[status]}</option>)}
          </select>
        </label>
        <label className="space-y-1 text-sm">İş
          <select aria-label="İş" className={controlClass} value={filters.jobId ?? ''} onChange={(event) => updateFilters({ jobId: event.target.value })} disabled={jobs.isPending || Boolean(jobs.error)}>
            <option value="">Tüm işler</option>
            {jobs.data?.map((job) => <option key={job.id} value={job.id}>{job.name}</option>)}
          </select>
        </label>
        <label className="space-y-1 text-sm">Ajan
          <select aria-label="Ajan" className={controlClass} value={filters.agentId ?? ''} onChange={(event) => updateFilters({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}>
            <option value="">Tüm ajanlar</option>
            {agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? 'Adsız ajan'}</option>)}
          </select>
        </label>
        <label className="space-y-1 text-sm">Tetikleyici
          <select aria-label="Tetikleyici" className={controlClass} value={filters.trigger ?? ''} onChange={(event) => updateFilters({ trigger: event.target.value as BackupTrigger | '' })}>
            <option value="">Tüm tetikleyiciler</option><option value="Manual">Elle başlatıldı</option><option value="Scheduled">Zamanlanmış</option><option value="Retry">Yeniden denenmiş çalıştırmalar</option>
          </select>
        </label>
        <label className="space-y-1 text-sm">Oluşturulma başlangıcı
          <input aria-label="Oluşturulma başlangıcı" type="date" max={toDay || undefined} className={controlClass} value={fromDay} onChange={(event) => { setFromDay(event.target.value); updateFilters({ from: dateFilterInstant(event.target.value) }) }} />
        </label>
        <label className="space-y-1 text-sm">Oluşturulma sonu
          <input aria-label="Oluşturulma sonu" type="date" min={fromDay || undefined} className={controlClass} value={toDay} onChange={(event) => { setToDay(event.target.value); updateFilters({ to: dateFilterInstant(event.target.value, true) }) }} />
        </label>
        <div className="flex flex-wrap items-center gap-3 sm:col-span-2 xl:col-span-3">
          <Button variant="outline" onClick={() => { setFilters({ page: 1, pageSize: 25 }); setFromDay(''); setToDay('') }}>Filtreleri temizle</Button>
          <span className="text-xs text-text-muted">Tarihler yerel saat diliminizdeki oluşturulma günlerini kapsar.</span>
        </div>
        {invalidDateRange && <p role="alert" className="text-sm text-error sm:col-span-2 xl:col-span-3">Başlangıç tarihi bitiş tarihinden sonra olamaz.</p>}
        {(jobs.error || agents.error) && <p role="alert" className="text-sm text-error sm:col-span-2 xl:col-span-3">Filtre seçenekleri yüklenemedi: {errorMessage(jobs.error ?? agents.error)}</p>}
      </section>
      <QueryState isPending={backups.isPending} error={backups.error} empty={backups.data?.totalCount === 0}>
        <DataTable rows={backups.data?.items ?? []} columns={columns} keyExtractor={(run) => run.id} emptyMessage="Bu filtrelerde yedekleme kaydı bulunamadı." />
      </QueryState>
      {backups.data && backups.data.totalCount > 0 && <nav aria-label="Yedekleme sayfaları" className="flex flex-wrap items-center justify-between gap-3 text-sm">
        <span className="text-text-muted">{backups.data.totalCount} kayıt · Sayfa {page} / {Math.max(1, Math.ceil(backups.data.totalCount / pageSize))}</span>
        <div className="flex gap-2">
          <Button variant="outline" disabled={page <= 1 || backups.isFetching} onClick={() => setFilters((current) => ({ ...current, page: page - 1 }))}>Önceki</Button>
          <Button variant="outline" disabled={page * pageSize >= backups.data.totalCount || backups.isFetching} onClick={() => setFilters((current) => ({ ...current, page: page + 1 }))}>Sonraki</Button>
        </div>
      </nav>}
    </div>
  )
}
