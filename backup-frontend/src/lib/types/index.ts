export type UserRole = 'administrator' | 'operator' | 'viewer'

export interface User {
  id: string
  username: string
  email: string
  role: UserRole
  status: 'active' | 'inactive'
}

export interface SettingThresholds {
  storageWarningPercent: number
  offlineAgentTimeoutMinutes: number
  defaultRetentionDays: number
  notifyOnFailure: boolean
}

export type AgentStatus = 'online' | 'offline' | 'waiting'

export interface Agent {
  id: string
  version: string
  status: AgentStatus
  lastHeartbeat: string
}

export interface SqlCapability {
  fullBackup: boolean
  differentialBackup: boolean
  transactionLogBackup: boolean
  compression: boolean
  checksum: boolean
  backupEncryption: boolean
  copyOnly: boolean
}

export interface DatabaseInfo {
  name: string
  state: string
  recoveryModel: 'FULL' | 'BULK_LOGGED' | 'SIMPLE'
  compatibilityLevel: number
}

export interface SqlInstance {
  id: string
  name: string
  version: string
  edition: string
  productLevel: string
  capabilities: SqlCapability
  databases: DatabaseInfo[]
}

export interface Client {
  id: string
  ownerUserId: string
  hostname: string
  ipAddress: string
  operatingSystem: string
  agent: Agent
  instances: SqlInstance[]
}

export type BackupType = 'mssql-full'

export interface BackupJob {
  id: string
  name: string
  clientId: string
  type: BackupType
  source: string
  storageProviderId: string
  schedule: string
  retentionDays: number
  enabled: boolean
  options: {
    compression?: boolean
    checksum?: boolean
    encryption?: boolean
    copyOnly?: boolean
  }
}

export type BackupStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'

export interface Backup {
  id: string
  jobId: string
  clientId: string
  type: BackupType
  status: BackupStatus
  startedAt: string
  completedAt?: string
  durationSeconds?: number
  fileSizeBytes?: number
  storageProviderId: string
  storagePath: string
  instanceId?: string
  databaseName?: string
  sqlVersion?: string
  sqlEdition?: string
  compatibilityLevel?: number
  recoveryModel?: string
  compression?: boolean
  encryption?: boolean
  checksum?: boolean
  sqlVerificationStatus: 'verified' | 'not-verified' | 'failed'
  storageVerificationStatus: 'verified' | 'not-verified' | 'failed'
}

export type StorageType = 'local' | 'central' | 'sftp' | 'ftp' | 'google-drive' | 'nas'

export interface StorageProvider {
  id: string
  ownerUserId: string
  clientIds: string[]
  name: string
  type: StorageType
  connectionStatus: 'connected' | 'disconnected' | 'unknown'
  usedBytes: number
  totalBytes: number
  lastSuccessfulUpload?: string
  configuration: Record<string, string>
}

export type LogLevel = 'DEBUG' | 'INFO' | 'WARNING' | 'ERROR'

export interface LogEntry {
  id: string
  timestamp: string
  level: LogLevel
  source: string
  message: string
  agentId?: string
  backupId?: string
}
