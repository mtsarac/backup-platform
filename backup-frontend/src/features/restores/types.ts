export type RestoreStatus = 'Pending' | 'Leased' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'AgentLost'
export type RestoreMode = 'NewDatabase' | 'ReplaceExistingDatabase'
export type RestoreTargetState = 'NotModified' | 'Online' | 'PartiallyRestored' | 'Unknown'
export interface RestoreTarget {
  host: string
  port?: number | null
  integratedSecurity: boolean
  databaseCredentialReference?: string | null
  encrypt: boolean
  trustServerCertificate: boolean
}
export interface RestoreOptions {
  terminateActiveConnections?: boolean
  verifyChecksum?: boolean
  verifyBackup?: boolean
}
export interface StartRestoreRequest {
  agentId: string
  target: RestoreTarget
  databaseName: string
  replicaId?: string
  mode: RestoreMode
  confirmOverwrite?: string
  options?: RestoreOptions
  precheckId?: string
}
export interface StartRestoreVerificationRequest {
  agentId: string
  target: RestoreTarget
  replicaId?: string
  options?: Pick<RestoreOptions, 'verifyChecksum' | 'verifyBackup'>
}
export interface StartRestoreVerificationVariables { artifactId: string; request: StartRestoreVerificationRequest; idempotencyKey: string }
export type StartRestorePrecheckRequest = Omit<StartRestoreRequest, 'confirmOverwrite' | 'precheckId'>
export interface RestoreProgress { stage: string | null; percent: number | null; at: string | null }
export interface RestoreAttempt {
  id: string
  number: number
  status: RestoreStatus
  createdAt: string
  leasedAt: string | null
  startedAt: string | null
  completedAt: string | null
  failureCode: string | null
  reason: string | null
  targetState: RestoreTargetState | null
  progressStage: string | null
  progressPercent: number | null
  progressAt: string | null
}
export type VerificationOutcome = 'Restored' | 'Failed' | 'NotRun'
export interface VerificationResult {
  checks: { checkId: string; status: 'Passed' | 'Failed'; reasonCode: string; message: string }[]
  userTableCount: number | null
  readableTableCount: number | null
  cleanup: 'Dropped' | 'NotCreated' | 'Failed'
  cleanupReasonCode: string | null
}
export interface RestoreCleanup {
  kind: 'VerificationDatabase' | 'RollbackCopy'
  state: 'Idle' | 'Retained' | 'Pending' | 'Completed'
  databaseName: string | null
  attempts: number
  nextAttemptAt: string | null
  lastFailureCode: string | null
  reason: string | null
  needsAttention: boolean
  canRequest: boolean
  retainedUntil: string | null
}
export interface RestoreResult {
  preCheck: { checkId: string; status: 'Passed' | 'Warning' | 'Failed' | 'Skipped'; reasonCode: string; message: string }[]
  warnings: { code: string; message: string }[]
  sourceBackup: {
    databaseName: string; backupType: string; sourceServerName: string | null; sourceServerVersion: string | null
    backupStartedAt: string | null; backupFinishedAt: string | null; position: number; backupSizeBytes: number
    compatibilityLevel: number | null; collation: string | null; isCopyOnly: boolean; hasChecksums: boolean; isCompressed: boolean
  } | null
  targetServer: { serverName: string | null; instanceName: string | null; version: string | null; edition: string | null; hostPlatform: string | null } | null
  // Physical names are deliberately omitted: the UI never renders filesystem paths.
  fileMappings: { logicalName: string; fileType: string; fileGroupName: string | null; sizeBytes: number }[]
  startedAt: string | null
  finishedAt: string | null
  restoreStartedAt: string | null
  restoreFinishedAt: string | null
  checksumVerified: boolean
  backupVerified: boolean
  databaseState: string | null
  targetStateNote: string | null
  verification?: VerificationResult | null
  retainedDatabaseName?: string | null
  discovery?: {
    instance: { engine: string; serverName: string; instanceName: string | null; version: string | null; edition: string | null; hostPlatform: string | null }
    databases: { name: string; isSystem: boolean; availability: string }[]
  } | null
}
export interface RestoreView {
  id: string
  artifactId: string
  replicaId: string
  agentId: string
  databaseName: string
  status: RestoreStatus
  mode: RestoreMode
  overwritePolicy: 'Forbidden' | 'ReplaceExisting'
  sourceAccess: 'AgentLocal' | 'CentralStorage'
  purpose: 'Execution' | 'Precheck' | 'Discovery' | 'Verification'
  target: RestoreTarget
  options: RestoreOptions
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  cancellationRequestedAt: string | null
  failureCode: string | null
  reason: string | null
  targetState: RestoreTargetState | null
  targetStateSummary: string | null
  progress: RestoreProgress
  replicaPinned: boolean
  result: RestoreResult | null
  attempts: RestoreAttempt[]
  verificationOutcome?: VerificationOutcome | null
  cleanup?: RestoreCleanup | null
  sourceDatabaseName?: string | null
  sourceJobName?: string | null
  sourceAgentName?: string | null
  artifactCreatedAt?: string | null
}
export interface RestoreFilters { page?: number; pageSize?: number; status?: RestoreStatus | ''; artifactId?: string; agentId?: string; purpose?: 'Verification' }
export interface RestorePage { items: RestoreView[]; totalCount: number; page: number; pageSize: number }
export interface StartRestoreVariables { artifactId: string; request: StartRestoreRequest; idempotencyKey: string }
export interface StartRestorePrecheckVariables { artifactId: string; request: StartRestorePrecheckRequest; idempotencyKey: string }
export interface StartRestoreDiscoveryRequest { agentId: string; target: RestoreTarget; replicaId?: string }
export interface StartRestoreDiscoveryVariables { artifactId: string; request: StartRestoreDiscoveryRequest; idempotencyKey: string }
export interface RestoreTargetAgent {
  agentId: string
  hostName: string | null
  status: string
  connectivity: string
  canRestore: boolean
  canPrecheck: boolean
  canDiscover: boolean
  canVerify?: boolean
  sourceEligible: boolean
  sourceAccess: string | null
  rejectionCode: string | null
  rejectionMessage: string | null
}
export interface RestoreConfiguredTarget {
  jobId: string
  jobName: string
  agentId: string
  database: string
  target: RestoreTarget
}
export interface RestoreTargetsResponse {
  engine?: 'mssql' | 'postgres' | 'unsupported'
  artifactId: string
  sourceDatabase: string
  backupType: string
  sizeBytes: number
  agents: RestoreTargetAgent[]
  targets: RestoreConfiguredTarget[]
}
