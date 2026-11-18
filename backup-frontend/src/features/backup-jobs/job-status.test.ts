import { expect, it } from 'vitest'
import { healthyJobHealth } from './test-fixtures'
import { jobStatus, type JobStatusInput } from './job-status'
import type { BackupJobHealth } from './health'

const health = (state: BackupJobHealth['state'], ...reasons: string[]): BackupJobHealth => ({
  ...healthyJobHealth, state,
  factors: {
    freshness: { ...healthyJobHealth.factors.freshness, score: 0, reason: reasons[0] ?? 'backup_fresh' },
    replication: { ...healthyJobHealth.factors.replication, score: reasons[1] ? 0 : 30, reason: reasons[1] ?? 'replicas_verified' },
    restoreVerification: healthyJobHealth.factors.restoreVerification,
    storageCapacity: healthyJobHealth.factors.storageCapacity,
  },
})
const schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'UTC', missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' } as const
const base: JobStatusInput = { job: { enabled: true, schedule }, health: healthyJobHealth, failedRecently: false, hasSucceeded: true, offline: false, running: false }

it.each([
  ['a Critical job is attention', { health: health('Critical', 'backup_stale') }, 'attention'],
  ['a Degraded job is attention', { health: health('Degraded', 'backup_stale') }, 'attention'],
  ['an offline computer is attention even when healthy', { offline: true }, 'attention'],
  ['attention beats a running backup', { health: health('Critical', 'backup_stale'), running: true }, 'attention'],
  ['a running backup shows progress', { running: true }, 'running'],
  ['a new job is neutral although the backend says Critical', { health: health('Critical', 'backup_missing', 'no_verified_replica'), hasSucceeded: false }, 'first'],
  ['a job whose first backup failed is attention', { health: health('Critical', 'backup_missing'), hasSucceeded: false, failedRecently: true }, 'attention'],
  ['a manual job is neutral although freshness is low', { job: { enabled: true, schedule: null }, health: health('Critical', 'schedule_unavailable') }, 'manual'],
  ['a manual job with a real problem is attention', { job: { enabled: true, schedule: null }, health: health('Degraded', 'schedule_unavailable', 'no_verified_replica') }, 'attention'],
  ['a paused job is neutral although freshness is low', { job: { enabled: false, schedule }, health: health('Critical', 'backup_stale') }, 'paused'],
  ['a job with a paused schedule is paused', { job: { enabled: true, schedule: { ...schedule, enabled: false } }, health: health('Critical', 'backup_stale') }, 'paused'],
  ['a healthy job is current', {}, 'current'],
] satisfies [string, Partial<JobStatusInput>, string][])('%s', (_name, patch, expected) => {
  expect(jobStatus({ ...base, ...patch })).toBe(expected)
})
