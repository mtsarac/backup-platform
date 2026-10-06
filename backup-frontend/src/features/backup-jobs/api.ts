import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BackupJob, BackupJobInput, Occurrence } from './types'

const jobPath = (tenantId: string, id?: string) => tenantPath(tenantId, `backup-jobs${id ? `/${encodeURIComponent(id)}` : ''}`)
export const getBackupJobs = (tenantId: string, signal?: AbortSignal) => apiRequest<BackupJob[]>(jobPath(tenantId), { signal })
export const getBackupJob = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<BackupJob>(jobPath(tenantId, id), { signal })
export const getOccurrences = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<Occurrence[]>(`${jobPath(tenantId, id)}/schedule/occurrences?limit=10`, { signal })
export function saveBackupJob(tenantId: string, input: BackupJobInput, id?: string) {
  const { schedule, ...rest } = input
  const wireSchedule = schedule ? { enabled: schedule.enabled, cronExpression: schedule.cronExpression, timeZone: schedule.timeZone, missedRunGraceSeconds: schedule.missedRunGraceSeconds, overlapPolicy: schedule.overlapPolicy } : undefined
  if (!id) return apiRequest<BackupJob>(jobPath(tenantId), { method: 'POST', json: { ...rest, ...(wireSchedule ? { schedule: wireSchedule } : {}) } })
  return apiRequest<BackupJob>(jobPath(tenantId, id), { method: 'PUT', json: { ...rest, ...(wireSchedule ? { schedule: wireSchedule } : { removeSchedule: true }) } })
}
export const setJobEnabled = (tenantId: string, id: string, enabled: boolean) => apiRequest<BackupJob>(`${jobPath(tenantId, id)}/${enabled ? 'enable' : 'disable'}`, { method: 'POST' })
export const startJobRun = (tenantId: string, id: string, key: string) => apiRequest<{ id: string }>(`${jobPath(tenantId, id)}/runs`, { method: 'POST', headers: { 'Idempotency-Key': key } })
