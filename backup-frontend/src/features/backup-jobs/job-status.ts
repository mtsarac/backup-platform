import { tx } from '@/lib/i18n'
import type { BackupJob } from './types'
import type { BackupJobHealth } from './health'

export type JobStatus = 'attention' | 'running' | 'first' | 'manual' | 'paused' | 'current'

export interface JobStatusInput {
  job: Pick<BackupJob, 'enabled' | 'schedule'>
  health: BackupJobHealth
  /** A failed attempt in the last 14 days (strip day `Failed`). */
  failedRecently: boolean
  hasSucceeded: boolean
  offline: boolean
  running: boolean
}

// Freshness and schedule penalties mean nothing for manual and paused jobs; the other reasons still do.
const scheduleReasons = ['backup_missing', 'backup_stale', 'schedule_unavailable', 'restore_untested', 'capacity_warning', 'capacity_unavailable']

/** The row status of the spec's table, first match wins; shared by the job panel and the home page. */
export function jobStatus({ job, health, failedRecently, hasSucceeded, offline, running }: JobStatusInput): JobStatus {
  const first = !hasSucceeded && !failedRecently
  const paused = !job.enabled || job.schedule?.enabled === false
  const manual = !job.schedule
  const lostPoints = Object.values(health.factors).filter(factor => factor.score < factor.maxScore).map(factor => factor.reason)
  const problem = health.state !== 'Healthy' && (paused || manual ? lostPoints.some(reason => !scheduleReasons.includes(reason)) : true)
  if (offline || (!first && problem)) return 'attention'
  if (running) return 'running'
  if (first) return 'first'
  if (paused) return 'paused'
  if (manual) return 'manual'
  return 'current'
}

export const jobStatusLevel = { attention: 'urgent', running: 'running', first: 'neutral', manual: 'neutral', paused: 'neutral', current: 'safe' } as const

export function jobStatusLabel(status: JobStatus, percent?: number | null): string {
  switch (status) {
    case 'attention': return tx('Dikkat gerekiyor')
    case 'running': return percent == null ? tx('Çalışıyor') : tx('Çalışıyor %{0}', { '0': Math.round(percent) })
    case 'first': return tx('İlk yedek bekleniyor')
    case 'manual': return tx('Elle çalışır')
    case 'paused': return tx('Durduruldu')
    case 'current': return tx('Güncel')
  }
}
