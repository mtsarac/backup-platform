import { useState } from 'react'
import { Link } from 'react-router-dom'
import { DataTable } from '@/components/data/data-table'
import type { Column } from '@/components/data/data-table'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { useTenant } from '@/features/tenancy/use-tenant'
import { useBackupJobs, useRunBackupJob, useToggleBackupJob } from '@/features/backup-jobs/hooks'
import type { BackupJob } from '@/features/backup-jobs/types'
import { formatSeconds } from '@/features/backups/presentation'
import { ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'

export function BackupJobsPage() {
  const { tenantId, canOperate } = useTenant()
  const jobs = useBackupJobs()
  const run = useRunBackupJob()
  const toggle = useToggleBackupJob()
  const [search, setSearch] = useState('')
  const [actionError, setActionError] = useState<unknown>(null)
  const [startedRun, setStartedRun] = useState<string>()
  const base = `/tenants/${tenantId}`
  const columns: Column<BackupJob>[] = [
    { key: 'name', header: 'İş adı', wrap: true, render: job => job.name },
    { key: 'agent', header: 'Agent', wrap: true, render: job => job.agentName || 'Agent adı bulunamadı' },
    { key: 'database', header: 'Database', wrap: true, render: job => job.configuration.database },
    { key: 'enabled', header: 'Durum', render: job => job.enabled ? 'Etkin' : 'Devre dışı' },
    { key: 'schedule', header: 'Zamanlama', wrap: true, render: job => job.schedule ? <span>{job.schedule.cronExpression}<br /><small>{job.schedule.enabled ? 'Etkin' : 'Kapalı'} · {job.schedule.timeZone}</small></span> : 'Manuel' },
    { key: 'overlap', header: 'Çakışma', wrap: true, render: job => job.overlapPolicy === 'Skip' ? 'Atla (Skip)' : 'Kuyruğa al (Queue)' },
    { key: 'retention', header: 'Saklama', wrap: true, render: job => job.retention?.enabled ? `KeepFor ${formatSeconds(job.retention.keepForSeconds)} · silme bekleme ${formatSeconds(job.retention.deleteGracePeriodSeconds)}` : 'Süresiz' },
    { key: 'last', header: 'Son çalıştırma', wrap: true, render: job => job.lastRun ? <Link to={`${base}/backup-runs/${job.lastRun.id}`} className="space-y-1 text-primary hover:underline"><StatusPill status={job.lastRun.status} /><span className="block text-xs">{new Date(job.lastRun.createdAt).toLocaleString('tr-TR')}</span></Link> : 'Henüz çalıştırılmadı' },
    { key: 'next', header: 'Sonraki çalışma', wrap: true, render: job => { const next = job.nextOccurrenceAt ?? job.schedule?.nextOccurrenceAt; return next ? new Date(next).toLocaleString('tr-TR') : '—' } },
    { key: 'actions', header: 'İşlemler', wrap: true, render: job => canOperate ? <div className="flex flex-wrap items-center gap-2">
      <Button size="sm" disabled={run.isPending || !job.enabled} onClick={event => { if (event.detail > 1) return; setActionError(null); setStartedRun(undefined); void run.run(job.id).then(result => { if (result) setStartedRun(result.id) }).catch(setActionError) }}>Şimdi çalıştır</Button>
      <Link to={`${base}/backup-jobs/${job.id}/edit`} className="text-primary hover:underline">Düzenle</Link>
      <Button size="sm" variant="outline" disabled={toggle.isPending} onClick={() => { setActionError(null); toggle.mutate({ id: job.id, enabled: !job.enabled }, { onError: setActionError }) }}>{job.enabled ? 'Devre dışı bırak' : 'Etkinleştir'}</Button>
    </div> : 'Salt okunur' },
  ]
  const overlap = actionError instanceof ApiError && actionError.status === 409 && actionError.activeRunId
  const filtered = (jobs.data ?? []).filter(job => `${job.name} ${job.configuration.database} ${job.agentName ?? ''}`.toLocaleLowerCase('tr-TR').includes(search.toLocaleLowerCase('tr-TR')))
  return <div className="space-y-4">
    <header className="flex flex-wrap items-center justify-between gap-3"><div><h1 className="text-2xl font-semibold text-text-heading">Yedekleme işleri</h1><p className="text-sm text-text-muted">MSSQL Full yedekleme, zamanlama ve saklama politikaları.</p></div>{canOperate && <Link className="rounded-md bg-primary px-4 py-2 text-sm text-primary-foreground" to={`${base}/backup-jobs/new`}>Yeni iş</Link>}</header>
    <label className="block max-w-md space-y-1 text-sm">İş, Agent veya Database ara<input value={search} onChange={event => setSearch(event.target.value)} className="block h-9 w-full rounded-md border border-border bg-surface px-3" /></label>
    {actionError !== null && <div role="alert" className="rounded-md border border-destructive/30 p-3 text-sm">{overlap ? <>Başka bir çalıştırma etkin olduğu için yeni çalışma başlatılmadı. <Link className="text-primary underline" to={`${base}/backup-runs/${overlap}`}>Etkin çalıştırmayı aç</Link></> : <>{errorMessage(actionError)} Ağ sonucu belirsizse aynı “Şimdi çalıştır” düğmesi aynı işlem anahtarıyla yeniden dener.</>}</div>}
    {startedRun && <p role="status" className="text-sm">Çalıştırma oluşturuldu. <Link className="text-primary underline" to={`${base}/backup-runs/${startedRun}`}>Çalıştırmayı aç</Link></p>}
    <QueryState isPending={jobs.isPending} error={jobs.error}><DataTable rows={filtered} columns={columns} keyExtractor={job => job.id} emptyMessage={search ? 'Aramanızla eşleşen iş yok.' : 'Henüz yedekleme işi oluşturulmadı.'} /></QueryState>
  </div>
}
