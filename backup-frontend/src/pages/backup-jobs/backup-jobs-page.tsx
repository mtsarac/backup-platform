import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Plus, DatabaseBackup } from 'lucide-react'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { EmptyState } from '@/components/ui/empty-state'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJobs, useRunBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import { scheduleLabel } from '@/features/backup-jobs/job-wizard-model'
import type { BackupJob } from '@/features/backup-jobs/types'
import { formatSeconds } from '@/features/backups/presentation'
import { linkClass } from '@/features/platform/styles'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

const pageSize = 25

export function BackupJobsPage() {
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
  const newJob = canOperate ? <Link className={buttonVariants()} to={`${base}/backup-jobs/new`}><Plus aria-hidden="true" />Yeni iş</Link> : undefined
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: 'İş adı', wrap: true, render: job => <span className="space-y-1"><span className="block font-medium text-text-heading">{job.name}</span><StatusPill status={job.enabled ? 'Active' : 'Disabled'} /></span> },
    { key: 'agent', header: 'Agent', wrap: true, render: job => job.agentName || 'Agent adı bulunamadı' },
    { key: 'database', header: 'Database', wrap: true, render: job => job.configuration.database },
    { key: 'schedule', header: 'Zamanlama', wrap: true, render: job => {
      const next = job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt
      return <span className="space-y-0.5">
        <span className="block">{scheduleLabel(job.schedule)}{job.schedule && !job.schedule.enabled && ' (kapalı)'}</span>
        {next && <span className="block text-sm text-text-muted">Sonraki: {new Date(next).toLocaleString('tr-TR')}</span>}
        <span className="block text-sm text-text-muted">{job.overlapPolicy === 'Skip' ? 'Çakışma: atla' : 'Çakışma: kuyruğa al'} · {job.retention?.enabled ? `saklama ${formatSeconds(job.retention.keepForSeconds)}` : 'süresiz saklama'}</span>
      </span>
    } },
    { key: 'last', header: 'Son çalıştırma', wrap: true, render: job => job.lastRun ? <Link to={`${base}/backup-runs/${job.lastRun.id}`} className="space-y-1 text-primary hover:underline"><StatusPill status={job.lastRun.status} /><span className="block text-sm">{new Date(job.lastRun.createdAt).toLocaleString('tr-TR')}</span></Link> : <span className="text-text-muted">Henüz çalıştırılmadı</span> },
    { key: 'actions', header: 'İşlemler', wrap: true, render: job => canOperate ? <div className="flex flex-wrap items-center gap-2">
      <Button size="sm" disabled={run.isPending || !job.enabled} onClick={event => { if (event.detail > 1) return; setActionError(null); setStartedRun(undefined); void run.run(job.id).then(result => { if (result) setStartedRun(result.id) }).catch(setActionError) }}>Şimdi çalıştır</Button>
      <Link to={`${base}/backup-jobs/${job.id}/edit`} className={linkClass}>Düzenle</Link>
      <Button size="sm" variant="outline" disabled={toggle.isPending} onClick={() => { setActionError(null); toggle.mutate({ id: job.id, enabled: !job.enabled }, { onError: setActionError }) }}>{job.enabled ? 'Devre dışı bırak' : 'Etkinleştir'}</Button>
    </div> : <span className="text-text-muted">Salt okunur</span> },
  ]
  const overlap = actionError instanceof ApiError && actionError.status === 409 && actionError.activeRunId
  const all = jobs.data ?? []
  const filtered = all.filter(job => (state === 'all' || job.enabled === (state === 'enabled')) && `${job.name} ${job.configuration.database} ${job.agentName ?? ''}`.toLocaleLowerCase('tr-TR').includes(search.toLocaleLowerCase('tr-TR')))
  const current = Math.min(page, Math.max(1, Math.ceil(filtered.length / pageSize)))
  const filtering = search !== '' || state !== 'all'
  return <div className="space-y-6">
    <PageHeader title="Yedekleme işleri" description="MSSQL ve PostgreSQL Full yedekleme, zamanlama ve saklama politikaları." actions={newJob} />
    {actionError !== null && <Notice tone="error" title="İşlem tamamlanamadı">{overlap ? <>Başka bir çalıştırma etkin olduğu için yeni çalışma başlatılmadı. <Link className={linkClass} to={`${base}/backup-runs/${overlap}`}>Etkin çalıştırmayı aç</Link></> : <>{errorMessage(actionError)} Ağ sonucu belirsizse aynı “Şimdi çalıştır” düğmesi aynı işlem anahtarıyla yeniden dener.</>}</Notice>}
    {startedRun && <Notice tone="success" title="Çalıştırma oluşturuldu">Backup Run kuyruğa alındı. <Link className={linkClass} to={`${base}/backup-runs/${startedRun}`}>Çalıştırmayı aç</Link></Notice>}
    <QueryState isPending={jobs.isPending} error={jobs.error}>
      {all.length === 0 ? <div className="rounded-xl border border-border bg-surface"><EmptyState icon={DatabaseBackup} title="Henüz yedekleme işi oluşturulmadı." action={newJob}>{canOperate ? 'İlk Backup Job’unuzu oluşturun; Agent, Database ve depolama hedefini birlikte seçersiniz.' : 'Yedekleme işlerini Owner, Admin veya Operator oluşturabilir.'}</EmptyState></div> : <div className="space-y-4">
        <Filters label="Yedekleme işi filtreleri" onClear={filtering ? () => { setSearch(''); setState('all'); setPage(1) } : undefined}>
          <FilterField label="İş, Agent veya Database ara"><input type="search" value={search} aria-label="İş, Agent veya Database ara" onChange={event => { setSearch(event.target.value); setPage(1) }} className={filterControlClass} /></FilterField>
          <FilterField label="Durum"><select value={state} aria-label="Durum" onChange={event => { setState(event.target.value as typeof state); setPage(1) }} className={filterControlClass}><option value="all">Tümü</option><option value="enabled">Etkin</option><option value="disabled">Devre dışı</option></select></FilterField>
        </Filters>
        <DataTable rows={filtered.slice((current - 1) * pageSize, current * pageSize)} columns={columns} keyExtractor={job => job.id} emptyMessage="Aramanızla eşleşen iş yok." />
        {filtered.length > pageSize && <Pagination label="Yedekleme işi sayfaları" page={current} pageSize={pageSize} totalCount={filtered.length} onPageChange={setPage} />}
      </div>}
    </QueryState>
  </div>
}
