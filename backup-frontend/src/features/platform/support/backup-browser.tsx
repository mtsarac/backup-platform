import { tx, useLanguage } from '@/lib/i18n'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { Tabs } from '@/components/ui/tabs'
import { SearchablePicker } from '@/components/ui/searchable-picker'
import { ArtifactDetails } from '@/features/artifacts/artifact-details'
import { artifactState } from '@/features/artifacts/format'
import { runStatuses } from '@/features/backups/presentation'
import { PLATFORM_PAGE_SIZE, useListParams } from '@/features/platform/use-list-params'
import { failureMessage } from '@/lib/api/failure-messages'
import { SupportArtifactDownload } from './artifact-download'
import { useSupportArtifact, useSupportArtifacts, useSupportJobs, useSupportRuns } from './hooks'
import { supportAccess, supportScope } from './presentation'
import { SupportQueryState } from './support-query-state'
import type { SupportArtifact, SupportJob, SupportRun, SupportSession } from './types'
import { formatBytes, formatDateTime } from '@/lib/format'
import { statusLabel } from '@/lib/status'

export function SupportBackupBrowser({ session }: { session: SupportSession }) {
  useLanguage()
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
    { key: 'database', header: tx('Database'), wrap: true, render: artifact => <button className="font-medium text-primary hover:underline" onClick={() => update({ artifact: artifact.id })}>{artifact.databaseName ?? tx('Database bildirilmedi')}</button> },
    { key: 'job', header: tx('Backup Job'), wrap: true, render: artifact => artifact.jobName ?? '—' },
    { key: 'agent', card: 'hidden', header: tx('Agent'), wrap: true, render: artifact => artifact.agentName ?? '—' },
    { key: 'size', header: tx('Boyut'), render: artifact => formatBytes(artifact.sizeBytes) },
    { key: 'created', header: tx('Oluşturulma'), render: artifact => formatDateTime(artifact.createdAt) },
    { key: 'retention', card: 'hidden', header: tx('Saklama sonu'), render: artifact => artifact.retentionExpiresAt ? formatDateTime(artifact.retentionExpiresAt) : tx('Süre sınırı yok') },
    { key: 'state', card: 'status', header: tx('Durum'), render: artifact => <StatusPill status={artifactState(artifact)} /> },
    { key: 'replicas', card: 'hidden', header: tx('Replikalar'), render: artifact => <div className="flex flex-wrap gap-1">{artifact.replicas.map(replica => <StatusPill key={replica.id} status={replica.verificationState} />)}</div> },
  ]
  const jobColumns: Column<SupportJob>[] = [
    { key: 'name', header: tx('Backup Job'), wrap: true, render: job => <button className="text-primary hover:underline" onClick={() => update({ view: 'runs', jobId: job.id })}>{job.name}</button> },
    { key: 'agent', header: tx('Agent'), wrap: true, render: job => job.agentName },
    { key: 'database', header: tx('Database'), wrap: true, render: job => job.database },
    { key: 'type', card: 'hidden', header: tx('Tür'), render: job => `${job.engine} · ${job.backupType}` },
    { key: 'storage', card: 'hidden', header: tx('Storage Provider'), render: job => job.storageProvider },
    { key: 'enabled', card: 'hidden', header: tx('Etkin'), render: job => job.enabled ? tx('Evet') : tx('Hayır') },
    { key: 'schedule', card: 'hidden', header: tx('Zamanlama'), wrap: true, render: job => job.schedule?.enabled ? `${job.schedule.cronExpression} · ${job.schedule.timeZone}` : tx('Kapalı') },
    { key: 'retention', card: 'hidden', header: tx('Saklama'), render: job => job.retention?.enabled ? tx("{0} sn", { "0": job.retention.keepForSeconds }) : tx('Süre sınırı yok') },
    { key: 'last', card: 'status', header: tx('Son Run'), render: job => job.lastRun ? <StatusPill status={job.lastRun.status} /> : '—' },
  ]
  const runColumns: Column<SupportRun>[] = [
    { key: 'job', header: tx('Backup Job'), wrap: true, render: run => run.jobName },
    { key: 'agent', header: tx('Agent'), wrap: true, render: run => run.agentName },
    { key: 'status', card: 'status', header: tx('Durum'), render: run => <StatusPill status={run.status} /> },
    { key: 'trigger', card: 'hidden', header: tx('Tetikleyici'), render: run => run.triggerType },
    { key: 'times', header: tx('Başlangıç / Bitiş'), render: run => <div>{formatDateTime(run.startedAt)}<br />{formatDateTime(run.completedAt)}</div> },
    { key: 'reason', header: tx('Sonuç'), wrap: true, render: run => run.reasonCode ? failureMessage(run.reasonCode) : '—' },
    { key: 'attempts', card: 'hidden', header: tx('Denemeler'), wrap: true, render: run => <details><summary>{run.currentAttemptNumber}{tx(". attempt ·")}{' '}{run.attempts.length}{' '}{tx("kayıt")}</summary>{run.attempts.map(attempt => <p key={attempt.number}>{attempt.number} · {tx(attempt.status)} · {formatDateTime(attempt.startedAt)} · {formatDateTime(attempt.completedAt)}{attempt.reasonCode ? ` · ${failureMessage(attempt.reasonCode)}` : ''}</p>)}</details> },
    { key: 'artifacts', card: 'actions', header: tx('Artifact'), wrap: true, render: run => <div className="grid gap-2">{run.artifactIds.map((id, index) => <div key={id}><button className="text-primary hover:underline" onClick={() => update({ view: 'artifacts', artifact: id })}>{tx("Artifact")}{' '}{index + 1}{' '}{tx("ayrıntıları")}</button><p className="break-all font-mono text-xs text-text-muted">{id}</p></div>)}</div> },
  ]
  if (!access.view) return <Notice>{tx("Backup Browser için etkin oturum sahipliği ve ViewBackupMetadata izni gerekir.")}</Notice>
  const listing = view === 'runs' ? runs : artifacts
  return <div className="space-y-6">
    <Tabs label={tx("Backup Browser görünümleri")} idPrefix="support-browser" value={view} onChange={(value) => update({ view: value })} items={[{ value: 'jobs', label: tx('Backup Jobs') }, { value: 'runs', label: tx('Backup Runs') }, { value: 'artifacts', label: tx('Artifacts') }]} />
    <div role="tabpanel" id={`support-browser-panel-${view}`} aria-labelledby={`support-browser-tab-${view}`} className="space-y-4">
    {view !== 'jobs' && <Filters label={tx("Backup filtreleri")}>
      <SearchablePicker label={tx("Yedekleme işi")} placeholder={tx("Tüm Backup Jobs")} value={jobId} onChange={value => update({ jobId: value })}
        options={(jobs.data ?? []).map(job => ({ value: job.id, label: job.name, description: job.database }))} disabled={jobs.isPending || Boolean(jobs.error)} />
      {view === 'runs' ? <FilterField label={tx("Run durumu")}><select className={filterControlClass} value={get('runStatus')} onChange={event => update({ runStatus: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{runStatuses.map(status => <option key={status} value={status}>{statusLabel('general', status)}</option>)}</select></FilterField> : <>
        <FilterField label={tx("Artifact durumu")}><select className={filterControlClass} value={get('state')} onChange={event => update({ state: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{['Available', 'DeletePending', 'DeleteFailed', 'Deleted'].map(state => <option key={state}>{state}</option>)}</select></FilterField>
        <FilterField label={tx("Başlangıç tarihi")}><input className={filterControlClass} type="date" value={from} onChange={event => update({ from: event.target.value })} /></FilterField>
        <FilterField label={tx("Bitiş tarihi")}><input className={filterControlClass} type="date" value={to} onChange={event => update({ to: event.target.value })} /></FilterField>
      </>}
    </Filters>}
    {jobs.error && <SupportQueryState isPending={false} error={jobs.error} />}
    {view === 'jobs' ? <SupportQueryState isPending={jobs.isPending} error={jobs.error}><DataList rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} /></SupportQueryState> : invalidDates && view === 'artifacts' ? <Notice tone="warning">{tx("Tarih aralığı geçersiz. Bitiş başlangıçtan önce olamaz.")}</Notice> : <SupportQueryState isPending={listing.isPending} error={listing.error}>
      {view === 'runs' ? <DataList rows={runs.data?.items ?? []} columns={runColumns} keyExtractor={run => run.id} /> : <DataList rows={artifacts.data?.items ?? []} columns={columns} keyExtractor={artifact => artifact.id} />}
      {listing.data && <Pagination page={page} pageSize={listing.data.pageSize} totalCount={listing.data.totalCount} disabled={listing.isFetching} onPageChange={next => update({ browserPage: next })} />}
    </SupportQueryState>}
    </div>
    {get('artifact') && <SupportArtifactPanel key={get('artifact')} session={session} artifactId={get('artifact')} />}
  </div>
}
function SupportArtifactPanel({ session, artifactId }: { session: SupportSession; artifactId: string }) {
  useLanguage()
  const { update } = useListParams('browserPage')
  const artifact = useSupportArtifact(supportScope(session, 'view'), artifactId)
  const access = supportAccess(session)
  return <Section title={tx("Artifact detayları")} actions={<Button variant="outline" onClick={() => update({ artifact: '' })}>{tx("Detayları gizle")}</Button>}>
    <div className="space-y-4">
    <SupportQueryState isPending={artifact.isPending} error={artifact.error}>{artifact.data && <>
      <div className="flex flex-wrap gap-3"><Button disabled={!access.restore || artifact.data.state !== 'Available' || artifact.data.backupType !== 'Full'} onClick={() => update({ tab: 'restores', restoreArtifact: artifactId, restoreId: '' })}>{tx("Restore…")}</Button>{!access.restore && <p className="text-sm text-text-muted">{tx("RestoreBackup izni gerekir.")}</p>}</div>
      <SupportArtifactDownload scope={supportScope(session, 'download')} artifactId={artifactId} eligible={artifact.data.state === 'Available'} />
      <ArtifactDetails artifact={artifact.data} />
    </>}</SupportQueryState>
    </div>
  </Section>
}
