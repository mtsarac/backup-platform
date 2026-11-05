import { discoveredConfiguration } from '@/features/discovery/job-prefill'
import type { DiscoveryInstanceDetail } from '@/features/discovery/types'
import type { BackupConfiguration, BackupJob, FileBackupPolicy, RetentionPolicy, RetryPolicy, Schedule } from '@/features/backup-jobs/types'

/** Everything the job form edits, in the shape the wizard and the edit page share. */
export interface JobValues {
  name: string; agentId: string; enabled: boolean; configuration: BackupConfiguration
  scheduled: boolean; schedule: Schedule; overlap: 'Skip' | 'Queue'; retry: RetryPolicy; retention: RetentionPolicy
  /** Databases picked from the discovery inventory; one job is created per name. */
  selected: string[]
}
export interface JobPrefill { instance: DiscoveryInstanceDetail; databases: string[] }

export type Engine = BackupConfiguration['engine']
export const defaultFiles: FileBackupPolicy = { paths: ['.'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' }

const initialConfiguration: BackupConfiguration = { engine: 'mssql', database: '', host: '', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storagePrefix: '', destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }], requiredVerifiedReplicaCount: 1, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred', encryptionPolicy: 'Off' }
const initialSchedule: Schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip' }
const initialRetry: RetryPolicy = { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 }
const initialRetention: RetentionPolicy = { enabled: true, keepForSeconds: 1209600, deleteGracePeriodSeconds: 86400 }

export function initialValues(job?: BackupJob, prefill?: JobPrefill): JobValues {
  // The PostgreSQL dump has no COPY_ONLY switch, so the saved flag is not resubmitted.
  const saved = job && (job.configuration.engine === 'postgres' ? { ...job.configuration, copyOnly: false } : job.configuration)
  return {
    name: job?.name ?? '', agentId: job?.agentId ?? prefill?.instance.agentId ?? '', enabled: job?.enabled ?? true,
    configuration: saved ?? (prefill?.databases.length ? discoveredConfiguration(initialConfiguration, prefill.instance, prefill.databases) : initialConfiguration),
    scheduled: Boolean(job?.schedule), schedule: job?.schedule ?? initialSchedule, overlap: job?.overlapPolicy ?? 'Skip',
    retry: job?.retryPolicy ?? initialRetry, retention: job?.retention ?? initialRetention, selected: prefill?.databases ?? [],
  }
}

const sourceKeys = ['database', 'host', 'port', 'integratedSecurity', 'databaseCredentialReference', 'databaseLogin', 'files', 'copyOnly', 'checksumPolicy'] as const
type SourceFields = Pick<BackupConfiguration, (typeof sourceKeys)[number]>
/** The source fields each engine had when the user left it, so switching engines back and forth never loses what was typed. */
export type EngineMemory = Partial<Record<Engine, SourceFields>>
const noLogin = { databaseCredentialReference: null, databaseLogin: null } as const
const engineDefaults: Record<Engine, SourceFields> = {
  mssql: { database: '', host: '', port: null, integratedSecurity: true, ...noLogin, files: null, copyOnly: true, checksumPolicy: 'Required' },
  postgres: { database: '', host: '', port: 5432, integratedSecurity: false, ...noLogin, files: null, copyOnly: false, checksumPolicy: 'Preferred' },
  'file-system': { database: '', host: 'localhost', port: null, integratedSecurity: true, ...noLogin, files: defaultFiles, copyOnly: true, checksumPolicy: 'Required' },
}

/** Moves to another engine: its remembered fields come back, a never-visited engine starts from its defaults, and the engine left behind is remembered. */
export function switchEngine(configuration: BackupConfiguration, memory: EngineMemory, engine: Engine) {
  const remembered: EngineMemory = { ...memory, [configuration.engine]: Object.fromEntries(sourceKeys.map(key => [key, configuration[key] ?? null])) as SourceFields }
  return { memory: remembered, configuration: { ...configuration, ...(remembered[engine] ?? engineDefaults[engine]), engine } }
}
