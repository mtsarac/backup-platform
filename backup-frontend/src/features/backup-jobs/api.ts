import { mockBackupJobs } from '@/lib/api/mock-data'
import type { BackupJob } from '@/lib/types'

export function getBackupJobs(): Promise<readonly BackupJob[]> {
  return Promise.resolve(mockBackupJobs)
}

export function getBackupJob(id: string): Promise<BackupJob | undefined> {
  return Promise.resolve(mockBackupJobs.find((job) => job.id === id))
}

export function createBackupJob(job: BackupJob): Promise<BackupJob> {
  return Promise.resolve(job)
}
