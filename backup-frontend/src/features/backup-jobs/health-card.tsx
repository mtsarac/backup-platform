import { StatusPill } from '@/components/data/status-pill'
import { Section } from '@/components/ui/section'
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
        <p className="text-3xl font-semibold tabular-nums text-text-heading">{health.score} / 100</p>
        <dl className="grid gap-4 sm:grid-cols-2">
          {(Object.keys(labels) as (keyof typeof labels)[]).map(key => {
            const factor = health.factors[key]
            return <div key={key} className="rounded-lg border border-border p-3">
              <dt className="text-sm font-medium text-text-heading">{tx(labels[key])}</dt>
              <dd className="mt-1 space-y-1 text-sm"><p className="tabular-nums">{factor.score} / {factor.maxScore}</p>
                <p className="text-text-muted">{tx(reasons[factor.reason] ?? 'Bu etken için açıklama bulunamadı.')}</p>
              </dd>
            </div>
          })}
        </dl>
        <p className="text-xs text-text-muted">{tx('Değerlendirme zamanı:')}{' '}{formatDateTime(health.evaluatedAt)}</p>
      </div>}
  </Section>
}
