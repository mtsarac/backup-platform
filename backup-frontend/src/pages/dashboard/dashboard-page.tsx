import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { AlertCircle, Database, HardDrive, Monitor, PlayCircle } from 'lucide-react'
import type { ElementType } from 'react'
import { apiRequest, tenantPath } from '@/lib/api/client'
import { failureMessage } from '@/lib/api/failure-messages'
import { useTenant } from '@/features/tenancy/use-tenant'
import { QueryState } from '@/components/data/query-state'
import { StatusPill } from '@/components/data/status-pill'
import { DataTable, type Column } from '@/components/data/data-table'
import type { BackupRun } from '@/features/backups/types'
import type { BackupJob } from '@/features/backup-jobs/types'

interface Dashboard {
  jobs: { total: number; enabled: number; scheduled: number }
  agents: { total: number; online: number; offline: number; unknown: number; disabled: number; revoked: number }
  runs: { active: number; running: number; retryPending: number; cancellationRequested: number; storageFailedLast24Hours: number; failedLast24Hours: number; succeededLast24Hours: number }
  artifacts: { total: number; sizeBytes: number; deletePending: number; deleteFailed: number }
  recentRuns: BackupRun[]
  upcomingJobs: BackupJob[]
}
function KpiCard({ label, value, icon: Icon, danger }: { label: string; value: number; icon: ElementType; danger?: boolean }) {
  return <div className="rounded-lg border border-border bg-surface p-4"><div className="flex items-center justify-between"><span className="text-sm text-text-muted">{label}</span><Icon className={`size-4 ${danger ? 'text-error' : 'text-primary'}`} /></div><p className={`mt-2 text-3xl font-semibold ${danger ? 'text-error' : 'text-text-heading'}`}>{value}</p></div>
}
export function DashboardPage() {
  const { tenantId, tenant } = useTenant()
  const query = useQuery({ queryKey: ['tenant', tenantId, 'dashboard'], queryFn: ({ signal }) => apiRequest<Dashboard>(tenantPath(tenantId, 'dashboard'), { signal }), refetchInterval: 15_000, refetchIntervalInBackground: false })
  const data = query.data
  const base = `/tenants/${tenantId}`
  const columns: Column<BackupRun>[] = [
    { key: 'job', header: 'Job / Database', render: run => <Link className="text-primary hover:underline" to={`${base}/backup-runs/${run.id}`}>{run.jobName} / {run.configuration.database}</Link> },
    { key: 'agent', header: 'Agent', render: run => run.agentName ?? '—' },
    { key: 'date', header: 'Başlangıç', render: run => new Date(run.startedAt ?? run.createdAt).toLocaleString('tr-TR') },
    { key: 'status', header: 'Durum', render: run => <StatusPill status={run.status} /> },
    { key: 'attention', header: 'Bilgi', render: run => run.reasonCode ? failureMessage(run.reasonCode) : run.status === 'RetryPending' ? 'Yeni attempt bekleniyor.' : run.status === 'CancellationRequested' ? 'Agent iptali henüz tamamlamadı.' : '—' },
  ]
  return <div className="space-y-6">
    <div><h1 className="text-2xl font-semibold text-text-heading">Dashboard</h1><p className="mt-1 text-sm text-text-muted">{tenant.name} · Gerçek backup ve Agent durumu</p></div>
    <QueryState isPending={query.isPending} error={query.error}>
      {data && <>
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4"><KpiCard label="Agents" value={data.agents.total} icon={Monitor} /><KpiCard label="Scheduled Jobs" value={data.jobs.scheduled} icon={Database} /><KpiCard label="Başarılı · son 24 saat" value={data.runs.succeededLast24Hours} icon={HardDrive} /><KpiCard label="Başarısız · son 24 saat" value={data.runs.failedLast24Hours} icon={AlertCircle} danger={data.runs.failedLast24Hours > 0} /></div>
        <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Agent bağlantı durumu ve yaşam döngüsü</h2><dl className="mt-3 flex flex-wrap gap-5 text-sm">{(Object.entries({ online: 'Çevrimiçi', offline: 'Çevrimdışı', unknown: 'Bağlantı bilinmiyor', disabled: 'Devre dışı (lifecycle)', revoked: 'İptal edilmiş (lifecycle)' }) as [keyof Dashboard['agents'], string][]).map(([key, label]) => <div key={key}><dt className="text-text-muted">{label}</dt><dd className="font-semibold">{data.agents[key]}</dd></div>)}</dl></section>
        {data.artifacts.deleteFailed > 0 && <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-error">{data.artifacts.deleteFailed} artifact silinemedi. <Link className="underline" to={`${base}/artifacts?state=DeleteFailed`}>Silme hatalarını inceleyin.</Link></div>}
        {data.runs.storageFailedLast24Hours > 0 && <div role="alert" className="rounded-lg border border-error/30 bg-error-subtle p-4 text-error">{data.runs.storageFailedLast24Hours} backup sırasında storage yazma veya replica doğrulama hatası oluştu. <Link className="underline" to={`${base}/backup-runs?status=Failed`}>Başarısız backups</Link></div>}
        <section className="flex flex-wrap gap-5 rounded-lg border border-border bg-surface p-4 text-sm"><span>Running: <strong>{data.runs.running}</strong></span><span>RetryPending: <strong>{data.runs.retryPending}</strong></span><span>CancellationRequested: <strong>{data.runs.cancellationRequested}</strong></span></section>
        <div className="grid gap-4 sm:grid-cols-3"><KpiCard label="Aktif Runs" value={data.runs.active} icon={PlayCircle} /><KpiCard label="Etkin Jobs" value={data.jobs.enabled} icon={Database} /><KpiCard label="Silme bekleyen artifacts" value={data.artifacts.deletePending} icon={HardDrive} /></div>
        <section className="space-y-3"><div className="flex items-center justify-between"><h2 className="text-lg font-medium text-text-heading">Son backups</h2><Link className="text-sm text-primary" to={`${base}/backup-runs`}>Tüm history</Link></div><DataTable rows={data.recentRuns} columns={columns} keyExtractor={run => run.id} emptyMessage="Bu tenant için henüz backup çalıştırılmadı." /></section>
        <section className="rounded-lg border border-border bg-surface p-4"><h2 className="font-medium text-text-heading">Sonraki scheduled backups</h2>{data.upcomingJobs.length ? <ul className="mt-3 divide-y divide-border">{data.upcomingJobs.map(job => <li key={job.id} className="flex flex-wrap justify-between gap-2 py-2"><Link className="text-primary" to={`${base}/backup-jobs/${job.id}/edit`}>{job.name}</Link><span className="text-sm text-text-muted">{job.schedule?.nextOccurrenceAt ? new Date(job.schedule.nextOccurrenceAt).toLocaleString('tr-TR') : '—'} · {job.schedule?.timeZone}</span></li>)}</ul> : <p className="mt-3 text-sm text-text-muted">Etkin schedule yok.</p>}</section>
      </>}
    </QueryState>
  </div>
}
