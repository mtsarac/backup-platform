import { apiRequest, tenantPath } from '@/lib/api/client'
import type { BackupJob, BackupJobInput, Occurrence } from './types'
import type { BackupJobHealth } from './health'

const jobPath = (tenantId: string, id?: string) => tenantPath(tenantId, `backup-jobs${id ? `/${encodeURIComponent(id)}` : ''}`)
export const getBackupJobs = (tenantId: string, signal?: AbortSignal, includeArchived = false) => apiRequest<BackupJob[]>(`${jobPath(tenantId)}${includeArchived ? `?${new URLSearchParams({ includeArchived: 'true' })}` : ''}`, { signal })
export const getBackupJob = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<BackupJob>(jobPath(tenantId, id), { signal })
export const getBackupJobHealth = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<BackupJobHealth>(`${jobPath(tenantId, id)}/health`, { signal })
export const getOccurrences = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<Occurrence[]>(`${jobPath(tenantId, id)}/schedule/occurrences?limit=10`, { signal })
export function saveBackupJob(tenantId: string, input: BackupJobInput, id?: string, idempotencyKey?: string) {
  const { schedule, ...rest } = input
  const wireSchedule = schedule ? { enabled: schedule.enabled, cronExpression: schedule.cronExpression, timeZone: schedule.timeZone, missedRunGraceSeconds: schedule.missedRunGraceSeconds, overlapPolicy: schedule.overlapPolicy } : undefined
  if (!id) return apiRequest<BackupJob>(jobPath(tenantId), { method: 'POST', json: { ...rest, ...(wireSchedule ? { schedule: wireSchedule } : {}) }, ...(idempotencyKey ? { headers: { 'Idempotency-Key': idempotencyKey } } : {}) })
  return apiRequest<BackupJob>(jobPath(tenantId, id), { method: 'PUT', json: { ...rest, ...(wireSchedule ? { schedule: wireSchedule } : { removeSchedule: true }) } })
}
export const setJobEnabled = (tenantId: string, id: string, enabled: boolean) => apiRequest<BackupJob>(`${jobPath(tenantId, id)}/${enabled ? 'enable' : 'disable'}`, { method: 'POST' })
export const startJobRun = (tenantId: string, id: string, key: string) => apiRequest<{ id: string }>(`${jobPath(tenantId, id)}/runs`, { method: 'POST', headers: { 'Idempotency-Key': key } })
export const archiveBackupJob = (tenantId: string, id: string) => apiRequest<void>(jobPath(tenantId, id), { method: 'DELETE' })
export interface HealthTimeline { jobs: { jobId: string; days: { date: string; status: string; attemptCount: number }[] }[] }
export const getHealthTimeline = (tenantId: string, signal?: AbortSignal) => apiRequest<HealthTimeline>(`${jobPath(tenantId)}/health-timeline?timeZone=${encodeURIComponent(Intl.DateTimeFormat().resolvedOptions().timeZone)}`, { signal })
