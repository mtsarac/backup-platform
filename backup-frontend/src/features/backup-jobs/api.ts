import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BackupJob, BackupJobInput, Occurrence } from './types'

const jobPath = (tenantId: string, id?: string) => tenantPath(tenantId, `backup-jobs${id ? `/${encodeURIComponent(id)}` : ''}`)
export const getBackupJobs = (tenantId: string, signal?: AbortSignal) => apiRequest<BackupJob[]>(jobPath(tenantId), { signal })
export const getBackupJob = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<BackupJob>(jobPath(tenantId, id), { signal })
export const getOccurrences = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<Occurrence[]>(`${jobPath(tenantId, id)}/schedule/occurrences?limit=10`, { signal })
export async function saveBackupJob(tenantId: string, input: BackupJobInput, id?: string) {
  if (!id) {
    const job = await apiRequest<BackupJob>(jobPath(tenantId), { method: 'POST', json: input })
    if (input.enabled === false) return setJobEnabled(tenantId, job.id, false)
    return job
  }
  const { schedule, retention, enabled, ...configuration } = input
  let job = await apiRequest<BackupJob>(jobPath(tenantId, id), { method: 'PUT', json: configuration })
  job = await apiRequest<BackupJob>(`${jobPath(tenantId, id)}/schedule`, { method: schedule ? 'PUT' : 'DELETE', ...(schedule ? { json: schedule } : {}) })
  if (retention) job = await apiRequest<BackupJob>(`${jobPath(tenantId, id)}/retention`, { method: 'PUT', json: retention })
  if (enabled !== undefined && enabled !== job.enabled) job = await setJobEnabled(tenantId, id, enabled)
  return job
}
export const setJobEnabled = (tenantId: string, id: string, enabled: boolean) => apiRequest<BackupJob>(`${jobPath(tenantId, id)}/${enabled ? 'enable' : 'disable'}`, { method: 'POST' })
export const startJobRun = (tenantId: string, id: string, key: string) => apiRequest<{ id: string }>(`${jobPath(tenantId, id)}/runs`, { method: 'POST', headers: { 'Idempotency-Key': key } })
