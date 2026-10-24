import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Section } from '@/components/ui/section'
import { QueryState } from '@/components/data/query-state'
import { apiRequest, tenantPath } from '@/lib/api/client'
import { currentLocale, tx, useLanguage } from '@/lib/i18n'
import type { BackupHealthState, BackupJobHealth } from '@/features/backup-jobs/health'
import { formatBytes, formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

interface HealthSummary {
  totalJobs: number
  averageScore: number | null
  state: BackupHealthState | null
  healthy: number
  degraded: number
  critical: number
  jobs: BackupJobHealth[]
  capacityRisks: { connectionId: string; connectionName: string; status: string; totalBytes: number | null; usedBytes: number | null; freeBytes: number | null; reportedAt: string }[]
  recentSizeAnomalies: { runId: string; jobId: string; jobName: string; completedAt: string; sizeBytes: number; baselineMeanBytes: number; deviationRatio: number }[]
  recentSizeAnomaliesTotal: number
}

export function ObservabilityPanel({ tenantId }: { tenantId: string }) {
  useLanguage()
  const query = useQuery({ queryKey: ['tenant', tenantId, 'observability'], queryFn: ({ signal }) => apiRequest<HealthSummary>(tenantPath(tenantId, 'observability/health-summary'), { signal }), refetchInterval: 15_000, refetchIntervalInBackground: false })
  const data = query.data
  const base = `/tenants/${tenantId}`
  return <Section title={tx('Gözlemlenebilirlik & Güvenilirlik')}>
    <QueryState isPending={query.isPending} error={query.error}>
      {data && <div className="space-y-5">
        <dl className="grid grid-cols-2 gap-4 lg:grid-cols-4">
          <div><dt className="text-sm text-text-muted">{tx('Ortalama sağlık puanı')}</dt><dd className="text-2xl font-semibold tabular-nums">{data.averageScore === null ? '—' : data.averageScore.toLocaleString(currentLocale(), { maximumFractionDigits: 1 })}<span className="text-sm text-text-muted"> / 100</span></dd></div>
          <div><dt className="text-sm text-text-muted">{tx('Sağlıklı işler')}</dt><dd className="text-2xl font-semibold tabular-nums text-success">{data.healthy}</dd></div>
          <div><dt className="text-sm text-text-muted">{tx('Zayıflamış işler')}</dt><dd className="text-2xl font-semibold tabular-nums text-warning">{data.degraded}</dd></div>
          <div><dt className="text-sm text-text-muted">{tx('Kritik işler')}</dt><dd className="text-2xl font-semibold tabular-nums text-error">{data.critical}</dd></div>
        </dl>
        {data.totalJobs === 0 && <p className="text-sm text-text-muted">{tx('Sağlık değerlendirmesi için henüz iş yok.')}</p>}
        <div className="grid gap-5 xl:grid-cols-3">
          <div><h3 className="mb-2 font-medium">{tx('İncelenmesi gereken işler')}</h3>
            {data.jobs.some(job => job.state !== 'Healthy') ? <ul className="divide-y divide-border">{data.jobs.filter(job => job.state !== 'Healthy').sort((a, b) => a.score - b.score).map(job => <li key={job.jobId} className="flex flex-wrap items-center justify-between gap-2 py-2">
              <Link className="min-w-0 break-words text-primary underline-offset-4 hover:underline" to={`${base}/backup-jobs/${job.jobId}/edit`}>{job.jobName}</Link>
              <StatusPill level={job.state === 'Critical' ? 'urgent' : 'attention'}>{tx('{0} / 100', { '0': job.score })}</StatusPill>
            </li>)}</ul> : <p className="text-sm text-text-muted">{tx('Düşük sağlık puanlı iş yok.')}</p>}
          </div>
          <div><h3 className="mb-2 font-medium">{tx('Depolama kapasite riskleri')}</h3>
            {data.capacityRisks.length ? <ul className="divide-y divide-border">{data.capacityRisks.map(risk => <li key={risk.connectionId} className="space-y-1 py-2">
              <div className="flex flex-wrap items-center justify-between gap-2"><Link className="min-w-0 break-words text-primary underline-offset-4 hover:underline" to={`${base}/storage/connections/${risk.connectionId}`}>{risk.connectionName}</Link><StatusPill level={risk.status === 'Critical' ? 'urgent' : 'attention'}>{risk.status === 'Critical' ? tx('Kritik kapasite') : tx('Kapasite uyarısı')}</StatusPill></div>
              {risk.freeBytes !== null && <p className="text-sm text-text-muted">{tx('Boş alan: {0}', { '0': formatBytes(risk.freeBytes) })}</p>}
              <p className="text-xs text-text-muted">{tx('Son ölçüm: {0}', { '0': formatDateTime(risk.reportedAt) })}</p>
            </li>)}</ul> : <p className="text-sm text-text-muted">{tx('Kapasite eşiğini aşan hedef yok.')}</p>}
          </div>
          <div><h3 className="mb-2 font-medium">{tx('Son 7 günün boyut anomalileri')}</h3>
            {data.recentSizeAnomalies.length ? <ul className="divide-y divide-border">{data.recentSizeAnomalies.map(anomaly => <li key={anomaly.runId} className="space-y-1 py-2">
              <Link className="break-words text-primary underline-offset-4 hover:underline" to={`${base}/backup-runs/${anomaly.runId}`}>{anomaly.jobName}</Link>{' '}<StatusPill level="attention">{tx('Boyut Anomalisi')}</StatusPill>
              <p className="text-sm text-text-muted">{tx('Yedek: {0} · Geçmiş ortalama: {1}', { '0': formatBytes(anomaly.sizeBytes), '1': formatBytes(anomaly.baselineMeanBytes) })}</p>
              <p className="text-xs text-text-muted">{formatDateTime(anomaly.completedAt)}</p>
            </li>)}</ul> : <p className="text-sm text-text-muted">{tx('Son 7 günde boyut anomalisi yok.')}</p>}
            {data.recentSizeAnomaliesTotal > data.recentSizeAnomalies.length && <p className="mt-2 text-sm text-text-muted">{tx('Toplam {0} anomalinin son {1} kaydı gösteriliyor.', { '0': data.recentSizeAnomaliesTotal, '1': data.recentSizeAnomalies.length })}</p>}
          </div>
        </div>
      </div>}
    </QueryState>
  </Section>
}
