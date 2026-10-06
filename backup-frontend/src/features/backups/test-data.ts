import type { BackupRun } from './types'

export function makeRun(patch: Partial<BackupRun> = {}): BackupRun {
  return {
    id: 'run-id', jobId: 'job-id', agentId: 'agent-id', jobName: 'Günlük SQL yedeği', agentName: 'SQL ajanı',
    status: 'Running', backupType: 'mssql-full', triggerType: 'Manual',
    configuration: { database: 'Uygulama', engine: 'mssql', host: 'sql-host', port: 1433, storageProvider: 'local' },
    currentAttemptNumber: 1, createdAt: '2026-10-06T10:00:00Z', startedAt: '2026-10-06T10:01:00Z',
    completedAt: null, cancellationRequestedAt: null, reasonCode: null, reason: null,
    scheduledFor: null, scheduleDelaySeconds: null, claimDeadline: null, retention: null,
    retry: { maxAttempts: 3, attemptsUsed: 1, state: 'None', nextAttemptAt: null, lastFailureCode: null,
      lastFailureReason: null, lastFailureRetryable: null, overlapPolicy: 'Skip',
      policy: { maxAttempts: 3, initialDelaySeconds: 10, maxDelaySeconds: 60, backoff: 'Exponential', jitter: 0.1 } },
    attempts: [{ id: 'attempt-id', number: 1, status: 'Running', trigger: 'Manual',
      createdAt: '2026-10-06T10:00:00Z', startedAt: '2026-10-06T10:01:00Z', completedAt: null,
      reasonCode: null, reason: null, retryable: null, progressStage: 'BackingUp', progressPercent: 35,
      progressAt: '2026-10-06T10:02:00Z', artifacts: [] }],
    ...patch,
  }
}
