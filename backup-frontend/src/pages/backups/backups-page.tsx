import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Activity } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useAgents } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useBackups } from '@/features/backups/hooks'
import { dateFilterInstant, runStatuses } from '@/features/backups/presentation'
import { SizeAnomalyBadge, SizeAnomalyComparison } from '@/features/backups/size-anomaly'
import type { BackupRun, BackupRunFilters, BackupRunStatus, BackupTrigger } from '@/features/backups/types'
import { linkClass } from '@/components/ui/form-styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatDuration } from '@/lib/format'
import { statusLabel } from '@/lib/status'

export function BackupsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const [filters, setFilters] = useState<BackupRunFilters>({ page: 1, pageSize: 25 })
  const [fromDay, setFromDay] = useState('')
  const [toDay, setToDay] = useState('')
  const backups = useBackups(filters)
  const jobs = useBackupJobs()
  const agents = useAgents(tenantId, true)
  const page = filters.page ?? 1
  const pageSize = filters.pageSize ?? 25
  const filtering = Boolean(filters.status || filters.jobId || filters.agentId || filters.trigger || filters.from || filters.to)
  const invalidDateRange = Boolean(fromDay && toDay && fromDay > toDay)
  const updateFilters = (patch: Partial<BackupRunFilters>) => setFilters((current) => ({ ...current, ...patch, page: 1 }))
  const clearFilters = () => { setFilters({ page: 1, pageSize: 25 }); setFromDay(''); setToDay('') }
  const columns: Column<BackupRun>[] = [
    { key: 'job', card: 'title', header: tx('İş'), wrap: true, render: (run) => <Link className={`font-medium ${linkClass}`} to={`/tenants/${tenantId}/backup-runs/${run.id}`}>{run.jobName ?? jobs.data?.find((job) => job.id === run.jobId)?.name ?? tx('Yedekleme işi')}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: (run) => <div className="flex flex-col items-start gap-1"><StatusPill status={run.status} /><SizeAnomalyBadge run={run} /><SizeAnomalyComparison run={run} /></div> },
    { key: 'started', header: tx('Başlangıç'), render: (run) => <RelativeTime value={run.startedAt} /> },
    { key: 'duration', header: tx('Süre'), render: (run) => formatDuration(run.startedAt, run.completedAt) },
  ]
  const filterError = jobs.error ?? agents.error

  return (
    <div className="space-y-6">
      <PageHeader title={tx("Yedekleme geçmişi")} description={tx("Her yedekleme işinin ne zaman çalıştığını ve nasıl sonuçlandığını buradan izleyin.")} />
      <Filters label={tx("Yedekleme filtreleri")} onClear={filtering ? clearFilters : undefined}
        footer={<span className="text-sm text-text-muted">{tx("Tarihler yerel saat diliminizdeki oluşturulma günlerini kapsar.")}</span>}>
        <FilterField label={tx("Durum")}>
          <select aria-label={tx("Durum")} className={filterControlClass} value={filters.status ?? ''} onChange={(event) => updateFilters({ status: event.target.value as BackupRunStatus | '' })}>
            <option value="">{tx("Tüm durumlar")}</option>
            {runStatuses.map((status) => <option key={status} value={status}>{statusLabel('general', status)}</option>)}
          </select>
        </FilterField>
        <FilterField label={tx("İş")}>
          <select aria-label={tx("İş")} className={filterControlClass} value={filters.jobId ?? ''} onChange={(event) => updateFilters({ jobId: event.target.value })} disabled={jobs.isPending || Boolean(jobs.error)}>
            <option value="">{tx("Tüm işler")}</option>
            {jobs.data?.map((job) => <option key={job.id} value={job.id}>{job.name}</option>)}
          </select>
        </FilterField>
        <FilterField label={tx("Yedekleme bilgisayarı")}>
          <select aria-label={tx("Yedekleme bilgisayarı")} className={filterControlClass} value={filters.agentId ?? ''} onChange={(event) => updateFilters({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}>
            <option value="">{tx("Tüm yedekleme bilgisayarları")}</option>
            {agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız yedekleme bilgisayarı')}</option>)}
          </select>
        </FilterField>
        <FilterField label={tx("Tetikleyici")}>
          <select aria-label={tx("Tetikleyici")} className={filterControlClass} value={filters.trigger ?? ''} onChange={(event) => updateFilters({ trigger: event.target.value as BackupTrigger | '' })}>
            <option value="">{tx("Tüm tetikleyiciler")}</option><option value="Manual">{tx("Elle başlatıldı")}</option><option value="Scheduled">{tx("Zamanlanmış")}</option><option value="Retry">{tx("Yeniden denemeler")}</option>
          </select>
        </FilterField>
        <FilterField label={tx("Oluşturulma başlangıcı")}>
          <input aria-label={tx("Oluşturulma başlangıcı")} type="date" max={toDay || undefined} className={filterControlClass} value={fromDay} onChange={(event) => { setFromDay(event.target.value); updateFilters({ from: dateFilterInstant(event.target.value) }) }} />
        </FilterField>
        <FilterField label={tx("Oluşturulma sonu")}>
          <input aria-label={tx("Oluşturulma sonu")} type="date" min={fromDay || undefined} className={filterControlClass} value={toDay} onChange={(event) => { setToDay(event.target.value); updateFilters({ to: dateFilterInstant(event.target.value, true) }) }} />
        </FilterField>
      </Filters>
      {invalidDateRange && <Notice tone="warning">{tx("Başlangıç tarihi bitiş tarihinden sonra olamaz.")}</Notice>}
      {filterError && <ErrorNotice title={tx("Filtre seçenekleri yüklenemedi")} error={filterError} />}
      <QueryState isPending={backups.isPending} error={backups.error}>
        {backups.data && (backups.data.totalCount === 0
          ? <div className="rounded-xl border border-border bg-surface">{filtering
            ? <EmptyState icon={Activity} message={tx("Bu filtrelere uygun yedekleme denemesi bulunamadı.")} action={<Button variant="outline" onClick={clearFilters}>{tx("Filtreleri temizle")}</Button>} />
            : <EmptyState icon={Activity} message={tx("Henüz yedekleme denemesi yok. Bir yedekleme işi çalıştığında burada görünür.")} action={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-jobs`}>{tx("Yedekleme işlerine git")}</Link>} />}</div>
          : <>
            <DataList rows={backups.data.items} columns={columns} keyExtractor={(run) => run.id} />
            <Pagination label={tx("Yedekleme sayfaları")} page={page} pageSize={pageSize} totalCount={backups.data.totalCount} disabled={backups.isFetching} onPageChange={(next) => setFilters((current) => ({ ...current, page: next }))} />
          </>)}
      </QueryState>
    </div>
  )
}
