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
