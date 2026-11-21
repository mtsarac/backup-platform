import { tx, useLanguage } from '@/lib/i18n'
import { Select } from '@/components/ui/text-input'
import { Activity } from 'lucide-react'
import { Link, useLocation, useParams } from 'react-router-dom'
import { BackupDetailPage } from '@/pages/backups/backup-detail-page'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { buttonVariants } from '@/components/ui/button-variants'
import { Disclosure } from '@/components/ui/disclosure'
import { FilterField, Filters } from '@/components/ui/filters'
import { ErrorNotice } from '@/components/ui/notice'
import { useAgents } from '@/features/agents/hooks'
import { useBackupJobs } from '@/features/backup-jobs/hooks'
import { useBackups } from '@/features/backups/hooks'
import { dateFilterInstant, runStatuses } from '@/features/backups/presentation'
import { SizeAnomalyBadge, SizeAnomalyComparison } from '@/features/backups/size-anomaly'
import type { BackupRun } from '@/features/backups/types'
import { DateRangeFilter } from '@/features/lists/date-range-filter'
import { ListEmpty } from '@/features/lists/list-empty'
import { pastLastPageMessage } from '@/features/lists/list-messages'
import { ListPager } from '@/features/lists/list-pager'
import { ListToolbar } from '@/features/lists/list-toolbar'
import { toServerQuery } from '@/features/lists/server-list-query'
import { isInvertedRange } from '@/features/lists/url-params'
import { sortProps, useListState } from '@/features/lists/use-list-state'
import { linkClass } from '@/components/ui/form-styles'
import { useTenant } from '@/features/tenancy/use-tenant'
import { formatBytes, formatDateTime, formatDuration } from '@/lib/format'
import { statusLabel } from '@/lib/status'

const listConfig = {
  search: true,
  filters: { status: runStatuses, jobId: 'text', agentId: 'text', from: 'date', to: 'date' },
  // The default order is by creation (queued runs have not started yet), so Oluşturulma is a column of its own; Başlangıç sorts by start time.
  sort: { first: { createdAt: 'desc', startedAt: 'desc', status: 'asc', jobName: 'asc', size: 'asc', duration: 'asc' }, default: { key: 'createdAt', dir: 'desc' } },
} as const

export function BackupsPage() {
  useLanguage()
  const { tenantId } = useTenant()
  const state = useListState(listConfig)
  const { id } = useParams()
  const { search } = useLocation()
  const { from: fromDay, to: toDay, status, jobId, agentId } = state.filters
  const filters = { ...toServerQuery(state), from: dateFilterInstant(fromDay), to: dateFilterInstant(toDay, true) }
  const invalidDateRange = isInvertedRange(fromDay, toDay)
  const backups = useBackups(filters, { enabled: !invalidDateRange })
  const jobs = useBackupJobs({ includeArchived: true })
  const agents = useAgents(tenantId, true)
  const filtering = state.filtering
  const columns: Column<BackupRun>[] = [
    { key: 'jobName', sortable: true, card: 'title', header: tx('İş'), wrap: true, render: (run) => <Link aria-current={run.id === id ? 'page' : undefined} className={`font-medium ${linkClass}`} to={`/tenants/${tenantId}/backup-runs/${run.id}${search}`}>{run.jobName ?? jobs.data?.find((job) => job.id === run.jobId)?.name ?? tx('Yedekleme işi')}</Link> },
    { key: 'status', sortable: true, card: 'status', header: tx('Durum'), render: (run) => <div className="flex flex-col items-start gap-1"><StatusPill status={run.status} /><SizeAnomalyBadge run={run} /><SizeAnomalyComparison run={run} /></div> },
    { key: 'size', sortable: true, header: tx('Yedek dosyası'), wrap: true, render: (run) => {
      if (run.status !== 'Succeeded') return '—'
      const artifact = [...run.attempts].reverse().flatMap((attempt) => attempt.artifacts)[0]
      if (!artifact) return '—'
      const copies = artifact.replication?.verifiedReplicas ?? run.replication?.verifiedReplicas ?? artifact.replicas.filter((replica) => replica.usable).length
      const deletion = artifact.state === 'DeletePending' ? tx('Silme bekliyor') : artifact.state === 'DeleteFailed' ? tx('Silme başarısız') : artifact.state === 'Deleted' ? tx('Silindi') : null
      return <div><p>{formatBytes(artifact.sizeBytes)} · {tx('{0} kopya', { '0': copies })}</p><p>{artifact.retentionExpiresAt ? tx('Saklama sonu: {0}', { '0': formatDateTime(artifact.retentionExpiresAt) }) : tx('Süre sınırı yok')}</p>{deletion && <p>{deletion}</p>}</div>
    } },
    { key: 'createdAt', sortable: true, header: tx('Oluşturulma'), render: (run) => <RelativeTime value={run.createdAt} /> },
    { key: 'startedAt', sortable: true, header: tx('Başlangıç'), render: (run) => <RelativeTime value={run.startedAt} /> },
    { key: 'duration', sortable: true, header: tx('Süre'), render: (run) => formatDuration(run.startedAt, run.completedAt) },
  ]
  const filterError = jobs.error ?? agents.error

  return (
    <div className={id ? 'grid gap-4 md:grid-cols-[minmax(0,1fr)_minmax(0,2fr)]' : undefined}>
      <div className={`min-w-0 space-y-6 ${id ? 'hidden md:block' : ''}`}>
      <ListToolbar state={state} label={tx("Yedekleme filtreleri")} total={backups.data?.totalCount} search={{ label: tx('Ara') }}>
        <FilterField label={tx("Durum")}>
          <Select aria-label={tx("Durum")} value={status} onChange={(event) => state.setFilter('status', event.target.value)}>
            <option value="">{tx("Tüm durumlar")}</option>
            {runStatuses.map((value) => <option key={value} value={value}>{statusLabel('general', value)}</option>)}
          </Select>
        </FilterField>
        <FilterField label={tx("İş")}>
          <Select aria-label={tx("İş")} value={jobId} onChange={(event) => state.setFilter('jobId', event.target.value)} disabled={jobs.isPending || Boolean(jobs.error)}>
            <option value="">{tx("Tüm işler")}</option>
            {jobs.data?.map((job) => <option key={job.id} value={job.id}>{job.archivedAt ? tx('{0} (kaldırıldı)', { '0': job.name }) : job.name}</option>)}
          </Select>
        </FilterField>
      </ListToolbar>
      <Disclosure title={tx('Diğer filtreler')} open={Boolean(agentId || fromDay || toDay)}>
        <Filters label={tx("Diğer filtreler")} footer={<span className="text-sm text-text-muted">{tx("Tarihler yerel saat diliminizdeki oluşturulma günlerini kapsar.")}</span>}>
        <FilterField label={tx("Yedekleme bilgisayarı")}>
          <Select aria-label={tx("Yedekleme bilgisayarı")} value={agentId} onChange={(event) => state.setFilter('agentId', event.target.value)} disabled={agents.isPending || Boolean(agents.error)}>
            <option value="">{tx("Tüm yedekleme bilgisayarları")}</option>
            {agents.data?.map((agent) => <option key={agent.agentId} value={agent.agentId}>{agent.hostName ?? tx('Adsız yedekleme bilgisayarı')}</option>)}
          </Select>
        </FilterField>
        <DateRangeFilter from={fromDay} to={toDay} fromLabel={tx("Oluşturulma başlangıcı")} toLabel={tx("Oluşturulma sonu")} onChange={(which, value) => state.setFilter(which, value)} />
      </Filters>
      </Disclosure>
      {filterError && <ErrorNotice title={tx("Filtre seçenekleri yüklenemedi")} error={filterError} />}
      {!invalidDateRange && <QueryState isPending={backups.isPending} error={backups.error}>
        {backups.data && (backups.data.totalCount === 0
          ? <ListEmpty icon={Activity} filtering={filtering} onClear={state.clear} filteredMessage={tx("Bu filtrelere uygun yedekleme denemesi bulunamadı.")}
            message={tx("Henüz yedekleme denemesi yok. Bir yedekleme işi çalıştığında burada görünür.")} action={<Link className={buttonVariants()} to={`/tenants/${tenantId}/backup-jobs`}>{tx("Yedekleme işlerine git")}</Link>} />
          : <>
            <DataList rows={backups.data.items} columns={id ? columns.filter((column) => column.key === 'jobName' || column.key === 'status') : columns} sort={sortProps(state)} keyExtractor={(run) => run.id} emptyMessage={pastLastPageMessage()} />
            <ListPager label={tx("Yedekleme sayfaları")} state={state} total={backups.data.totalCount} disabled={backups.isFetching} />
          </>)}
      </QueryState>}
      </div>
      {id && <section aria-label={tx('Yedekleme denemesi')} className="min-w-0 rounded-lg border border-border bg-surface p-4"><BackupDetailPage /></section>}
    </div>
  )
}
