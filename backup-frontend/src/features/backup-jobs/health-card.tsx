import { StatusPill } from '@/components/data/status-pill'
import { Disclosure } from '@/components/ui/disclosure'
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
  return <section aria-label={tx('Yedekleme sağlığı')} className="space-y-2">
    <div className="flex flex-wrap items-center gap-2">
      <h2 className="text-base font-semibold text-text-heading">{tx('Yedekleme sağlığı')}</h2>
      {health && !query.error && <><StatusPill domain="backupHealth" status={health.state} /><span className="text-sm tabular-nums">{health.score} / 100</span></>}
    </div>
    {query.isPending ? <p role="status" className="text-sm text-text-muted">{tx('Sağlık bilgisi yükleniyor…')}</p>
      : query.error || !health ? <p role="alert" className="text-sm text-error">{tx('Sağlık bilgisi alınamadı.')}</p>
      : <>
        <ul className="space-y-1 text-sm">
          {(Object.keys(labels) as (keyof typeof labels)[]).filter(key => health.factors[key].score < health.factors[key].maxScore).map(key =>
            <li key={key}>{tx(labels[key])}: {tx(reasons[health.factors[key].reason] ?? 'Bu etken için açıklama bulunamadı.')}</li>)}
        </ul>
        <Disclosure title={tx('Ayrıntılar')}>
          <p className="text-sm text-text-muted">{tx('Sağlık durumu yalnızca bilgilendirir; yedeklemeleri engellemez.')}</p>
          <dl className="mt-2 space-y-2 text-sm">
            {(Object.keys(labels) as (keyof typeof labels)[]).map(key => {
              const factor = health.factors[key]
              return <div key={key}><dt className="font-medium">{tx(labels[key])}: {factor.score} / {factor.maxScore}</dt><dd>{tx(reasons[factor.reason] ?? 'Bu etken için açıklama bulunamadı.')}</dd></div>
            })}
          </dl>
          <p className="mt-2 text-xs text-text-muted">{tx('Değerlendirme zamanı:')}{' '}{formatDateTime(health.evaluatedAt)}</p>
        </Disclosure>
      </>}
  </section>
}
