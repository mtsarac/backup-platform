import { tx } from '@/lib/i18n'
import { initialValues } from '@/features/backup-jobs/job-form-values'
import type { BackupDestination, BackupJobInput, RetentionPolicy } from '@/features/backup-jobs/types'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'

/** What the summary screen lets the user change; everything else is the wizard's pinned value. */
export interface FirstBackupSettings { destinations: BackupDestination[]; scheduled: boolean; cronExpression: string; retention: RetentionPolicy }
export interface FirstBackupItem { key: string; instance: DiscoveryInstanceDetail; database: string }

export const itemKey = (instanceId: string, database: string) => `${instanceId}|${database}`
/** "Muhasebe" becomes "Muhasebe veritabanı yedeği"; the user only renames it afterwards. */
export const autoJobName = (database: string) => tx('{0} veritabanı yedeği', { '0': database })

/** Starting answers: the wizard's defaults for a new job (daily 02:00, 30 days, one local destination). */
export function defaultSettings(): FirstBackupSettings {
  const { configuration, schedule, retention } = initialValues()
  return { destinations: configuration.destinations, scheduled: true, cronExpression: schedule.cronExpression, retention }
}

/** One job per picked database, built on the same form model as the create wizard so the pinned values are identical. */
export function firstBackupJobInput(item: FirstBackupItem, name: string, settings: FirstBackupSettings, canAdmin: boolean): BackupJobInput {
  const values = initialValues(undefined, { instance: item.instance, databases: [item.database] })
  return {
    name, agentId: values.agentId, configuration: { ...values.configuration, destinations: settings.destinations },
    retryPolicy: values.retry, overlapPolicy: values.overlap, enabled: values.enabled,
    schedule: settings.scheduled ? { ...values.schedule, cronExpression: settings.cronExpression, overlapPolicy: values.overlap } : null,
    ...(canAdmin ? { retention: settings.retention } : {}),
  }
}
