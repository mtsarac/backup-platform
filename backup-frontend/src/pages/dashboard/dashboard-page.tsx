import { tx, useLanguage } from '@/lib/i18n'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { AlertCircle, CheckCircle2, Database, HardDrive, LayoutGrid, List, Monitor, Plus, ShieldAlert } from 'lucide-react'
import { useState } from 'react'
import { apiRequest, tenantPath } from '@/lib/api/client'
import { failureMessage } from '@/lib/api/failure-messages'
import { useTenant } from '@/features/tenancy/use-tenant'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { KpiCard } from '@/components/data/kpi-card'
import { DataTable, type Column } from '@/components/data/data-table'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Section } from '@/components/ui/section'
import type { BackupRun } from '@/features/backups/types'
import { ReplicationBadge } from '@/features/backups/replication'
import type { BackupJob } from '@/features/backup-jobs/types'
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

const VIEW_KEY = 'backup-dashboard-view'

const bannerTones = {
  error: { box: 'border-error/40 bg-error-subtle', text: 'text-error', icon: ShieldAlert },
  success: { box: 'border-success/40 bg-success-subtle', text: 'text-success', icon: CheckCircle2 },
  warning: { box: 'border-warning/40 bg-warning-subtle', text: 'text-warning', icon: ShieldAlert },
} as const

/** The one sentence the dashboard exists to answer: is anything wrong right now? */
function StatusBanner({ issues, succeeded, href }: { issues: number; succeeded: number; href: string }) {
  useLanguage()
  const tone = issues > 0 ? 'error' : succeeded > 0 ? 'success' : 'warning'
  const { box, text, icon: Icon } = bannerTones[tone]
  return <section role="status" className={`flex flex-wrap items-center gap-4 rounded-xl border px-5 py-4 ${box}`}>
    <Icon aria-hidden="true" className={`size-9 shrink-0 ${text}`} />
    <div className="min-w-0 flex-1">
      {issues > 0 ? <><p className={`text-xl font-semibold ${text}`}>{issues}{' '}{tx("kategoride uyarı var")}</p><p className="text-[0.9375rem] text-text">{tx("Başarısız yedek, storage, replikasyon ve silme sayaçları değerlendirilir; bu sayı toplam hata sayısı değildir.")}</p></>
        : succeeded > 0 ? <><p className={`text-xl font-semibold ${text}`}>{tx("Son 24 saatte")}{' '}{succeeded}{' '}{tx("başarılı yedek")}</p><p className="text-[0.9375rem] text-text">{tx("Bu sonuç, tüm agent bağlantılarının veya planlı görevlerin sağlıklı olduğunu göstermez.")}</p></>
          : <><p className={`text-xl font-semibold ${text}`}>{tx("Son 24 saatte yedek alınmadı")}</p><p className="text-[0.9375rem] text-text">{tx("Planlı görevlerinizin çalıştığından emin olun.")}</p></>}
    </div>
    {issues > 0 && <Link to={href} className={buttonVariants()}>{tx("Yedekleri incele")}</Link>}
  </section>
}

function Row({ label, value, tone }: { label: string; value: number; tone?: 'error' }) {
  useLanguage()
  return <div className="flex items-center justify-between gap-3 py-1.5"><dt className="text-[0.9375rem] text-text-muted">{tx(label)}</dt><dd className={`font-semibold tabular-nums ${tone === 'error' && value > 0 ? 'text-error' : 'text-text-heading'}`}>{value}</dd></div>
}

export function DashboardPage() {
  useLanguage()
  const { tenantId, tenant, canAdmin } = useTenant()
  const query = useQuery({ queryKey: ['tenant', tenantId, 'dashboard'], queryFn: ({ signal }) => apiRequest<Dashboard>(tenantPath(tenantId, 'dashboard'), { signal }), refetchInterval: 15_000, refetchIntervalInBackground: false })
  const data = query.data
  const [layout, setLayout] = useState<'table' | 'cards'>(() => localStorage.getItem(VIEW_KEY) === 'table' ? 'table' : 'cards')
  const pickLayout = (next: 'table' | 'cards') => { localStorage.setItem(VIEW_KEY, next); setLayout(next) }
  const base = `/tenants/${tenantId}`
  const columns: Column<BackupRun>[] = [
    { key: 'job', header: tx('Job / Database'), wrap: true, render: run => <Link className="font-medium text-primary underline-offset-4 hover:underline" to={`${base}/backup-runs/${run.id}`}>{run.jobName} / {run.configuration.database}</Link> },
    { key: 'agent', header: tx('Agent'), render: run => run.agentName ?? '—' },
    { key: 'date', header: tx('Başlangıç'), render: run => formatDateTime(run.startedAt ?? run.createdAt) },
    { key: 'status', header: tx('Durum'), render: run => <span className="inline-flex flex-wrap items-center gap-2"><StatusPill status={run.status} /><ReplicationBadge replication={run.replication} /></span> },
    { key: 'attention', header: tx('Bilgi'), wrap: true, render: run => run.reasonCode ? failureMessage(run.reasonCode) : run.status === 'RetryPending' ? tx('Yeni attempt bekleniyor.') : run.status === 'CancellationRequested' ? tx('Agent iptali henüz tamamlamadı.') : '—' },
  ]
  const replicationShort = data?.runs.replicationInsufficientLast24Hours ?? 0
  return <>
    <PageHeader title={tx("Dashboard")} description={tx("{0} · Gerçek backup ve Agent durumu", { "0": tenant.name })}
      actions={<><Link to={`${base}/backup-runs`} className={buttonVariants({ variant: 'outline' })}>{tx("Backup Runs")}</Link>{canAdmin && <Link to={`${base}/backup-jobs/new`} className={buttonVariants()}><Plus aria-hidden="true" className="size-4" />{tx("Yeni Backup Job")}</Link>}</>} />
    <QueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <StatusBanner succeeded={data.runs.succeededLast24Hours} href={`${base}/backup-runs?status=Failed`} issues={[data.runs.failedLast24Hours, data.runs.storageFailedLast24Hours, replicationShort, data.artifacts.deleteFailed].filter(count => count > 0).length} />
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          <KpiCard label={tx("Agents")} hint={tx("{0} çevrimiçi · bağlı bilgisayar / sunucu", { "0": data.agents.online })} value={data.agents.total} icon={<Monitor className="size-5" />} />
          <KpiCard label={tx("Scheduled Jobs")} hint={tx("Otomatik çalışan görev")} value={data.jobs.scheduled} icon={<Database className="size-5" />} />
          <KpiCard label={tx("Yedek üretildi · son 24 saat")} value={data.runs.succeededLast24Hours} tone="success" icon={<HardDrive className="size-5" />} />
          <KpiCard label={tx("Başarısız · son 24 saat")} value={data.runs.failedLast24Hours} tone={data.runs.failedLast24Hours > 0 ? 'error' : undefined} icon={<AlertCircle className="size-5" />} />
        </div>
        <ObservabilityPanel tenantId={tenantId} />
        {data.artifacts.deleteFailed > 0 && <ErrorNotice what={`${data.artifacts.deleteFailed} ${tx("artifact silinemedi.")}`} dataImpact={tx("Silinemeyen dosyalar depoda duruyor; diğer yedekleriniz etkilenmedi.")} action={tx("Nedenini inceleyip silmeyi yeniden deneyin.")} control={<Link className="font-medium underline" to={`${base}/artifacts?state=DeleteFailed`}>{tx("Silme hatalarını inceleyin.")}</Link>} />}
        {data.runs.storageFailedLast24Hours > 0 && <ErrorNotice what={`${data.runs.storageFailedLast24Hours} ${tx("backup sırasında storage yazma veya replica doğrulama hatası oluştu.")}`} dataImpact={tx("Başarısız denemeler yedek üretmedi; önceki başarılı yedekleriniz korunur.")} action={tx("Başarısız yedeklemeleri inceleyin.")} control={<Link className="font-medium underline" to={`${base}/backup-runs?status=Failed`}>{tx("Başarısız backups")}</Link>} />}
        {replicationShort > 0 && <ErrorNotice what={`${replicationShort} ${tx("backup son 24 saatte kullanılabilir yedek üretti, ancak gereken doğrulanmış replika sayısına ulaşamadı.")}`} dataImpact={tx("Yedekler kullanılabilir durumda ama gereken kopya sayısına ulaşılamadı.")} action={tx("Eksik kopyaları onarmak için sonuçları inceleyin.")} control={<Link className="font-medium underline" to={`${base}/backup-runs?status=Succeeded`}>{tx("Replikasyon sonuçlarını inceleyin.")}</Link>} />}
        <div className="grid gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(18rem,1fr)]">
          <section className="min-w-0 space-y-3" aria-labelledby="recent-runs-heading">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 id="recent-runs-heading" className="text-base font-semibold text-text-heading">{tx("Son backups")}</h2>
              <div className="flex items-center gap-2">
                <Button variant={layout === 'cards' ? 'default' : 'outline'} size="sm" aria-pressed={layout === 'cards'} onClick={() => pickLayout('cards')}><LayoutGrid />{tx("Kartlar")}</Button>
                <Button variant={layout === 'table' ? 'default' : 'outline'} size="sm" aria-pressed={layout === 'table'} onClick={() => pickLayout('table')}><List />{tx("Liste")}</Button>
                <Link className="text-sm font-medium text-primary underline-offset-4 hover:underline" to={`${base}/backup-runs`}>{tx("Tüm history")}</Link>
              </div>
            </div>
            <DataTable layout={layout} rows={data.recentRuns} columns={columns} keyExtractor={run => run.id} emptyMessage={tx("Bu tenant için henüz backup kaydı yok.")} />
          </section>
          <div className="space-y-4">
            <Section title={tx("Agent bağlantı durumu ve yaşam döngüsü")}><dl className="divide-y divide-border">
              <Row label={tx("Çevrimiçi")} value={data.agents.online} /><Row label={tx("Çevrimdışı")} value={data.agents.offline} tone="error" /><Row label={tx("Bağlantı bilinmiyor")} value={data.agents.unknown} />
              <Row label={tx("Devre dışı (lifecycle)")} value={data.agents.disabled} /><Row label={tx("İptal edilmiş (lifecycle)")} value={data.agents.revoked} />
            </dl></Section>
            <Section title={tx("Çalışan işler")}><dl className="divide-y divide-border">
              <Row label={tx("Running")} value={data.runs.running} /><Row label={tx("RetryPending")} value={data.runs.retryPending} /><Row label={tx("CancellationRequested")} value={data.runs.cancellationRequested} />
              <Row label={tx("Aktif Runs")} value={data.runs.active} /><Row label={tx("Etkin Jobs")} value={data.jobs.enabled} /><Row label={tx("Silme bekleyen artifacts")} value={data.artifacts.deletePending} />
            </dl></Section>
            <Section title={tx("Sonraki scheduled backups")}>{data.upcomingJobs.length
              ? <ul className="divide-y divide-border">{data.upcomingJobs.map(job => <li key={job.id} className="space-y-0.5 py-2 first:pt-0 last:pb-0"><Link className="font-medium text-primary underline-offset-4 hover:underline" to={`${base}/backup-jobs/${job.id}/edit`}>{job.name}</Link><p className="text-sm text-text-muted">{job.schedule?.nextOccurrenceAt ? formatDateTime(job.schedule.nextOccurrenceAt) : '—'} · {job.schedule?.timeZone}</p></li>)}</ul>
              : <p className="text-sm text-text-muted">{tx("Etkin schedule yok.")}</p>}</Section>
          </div>
        </div>
      </>}
    </QueryState>
  </>
}
