import { tx } from '@/lib/i18n'
import type { BackupConfiguration, RetryPolicy, Schedule } from '@/features/backup-jobs/types'
import { loginErrors } from '@/features/backup-jobs/database-login-validation'
import { validFilePaths } from './file-path-list'
import { formatSeconds } from '@/lib/format'

/** One question per step; the last one is the summary. */
export const jobSteps = ['İş adı', 'Bilgisayar', 'Kaynak', 'Depolama', 'Zamanlama', 'Saklama', 'Şifreleme', 'Özet'] as const
export const nameStep = 0
export const agentStep = 1
export const sourceStep = 2
export const storageStep = 3
export const scheduleStep = 4
export const retentionStep = 5
export const protectionStep = 6
export const summaryStep = jobSteps.length - 1
export const schedulePresets = [['Her gün 02:00', '0 2 * * *'], ['Her gün 08:00', '0 8 * * *'], ['Günde 2 kez (08:00 ve 20:00)', '0 8,20 * * *']] as const
const day = 86400
export const retentionPresets = [7, 14, 30, 90].map(days => [days * day, days] as const)

/** Preset name for a cron expression; undefined when it is a custom one. */
export function schedulePresetLabel(cronExpression: string) {
  const label = schedulePresets.find(([, cron]) => cron === cronExpression)?.[0]
  return label && tx(label)
}

/** Human label for a schedule: a preset name when the cron matches one, the raw cron otherwise. */
export function scheduleLabel(schedule: Schedule | null | undefined) {
  if (!schedule) return tx('Manuel')
  return schedulePresetLabel(schedule.cronExpression) ?? schedule.cronExpression
}

/** Whole days when the period is one, otherwise hours/minutes/seconds. */
export function keepForLabel(seconds: number) {
  return seconds > 0 && seconds % day === 0 ? tx('{0} gün', { '0': seconds / day }) : formatSeconds(seconds)
}

// Field-name prefixes owned by each step; server field errors are routed back to the step that shows them.
const owners: readonly (readonly string[])[] = [
  ['name', 'enabled'],
  ['agentId'],
  ['configuration.engine', 'configuration.host', 'configuration.database', 'configuration.files', 'configuration.databaseCredentialReference', 'configuration.databaseLogin.userName', 'configuration.databaseLogin.password', 'configuration.port', 'configuration.integratedSecurity', 'configuration.encrypt', 'configuration.trustServerCertificate'],
  ['configuration.destinations', 'configuration.requiredVerifiedReplicaCount', 'configuration.enforceThreeTwoOne', 'configuration.sourceSite', 'configuration.storagePrefix', 'configuration.stagingReference'],
  ['cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy', 'retryPolicy.'],
  ['retention.'],
  ['configuration.copyOnly', 'configuration.checksumPolicy', 'configuration.compressionPolicy', 'configuration.encryptionPolicy'],
]
/** A prefix owns a field when it is the field, a parent path of it, or a trailing-dot group; never a mere string prefix ("configuration.encrypt" must not claim "configuration.encryptionPolicy"). */
function owns(prefix: string, name: string) {
  return name === prefix || name === prefix.split('.').at(-1) || name.startsWith(`${prefix}.`) || name.startsWith(`${prefix}[`) || (prefix.endsWith('.') && name.startsWith(prefix))
}
export function stepOfField(field: string): number | undefined {
  const name = field.replace(/^schedule\./, '')
  const step = owners.findIndex(prefixes => prefixes.some(prefix => owns(prefix, name)))
  return step < 0 ? undefined : step
}

export interface StepValues { name: string; agentId: string; configuration: BackupConfiguration; scheduled: boolean; schedule: Schedule; retry: RetryPolicy }
/** Required fields and numeric sanity only; cron syntax and policy limits remain server-authoritative. */
export function validateStep(step: number, values: StepValues): Record<string, string> {
  const errors: Record<string, string> = {}
  if (step === nameStep) {
    if (!values.name.trim()) errors.name = tx('İş adını girin.')
  } else if (step === agentStep) {
    if (!values.agentId) errors.agentId = tx('Bir yedekleme bilgisayarı seçin.')
  } else if (step === sourceStep) {
    const { host, database, port } = values.configuration
    if (values.configuration.engine === 'file-system') {
      if (!/^[A-Za-z0-9_.-]{1,128}$/.test(database)) errors['configuration.database'] = tx('Geçerli bir dosya kaynağı referansı girin.')
      const files = values.configuration.files
      if (!files || !validFilePaths(files.paths)) errors['configuration.files.paths'] = tx('1–256 geçerli göreli yol girin.')
      if (files && !validFilePaths(files.includes, false, false)) errors['configuration.files.includes'] = tx('En fazla 256 geçerli göreli filtre girin.')
      if (files && !validFilePaths(files.excludes, false, false)) errors['configuration.files.excludes'] = tx('En fazla 256 geçerli göreli filtre girin.')
      return errors
    }
    if (!host.trim()) errors['configuration.host'] = tx('Database sunucusunun host adını girin.')
    if (!database.trim()) errors['configuration.database'] = tx('Database adını girin.')
    if (values.configuration.engine === 'postgres') {
      if (new TextEncoder().encode(database).length > 63) errors['configuration.database'] = tx('PostgreSQL Database adı en fazla 63 UTF-8 byte olabilir.')
    }
    if (!values.configuration.integratedSecurity) {
      const login = loginErrors(values.configuration.databaseLogin, Boolean(values.configuration.databaseCredentialReference))
      if (login?.userName) errors['configuration.databaseLogin.userName'] = login.userName
      if (login?.password) errors['configuration.databaseLogin.password'] = login.password
    }
    if (values.configuration.engine === 'mssql' && host.includes('\\') && port !== null) errors['configuration.port'] = tx('Instance adı içeren host ile port birlikte verilemez; birini seçin.')
    if (port !== null && !(Number.isInteger(port) && port >= 1 && port <= 65535)) errors['configuration.port'] = tx('Port 1 ile 65535 arasında bir tam sayı olmalı.')
  } else if (step === scheduleStep) {
    if (values.scheduled && !values.schedule.cronExpression.trim()) errors.cronExpression = tx('Cron ifadesini girin veya hazır bir zamanlama seçin.')
    if (!Number.isInteger(values.retry.maxAttempts) || values.retry.maxAttempts < 1) errors['retryPolicy.maxAttempts'] = tx('Toplam deneme sayısı en az 1 olan bir tam sayı olmalı.')
    if (values.scheduled && (!Number.isFinite(values.schedule.missedRunGraceSeconds) || values.schedule.missedRunGraceSeconds < 0)) errors.missedRunGraceSeconds = tx('Tolerans sıfır veya daha büyük olmalı.')
    if (!Number.isFinite(values.retry.initialDelaySeconds) || values.retry.initialDelaySeconds < 0) errors['retryPolicy.initialDelaySeconds'] = tx('Gecikme sıfır veya daha büyük olmalı.')
    if (!Number.isFinite(values.retry.maxDelaySeconds) || values.retry.maxDelaySeconds < values.retry.initialDelaySeconds) errors['retryPolicy.maxDelaySeconds'] = tx('En uzun gecikme ilk gecikmeden küçük olamaz.')
    if (!Number.isFinite(values.retry.jitter) || values.retry.jitter < 0 || values.retry.jitter > 1) errors['retryPolicy.jitter'] = tx('Rastgele gecikme oranı 0 ile 1 arasında olmalı.')
  }
  return errors
}
