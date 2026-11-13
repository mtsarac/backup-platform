import { StatusPill } from '@/components/data/status-pill'
import { Section, Facts } from '@/components/ui/section'
import { tx, useLanguage } from '@/lib/i18n'
import { useBackupHealth } from './health'
import { formatDateTime } from '@/lib/format'

const reasons: Record<string, string> = {
  schedule_unavailable: 'Zamanlama olmadığı için yedek güncelliği değerlendirilemiyor.',
  backup_missing: 'Başarılı yedek bulunamadı.',
  backup_fresh: 'Son başarılı yedek, planlanan periyot içinde.',
  backup_stale: 'Son başarılı yedek, planlanan periyodu aştı.',
  replicas_verified: 'Gerekli doğrulanmış kopyalar mevcut.',
  no_verified_replica: 'Kullanılabilir doğrulanmış kopya yok.',
  replicas_missing: 'Doğrulanmış kopya sayısı, gerekli kopya sayısından az.',
  restore_failed: 'Son izole geri yükleme testi başarısız oldu.',
  restore_passed: 'Son 30 gün içinde izole geri yükleme testi başarılı.',
  restore_untested: 'Son 30 gün içinde başarılı izole geri yükleme testi yok.',
  capacity_unavailable: 'Güncel ve ölçülebilir depolama kapasitesi bilgisi yok.',
  capacity_critical: 'En dolu depolama hedefi %90 veya üzerinde.',
  capacity_warning: 'En dolu depolama hedefi %80 veya üzerinde, %90 altında.',
  capacity_available: 'Depolama hedefleri %80 altında.',
}
const labels = { freshness: 'Yedek güncelliği', replication: 'Doğrulanmış kopyalar', restoreVerification: 'İzole geri yükleme testi', storageCapacity: 'Depolama kapasitesi' } as const

export function BackupHealthCard({ jobId }: { jobId: string }) {
  useLanguage()
  const query = useBackupHealth(jobId)
  const health = query.data
  return <Section title={tx('Yedekleme sağlığı')} description={tx('Sağlık durumu yalnızca bilgilendirir; yedeklemeleri engellemez.')}
    actions={health && !query.error ? <StatusPill domain="backupHealth" status={health.state} /> : undefined}>
    {query.isPending ? <p role="status" className="text-sm text-text-muted">{tx('Sağlık bilgisi yükleniyor…')}</p>
      : query.error || !health ? <p role="alert" className="text-sm text-error">{tx('Sağlık bilgisi alınamadı.')}</p>
      : <div className="space-y-4">
        <p className="text-2xl font-semibold tabular-nums text-text-heading">{health.score} / 100</p>
        <Facts>
          {(Object.keys(labels) as (keyof typeof labels)[]).map(key => {
            const factor = health.factors[key]
            return <div key={key} className="min-w-0 border-l-2 border-border pl-3"
              onMouseEnter={event => { const details = event.currentTarget.querySelector('details'); if (details) details.open = true }}
              onMouseLeave={event => { const details = event.currentTarget.querySelector('details'); if (details && !details.contains(document.activeElement)) details.open = false }}>
              <dt className="text-sm font-medium text-text-heading">{tx(labels[key])}</dt>
              <dd className="mt-1 text-sm">
                <details className="relative"
                  onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) event.currentTarget.open = false }}
                  onKeyDown={event => { if (event.key === 'Escape') event.currentTarget.open = false }}>
                  <summary onFocus={event => { if (event.currentTarget.matches(':focus-visible')) event.currentTarget.closest('details')!.open = true }}
                    onClick={event => { if (event.detail > 0) { event.preventDefault(); event.currentTarget.closest('details')!.open = true } }}
                    className="w-fit cursor-pointer list-none rounded-sm tabular-nums underline decoration-dotted underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent [&::-webkit-details-marker]:hidden">
                    {factor.score} / {factor.maxScore}
                  </summary>
                  <p className="absolute left-0 top-full z-20 mt-1 w-64 max-w-[min(16rem,calc(100vw-3rem))] rounded-lg border border-border bg-surface p-3 text-sm text-text shadow-overlay">
                    {tx(reasons[factor.reason] ?? 'Bu etken için açıklama bulunamadı.')}
                  </p>
                </details>
              </dd>
            </div>
          })}
        </Facts>
        <p className="text-xs text-text-muted">{tx('Değerlendirme zamanı:')}{' '}{formatDateTime(health.evaluatedAt)}</p>
      </div>}
  </Section>
}
