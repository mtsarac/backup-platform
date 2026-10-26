import { tx, useLanguage } from '@/lib/i18n'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { AlertCircle, Database, HardDrive, Monitor, Plus } from 'lucide-react'
import { apiRequest, tenantPath } from '@/lib/api/client'
import { failureMessage } from '@/lib/api/failure-messages'
import { useTenant } from '@/features/tenancy/use-tenant'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { statusLabel } from '@/lib/status'
import { KpiCard } from '@/components/data/kpi-card'
import { DataList, type Column } from '@/components/data/data-list'
import { RelativeTime } from '@/components/data/relative-time'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice } from '@/components/ui/notice'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import type { BackupRun } from '@/features/backups/types'
import { ReplicationSummary } from '@/features/backups/replication'
import type { BackupJob } from '@/features/backup-jobs/types'
import { HealthStatusCard } from './health-status-card'
import { useHealthSummary } from './health-summary'
import { ObservabilityPanel } from './observability-panel'
import { formatDateTime } from '@/lib/format'

interface Dashboard {
  jobs: { total: number; enabled: number; scheduled: number }
  agents: { total: number; online: number; offline: number; unknown: number; disabled: number; revoked: number }
  runs: { active: number; running: number; retryPending: number; cancellationRequested: number; storageFailedLast24Hours: number; failedLast24Hours: number; succeededLast24Hours: number; replicationInsufficientLast24Hours?: number }
  artifacts: { total: number; sizeBytes: number; deletePending: number; deleteFailed: number }
  recentRuns: BackupRun[]
  upcomingJobs: BackupJob[]
}

function Row({ label, value, tone }: { label: string; value: number; tone?: 'error' }) {
  return <div className="flex items-center justify-between gap-3 py-1.5"><dt className="text-[0.9375rem] text-text-muted">{label}</dt><dd className={`font-semibold tabular-nums ${tone === 'error' && value > 0 ? 'text-error' : 'text-text-heading'}`}>{value}</dd></div>
}

/** One plain sentence about a run: why it failed, or what it is waiting for. */
function runNote(run: BackupRun): string {
  if (run.reasonCode) return failureMessage(run.reasonCode)
  if (run.status === 'RetryPending') return tx("Yeniden deneme bekleniyor.")
  if (run.status === 'CancellationRequested') return tx("Yedekleme bilgisayarı iptali henüz tamamlamadı.")
  return '—'
}

export function DashboardPage() {
  useLanguage()
  const { tenantId, tenant, canAdmin } = useTenant()
  const query = useQuery({ queryKey: ['tenant', tenantId, 'dashboard'], queryFn: ({ signal }) => apiRequest<Dashboard>(tenantPath(tenantId, 'dashboard'), { signal }), refetchInterval: 15_000, refetchIntervalInBackground: false })
  const data = query.data
  const base = `/tenants/${tenantId}`
  const columns: Column<BackupRun>[] = [
    { key: 'job', card: 'title', header: tx('Yedekleme işi'), wrap: true, render: run => <Link className="font-medium text-primary underline-offset-4 hover:underline" to={`${base}/backup-runs/${run.id}`}>{run.jobName}</Link> },
    { key: 'agent', header: tx('Yedekleme bilgisayarı'), render: run => run.agentName ?? '—' },
    { key: 'date', header: tx('Başlangıç'), render: run => <RelativeTime value={run.startedAt ?? run.createdAt} /> },
    { key: 'status', header: tx('Durum'), card: 'status', render: run => <StatusPill status={run.status} /> },
    { key: 'attention', header: tx('Bilgi'), wrap: true, render: run => <div className="space-y-2"><p>{runNote(run)}</p><TechnicalDetails items={[{ label: tx('Veritabanı'), value: run.configuration.database }]}>{run.replication && <ReplicationSummary replication={run.replication} />}</TechnicalDetails></div> },
  ]
  const replicationShort = data?.runs.replicationInsufficientLast24Hours ?? 0
  const noJobs = useHealthSummary(tenantId).data?.totalJobs === 0
  return <>
    <PageHeader title={tx("Ana Sayfa")} description={tx("{0} · Yedeklerinizin durumu", { "0": tenant.name })}
      actions={<><Link to={`${base}/backup-runs`} className={buttonVariants({ variant: 'outline' })}>{tx("Yedekleme denemeleri")}</Link>{canAdmin && <Link to={`${base}/backup-jobs/new`} className={buttonVariants()}><Plus aria-hidden="true" className="size-4" />{tx("Yeni yedekleme işi")}</Link>}</>} />
    <HealthStatusCard tenantId={tenantId} canAdmin={canAdmin} />
    <QueryState isPending={query.isPending} error={query.error} onRetry={() => void query.refetch()}>
      {data && <>
        {!noJobs && <section className="min-w-0 space-y-3" aria-labelledby="recent-runs-heading">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="recent-runs-heading" className="text-base font-semibold text-text-heading">{tx("Son yedeklemeler")}</h2>
            <Link className="text-sm font-medium text-primary underline-offset-4 hover:underline" to={`${base}/backup-runs`}>{tx("Tüm geçmiş")}</Link>
          </div>
          <DataList rows={data.recentRuns} columns={columns} keyExtractor={run => run.id} emptyMessage={tx("Henüz yedekleme kaydı yok.")} />
        </section>}
        <details className="group rounded-xl border border-border bg-surface shadow-card">
          <summary className="min-h-11 cursor-pointer select-none rounded-xl px-5 py-3 font-semibold text-text-heading">{tx("Ayrıntılar")}</summary>
          <div className="space-y-6 border-t border-border p-5">
            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
              <KpiCard label={tx("Yedekleme bilgisayarları")} hint={tx("{0} çevrimiçi · bağlı bilgisayar / sunucu", { "0": data.agents.online })} value={data.agents.total} icon={<Monitor className="size-5" />} />
              <KpiCard label={tx("Planlı yedekleme işleri")} hint={tx("Otomatik çalışan görev")} value={data.jobs.scheduled} icon={<Database className="size-5" />} />
              <KpiCard label={tx("Yedek üretildi · son 24 saat")} value={data.runs.succeededLast24Hours} tone="success" icon={<HardDrive className="size-5" />} />
              <KpiCard label={tx("Başarısız · son 24 saat")} value={data.runs.failedLast24Hours} tone={data.runs.failedLast24Hours > 0 ? 'error' : undefined} icon={<AlertCircle className="size-5" />} />
            </div>
            {data.artifacts.deleteFailed > 0 && <ErrorNotice what={tx("{0} yedek dosyası silinemedi.", { "0": data.artifacts.deleteFailed })} dataImpact={tx("Silinemeyen dosyalar depoda duruyor; diğer yedekleriniz etkilenmedi.")} action={tx("Nedenini inceleyip silmeyi yeniden deneyin.")} control={<Link className="font-medium underline" to={`${base}/artifacts?state=DeleteFailed`}>{tx("Silme hatalarını inceleyin.")}</Link>} />}
            {data.runs.storageFailedLast24Hours > 0 && <ErrorNotice what={tx("Son 24 saatte {0} yedeklemede depolamaya yazma veya kopya doğrulama hatası oluştu.", { "0": data.runs.storageFailedLast24Hours })} dataImpact={tx("Başarısız denemeler yedek üretmedi; önceki başarılı yedekleriniz korunur.")} action={tx("Başarısız yedeklemeleri inceleyin.")} control={<Link className="font-medium underline" to={`${base}/backup-runs?status=Failed`}>{tx("Başarısız yedeklemeler")}</Link>} />}
            {replicationShort > 0 && <ErrorNotice what={tx("Son 24 saatte {0} yedekleme kullanılabilir yedek oluşturdu, ancak gereken doğrulanmış kopya sayısına ulaşamadı.", { "0": replicationShort })} dataImpact={tx("Yedekler kullanılabilir durumda ama gereken kopya sayısına ulaşılamadı.")} action={tx("Eksik kopyaları onarmak için sonuçları inceleyin.")} control={<Link className="font-medium underline" to={`${base}/backup-runs?status=Succeeded`}>{tx("Replikasyon sonuçlarını inceleyin.")}</Link>} />}
            <ObservabilityPanel tenantId={tenantId} />
            <div className="grid gap-4 lg:grid-cols-3">
              <Section title={tx("Agent bağlantı durumu ve yaşam döngüsü")}><dl className="divide-y divide-border">
                <Row label={tx("Çevrimiçi")} value={data.agents.online} /><Row label={tx("Çevrimdışı")} value={data.agents.offline} tone="error" /><Row label={tx("Bağlantı bilinmiyor")} value={data.agents.unknown} />
                <Row label={tx("Devre dışı (lifecycle)")} value={data.agents.disabled} /><Row label={tx("İptal edilmiş (lifecycle)")} value={data.agents.revoked} />
              </dl></Section>
              <Section title={tx("Çalışan işler")}><dl className="divide-y divide-border">
                <Row label={statusLabel('general', 'Running')} value={data.runs.running} /><Row label={statusLabel('general', 'RetryPending')} value={data.runs.retryPending} /><Row label={statusLabel('general', 'CancellationRequested')} value={data.runs.cancellationRequested} />
                <Row label={tx("Etkin yedekleme denemeleri")} value={data.runs.active} /><Row label={tx("Etkin yedekleme işleri")} value={data.jobs.enabled} /><Row label={tx("Silinmeyi bekleyen yedek dosyaları")} value={data.artifacts.deletePending} />
              </dl></Section>
              <Section title={tx("Sonraki planlı yedeklemeler")}>{data.upcomingJobs.length
                ? <ul className="divide-y divide-border">{data.upcomingJobs.map(job => <li key={job.id} className="space-y-0.5 py-2 first:pt-0 last:pb-0"><Link className="font-medium text-primary underline-offset-4 hover:underline" to={`${base}/backup-jobs/${job.id}/edit`}>{job.name}</Link><p className="text-sm text-text-muted">{job.schedule?.nextOccurrenceAt ? formatDateTime(job.schedule.nextOccurrenceAt) : '—'} · {job.schedule?.timeZone}</p></li>)}</ul>
                : <p className="text-sm text-text-muted">{tx("Etkin zamanlama yok.")}</p>}</Section>
            </div>
          </div>
        </details>
      </>}
    </QueryState>
  </>
}
