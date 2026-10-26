import { currentLocale, tx, useLanguage } from '@/lib/i18n'
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
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJobs, useRunBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { linkClass } from '@/components/ui/form-styles'
import { ApiError } from '@/lib/api/client'

const pageSize = 25
const [runHint, disableHint, enableHint] = ['job-run-hint', 'job-disable-hint', 'job-enable-hint']

export function BackupJobsPage() {
  useLanguage()
  const { tenantId, canOperate } = useTenant()
  const jobs = useBackupJobs()
  const run = useRunBackupJob()
  const toggle = useToggleBackupJob()
  const [search, setSearch] = useState('')
  const [state, setState] = useState<'all' | 'enabled' | 'disabled'>('all')
  const [page, setPage] = useState(1)
  const [actionError, setActionError] = useState<unknown>(null)
  const [startedRun, setStartedRun] = useState<string>()
  const base = `/tenants/${tenantId}`
  const all = jobs.data ?? []
  const newJob = canOperate && all.length > 0 ? <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />{tx("Yeni iş")}</Link> : undefined
  const describedBy = (id: string) => ({ 'aria-describedby': id })
  const columns: Column<BackupJob>[] = [
    { key: 'name', card: 'title', header: tx('İş adı'), wrap: true, render: job => <Link to={`${base}/backup-jobs/${job.id}/edit`} className={`block font-medium ${linkClass}`}>{job.name}</Link> },
    { key: 'status', card: 'status', header: tx('Durum'), render: job => <StatusPill status={job.enabled ? 'Active' : 'Disabled'} /> },
    { key: 'database', header: tx('Veritabanı'), wrap: true, render: job => job.configuration.database },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: job => job.agentName || tx('Yedekleme bilgisayarı bulunamadı') },
    { key: 'last', header: tx('Son sonuç'), wrap: true, render: job => job.lastRun ? <Link to={`${base}/backup-runs/${job.lastRun.id}`} className="space-y-1 text-primary hover:underline"><StatusPill status={job.lastRun.status} /><span className="block text-sm"><RelativeTime value={job.lastRun.createdAt} /></span></Link> : <span className="text-text-muted">{tx("Henüz çalıştırılmadı")}</span> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), wrap: true, render: job => canOperate ? <div className="flex flex-wrap items-center gap-2">
      <Button size="sm" {...describedBy(runHint)} disabled={run.isPending || !job.enabled} onClick={event => { if (event.detail > 1) return; setActionError(null); setStartedRun(undefined); void run.run(job.id).then(result => { if (result) setStartedRun(result.id) }).catch(setActionError) }}>{tx("Şimdi çalıştır")}</Button>
      <Link to={`${base}/backup-jobs/${job.id}/edit`} className={linkClass}>{tx("Düzenle")}</Link>
      <Button size="sm" variant="outline" {...describedBy(job.enabled ? disableHint : enableHint)} disabled={toggle.isPending} onClick={() => { setActionError(null); toggle.mutate({ id: job.id, enabled: !job.enabled }, { onError: setActionError }) }}>{job.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
    </div> : <span className="text-text-muted">{tx("Yöneticinize danışın")}</span> },
  ]
  const overlap = actionError instanceof ApiError && actionError.status === 409 && actionError.activeRunId
  const filtered = all.filter(job => (state === 'all' || job.enabled === (state === 'enabled')) && `${job.name} ${job.configuration.database} ${job.agentName ?? ''}`.toLocaleLowerCase(currentLocale()).includes(search.toLocaleLowerCase(currentLocale())))
  const current = Math.min(page, Math.max(1, Math.ceil(filtered.length / pageSize)))
  const filtering = search !== '' || state !== 'all'
  return <div className="space-y-6">
    <PageHeader title={tx("Yedekleme işleri")} description={tx("Hangi veritabanının hangi bilgisayardan yedeklendiğini ve son sonucu buradan izleyin.")} actions={newJob} />
    {actionError !== null && (overlap
      ? <ErrorNotice title={tx("İşlem tamamlanamadı")} what={tx("Başka bir yedekleme denemesi sürdüğü için yenisi başlatılmadı.")} dataImpact={tx("Süren deneme kesintisiz devam ediyor; yedekleriniz değişmedi.")} action={tx("Süren denemenin bitmesini bekleyin.")}
        control={<Link className={linkClass} to={`${base}/backup-runs/${overlap}`}>{tx("Süren denemeyi aç")}</Link>} />
      : <ErrorNotice title={tx("İşlem tamamlanamadı")} error={actionError} />)}
    {startedRun && <Notice tone="success" title={tx("Yedekleme denemesi başlatıldı")}>{tx("Yedekleme denemesi kuyruğa alındı.")}{' '}<Link className={linkClass} to={`${base}/backup-runs/${startedRun}`}>{tx("Denemeyi aç")}</Link></Notice>}
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      {all.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={DatabaseBackup} message={tx("Henüz yedekleme işi oluşturulmadı.")} canAct={canOperate} action={<Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />{tx("İlk yedeğinizi oluşturun")}</Link>} /></div> : <div className="space-y-4">
        {canOperate && <div className="space-y-1 text-sm text-text-muted">
          <p id={runHint}>{tx("Şimdi çalıştır: yedekleme hemen başlar; zamanlanmış çalışmalar etkilenmez.")}</p>
          <p id={disableHint}>{tx("Devre dışı bırak: zamanlanmış yedekleme durur, mevcut yedekler silinmez.")}</p>
          <p id={enableHint}>{tx("Etkinleştir: zamanlanmış yedekleme yeniden başlar.")}</p>
        </div>}
        <Filters label={tx("Yedekleme işi filtreleri")} onClear={filtering ? () => { setSearch(''); setState('all'); setPage(1) } : undefined}>
          <FilterField label={tx("İş, yedekleme bilgisayarı veya veritabanı ara")}><input type="search" value={search} aria-label={tx("İş, yedekleme bilgisayarı veya veritabanı ara")} onChange={event => { setSearch(event.target.value); setPage(1) }} className={filterControlClass} /></FilterField>
          <FilterField label={tx("Durum")}><select value={state} aria-label={tx("Durum")} onChange={event => { setState(event.target.value as typeof state); setPage(1) }} className={filterControlClass}><option value="all">{tx("Tümü")}</option><option value="enabled">{tx("Etkin")}</option><option value="disabled">{tx("Devre dışı")}</option></select></FilterField>
        </Filters>
        <DataList rows={filtered.slice((current - 1) * pageSize, current * pageSize)} columns={columns} keyExtractor={job => job.id} emptyMessage={tx("Aramanızla eşleşen iş yok.")} />
        {filtered.length > pageSize && <Pagination label={tx("Yedekleme işi sayfaları")} page={current} pageSize={pageSize} totalCount={filtered.length} onPageChange={setPage} />}
      </div>}
    </QueryState>
  </div>
}
