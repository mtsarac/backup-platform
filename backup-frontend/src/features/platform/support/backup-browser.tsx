import { tx, useLanguage } from '@/lib/i18n'
import { DataList, type Column } from '@/components/data/data-list'
import { Pagination } from '@/components/data/pagination'
import { RelativeTime } from '@/components/data/relative-time'
import { StatusPill } from '@/components/data/status-pill'
import { Button } from '@/components/ui/button'
import { FilterField, Filters, filterControlClass } from '@/components/ui/filters'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { TechnicalDetails } from '@/components/ui/technical-details'
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
import type { SupportArtifact, SupportJob, SupportRun, SupportSession } from './types'
import { formatBytes, formatDateTime, formatSeconds } from '@/lib/format'
import { statusLabel } from '@/lib/status'
import { QueryState } from '@/components/data/query-state'
import { errorMessage } from '@/lib/api/failure-messages'

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
  // Visible: names, result, size and everyday times. Collapsed and copyable: ids, raw states, trigger, schedule internals.
  const columns: Column<SupportArtifact>[] = [
    { key: 'database', card: 'title', header: tx('Veritabanı'), wrap: true, render: artifact => <button className="font-medium text-primary hover:underline" onClick={() => update({ artifact: artifact.id })}>{artifact.databaseName ?? tx('Veritabanı bildirilmedi')}</button> },
    { key: 'job', header: tx('Yedekleme işi'), wrap: true, render: artifact => artifact.jobName ?? '—' },
    { key: 'size', header: tx('Boyut'), render: artifact => formatBytes(artifact.sizeBytes) },
    { key: 'created', header: tx('Oluşturulma'), render: artifact => <RelativeTime value={artifact.createdAt} /> },
    { key: 'state', card: 'status', header: tx('Durum'), render: artifact => <StatusPill status={artifactState(artifact)} /> },
    { key: 'details', card: 'actions', header: tx('Ayrıntılar'), wrap: true, render: artifact => <TechnicalDetails items={[
      { label: tx('Yedek dosyası kimliği'), value: artifact.id },
      { label: tx('Ham durum'), value: artifact.state },
      { label: tx('Yedekleme bilgisayarı'), value: artifact.agentName ?? '—' },
      { label: tx('Saklama sonu'), value: artifact.retentionExpiresAt ? formatDateTime(artifact.retentionExpiresAt) : tx('Süre sınırı yok') },
      ...artifact.replicas.map((replica, index) => ({ label: tx('Kopya {0}', { '0': index + 1 }), value: `${replica.storageProvider} · ${replica.verificationState}` })),
    ]} /> },
  ]
  const jobColumns: Column<SupportJob>[] = [
    { key: 'name', card: 'title', header: tx('Yedekleme işi'), wrap: true, render: job => <button className="font-medium text-primary hover:underline" onClick={() => update({ view: 'runs', jobId: job.id })}>{job.name}</button> },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: job => job.agentName },
    { key: 'database', header: tx('Veritabanı'), wrap: true, render: job => job.database },
    { key: 'enabled', header: tx('Etkin'), render: job => job.enabled ? tx('Evet') : tx('Hayır') },
    { key: 'last', card: 'status', header: tx('Son sonuç'), render: job => job.lastRun ? <StatusPill status={job.lastRun.status} /> : '—' },
    { key: 'details', card: 'actions', header: tx('Ayrıntılar'), wrap: true, render: job => <TechnicalDetails items={[
      { label: tx('İş kimliği'), value: job.id },
      { label: tx('Veritabanı motoru ve yedek türü'), value: `${job.engine} · ${job.backupType}` },
      { label: tx('Depolama sağlayıcısı'), value: job.storageProvider },
      ...(job.schedule?.enabled ? [
        { label: tx('Zamanlama ifadesi (cron)'), value: job.schedule.cronExpression },
        { label: tx('Saat dilimi'), value: job.schedule.timeZone },
      ] : [{ label: tx('Zamanlama'), value: tx('Kapalı') }]),
      { label: tx('Saklama'), value: job.retention?.enabled ? formatSeconds(job.retention.keepForSeconds) : tx('Süre sınırı yok') },
    ]} /> },
  ]
  const runColumns: Column<SupportRun>[] = [
    { key: 'job', card: 'title', header: tx('Yedekleme işi'), wrap: true, render: run => run.jobName },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), wrap: true, render: run => run.agentName },
    { key: 'status', card: 'status', header: tx('Durum'), render: run => <StatusPill status={run.status} /> },
    { key: 'started', header: tx('Başladı'), render: run => <RelativeTime value={run.startedAt} /> },
    { key: 'reason', header: tx('Sonuç'), wrap: true, render: run => run.reasonCode ? failureMessage(run.reasonCode) : '—' },
    { key: 'details', card: 'actions', header: tx('Ayrıntılar'), wrap: true, render: run => <div className="space-y-2">
      {run.artifactIds.map((id, index) => <div key={id}><button className="text-primary hover:underline" onClick={() => update({ view: 'artifacts', artifact: id })}>{tx("Yedek dosyası {0} ayrıntıları", { '0': index + 1 })}</button></div>)}
      <TechnicalDetails items={[
        { label: tx('Deneme kimliği'), value: run.id },
        { label: tx('Tetikleyici'), value: run.triggerType },
        { label: tx('Bitiş'), value: formatDateTime(run.completedAt) },
        ...(run.reasonCode ? [{ label: tx('Hata kodu'), value: run.reasonCode }] : []),
        ...run.attempts.map(attempt => ({ label: tx('{0}. girişim', { '0': attempt.number }), value: [statusLabel('general', attempt.status), formatDateTime(attempt.startedAt), formatDateTime(attempt.completedAt), attempt.reasonCode ? failureMessage(attempt.reasonCode) : ''].filter(Boolean).join(' · ') })),
        ...run.artifactIds.map((id, index) => ({ label: tx('Yedek dosyası {0} kimliği', { '0': index + 1 }), value: id })),
      ]} />
    </div> },
  ]
  if (!access.view) return <Notice>{tx("Backup Browser için etkin oturum sahipliği ve ViewBackupMetadata izni gerekir.")}</Notice>
  const listing = view === 'runs' ? runs : artifacts
  return <div className="space-y-6">
    <Tabs label={tx("Yedek tarayıcısı görünümleri")} idPrefix="support-browser" value={view} onChange={(value) => update({ view: value })} items={[{ value: 'jobs', label: tx('Yedekleme işleri') }, { value: 'runs', label: tx('Yedekleme denemeleri') }, { value: 'artifacts', label: tx('Yedek dosyaları') }]} />
    <div role="tabpanel" id={`support-browser-panel-${view}`} aria-labelledby={`support-browser-tab-${view}`} className="space-y-4">
    {view !== 'jobs' && <Filters label={tx("Yedek filtreleri")}>
      <SearchablePicker label={tx("Yedekleme işi")} placeholder={tx("Tüm yedekleme işleri")} value={jobId} onChange={value => update({ jobId: value })}
        options={(jobs.data ?? []).map(job => ({ value: job.id, label: job.name, description: job.database }))} disabled={jobs.isPending || Boolean(jobs.error)} />
      {view === 'runs' ? <FilterField label={tx("Deneme durumu")}><select className={filterControlClass} value={get('runStatus')} onChange={event => update({ runStatus: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{runStatuses.map(status => <option key={status} value={status}>{statusLabel('general', status)}</option>)}</select></FilterField> : <>
        <FilterField label={tx("Yedek dosyası durumu")}><select className={filterControlClass} value={get('state')} onChange={event => update({ state: event.target.value })}><option value="">{tx("Tüm durumlar")}</option>{['Available', 'DeletePending', 'DeleteFailed', 'Deleted'].map(state => <option key={state} value={state}>{statusLabel('general', state)}</option>)}</select></FilterField>
        <FilterField label={tx("Başlangıç tarihi")}><input className={filterControlClass} type="date" value={from} onChange={event => update({ from: event.target.value })} /></FilterField>
        <FilterField label={tx("Bitiş tarihi")}><input className={filterControlClass} type="date" value={to} onChange={event => update({ to: event.target.value })} /></FilterField>
      </>}
    </Filters>}
    {jobs.error && <QueryState forbiddenMessage={errorMessage} isPending={false} error={jobs.error} />}
    {view === 'jobs' ? <QueryState forbiddenMessage={errorMessage} isPending={jobs.isPending} error={jobs.error}><DataList rows={jobs.data ?? []} columns={jobColumns} keyExtractor={job => job.id} /></QueryState> : invalidDates && view === 'artifacts' ? <Notice tone="warning">{tx("Tarih aralığı geçersiz. Bitiş başlangıçtan önce olamaz.")}</Notice> : <QueryState forbiddenMessage={errorMessage} isPending={listing.isPending} error={listing.error}>
      {view === 'runs' ? <DataList rows={runs.data?.items ?? []} columns={runColumns} keyExtractor={run => run.id} /> : <DataList rows={artifacts.data?.items ?? []} columns={columns} keyExtractor={artifact => artifact.id} />}
      {listing.data && <Pagination page={page} pageSize={listing.data.pageSize} totalCount={listing.data.totalCount} disabled={listing.isFetching} onPageChange={next => update({ browserPage: next })} />}
    </QueryState>}
    </div>
    {get('artifact') && <SupportArtifactPanel key={get('artifact')} session={session} artifactId={get('artifact')} />}
  </div>
}
function SupportArtifactPanel({ session, artifactId }: { session: SupportSession; artifactId: string }) {
  useLanguage()
  const { update } = useListParams('browserPage')
  const artifact = useSupportArtifact(supportScope(session, 'view'), artifactId)
  const access = supportAccess(session)
  return <Section title={tx("Yedek dosyası ayrıntıları")} actions={<Button variant="outline" onClick={() => update({ artifact: '' })}>{tx("Detayları gizle")}</Button>}>
    <div className="space-y-4">
    <QueryState forbiddenMessage={errorMessage} isPending={artifact.isPending} error={artifact.error}>{artifact.data && <>
      <div className="flex flex-wrap gap-3"><Button disabled={!access.restore || artifact.data.state !== 'Available' || artifact.data.backupType !== 'Full'} onClick={() => update({ tab: 'restores', restoreArtifact: artifactId, restoreId: '' })}>{tx("Restore…")}</Button>{!access.restore && <p className="text-sm text-text-muted">{tx("RestoreBackup izni gerekir.")}</p>}</div>
      <SupportArtifactDownload scope={supportScope(session, 'download')} artifactId={artifactId} eligible={artifact.data.state === 'Available'} />
      <ArtifactDetails artifact={artifact.data} />
    </>}</QueryState>
    </div>
  </Section>
}
