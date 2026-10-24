import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Plus, DatabaseBackup } from 'lucide-react'
import { DataList } from '@/components/data/data-list'
import type { Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJobs, useRunBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import { scheduleLabel } from '@/features/backup-jobs/job-wizard-model'
import type { BackupJob } from '@/features/backup-jobs/types'
import { linkClass } from '@/components/ui/form-styles'
import { ApiError } from '@/lib/api/client'
import { formatDateTime, formatSeconds } from '@/lib/format'

const pageSize = 25

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
  const newJob = canOperate ? <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />{tx("Yeni iş")}</Link> : undefined
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: tx('İş adı'), wrap: true, render: job => <span className="space-y-1"><Link to={`${base}/backup-jobs/${job.id}/edit`} className={`block font-medium ${linkClass}`}>{job.name}</Link><StatusPill status={job.enabled ? 'Active' : 'Disabled'} /></span> },
    { key: 'agent', header: tx('Agent'), wrap: true, render: job => job.agentName || tx('Agent adı bulunamadı') },
    { key: 'database', header: tx('Database'), wrap: true, render: job => job.configuration.database },
    { key: 'encryption', card: 'hidden', header: tx('Şifreleme'), render: job => <StatusPill domain="encryptionPolicy" status={job.configuration.encryptionPolicy ?? 'Off'} /> },
    { key: 'schedule', card: 'hidden', header: tx('Zamanlama'), wrap: true, render: job => {
      const next = job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt
      return <span className="space-y-0.5">
        <span className="block">{scheduleLabel(job.schedule)}{job.schedule && !job.schedule.enabled && tx(' (kapalı)')}</span>
        {next && <span className="block text-sm text-text-muted">{tx("Sonraki:")}{' '}{formatDateTime(next)}</span>}
        <span className="block text-sm text-text-muted">{job.overlapPolicy === 'Skip' ? tx('Çakışma: atla') : tx('Çakışma: kuyruğa al')} · {job.retention?.enabled ? tx("saklama {0}", { "0": formatSeconds(job.retention.keepForSeconds) }) : tx('süresiz saklama')}</span>
      </span>
    } },
    { key: 'last', header: tx('Son çalıştırma'), wrap: true, render: job => job.lastRun ? <Link to={`${base}/backup-runs/${job.lastRun.id}`} className="space-y-1 text-primary hover:underline"><StatusPill status={job.lastRun.status} /><span className="block text-sm">{formatDateTime(job.lastRun.createdAt)}</span></Link> : <span className="text-text-muted">{tx("Henüz çalıştırılmadı")}</span> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), wrap: true, render: job => canOperate ? <div className="flex flex-wrap items-center gap-2">
      <Button size="sm" disabled={run.isPending || !job.enabled} onClick={event => { if (event.detail > 1) return; setActionError(null); setStartedRun(undefined); void run.run(job.id).then(result => { if (result) setStartedRun(result.id) }).catch(setActionError) }}>{tx("Şimdi çalıştır")}</Button>
      <Link to={`${base}/backup-jobs/${job.id}/edit`} className={linkClass}>{tx("Düzenle")}</Link>
      <Button size="sm" variant="outline" disabled={toggle.isPending} onClick={() => { setActionError(null); toggle.mutate({ id: job.id, enabled: !job.enabled }, { onError: setActionError }) }}>{job.enabled ? tx('Devre dışı bırak') : tx('Etkinleştir')}</Button>
    </div> : <span className="text-text-muted">{tx("Salt okunur")}</span> },
  ]
  const overlap = actionError instanceof ApiError && actionError.status === 409 && actionError.activeRunId
  const all = jobs.data ?? []
  const filtered = all.filter(job => (state === 'all' || job.enabled === (state === 'enabled')) && `${job.name} ${job.configuration.database} ${job.agentName ?? ''}`.toLocaleLowerCase(currentLocale()).includes(search.toLocaleLowerCase(currentLocale())))
  const current = Math.min(page, Math.max(1, Math.ceil(filtered.length / pageSize)))
  const filtering = search !== '' || state !== 'all'
  return <div className="space-y-6">
    <PageHeader title={tx("Yedekleme işleri")} description={tx("MSSQL ve PostgreSQL Full yedekleme, zamanlama ve saklama politikaları.")} actions={newJob} />
    {actionError !== null && (overlap
      ? <ErrorNotice title={tx("İşlem tamamlanamadı")} what={tx("Başka bir çalıştırma etkin olduğu için yeni çalışma başlatılmadı.")} dataImpact={tx("Etkin çalıştırma kesintisiz sürüyor; yedekleriniz değişmedi.")} action={tx("Etkin çalıştırmanın bitmesini bekleyin.")}
        control={<Link className={linkClass} to={`${base}/backup-runs/${overlap}`}>{tx("Etkin çalıştırmayı aç")}</Link>} />
      : <ErrorNotice title={tx("İşlem tamamlanamadı")} error={actionError} />)}
    {startedRun && <Notice tone="success" title={tx("Çalıştırma oluşturuldu")}>{tx("Backup Run kuyruğa alındı.")}{' '}<Link className={linkClass} to={`${base}/backup-runs/${startedRun}`}>{tx("Çalıştırmayı aç")}</Link></Notice>}
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      {all.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={DatabaseBackup} message={tx("Henüz yedekleme işi oluşturulmadı.")} canAct={canOperate} action={<Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />{tx("İlk yedeğinizi oluşturun")}</Link>} /></div> : <div className="space-y-4">
        <Filters label={tx("Yedekleme işi filtreleri")} onClear={filtering ? () => { setSearch(''); setState('all'); setPage(1) } : undefined}>
          <FilterField label={tx("İş, Agent veya Database ara")}><input type="search" value={search} aria-label={tx("İş, Agent veya Database ara")} onChange={event => { setSearch(event.target.value); setPage(1) }} className={filterControlClass} /></FilterField>
          <FilterField label={tx("Durum")}><select value={state} aria-label={tx("Durum")} onChange={event => { setState(event.target.value as typeof state); setPage(1) }} className={filterControlClass}><option value="all">{tx("Tümü")}</option><option value="enabled">{tx("Etkin")}</option><option value="disabled">{tx("Devre dışı")}</option></select></FilterField>
        </Filters>
        <DataList rows={filtered.slice((current - 1) * pageSize, current * pageSize)} columns={columns} keyExtractor={job => job.id} emptyMessage={tx("Aramanızla eşleşen iş yok.")} />
        {filtered.length > pageSize && <Pagination label={tx("Yedekleme işi sayfaları")} page={current} pageSize={pageSize} totalCount={filtered.length} onPageChange={setPage} />}
      </div>}
    </QueryState>
  </div>
}
