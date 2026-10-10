import type { BackupMetadata } from './backup-metadata'

export type ReplicationStatus = 'Healthy' | 'Degraded' | 'InsufficientReplicas' | 'Unavailable'

/** One snapshot destination of a run: Verified, Failed, Cancelled, NotAttempted or Deleted. */
export interface ReplicationDestination {
  storageProvider: string
  storageConnectionId: string | null
  isPrimary: boolean
  state: string
  failureCode: string | null
  failureReason: string | null
  objectMayRemain: boolean
  replicaId: string | null
  replicaStatus: string | null
}

/**
 * The backend's authoritative replication compliance, independent of the run status: a run can be Succeeded and still be Degraded or
 * InsufficientReplicas. `destinations` is empty on artifact listings, where only the replicas are known.
 */
export interface ReplicationHealth {
  requiredVerifiedReplicas: number
  verifiedReplicas: number
  destinationCount: number
  status: ReplicationStatus
  policySatisfied: boolean
  destinations: ReplicationDestination[]
}

export type ArtifactState = 'Available' | 'DeletePending' | 'DeleteFailed' | 'Deleted'

export interface BackupReplica {
  id: string
  storageProvider: string
  storageConnectionId: string
  storageTarget: string
  key: string
  sizeBytes: number
  checksum: string
  checksumAlgorithm: string
  verificationState: string
  verificationMethod: string
  createdAt: string
  verifiedAt: string | null
  status: string
  usable: boolean
  deleteRequestedAt: string | null
  deleteAfter: string | null
  deleteReason: string | null
  deletedAt: string | null
  deleteAttempts: number
  deleteFailureCode: string | null
  deleteNextAttemptAt: string | null
  deletionExecutor: string
  waitingForAgent: boolean
}

export interface BackupArtifact {
  id: string
  sizeBytes: number
  sourceChecksum: string
  artifactChecksum: string
  checksumAlgorithm: string
  createdAt: string
  replicas: BackupReplica[]
  state: string
  acceptedAt: string | null
  retentionExpiresAt: string | null
  retentionPinnedAt: string | null
  deleteAfter: string | null
  deleteReason: string | null
  runId: string | null
  jobId: string | null
  attemptId: string | null
  jobName?: string | null
  databaseName?: string | null
  agentName?: string | null
  agentId?: string | null
  cancelDeleteAllowed?: boolean
  backupType?: string
  backupMetadata?: BackupMetadata | null
  replication?: ReplicationHealth | null
}

export interface ArtifactList {
  items: BackupArtifact[]
  totalCount: number
  page: number
  pageSize: number
}

export interface ArtifactFilters {
  page: number
  pageSize: number
  state: ArtifactState | ''
  from: string
  to: string
}
