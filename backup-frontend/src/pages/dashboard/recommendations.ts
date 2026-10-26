import { tx } from '@/lib/i18n'
import type { BackupHealthFactor, BackupJobHealth } from '@/features/backup-jobs/health'

export const MAX_RECOMMENDATIONS = 3

/** The one place that turns a backend health reason code into everyday Turkish. Healthy codes are absent on purpose. */
const messages: Record<string, () => string> = {
  schedule_unavailable: () => tx("Yedekleme zamanlaması yok"),
  backup_missing: () => tx("Henüz başarılı yedek alınmadı"),
  backup_stale: () => tx("Yedek zamanında alınmadı"),
  no_verified_replica: () => tx("Yedeğin doğrulanmış kopyası yok"),
  replicas_missing: () => tx("Yedeğin yeterince kopyası yok"),
  restore_failed: () => tx("Geri yükleme denemesi başarısız oldu"),
  restore_untested: () => tx("Yedek henüz geri yükleme ile denenmedi"),
  capacity_unavailable: () => tx("Depolama alanı ölçülemiyor"),
  capacity_critical: () => tx("Depolama alanı dolmak üzere"),
  capacity_warning: () => tx("Depolama alanı doluyor"),
}

/** Lowest score share wins; a tie goes to the factor that weighs more in the score. */
function weakestFactor(job: BackupJobHealth): BackupHealthFactor {
  return Object.values(job.factors).reduce((weakest, factor) => {
    const gap = factor.score / factor.maxScore - weakest.score / weakest.maxScore
    return gap < 0 || (gap === 0 && factor.maxScore > weakest.maxScore) ? factor : weakest
  })
}

export interface Recommendation { jobId: string; jobName: string; message: string }

/** Up to three, most urgent job first, one per unhealthy job. */
export function recommendations(jobs: readonly BackupJobHealth[]): Recommendation[] {
  return jobs
    .filter(job => job.state !== 'Healthy')
    .toSorted((a, b) => a.score - b.score)
    .slice(0, MAX_RECOMMENDATIONS)
    .map(job => ({ jobId: job.jobId, jobName: job.jobName, message: (messages[weakestFactor(job).reason] ?? (() => tx("Dikkat gerekiyor")))() }))
}
