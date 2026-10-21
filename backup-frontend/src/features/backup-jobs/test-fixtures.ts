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
