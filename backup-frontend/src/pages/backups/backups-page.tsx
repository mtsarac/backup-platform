import { tx, useLanguage } from '@/lib/i18n'
import { Select, TextInput } from '@/components/ui/text-input'
import { Activity } from 'lucide-react'
import { Link } from 'react-router-dom'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters } from '@/components/ui/filters'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { useAgents } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useBackups } from '@/features/backups/hooks'
import { dateFilterInstant, runStatuses } from '@/features/backups/presentation'
import { SizeAnomalyBadge, SizeAnomalyComparison } from '@/features/backups/size-anomaly'
import type { BackupRun, BackupRunFilters } from '@/features/backups/types'
import { LIST_PAGE_SIZE, dayParam, oneOf, useListParams } from '@/features/platform/use-list-params'
import { linkClass } from '@/components/ui/form-styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatDuration } from '@/lib/format'
import { statusLabel } from '@/lib/status'

const pageSize = LIST_PAGE_SIZE

export function BackupsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const { page, get, update } = useListParams()
  const fromDay = dayParam(get('from'))
  const toDay = dayParam(get('to'))
  const status = oneOf(get('status'), runStatuses)
  const trigger = oneOf(get('trigger'), ['Manual', 'Scheduled', 'Retry'])
  const filters: BackupRunFilters = { page, pageSize, status, jobId: get('jobId') || undefined, agentId: get('agentId') || undefined, trigger, from: dateFilterInstant(fromDay), to: dateFilterInstant(toDay, true) }
  const backups = useBackups(filters)
  const jobs = useBackupJobs()
  const agents = useAgents(tenantId, true)
  const filtering = Boolean(status || filters.jobId || filters.agentId || trigger || fromDay || toDay)
  const invalidDateRange = Boolean(fromDay && toDay && fromDay > toDay)
  const clearFilters = () => update({ status: '', jobId: '', agentId: '', trigger: '', from: '', to: '' })
  const columns: Column<BackupRun>[] = [
    { key: 'job', card: 'title', header: tx('İş'), wrap: true, render: (run) => <Link className={`font-medium ${linkClass}`} to={`/tenants/${tenantId}/backup-runs/${run.id}`}>{run.jobName ?? jobs.data?.find((job) => job.id === run.jobId)?.name ?? tx('Yedekleme işi')}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: (run) => <div className="flex flex-col items-start gap-1"><StatusPill status={run.status} /><SizeAnomalyBadge run={run} /><SizeAnomalyComparison run={run} /></div> },
    { key: 'started', header: tx('Başlangıç'), render: (run) => <RelativeTime value={run.startedAt} /> },
    { key: 'duration', header: tx('Süre'), render: (run) => formatDuration(run.startedAt, run.completedAt) },
  ]
  const filterError = jobs.error ?? agents.error

  return (
    <div className="space-y-6">
      <Filters label={tx("Yedekleme filtreleri")} onClear={filtering ? clearFilters : undefined}
        footer={<span className="text-sm text-text-muted">{tx("Tarihler yerel saat diliminizdeki oluşturulma günlerini kapsar.")}</span>}>
        <FilterField label={tx("Durum")}>
          <Select aria-label={tx("Durum")} value={status ?? ''} onChange={(event) => update({ status: event.target.value })}>
            <option value="">{tx("Tüm durumlar")}</option>
            {runStatuses.map((value) => <option key={value} value={value}>{statusLabel('general', value)}</option>)}
          </Select>
        </FilterField>
        <FilterField label={tx("İş")}>
          <Select aria-label={tx("İş")} value={filters.jobId ?? ''} onChange={(event) => update({ jobId: event.target.value })} disabled={jobs.isPending || Boolean(jobs.error)}>
            <option value="">{tx("Tüm işler")}</option>
            {jobs.data?.map((job) => <option key={job.id} value={job.id}>{job.name}</option>)}
          </Select>
        </FilterField>
        <FilterField label={tx("Yedekleme bilgisayarı")}>
          <Select aria-label={tx("Yedekleme bilgisayarı")} value={filters.agentId ?? ''} onChange={(event) => update({ agentId: event.target.value })} disabled={agents.isPending || Boolean(agents.error)}>
            <option value="">{tx("Tüm yedekleme bilgisayarları")}</option>
            {agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız yedekleme bilgisayarı')}</option>)}
          </Select>
        </FilterField>
        <FilterField label={tx("Tetikleyici")}>
          <Select aria-label={tx("Tetikleyici")} value={trigger ?? ''} onChange={(event) => update({ trigger: event.target.value })}>
            <option value="">{tx("Tüm tetikleyiciler")}</option><option value="Manual">{tx("Elle başlatıldı")}</option><option value="Scheduled">{tx("Zamanlanmış")}</option><option value="Retry">{tx("Yeniden denemeler")}</option>
          </Select>
        </FilterField>
        <FilterField label={tx("Oluşturulma başlangıcı")}>
          <TextInput aria-label={tx("Oluşturulma başlangıcı")} type="date" max={toDay || undefined} value={fromDay} onChange={(event) => update({ from: event.target.value })} />
        </FilterField>
        <FilterField label={tx("Oluşturulma sonu")}>
          <TextInput aria-label={tx("Oluşturulma sonu")} type="date" min={fromDay || undefined} value={toDay} onChange={(event) => update({ to: event.target.value })} />
        </FilterField>
      </Filters>
      {invalidDateRange && <Notice tone="warning">{tx("Başlangıç tarihi bitiş tarihinden sonra olamaz.")}</Notice>}
      {filterError && <ErrorNotice title={tx("Filtre seçenekleri yüklenemedi")} error={filterError} />}
      <QueryState isPending={backups.isPending} error={backups.error}>
        {backups.data && (backups.data.totalCount === 0
          ? <Card padding="none">{filtering
            ? <EmptyState icon={Activity} message={tx("Bu filtrelere uygun yedekleme denemesi bulunamadı.")} />
            : <EmptyState icon={Activity} message={tx("Henüz yedekleme denemesi yok. Bir yedekleme işi çalıştığında burada görünür.")} action={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-jobs`}>{tx("Yedekleme işlerine git")}</Link>} />}</Card>
          : <>
            <DataList rows={backups.data.items} columns={columns} keyExtractor={(run) => run.id} />
            <Pagination label={tx("Yedekleme sayfaları")} page={page} pageSize={pageSize} totalCount={backups.data.totalCount} disabled={backups.isFetching} onPageChange={(next) => update({ page: next })} />
          </>)}
      </QueryState>
    </div>
  )
}
