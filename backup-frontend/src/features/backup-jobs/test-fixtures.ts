import type { BackupJob } from './types'
import type { BackupJobHealth } from './health'

export const healthyJobHealth: BackupJobHealth = {
  jobId: 'job-a', jobName: 'Nightly', score: 100, state: 'Healthy', evaluatedAt: '2026-10-21T12:00:00Z',
  factors: {
    freshness: { score: 40, maxScore: 40, status: 'Available', reason: 'backup_fresh' },
    replication: { score: 30, maxScore: 30, status: 'Available', reason: 'replicas_verified' },
    restoreVerification: { score: 20, maxScore: 20, status: 'Passed', reason: 'restore_passed' },
    storageCapacity: { score: 10, maxScore: 10, status: 'Available', reason: 'capacity_available' },
  },
}

/** A saved job whose hidden values (retry, tolerance, prefix, flags, time zone) all differ from what a new job gets. */
export const savedJob = {
  id: 'job-a', name: 'Muhasebe', agentId: 'agent-a', agentName: 'Ofis', enabled: true, backupType: 'Full', createdAt: '2026-10-01T00:00:00Z', lastRun: null,
  configuration: {
    engine: 'mssql', database: 'ERP', host: 'sql01', port: 1433, integratedSecurity: false, databaseCredentialReference: 'cred-1', encrypt: true, trustServerCertificate: true,
    stagingReference: 'stage', storagePrefix: 'acme/erp', requiredVerifiedReplicaCount: 1, sourceSite: 'ist', copyOnly: false, checksumPolicy: 'Preferred', compressionPolicy: 'Disabled', encryptionPolicy: 'Optional',
    destinations: [{ storageProvider: 'local', storageConnectionId: 'conn-local', storageExecutionLocation: 'Agent', isPrimary: true }],
  },
  retryPolicy: { maxAttempts: 5, initialDelaySeconds: 45, maxDelaySeconds: 900, backoff: 'Linear', jitter: 0.5 }, overlapPolicy: 'Queue',
  schedule: { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Berlin', missedRunGraceSeconds: 7200, overlapPolicy: 'Queue' },
  retention: { enabled: true, keepForSeconds: 30 * 86400, deleteGracePeriodSeconds: 600 },
} as unknown as BackupJob
