import type { BackupArtifact, ReplicationHealth } from '@/features/artifacts/types'

export type BackupRunStatus = 'Pending' | 'Leased' | 'Running' | 'RetryPending' | 'CancellationRequested' | 'Succeeded' | 'Failed' | 'Cancelled' | 'AgentLost'
export type BackupTrigger = 'Manual' | 'Scheduled' | 'Retry' | 'ManualRetry' | 'AutomaticRetry'

export interface BackupRetryPolicy {
  maxAttempts: number
  initialDelaySeconds: number
  maxDelaySeconds: number
  backoff: string
  jitter: number
}

export interface BackupRetentionPolicy {
  enabled: boolean
  keepForSeconds: number
  deleteGracePeriodSeconds: number
}

export interface BackupAttempt {
  id: string
  number: number
  status: BackupRunStatus
  trigger: BackupTrigger | null
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  reasonCode: string | null
  reason: string | null
  retryable: boolean | null
  progressStage: string | null
  progressPercent: number | null
  progressAt: string | null
  artifacts: BackupArtifact[]
  /** Replication compliance of what this attempt transferred; null when nothing was transferred. */
  replication?: ReplicationHealth | null
}

export interface BackupRun {
  id: string
  jobId: string
  agentId: string
  jobName?: string | null
  agentName?: string | null
  status: BackupRunStatus
  backupType: string
  triggerType: BackupTrigger
  configuration: {
    database: string
    engine: string
    host: string
    port: number
    storageProvider: string
    /** The connection resolved when the run was created. */
    storageConnectionId?: string | null
  }
  currentAttemptNumber: number
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  cancellationRequestedAt: string | null
  reasonCode: string | null
  reason: string | null
  scheduledFor: string | null
  scheduleDelaySeconds: number | null
  claimDeadline: string | null
  retention: BackupRetentionPolicy | null
  retry: {
    maxAttempts: number
    attemptsUsed: number
    state: string
    nextAttemptAt: string | null
    lastFailureCode: string | null
    lastFailureReason: string | null
    lastFailureRetryable: boolean | null
    overlapPolicy: string
    policy: BackupRetryPolicy
  }
  attempts: BackupAttempt[]
  /** Authoritative replication compliance, independent of `status`: a Succeeded run can still be Degraded or InsufficientReplicas. */
  replication?: ReplicationHealth | null
}

export interface BackupRunFilters {
  status?: BackupRunStatus | ''
  jobId?: string
  agentId?: string
  from?: string
  to?: string
  trigger?: BackupTrigger | ''
  page?: number
  pageSize?: number
}

export interface BackupRunPage {
  items: BackupRun[]
  totalCount: number
  page: number
  pageSize: number
}
