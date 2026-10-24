import { useQuery } from '@tanstack/react-query'
import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BackupHealthState, BackupJobHealth } from '@/features/backup-jobs/health'

export interface HealthSummary {
  totalJobs: number
  averageScore: number | null
  /** The backend's verdict (worst job wins); null when the tenant has no jobs. Displayed as is, never recomputed. */
  state: BackupHealthState | null
  healthy: number
  degraded: number
  critical: number
  jobs: BackupJobHealth[]
  capacityRisks: { connectionId: string; connectionName: string; status: string; totalBytes: number | null; usedBytes: number | null; freeBytes: number | null; reportedAt: string }[]
  recentSizeAnomalies: { runId: string; jobId: string; jobName: string; completedAt: string; sizeBytes: number; baselineMeanBytes: number; deviationRatio: number }[]
  recentSizeAnomaliesTotal: number
}

export function useHealthSummary(tenantId: string) {
  return useQuery({ queryKey: ['tenant', tenantId, 'observability'], queryFn: ({ signal }) => apiRequest<HealthSummary>(tenantPath(tenantId, 'observability/health-summary'), { signal }), refetchInterval: 15_000, refetchIntervalInBackground: false })
}
