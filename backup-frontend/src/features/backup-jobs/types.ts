export type OptionPolicy = 'Disabled' | 'Preferred' | 'Required'
export interface BackupDestination {
  storageProvider: string
  /** null / absent = the tenant's default connection for `storageProvider`; runs always snapshot the resolved id. */
  storageConnectionId?: string | null
  storageExecutionLocation: 'Agent' | 'Backend'
  isPrimary: boolean
}
export interface DatabaseLogin { userName: string; password: string }
export interface BackupConfiguration {
  engine: 'mssql' | 'postgres'; database: string; host: string; port: number | null
  integratedSecurity: boolean; databaseCredentialReference: string | null
  /** Write-only: sent once, exchanged by the API for a reference, never returned. */
  databaseLogin?: DatabaseLogin | null
  encrypt: boolean; trustServerCertificate: boolean; stagingReference: string
  storagePrefix: string
  /** At least one; exactly one `isPrimary`. This list is the only source of storage targets. */
  destinations: BackupDestination[]
  requiredVerifiedReplicaCount: number
  copyOnly: boolean; checksumPolicy: OptionPolicy; compressionPolicy: OptionPolicy
}
export interface RetryPolicy { maxAttempts: number; initialDelaySeconds: number; maxDelaySeconds: number; backoff: string; jitter: number }
export interface RetentionPolicy { enabled: boolean; keepForSeconds: number; deleteGracePeriodSeconds: number }
export interface BackupJob extends BackupJobInput { id: string; enabled: boolean; backupType: string; createdAt: string; agentName?: string | null; nextOccurrenceAt?: string | null; lastRun?: { id: string; status: string; createdAt: string; startedAt: string | null; completedAt: string | null; reasonCode: string | null } | null }
export interface BackupJobInput { name: string; agentId: string; configuration: BackupConfiguration; retryPolicy: RetryPolicy; overlapPolicy: 'Skip' | 'Queue'; schedule: Schedule | null; retention?: RetentionPolicy; enabled?: boolean }
export interface Schedule { enabled: boolean; cronExpression: string; timeZone: string; missedRunGraceSeconds: number; overlapPolicy: 'Skip' | 'Queue'; effectiveFrom?: string; nextOccurrenceAt?: string | null }
export interface Occurrence { id: string; scheduledAt: string; outcome: string; reason: string; runId: string | null; decidedAt: string; delaySeconds: number }
