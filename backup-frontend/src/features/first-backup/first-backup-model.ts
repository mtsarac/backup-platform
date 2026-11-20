import { tx } from '@/lib/i18n'
import { initialValues } from '@/features/backup-jobs/job-form-values'
import type { BackupDestination, BackupJobInput, RetentionPolicy } from '@/features/backup-jobs/types'
import type { EncryptionPolicy } from '@/features/artifact-encryption/types'
import type { Agent } from '@/features/agents/types'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import { supportsDiscovery } from '@/features/discovery/status'

/** What the summary screen lets the user change; everything else is the wizard's pinned value. */
export interface FirstBackupSettings { destinations: BackupDestination[]; scheduled: boolean; cronExpression: string; retention: RetentionPolicy; encryptionPolicy: EncryptionPolicy }
export interface FirstBackupItem { key: string; instance: DiscoveryInstanceDetail; database: string }

/** How often the computer list is refreshed while a new computer is still reporting what it can do. */
export const CAPABILITY_POLL_MS = 3000
/** True while no computer can be scanned yet but one has not reported its capabilities: a new computer is online a moment before its first report arrives. */
export const awaitingCapabilities = (agents: readonly Agent[] | undefined) =>
  Boolean(agents) && !agents!.some(agent => supportsDiscovery(agent.capabilities)) && agents!.some(agent => agent.capabilities.length === 0 && !agent.capabilitiesReportedAt)

export const itemKey = (instanceId: string, database: string) => `${instanceId}|${database}`
/** "Muhasebe" becomes "Muhasebe veritabanı yedeği"; the user only renames it afterwards. */
export const autoJobName = (database: string) => tx('{0} veritabanı yedeği', { '0': database })

/** Starting answers: the wizard's defaults for a new job (daily 02:00, 30 days, one local destination, encryption off). */
export function defaultSettings(): FirstBackupSettings {
  const { configuration, schedule, retention } = initialValues()
  return { destinations: configuration.destinations, scheduled: true, cronExpression: schedule.cronExpression, retention, encryptionPolicy: configuration.encryptionPolicy }
}

/** One job per picked database, built on the same form model as the create wizard so the pinned values are identical. */
export function firstBackupJobInput(item: FirstBackupItem, name: string, settings: FirstBackupSettings): BackupJobInput {
  const values = initialValues(undefined, { instance: item.instance, databases: [item.database] })
  return {
    name, agentId: values.agentId, configuration: { ...values.configuration, destinations: settings.destinations, encryptionPolicy: settings.encryptionPolicy },
    retryPolicy: values.retry, overlapPolicy: values.overlap, enabled: values.enabled,
    schedule: settings.scheduled ? { ...values.schedule, cronExpression: settings.cronExpression, overlapPolicy: values.overlap } : null,
    retention: settings.retention,
  }
}
