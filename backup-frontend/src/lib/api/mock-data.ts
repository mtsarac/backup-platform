import type {
  Backup,
  BackupJob,
  Client,
  LogEntry,
  SettingThresholds,
  StorageProvider,
  User,
} from '@/lib/types'

export const mockClients: readonly Client[] = [
  {
    id: 'client-ank-001',
    ownerUserId: 'user-1',
    hostname: 'ANK-SQL-01',
    ipAddress: '10.10.4.21',
    operatingSystem: 'Windows Server 2022 Standard',
    agent: {
      id: 'agent-ank-001',
      version: '2.4.1',
      status: 'online',
      lastHeartbeat: '2026-09-08T11:42:00Z',
    },
    instances: [
      {
        id: 'sql-ank-default',
        name: 'MSSQLSERVER',
        version: '16.0.4125.3',
        edition: 'Enterprise Edition',
        productLevel: 'CU15',
        capabilities: {
          fullBackup: true,
          differentialBackup: true,
          transactionLogBackup: true,
          compression: true,
          checksum: true,
          backupEncryption: true,
          copyOnly: true,
        },
        databases: [
          {
            name: 'ERP_PROD',
            state: 'ONLINE',
            recoveryModel: 'FULL',
            compatibilityLevel: 160,
          },
          {
            name: 'REPORTING_CACHE',
            state: 'ONLINE',
            recoveryModel: 'SIMPLE',
            compatibilityLevel: 150,
          },
        ],
      },
    ],
  },
  {
    id: 'client-ist-002',
    ownerUserId: 'user-2',
    hostname: 'IST-FILE-02',
    ipAddress: '10.20.7.44',
    operatingSystem: 'Windows Server 2019 Datacenter',
    agent: {
      id: 'agent-ist-002',
      version: '2.3.9',
      status: 'offline',
      lastHeartbeat: '2026-09-08T08:15:00Z',
    },
    instances: [],
  },
]

export const mockBackupJobs: readonly BackupJob[] = [
  {
    id: 'job-erp-full-nightly',
    name: 'ERP_PROD Gece Full Backup',
    clientId: 'client-ank-001',
    type: 'mssql-full',
    source: 'MSSQLSERVER/ERP_PROD',
    storageProviderId: 'storage-main-nas',
    schedule: 'Her gün 02:00',
    retentionDays: 14,
    enabled: true,
    options: {
      compression: true,
      checksum: true,
      encryption: true,
    },
  },
]

export const mockBackups: readonly Backup[] = [
  {
    id: 'backup-20260908-erp-full',
    jobId: 'job-erp-full-nightly',
    clientId: 'client-ank-001',
    type: 'mssql-full',
    status: 'completed',
    startedAt: '2026-09-08T02:00:00Z',
    completedAt: '2026-09-08T02:18:43Z',
    durationSeconds: 1123,
    fileSizeBytes: 128_849_018_880,
    storageProviderId: 'storage-main-nas',
    storagePath: '\\\\backup-nas\\sql\\ERP_PROD\\2026-09-08.bak',
    instanceId: 'sql-ank-default',
    databaseName: 'ERP_PROD',
    sqlVersion: '16.0.4125.3',
    sqlEdition: 'Enterprise Edition',
    compatibilityLevel: 160,
    recoveryModel: 'FULL',
    compression: true,
    encryption: true,
    checksum: true,
    sqlVerificationStatus: 'verified',
    storageVerificationStatus: 'verified',
  },
  {
    id: 'backup-20260908-erp-full-running',
    jobId: 'job-erp-full-nightly',
    clientId: 'client-ank-001',
    type: 'mssql-full',
    status: 'running',
    startedAt: '2026-09-08T12:10:00Z',
    storageProviderId: 'storage-main-nas',
    storagePath: '\\\\backup-nas\\sql\\ERP_PROD\\2026-09-08.bak',
    instanceId: 'sql-ank-default',
    databaseName: 'ERP_PROD',
    sqlVersion: '16.0.4125.3',
    sqlEdition: 'Enterprise Edition',
    compatibilityLevel: 160,
    recoveryModel: 'FULL',
    compression: true,
    encryption: false,
    checksum: true,
    sqlVerificationStatus: 'not-verified',
    storageVerificationStatus: 'not-verified',
  },
]

export const mockStorageProviders: readonly StorageProvider[] = [
  {
    id: 'storage-main-nas',
    ownerUserId: 'user-1',
    clientIds: ['client-ank-001'],
    name: 'Merkez NAS',
    type: 'nas',
    connectionStatus: 'connected',
    usedBytes: 4_398_046_511_104,
    totalBytes: 10_995_116_277_760,
    lastSuccessfulUpload: '2026-09-08T02:19:10Z',
    configuration: {
      path: '\\\\backup-nas\\sql',
    },
  },
]

export const mockLogs: readonly LogEntry[] = [
  {
    id: 'log-20260908-001',
    timestamp: '2026-09-08T02:19:11Z',
    level: 'INFO',
    source: 'BackupWorker',
    message: 'ERP_PROD full backup başarıyla tamamlandı.',
    agentId: 'agent-ank-001',
    backupId: 'backup-20260908-erp-full',
  },
  {
    id: 'log-1',
    timestamp: '2026-09-09T08:00:00Z',
    level: 'INFO',
    source: 'Scheduler',
    message: 'Backup job job-erp-full-nightly started.',
    agentId: 'client-ank-001',
  },
  {
    id: 'log-2',
    timestamp: '2026-09-09T08:05:00Z',
    level: 'ERROR',
    source: 'Agent',
    message: 'Connection lost to client-ist-002.',
    agentId: 'client-ist-002',
  },
  {
    id: 'log-3',
    timestamp: '2026-09-09T07:30:00Z',
    level: 'WARNING',
    source: 'Storage',
    message: 'Storage provider storage-main-nas usage above 80%.',
  },
]

export const mockUsers: readonly User[] = [
  {
    id: '1',
    username: 'admin',
    email: 'admin@example.com',
    role: 'administrator',
    status: 'active',
  },
  {
    id: 'user-1',
    username: 'user',
    email: 'user@example.com',
    role: 'operator',
    status: 'active',
  },
  {
    id: 'user-2',
    username: 'user-2',
    email: 'user2@example.com',
    role: 'operator',
    status: 'inactive',
  },
]

export const mockSettings: SettingThresholds = {
  storageWarningPercent: 80,
  offlineAgentTimeoutMinutes: 10,
  defaultRetentionDays: 14,
  notifyOnFailure: true,
}
