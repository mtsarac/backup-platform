import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BackupJob } from '@/features/backup-jobs/types'
import type { BackupJobHealth, BackupHealthState } from '@/features/backup-jobs/health'
import type { BackupRun } from '@/features/backups/types'

export interface Dashboard {
  artifacts: { total: number; sizeBytes: number; deletePending: number; deleteFailed: number }
  recentRuns: BackupRun[]
  upcomingJobs: BackupJob[]
}

export interface HealthSummary {
  totalJobs: number
  /** The backend's verdict (worst job wins); null when the tenant has no jobs. */
  state: BackupHealthState | null
  jobs: BackupJobHealth[]
}

export const getDashboard = (tenantId: string, signal?: AbortSignal) => apiRequest<Dashboard>(tenantPath(tenantId, 'dashboard'), { signal })
export const getHealthSummary = (tenantId: string, signal?: AbortSignal) => apiRequest<HealthSummary>(tenantPath(tenantId, 'observability/health-summary'), { signal })
