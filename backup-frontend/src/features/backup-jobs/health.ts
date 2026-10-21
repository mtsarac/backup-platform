import { useQuery } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getBackupJobHealth } from './api'

export type BackupHealthState = 'Healthy' | 'Degraded' | 'Critical'
export interface BackupHealthFactor { score: number; maxScore: number; status: string; reason: string }
export interface BackupJobHealth {
  jobId: string
  jobName: string
  score: number
  state: BackupHealthState
  evaluatedAt: string
  factors: { freshness: BackupHealthFactor; replication: BackupHealthFactor; restoreVerification: BackupHealthFactor; storageCapacity: BackupHealthFactor }
}

export function useBackupHealth(jobId: string) {
  const { tenantId } = useTenant()
  return useQuery({
    queryKey: ['tenant', tenantId, 'backup-jobs', jobId, 'health'],
    queryFn: ({ signal }) => getBackupJobHealth(tenantId, jobId, signal),
    enabled: Boolean(jobId),
  })
}
