import { currentLocale, tx } from '@/lib/i18n'
import type { BackupJob } from '@/features/backup-jobs/types'
import type { BackupJobHealth } from '@/features/backup-jobs/health'
import { jobStatus, type JobStatus } from '@/features/backup-jobs/job-status'
import { canRetryRun, isActiveRun } from '@/features/backups/presentation'

export interface StripDay { date: string; status: string; attemptCount: number }

/** Why a row needs the owner, most urgent first; the band names one of these. */
export type Topic = 'stale' | 'firstFailed' | 'offline' | 'capacity' | 'replica' | 'restoreFailed' | 'lastFailed'
export type BandAction = 'runNow' | 'checkComputer' | 'manageStorage' | 'retryRestore' | null

export interface RowInput {
  job: BackupJob
  health?: BackupJobHealth
  days: StripDay[]
  hasSucceeded: boolean
  /** The job's newest successful attempt, for the row's "Geri yükle". */
  lastSuccessId?: string
  /** When the newest successful attempt finished. */
  lastSuccessAt?: string | null
  offline: boolean
  percent?: number | null
}

export interface ProtectionRow {
  job: BackupJob
  days: StripDay[]
  /** What the row shows. */
  status: JobStatus
  /** The status without the running overlay; the counts and the badge use it. */
  baseStatus: JobStatus
  percent: number | null
  score: number
  hasSucceeded: boolean
  lastSuccessId?: string
  lastSuccessAt?: string | null
  offline: boolean
  topic: Topic | null
  note: string | null
}

const noHealth = { jobId: '', jobName: '', score: 100, state: 'Healthy', evaluatedAt: '', factors: {} } as unknown as BackupJobHealth

const notes: Record<string, () => string> = {
  capacity_warning: () => tx('Depolama alanı doluyor.'),
  capacity_unavailable: () => tx('Depolama alanı ölçülemiyor.'),
}

export function buildRow({ job, health = noHealth, days, hasSucceeded, lastSuccessId, lastSuccessAt, offline, percent }: RowInput): ProtectionRow {
  const failedRecently = days.some(day => day.status === 'Failed')
  const running = Boolean(job.lastRun && isActiveRun(job.lastRun.status))
  const input = { job, health, failedRecently, hasSucceeded, offline }
  const baseStatus = jobStatus({ ...input, running: false })
  const status = running && baseStatus !== 'attention' ? jobStatus({ ...input, running }) : baseStatus
  const reasons = Object.values(health.factors).map(factor => factor.reason)
  const scheduled = Boolean(job.schedule) && job.enabled && job.schedule?.enabled !== false
  const firstFailed = !hasSucceeded && failedRecently
  const topic: Topic | null = baseStatus !== 'attention' ? null
    : firstFailed ? 'firstFailed'
    : !scheduled && job.lastRun != null && canRetryRun(job.lastRun.status) ? 'lastFailed'
    : scheduled && (reasons.includes('backup_stale') || reasons.includes('backup_missing')) ? 'stale'
    : offline ? 'offline'
    : reasons.includes('capacity_critical') ? 'capacity'
    : reasons.includes('no_verified_replica') || reasons.includes('replicas_missing') ? 'replica'
    : reasons.includes('restore_failed') ? 'restoreFailed' : null
  const noteReason = ['attention', 'current', 'running'].includes(status) ? reasons.find(reason => reason in notes) : undefined
  return { job, days, status, baseStatus, percent: running ? percent ?? null : null, score: health.score, hasSucceeded, lastSuccessId, lastSuccessAt, offline, topic, note: noteReason ? notes[noteReason]() : null }
}

export function countRows(rows: readonly ProtectionRow[]) {
  const first = rows.filter(row => !row.hasSucceeded).length
  return { total: rows.length - first, first, current: rows.filter(row => row.baseStatus === 'current' && row.hasSucceeded).length, attention: rows.filter(row => row.baseStatus === 'attention').length }
}

export type Badge = 'attention' | 'none' | 'first' | 'current'
export function headerBadge(rows: readonly ProtectionRow[]): Badge {
  if (rows.some(row => row.baseStatus === 'attention')) return 'attention'
  if (rows.length === 0) return 'none'
  return rows.some(row => !row.hasSucceeded) ? 'first' : 'current'
}

// A failed first attempt ranks with a missed backup: both are "the backup was not taken".
const rank: Record<Topic, number> = { stale: 0, firstFailed: 0, offline: 1, capacity: 2, replica: 3, restoreFailed: 4, lastFailed: 0 }


export type Band =
  | { kind: 'attention'; row: ProtectionRow; text: string; action: BandAction; more: number }
  | { kind: 'running'; row: ProtectionRow; text: string; more: number }

function bandText(row: ProtectionRow, topic: Topic): string {
  const name = row.job.name
  switch (topic) {
    case 'firstFailed': return tx('{0}: ilk yedek alınamadı', { '0': name })
    case 'lastFailed': return tx('{0}: son yedek alınamadı', { '0': name })
    case 'stale': return tx('{0}: yedek zamanında alınmadı', { '0': name })
    case 'offline': return tx('{0}: yedekleme bilgisayarı çevrimdışı', { '0': name })
    case 'capacity': return tx('{0}: depolama alanı dolmak üzere', { '0': name })
    case 'replica': return tx('{0}: yedeğin doğrulanmış kopyası yok', { '0': name })
    case 'restoreFailed': return tx('{0}: geri yükleme denemesi başarısız oldu', { '0': name })
  }
}

function bandAction(row: ProtectionRow, topic: Topic): BandAction {
  switch (topic) {
    case 'firstFailed': case 'stale': case 'lastFailed': return row.offline ? 'checkComputer' : 'runNow'
    case 'offline': return 'checkComputer'
    case 'capacity': return 'manageStorage'
    case 'replica': return null
    case 'restoreFailed': return row.lastSuccessId ? 'retryRestore' : null
  }
}

const withMore = (text: string, more: number) => more > 0 ? tx('{0} ve {1} veri daha', { '0': text, '1': more }) : text

/** The one most urgent topic of the priority table; without any attention, a running backup gets a neutral band. */
export function pickBand(rows: readonly ProtectionRow[]): Band | null {
  const topical = rows.filter((row): row is ProtectionRow & { topic: Topic } => row.topic !== null)
    .toSorted((a, b) => rank[a.topic] - rank[b.topic] || a.score - b.score)
  const [first] = topical
  if (first) return { kind: 'attention', row: first, text: withMore(bandText(first, first.topic), topical.length - 1), action: bandAction(first, first.topic), more: topical.length - 1 }
  if (rows.some(row => row.baseStatus === 'attention')) return null
  const running = rows.filter(row => row.status === 'running')
  const [one] = running
  if (!one) return null
  const text = one.percent == null ? tx('{0}: yedekleme çalışıyor', { '0': one.job.name }) : tx('{0}: yedekleme çalışıyor %{1}', { '0': one.job.name, '1': Math.round(one.percent) })
  return { kind: 'running', row: one, text: withMore(text, running.length - 1), more: running.length - 1 }
}

/** Newest "alındı" day of the strip, or null. Strip dates are `yyyy-MM-dd` in the requested time zone. */
export function lastSucceededDay(days: readonly StripDay[]): string | null {
  return days.filter(day => day.status === 'Succeeded').map(day => day.date).sort().at(-1) ?? null
}

/** A calendar day, not an instant: parsed as local parts so no time zone moves it. */
export function formatDay(date: string): string {
  const [year, month, day] = date.split('-').map(Number)
  return new Intl.DateTimeFormat(currentLocale(), { dateStyle: 'medium' }).format(new Date(year, month - 1, day))
}

export function dayWord(status: string): string {
  switch (status) {
    case 'Succeeded': return tx('alındı')
    case 'Failed': return tx('alınamadı')
    case 'Running': return tx('çalışıyor')
    case 'NoJob': return tx('iş yoktu')
    case 'NoBackup': return tx('yedek yoktu')
    default: return tx('bilgi yok')
  }
}

/** The text equivalent of one strip cell: day, word and, where attempts were made, their count. */
export function dayLabel(day: StripDay): string {
  const base = `${formatDay(day.date)}: ${dayWord(day.status)}`
  return day.attemptCount > 0 ? tx('{0}, {1} deneme', { '0': base, '1': day.attemptCount }) : base
}
