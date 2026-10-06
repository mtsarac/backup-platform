import type { BackupMetadata } from './backup-metadata'

export type ArtifactState = 'Available' | 'DeletePending' | 'DeleteFailed' | 'Deleted'

export interface BackupReplica {
  id: string
  storageProvider: string
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
