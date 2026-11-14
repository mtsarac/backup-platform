import { tx } from '@/lib/i18n'
import type { BackupConfiguration } from '@/features/backup-jobs/types'
import { loginErrors } from '@/features/backup-jobs/database-login-validation'
import { validFilePaths } from './file-path-validation'
import type { JobValues } from './job-form-values'
import { formatSeconds } from '@/lib/format'

/** Create flow, in order. Schedule and protection share one step; the edit page keeps them as separate sections, without the review step. */
export const jobSteps = ['source', 'storage', 'schedule-protection', 'review'] as const
export type JobStep = (typeof jobSteps)[number]
export type EditSection = 'source' | 'storage' | 'schedule' | 'protection'
export const editSections: readonly EditSection[] = ['source', 'storage', 'schedule', 'protection']
/** The wizard step that shows a section. */
export const wizardStepOf = (section: EditSection): JobStep => section === 'schedule' || section === 'protection' ? 'schedule-protection' : section

export function stepLabel(step: JobStep | EditSection) {
  switch (step) {
    case 'schedule-protection': return tx('Zamanlama ve Koruma')
    case 'source': return tx('Kaynak')
    case 'storage': return tx('Depolama')
    case 'schedule': return tx('Zamanlama')
    case 'protection': return tx('Koruma')
    case 'review': return tx('Özet ve onay')
  }
}

export const schedulePresets = [['Her gün 02:00', '0 2 * * *'], ['Her gün 08:00', '0 8 * * *'], ['Günde 2 kez (08:00 ve 20:00)', '0 8,20 * * *']] as const
const day = 86400
export const retentionPresets = [7, 14, 30, 90].map(days => [days * day, days] as const)

/** Preset name for a cron expression; undefined when it is a custom one. */
export function schedulePresetLabel(cronExpression: string) {
  const label = schedulePresets.find(([, cron]) => cron === cronExpression)?.[0]
  return label && tx(label)
}

/** Whole days when the period is one, otherwise hours/minutes/seconds. */
export function keepForLabel(seconds: number) {
  return seconds > 0 && seconds % day === 0 ? tx('{0} gün', { '0': seconds / day }) : formatSeconds(seconds)
}

// Configuration keys each step owns. Both server field routing and "what changed" are derived from this one table.
const configurationKeys: Record<EditSection, readonly (keyof BackupConfiguration)[]> = {
  source: ['engine', 'host', 'database', 'port', 'integratedSecurity', 'databaseCredentialReference', 'databaseLogin', 'files', 'encrypt', 'trustServerCertificate'],
  storage: ['destinations', 'requiredVerifiedReplicaCount', 'enforceThreeTwoOne', 'sourceSite', 'storagePrefix', 'stagingReference'],
  schedule: [],
  protection: ['copyOnly', 'checksumPolicy', 'compressionPolicy', 'encryptionPolicy'],
}
// Request fields outside `configuration`; `schedule.` prefixes are stripped before matching.
const requestFields: Record<EditSection, readonly string[]> = {
  source: ['name', 'enabled', 'agentId'],
  storage: [],
  schedule: ['cronExpression', 'timeZone', 'missedRunGraceSeconds', 'overlapPolicy', 'retryPolicy', 'retention'],
  protection: [],
}
const owners = Object.fromEntries(editSections.map(step => [step, [...requestFields[step], ...configurationKeys[step].map(key => `configuration.${key}`)]])) as Record<EditSection, string[]>

/** A prefix owns a field when it is the field, a parent path of it, or its bare name; never a mere string prefix ("encrypt" must not claim "encryptionPolicy"). */
function owns(prefix: string, name: string) {
  return name === prefix || name === prefix.split('.').at(-1) || name.startsWith(`${prefix}.`) || name.startsWith(`${prefix}[`)
}
/** The step that shows a server-reported field; undefined when no step does. */
export function stepOfField(field: string): EditSection | undefined {
  const name = field.replace(/^schedule\./, '')
  return editSections.find(step => owners[step].some(prefix => owns(prefix, name)))
}

/** What each step edits, for comparing a form against its saved state. */
function sectionValues(step: EditSection, values: JobValues) {
  const configuration = Object.fromEntries(configurationKeys[step].map(key => [key, values.configuration[key]]))
  switch (step) {
    case 'source': return { name: values.name, agentId: values.agentId, enabled: values.enabled, configuration }
    case 'schedule': return { scheduled: values.scheduled, schedule: values.schedule, overlap: values.overlap, retry: values.retry, retention: values.retention }
    default: return configuration
  }
}
/** The sections whose values differ from the saved ones; the edit page lists them before saving. */
export function changedSections(saved: JobValues, current: JobValues) {
  return editSections.filter(step => JSON.stringify(sectionValues(step, saved)) !== JSON.stringify(sectionValues(step, current)))
}

export type StepErrors = Record<string, string>
/** Required fields and numeric sanity only; cron syntax and policy limits remain server-authoritative. Each step validates only what it shows. */
export function validateStep(step: JobStep | EditSection, values: JobValues): StepErrors {
  if (step === 'schedule-protection') return { ...validateStep('schedule', values), ...validateStep('protection', values) }
  const errors: StepErrors = {}
  if (step === 'source') {
    if (!values.name.trim()) errors.name = tx('İş adını girin.')
    if (values.sourceMode === 'discovery' && !values.selected.length) { errors.source = tx('Otomatik keşiften bir veritabanı seçin veya kaynağı elle yapılandırın.'); return errors }
    if (!values.agentId) errors.agentId = tx('Bir yedekleme bilgisayarı seçin.')
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
  } else if (step === 'storage') {
    const { requiredVerifiedReplicaCount: count, destinations, enforceThreeTwoOne } = values.configuration
    const minimum = enforceThreeTwoOne ? 3 : 1
    if (!Number.isInteger(count) || count < minimum || count > destinations.length) {
      errors['configuration.requiredVerifiedReplicaCount'] = tx('{0} ile {1} arasında bir tam sayı girin.', { '0': minimum, '1': destinations.length })
    }
  } else if (step === 'schedule') {
    const { scheduled, schedule, retry } = values
    if (scheduled && !schedule.cronExpression.trim()) errors.cronExpression = tx('Cron ifadesini girin veya hazır bir zamanlama seçin.')
    if (!Number.isInteger(retry.maxAttempts) || retry.maxAttempts < 1) errors['retryPolicy.maxAttempts'] = tx('Toplam deneme sayısı en az 1 olan bir tam sayı olmalı.')
    if (scheduled && (!Number.isFinite(schedule.missedRunGraceSeconds) || schedule.missedRunGraceSeconds < 0)) errors.missedRunGraceSeconds = tx('Tolerans sıfır veya daha büyük olmalı.')
    if (!Number.isFinite(retry.initialDelaySeconds) || retry.initialDelaySeconds < 0) errors['retryPolicy.initialDelaySeconds'] = tx('Gecikme sıfır veya daha büyük olmalı.')
    if (!Number.isFinite(retry.maxDelaySeconds) || retry.maxDelaySeconds < retry.initialDelaySeconds) errors['retryPolicy.maxDelaySeconds'] = tx('En uzun gecikme ilk gecikmeden küçük olamaz.')
    if (!Number.isFinite(retry.jitter) || retry.jitter < 0 || retry.jitter > 1) errors['retryPolicy.jitter'] = tx('Rastgele gecikme oranı 0 ile 1 arasında olmalı.')
  }
  return errors
}

/** Every step's errors at once, with the first step that has any; used before saving, when steps may have been revisited out of order. */
export function validateJob(values: JobValues) {
  const byStep = editSections.map(step => [step, validateStep(step, values)] as const)
  return { errors: Object.assign({}, ...byStep.map(([, errors]) => errors)) as StepErrors, firstInvalid: byStep.find(([, errors]) => Object.keys(errors).length > 0)?.[0] }
}
