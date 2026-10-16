import type { BackupMetadata } from '@/features/artifacts/backup-metadata'
import type { Schedule } from '@/features/backup-jobs/types'
import type { RestoreOptions, RestoreTargetAgent } from '@/features/restores/types'
import type { PageParams, AuditDetailValue } from '@/features/platform/types'

export type SupportPermission = 'ViewBackupMetadata' | 'DownloadBackup' | 'RestoreBackup'
export type SupportStatus = 'Active' | 'Expired' | 'Closed' | 'Revoked'
export interface SupportSession {
  id: string; tenantId: string; tenantName: string; platformAdminId: string; actorDisplayName: string | null
  reason: string; permissions: SupportPermission[]; startedAt: string; expiresAt: string; status: SupportStatus
  closedAt: string | null; closedBy: string | null; closedByDisplayName: string | null; closeReason: string | null
  createdAt: string; isOwner: boolean; serverNow: string
}
export interface OpenSupportSessionInput { tenantId: string; reason: string; permissions: SupportPermission[]; durationMinutes: number }
export interface SupportSessionFilters extends PageParams { tenantId?: string; status?: SupportStatus | ''; mine?: boolean }
export interface SupportScope { sessionId: string; tenantId: string; enabled: boolean }
export interface SupportAuditEntry {
  id: string; at: string; action: string; actorUserId: string; actorDisplayName: string | null
  targetType: string; targetId: string; result: string | null; details: Record<string, AuditDetailValue>
}
export interface SupportJob {
  id: string; agentId: string; agentName: string; name: string; backupType: string; enabled: boolean
  engine: string; database: string; storageProvider: string; schedule: Schedule | null
  retention: { enabled: boolean; keepForSeconds: number; deleteGracePeriodSeconds: number } | null
  lastRun: { id: string; status: string; createdAt: string; startedAt: string | null; completedAt: string | null; reasonCode: string | null } | null
}
export interface SupportRun {
  id: string; jobId: string; jobName: string; agentId: string; agentName: string; status: string
  backupType: string; triggerType: string; currentAttemptNumber: number; createdAt: string
  startedAt: string | null; completedAt: string | null; reasonCode: string | null; reason: string | null
  attempts: { number: number; status: string; createdAt: string; startedAt: string | null; completedAt: string | null; reasonCode: string | null; reason: string | null }[]
  artifactIds: string[]
}
export interface SupportReplica {
  id: string; storageProvider: string; executionLocation: 'Backend' | 'Agent'; sizeBytes: number
  checksum: string; checksumAlgorithm: string; verificationState: string; verificationMethod: string
  createdAt: string; verifiedAt: string | null; status: string; usable: boolean
  deleteRequestedAt: string | null; deleteAfter: string | null; deletedAt: string | null
}
export interface SupportArtifact {
  id: string; sizeBytes: number; sourceChecksum: string; artifactChecksum: string; checksumAlgorithm: string
  createdAt: string; state: string; acceptedAt: string | null; retentionExpiresAt: string | null
  retentionPinnedAt: string | null; deleteAfter: string | null; deleteReason: string | null
  runId: string | null; jobId: string | null; jobName: string | null; databaseName: string | null
  agentId: string | null; agentName: string | null; backupType: string; backupMetadata: BackupMetadata | null; replicas: SupportReplica[]
}
export interface SupportDataFilters extends PageParams { jobId?: string; status?: string; state?: string; from?: string; to?: string }
export interface SupportDownloadAvailability {
  available: boolean; reason: string | null; checksum: string; sizeBytes: number
  replicas: { replicaId: string; storageProvider: string; available: boolean; reason: string | null }[]
}
export interface SupportDownload {
  downloadId: string; artifactId: string; replicaId: string; state: string; failureCode: string | null
  transferredBytes: number; sizeBytes: number; filename: string; createdAt: string; releasedAt: string | null
}
export interface SupportRestoreTargets {
  artifactId: string; sourceDatabase: string; backupType: string; sizeBytes: number
  engine?: 'mssql' | 'postgres' | 'unsupported'
  environment: { kind: 'CustomerEnvironment'; isolated: false; warning: string }
  agents: Omit<RestoreTargetAgent, 'canDiscover'>[]
  targets: { jobId: string; jobName: string; agentId: string; database: string; host: string; port: number | null; integratedSecurity: boolean }[]
}
export interface SupportPrecheckInput { targetJobId: string; databaseName: string; replicaId?: string; options?: Pick<RestoreOptions, 'verifyChecksum' | 'verifyBackup'> }
export interface SupportRestoreInput extends SupportPrecheckInput { precheckId: string }
