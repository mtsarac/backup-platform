import { tx } from '@/lib/i18n'
import type { BackupJob } from './types'
import type { BackupJobHealth } from './health'
import { canRetryRun } from '@/features/backups/presentation'

export type JobStatus = 'revoked' | 'attention' | 'running' | 'first' | 'manual' | 'paused' | 'current'

export interface JobStatusInput {
  job: Pick<BackupJob, 'enabled' | 'schedule' | 'agentRevoked' | 'lastRun'>
  health: BackupJobHealth
  /** A failed attempt in the last 14 days (strip day `Failed`). */
  failedRecently: boolean
  hasSucceeded: boolean
  offline: boolean
  running: boolean
}

// Freshness and schedule penalties mean nothing for manual and paused jobs; the other reasons still do.
const scheduleReasons = ['backup_missing', 'backup_stale', 'schedule_unavailable', 'capacity_warning', 'capacity_unavailable']

/** The row status of the spec's table, first match wins; shared by the job panel and the home page. */
export function jobStatus({ job, health, failedRecently, hasSucceeded, offline, running }: JobStatusInput): JobStatus {
  if (job.agentRevoked) return 'revoked'
  const first = !hasSucceeded && !failedRecently
  const paused = !job.enabled || job.schedule?.enabled === false
  const manual = !job.schedule
  const lostPoints = Object.values(health.factors).filter(factor => factor.score < factor.maxScore).map(factor => factor.reason)
  const problem = health.state !== 'Healthy' && (paused || manual ? lostPoints.some(reason => !scheduleReasons.includes(reason)) : true)
  // A manual job has no schedule to go stale, so its newest failed run is the only signal that the last backup was not taken.
  const lastFailed = manual && job.lastRun != null && canRetryRun(job.lastRun.status)
  if (offline || (!first && (problem || lastFailed))) return 'attention'
  if (running) return 'running'
  if (first) return 'first'
  if (paused) return 'paused'
  if (manual && job.lastRun?.status !== 'Succeeded') return 'manual'
  return 'current'
}

export const jobStatusLevel = { revoked: 'neutral', attention: 'urgent', running: 'running', first: 'neutral', manual: 'neutral', paused: 'neutral', current: 'safe' } as const

export function jobStatusLabel(status: JobStatus, percent?: number | null): string {
  switch (status) {
    case 'revoked': return tx('Bilgisayar iptal edildi')
    case 'attention': return tx('Dikkat gerekiyor')
    case 'running': return percent == null ? tx('Çalışıyor') : tx('Çalışıyor %{0}', { '0': Math.round(percent) })
    case 'first': return tx('İlk yedek bekleniyor')
    case 'manual': return tx('Elle çalışır')
    case 'paused': return tx('Durduruldu')
    case 'current': return tx('Güncel')
  }
}
