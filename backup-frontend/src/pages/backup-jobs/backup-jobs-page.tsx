import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { Select, TextInput } from '@/components/ui/text-input'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Plus, DatabaseBackup } from 'lucide-react'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Card } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters } from '@/components/ui/filters'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJobs, useRunBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { LIST_PAGE_SIZE, oneOf, useListParams } from '@/features/platform/use-list-params'
import { linkClass } from '@/components/ui/form-styles'
import { ApiError } from '@/lib/api/client'

const pageSize = LIST_PAGE_SIZE
const [runHint, disableHint, enableHint] = ['job-run-hint', 'job-disable-hint', 'job-enable-hint']

export function BackupJobsPage() {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const jobs = useBackupJobs()
  const run = useRunBackupJob()
  const toggle = useToggleBackupJob()
  const { page, get, update } = useListParams()
  const search = get('search')
  const state = oneOf(get('state'), ['enabled', 'disabled'])
  const [actionError, setActionError] = useState<unknown>(null)
  const [startedRun, setStartedRun] = useState<string>()
  const base = `/tenants/${tenantId}`
  const all = jobs.data ?? []
  const newJob = <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />{tx("Yeni iş")}</Link>
  const describedBy = (id: string) => ({ 'aria-describedby': id })
  const columns: Column<BackupJob>[] = [
    { key: 'name', card: 'title', header: tx('İş adı'), wrap: true, render: job => <Link to={`${base}/backup-jobs/${job.id}/edit`} className={`block font-medium ${linkClass}`}>{job.name}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: job => <StatusPill status={job.enabled ? 'Active' : 'Disabled'} /> },
    { key: 'database', header: tx('Veritabanı'), wrap: true, render: job => job.configuration.database },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: job => job.agentName || tx('Yedekleme bilgisayarı bulunamadı') },
    { key: 'last', header: tx('Son sonuç'), wrap: true, render: job => job.lastRun ? <div className="space-y-1">
      <Link to={`${base}/backup-runs/${job.lastRun.id}`} className="block space-y-1 text-primary hover:underline"><StatusPill status={job.lastRun.status} /><span className="block text-sm"><RelativeTime value={job.lastRun.createdAt} /></span></Link>
      <Link to={`${base}/backup-runs?jobId=${encodeURIComponent(job.id)}`} className={`block text-sm ${linkClass}`}>{tx("Tüm denemeler")}</Link>
    </div> : <span className="text-text-muted">{tx("Henüz çalıştırılmadı")}</span> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), wrap: true, render: job => canOperate ? <div className="flex flex-wrap items-center gap-2">
      <Button size="sm" {...describedBy(runHint)} disabled={run.isPending || !job.enabled} onClick={event => { if (event.detail > 1) return; setActionError(null); setStartedRun(undefined); void run.run(job.id).then(result => { if (result) setStartedRun(result.id) }).catch(setActionError) }}>{tx("Şimdi çalıştır")}</Button>
      <Button size="sm" variant="outline" {...describedBy(job.enabled ? disableHint : enableHint)} disabled={toggle.isPending} onClick={() => { setActionError(null); toggle.mutate({ id: job.id, enabled: !job.enabled }, { onError: setActionError }) }}>{job.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
    </div> : <span className="text-text-muted">{tx("Yöneticinize danışın")}</span> },
  ]
  const overlap = actionError instanceof ApiError && actionError.status === 409 && actionError.activeRunId
  const filtered = all.filter(job => (!state || job.enabled === (state === 'enabled')) && `${job.name} ${job.configuration.database} ${job.agentName ?? ''}`.toLocaleLowerCase(currentLocale()).includes(search.toLocaleLowerCase(currentLocale())))
  const current = Math.min(page, Math.max(1, Math.ceil(filtered.length / pageSize)))
  const filtering = search !== '' || state !== undefined
  return <div className="space-y-6">
    {actionError !== null && (overlap
      ? <ErrorNotice title={tx("İşlem tamamlanamadı")} what={tx("Başka bir yedekleme denemesi sürdüğü için yenisi başlatılmadı.")} dataImpact={tx("Süren deneme kesintisiz devam ediyor; yedekleriniz değişmedi.")} action={tx("Süren denemenin bitmesini bekleyin.")}
        control={<Link className={linkClass} to={`${base}/backup-runs/${overlap}`}>{tx("Süren denemeyi aç")}</Link>} />
      : <ErrorNotice title={tx("İşlem tamamlanamadı")} error={actionError} />)}
    {startedRun && <Notice tone="success" title={tx("Yedekleme denemesi başlatıldı")}>{tx("Yedekleme denemesi kuyruğa alındı.")}{' '}<Link className={linkClass} to={`${base}/backup-runs/${startedRun}`}>{tx("Denemeyi aç")}</Link></Notice>}
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      {all.length === 0 ? <Card padding="none"><EmptyState icon={DatabaseBackup} message={tx("Henüz yedekleme işi oluşturulmadı.")} canAct={canOperate} action={<Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />{tx("İlk yedeğinizi oluşturun")}</Link>} /></Card> : <div className="space-y-4">
        {canOperate && <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1 text-sm text-text-muted">
            <p id={runHint}>{tx("Şimdi çalıştır: yedekleme hemen başlar; zamanlanmış çalışmalar etkilenmez.")}</p>
            <p id={disableHint}>{tx("Devre dışı bırak: zamanlanmış yedekleme durur, mevcut yedekler silinmez.")}</p>
            <p id={enableHint}>{tx("Etkinleştir: zamanlanmış yedekleme yeniden başlar.")}</p>
          </div>
          {newJob}
        </div>}
        <Filters label={tx("Yedekleme işi filtreleri")} onClear={filtering ? () => update({ search: '', state: '' }) : undefined}>
          <FilterField label={tx("İş, yedekleme bilgisayarı veya veritabanı ara")}><TextInput type="search" value={search} aria-label={tx("İş, yedekleme bilgisayarı veya veritabanı ara")} onChange={event => update({ search: event.target.value }, { replace: true })} /></FilterField>
          <FilterField label={tx("Durum")}><Select value={state ?? 'all'} aria-label={tx("Durum")} onChange={event => update({ state: event.target.value === 'all' ? '' : event.target.value })}><option value="all">{tx("Tümü")}</option><option value="enabled">{tx("Etkin")}</option><option value="disabled">{tx("Devre dışı")}</option></Select></FilterField>
        </Filters>
        <DataList rows={filtered.slice((current - 1) * pageSize, current * pageSize)} columns={columns} keyExtractor={job => job.id} emptyMessage={tx("Aramanızla eşleşen iş yok.")} />
        {filtered.length > pageSize && <Pagination label={tx("Yedekleme işi sayfaları")} page={current} pageSize={pageSize} totalCount={filtered.length} onPageChange={next => update({ page: next })} />}
      </div>}
    </QueryState>
  </div>
}
