import { DataTable, type Column } from '@/components/data/data-table'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { ArtifactDetails } from '@/features/artifacts/artifact-details'
import { artifactDate, artifactSize, artifactState } from '@/features/artifacts/format'
import { runStatuses, runStatusLabels } from '@/features/backups/presentation'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { failureMessage } from '@/lib/api/failure-messages'
import { SupportArtifactDownload } from './artifact-download'
import { useSupportArtifact, useSupportArtifacts, useSupportJobs, useSupportRuns } from './hooks'
import { supportAccess, supportScope } from './presentation'
import { SupportQueryState } from './support-query-state'
import type { SupportArtifact, SupportJob, SupportRun, SupportSession } from './types'

export function SupportBackupBrowser({ session }: { session: SupportSession }) {
  const { page, get, update } = useListParams('browserPage')
  const view = ['jobs', 'runs', 'artifacts'].includes(get('view')) ? get('view') : 'artifacts'
  const access = supportAccess(session)
  const scope = supportScope(session, 'view')
  const jobs = useSupportJobs(scope)
  const jobId = get('jobId')
  const from = get('from'), to = get('to')
  const invalidDates = Boolean((from && !/^\d{4}-\d{2}-\d{2}$/.test(from)) || (to && !/^\d{4}-\d{2}-\d{2}$/.test(to)) || (from && to && from > to))
  const runs = useSupportRuns({ ...scope, enabled: scope.enabled && view === 'runs' }, { page, pageSize: PLATFORM_PAGE_SIZE, jobId, status: get('runStatus') })
  const artifacts = useSupportArtifacts({ ...scope, enabled: scope.enabled && view === 'artifacts' && !invalidDates }, { page, pageSize: PLATFORM_PAGE_SIZE, jobId, state: get('state'), from: from && !invalidDates ? `${from}T00:00:00.000Z` : '', to: to && !invalidDates ? `${to}T23:59:59.999Z` : '' })
  const columns: Column<SupportArtifact>[] = [
    { key: 'database', header: 'Database', wrap: true, render: artifact => <button className="font-medium text-primary hover:underline" onClick={() => update({ artifact: artifact.id })}>{artifact.databaseName ?? 'Database bildirilmedi'}</button> },
    { key: 'job', header: 'Backup Job', wrap: true, render: artifact => artifact.jobName ?? '—' },
    { key: 'agent', header: 'Agent', wrap: true, render: artifact => artifact.agentName ?? '—' },
    { key: 'size', header: 'Boyut', render: artifact => artifactSize(artifact.sizeBytes) },
    { key: 'created', header: 'Oluşturulma', render: artifact => artifactDate(artifact.createdAt) },
    { key: 'retention', header: 'Saklama sonu', render: artifact => artifact.retentionExpiresAt ? artifactDate(artifact.retentionExpiresAt) : 'Süre sınırı yok' },
    { key: 'state', header: 'Durum', render: artifact => <StatusPill status={artifactState(artifact)} /> },
    { key: 'replicas', header: 'Replikalar', render: artifact => <div className="flex flex-wrap gap-1">{artifact.replicas.map(replica => <StatusPill key={replica.id} status={replica.verificationState} />)}</div> },
  ]
  const jobColumns: Column<SupportJob>[] = [
    { key: 'name', header: 'Backup Job', wrap: true, render: job => <button className="text-primary hover:underline" onClick={() => update({ view: 'runs', jobId: job.id })}>{job.name}</button> },
    { key: 'agent', header: 'Agent', wrap: true, render: job => job.agentName },
    { key: 'database', header: 'Database', wrap: true, render: job => job.database },
    { key: 'type', header: 'Tür', render: job => `${job.engine} · ${job.backupType}` },
    { key: 'storage', header: 'Storage Provider', render: job => job.storageProvider },
    { key: 'enabled', header: 'Etkin', render: job => job.enabled ? 'Evet' : 'Hayır' },
    { key: 'schedule', header: 'Zamanlama', wrap: true, render: job => job.schedule?.enabled ? `${job.schedule.cronExpression} · ${job.schedule.timeZone}` : 'Kapalı' },
    { key: 'retention', header: 'Saklama', render: job => job.retention?.enabled ? `${job.retention.keepForSeconds} sn` : 'Süre sınırı yok' },
    { key: 'last', header: 'Son Run', render: job => job.lastRun ? <StatusPill status={job.lastRun.status} /> : '—' },
  ]
  const runColumns: Column<SupportRun>[] = [
    { key: 'job', header: 'Backup Job', wrap: true, render: run => run.jobName },
    { key: 'agent', header: 'Agent', wrap: true, render: run => run.agentName },
    { key: 'status', header: 'Durum', render: run => <StatusPill status={run.status} /> },
    { key: 'trigger', header: 'Tetikleyici', render: run => run.triggerType },
    { key: 'times', header: 'Başlangıç / Bitiş', render: run => <div>{artifactDate(run.startedAt)}<br />{artifactDate(run.completedAt)}</div> },
    { key: 'reason', header: 'Sonuç', wrap: true, render: run => run.reasonCode ? failureMessage(run.reasonCode) : '—' },
    { key: 'attempts', header: 'Denemeler', wrap: true, render: run => <details><summary>{run.currentAttemptNumber}. attempt · {run.attempts.length} kayıt</summary>{run.attempts.map(attempt => <p key={attempt.number}>{attempt.number} · {attempt.status} · {artifactDate(attempt.startedAt)} · {artifactDate(attempt.completedAt)}{attempt.reasonCode ? ` · ${failureMessage(attempt.reasonCode)}` : ''}</p>)}</details> },
    { key: 'artifacts', header: 'Artifact', wrap: true, render: run => <div className="grid gap-2">{run.artifactIds.map((id, index) => <div key={id}><button className="text-primary hover:underline" onClick={() => update({ view: 'artifacts', artifact: id })}>Artifact {index + 1} ayrıntıları</button><p className="break-all font-mono text-xs text-text-muted">{id}</p></div>)}</div> },
  ]
  if (!access.view) return <Notice>Backup Browser için etkin oturum sahipliği ve ViewBackupMetadata izni gerekir.</Notice>
  const listing = view === 'runs' ? runs : artifacts
  return <div className="space-y-6">
    <Tabs label="Backup Browser görünümleri" idPrefix="support-browser" value={view} onChange={(value) => update({ view: value })} items={[{ value: 'jobs', label: 'Backup Jobs' }, { value: 'runs', label: 'Backup Runs' }, { value: 'artifacts', label: 'Artifacts' }]} />
    <div role="tabpanel" id={`support-browser-panel-${view}`} aria-labelledby={`support-browser-tab-${view}`} className="space-y-4">
    {view !== 'jobs' && <Filters label="Backup filtreleri">
      <FilterField label="Backup Job"><select className={filterControlClass} value={jobId} onChange={event => update({ jobId: event.target.value })}><option value="">Tüm Backup Jobs</option>{jobs.data?.map(job => <option key={job.id} value={job.id}>{job.name}</option>)}</select></FilterField>
      {view === 'runs' ? <FilterField label="Run durumu"><select className={filterControlClass} value={get('runStatus')} onChange={event => update({ runStatus: event.target.value })}><option value="">Tüm durumlar</option>{runStatuses.map(status => <option key={status} value={status}>{runStatusLabels[status]}</option>)}</select></FilterField> : <>
        <FilterField label="Artifact durumu"><select className={filterControlClass} value={get('state')} onChange={event => update({ state: event.target.value })}><option value="">Tüm durumlar</option>{['Available', 'DeletePending', 'DeleteFailed', 'Deleted'].map(state => <option key={state}>{state}</option>)}</select></FilterField>
        <FilterField label="Başlangıç tarihi"><input className={filterControlClass} type="date" value={from} onChange={event => update({ from: event.target.value })} /></FilterField>
        <FilterField label="Bitiş tarihi"><input className={filterControlClass} type="date" value={to} onChange={event => update({ to: event.target.value })} /></FilterField>
      </>}
    </Filters>}
    {jobs.error && <SupportQueryState isPending={false} error={jobs.error} />}
    {view === 'jobs' ? <SupportQueryState isPending={jobs.isPending} error={jobs.error}><DataTable rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} /></SupportQueryState> : invalidDates && view === 'artifacts' ? <Notice tone="error">Tarih aralığı geçersiz. Bitiş başlangıçtan önce olamaz.</Notice> : <SupportQueryState isPending={listing.isPending} error={listing.error}>
      {view === 'runs' ? <DataTable rows={runs.data?.items ?? []} columns={runColumns} keyExtractor={run => run.id} /> : <DataTable rows={artifacts.data?.items ?? []} columns={columns} keyExtractor={artifact => artifact.id} />}
      {listing.data && <Pagination page={page} pageSize={listing.data.pageSize} totalCount={listing.data.totalCount} disabled={listing.isFetching} onPageChange={next => update({ browserPage: next })} />}
    </SupportQueryState>}
    </div>
    {get('artifact') && <SupportArtifactPanel key={get('artifact')} session={session} artifactId={get('artifact')} />}
  </div>
}
function SupportArtifactPanel({ session, artifactId }: { session: SupportSession; artifactId: string }) {
  const { update } = useListParams('browserPage')
  const artifact = useSupportArtifact(supportScope(session, 'view'), artifactId)
  const access = supportAccess(session)
  return <Section title="Artifact detayları" actions={<Button variant="outline" onClick={() => update({ artifact: '' })}>Detayları gizle</Button>}>
    <div className="space-y-4">
    <SupportQueryState isPending={artifact.isPending} error={artifact.error}>{artifact.data && <>
      <div className="flex flex-wrap gap-3"><Button disabled={!access.restore || artifact.data.state !== 'Available' || artifact.data.backupType !== 'Full'} onClick={() => update({ tab: 'restores', restoreArtifact: artifactId, restoreId: '' })}>Restore…</Button>{!access.restore && <p className="text-sm text-text-muted">RestoreBackup izni gerekir.</p>}</div>
      <SupportArtifactDownload scope={supportScope(session, 'download')} artifactId={artifactId} eligible={artifact.data.state === 'Available'} />
      <ArtifactDetails artifact={artifact.data} />
    </>}</SupportQueryState>
    </div>
  </Section>
}
