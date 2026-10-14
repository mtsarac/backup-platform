import type { BackupConfiguration, RetryPolicy, Schedule } from '@/features/backup-jobs/types'

export const jobSteps = ['Genel', 'Kaynak', 'Depolama', 'Zamanlama', 'Saklama & seçenekler', 'Özet'] as const
export const summaryStep = jobSteps.length - 1
export const schedulePresets = [['Her gece 02:00', '0 2 * * *'], ['Her sabah 08:00', '0 8 * * *'], ['Günde 2 kez (08:00 ve 20:00)', '0 8,20 * * *']] as const

/** Human label for a schedule: a preset name when the cron matches one, the raw cron otherwise. */
export function scheduleLabel(schedule: Schedule | null | undefined) {
  if (!schedule) return 'Manuel'
  return schedulePresets.find(([, cron]) => cron === schedule.cronExpression)?.[0] ?? schedule.cronExpression
}

// Field-name prefixes owned by each step; server field errors are routed back to the step that shows them.
const owners: readonly (readonly string[])[] = [
  ['name', 'agentId', 'enabled'],
  ['configuration.engine', 'configuration.host', 'configuration.database', 'configuration.databaseCredentialReference', 'configuration.port', 'configuration.integratedSecurity', 'configuration.encrypt', 'configuration.trustServerCertificate'],
  ['configuration.destinations', 'configuration.requiredVerifiedReplicaCount', 'configuration.storagePrefix', 'configuration.stagingReference'],
  ['cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy', 'retryPolicy.'],
  ['retention.', 'configuration.copyOnly', 'configuration.checksumPolicy', 'configuration.compressionPolicy'],
]
export function stepOfField(field: string): number | undefined {
  const name = field.replace(/^schedule\./, '')
  const step = owners.findIndex(prefixes => prefixes.some(prefix => name.startsWith(prefix) || name === prefix.split('.').at(-1)))
  return step < 0 ? undefined : step
}

export interface StepValues { name: string; agentId: string; configuration: BackupConfiguration; scheduled: boolean; schedule: Schedule; retry: RetryPolicy }
/** Required fields and numeric sanity only; cron syntax and policy limits remain server-authoritative. */
export function validateStep(step: number, values: StepValues): Record<string, string> {
  const errors: Record<string, string> = {}
  if (step === 0) {
    if (!values.name.trim()) errors.name = 'İş adını girin.'
    if (!values.agentId) errors.agentId = 'Bir Agent seçin.'
  } else if (step === 1) {
    const { host, database, port } = values.configuration
    if (!host.trim()) errors['configuration.host'] = 'SQL Server host veya instance adını girin.'
    if (!database.trim()) errors['configuration.database'] = 'Database adını girin.'
    if (port !== null && !(Number.isInteger(port) && port >= 1 && port <= 65535)) errors['configuration.port'] = 'Port 1 ile 65535 arasında bir tam sayı olmalı.'
  } else if (step === 3) {
    if (values.scheduled && !values.schedule.cronExpression.trim()) errors.cronExpression = 'Cron ifadesini girin veya hazır bir zamanlama seçin.'
    if (!Number.isInteger(values.retry.maxAttempts) || values.retry.maxAttempts < 1) errors['retryPolicy.maxAttempts'] = 'Toplam deneme sayısı en az 1 olan bir tam sayı olmalı.'
    if (values.scheduled && (!Number.isFinite(values.schedule.missedRunGraceSeconds) || values.schedule.missedRunGraceSeconds < 0)) errors.missedRunGraceSeconds = 'Tolerans sıfır veya daha büyük olmalı.'
    if (!Number.isFinite(values.retry.initialDelaySeconds) || values.retry.initialDelaySeconds < 0) errors['retryPolicy.initialDelaySeconds'] = 'Gecikme sıfır veya daha büyük olmalı.'
    if (!Number.isFinite(values.retry.maxDelaySeconds) || values.retry.maxDelaySeconds < values.retry.initialDelaySeconds) errors['retryPolicy.maxDelaySeconds'] = 'En uzun gecikme ilk gecikmeden küçük olamaz.'
    if (!Number.isFinite(values.retry.jitter) || values.retry.jitter < 0 || values.retry.jitter > 1) errors['retryPolicy.jitter'] = 'Rastgele gecikme oranı 0 ile 1 arasında olmalı.'
  }
  return errors
}
