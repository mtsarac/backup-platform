import { beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest } from '@/lib/api/client'
import { saveBackupJob } from './api'
import type { BackupJobInput } from './types'

vi.mock('@/lib/api/client', async importOriginal => ({ ...(await importOriginal()), apiRequest: vi.fn().mockResolvedValue({ id: 'job-a' }) }))

function sentJson(): Record<string, unknown> {
  const json = vi.mocked(apiRequest).mock.calls[0][1]?.json
  if (!json || typeof json !== 'object') throw new Error('json body missing')
  return Object.fromEntries(Object.entries(json))
}
const input: BackupJobInput = {
  name: 'Nightly', agentId: 'agent-a', overlapPolicy: 'Skip', enabled: true,
  configuration: { engine: 'mssql', database: 'ERP', host: 'sql', port: null, integratedSecurity: true, databaseCredentialReference: null, encrypt: true, trustServerCertificate: false, stagingReference: 'default', storageProvider: 'local', storagePrefix: '', storageExecutionLocation: 'Agent', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred' },
  retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 300, backoff: 'Exponential', jitter: 0.2 },
  schedule: { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip', nextOccurrenceAt: '2030-01-01T00:00:00Z' },
  retention: { enabled: true, keepForSeconds: 100, deleteGracePeriodSeconds: 10 },
}
const schedule = { enabled: true, cronExpression: '0 2 * * *', timeZone: 'Europe/Istanbul', missedRunGraceSeconds: 3600, overlapPolicy: 'Skip' }
beforeEach(() => vi.mocked(apiRequest).mockClear())

describe('saveBackupJob', () => {
  it('düzenlemede tek PUT ile zamanlama, saklama ve enabled gönderir', async () => {
    await saveBackupJob('tenant-a', input, 'job-a')
    expect(apiRequest).toHaveBeenCalledTimes(1)
    const [path, options] = vi.mocked(apiRequest).mock.calls[0]
    expect(path).toContain('backup-jobs/job-a')
    expect(options).toMatchObject({ method: 'PUT', json: { ...input, schedule } })
    expect(sentJson()).not.toHaveProperty('removeSchedule')
  })
  it('Operator için retention göndermez', async () => {
    await saveBackupJob('tenant-a', { ...input, retention: undefined }, 'job-a')
    expect(apiRequest).toHaveBeenCalledTimes(1)
    expect(sentJson().retention).toBeUndefined()
  })
  it('zamanlama kapalıyken removeSchedule: true gönderir', async () => {
    await saveBackupJob('tenant-a', { ...input, schedule: null }, 'job-a')
    expect(apiRequest).toHaveBeenCalledTimes(1)
    expect(sentJson().removeSchedule).toBe(true)
    expect(sentJson()).not.toHaveProperty('schedule')
  })
  it('pasif iş oluşturma tek POST ve enabled:false gönderir', async () => {
    await saveBackupJob('tenant-a', { ...input, enabled: false })
    expect(apiRequest).toHaveBeenCalledTimes(1)
    const [path, options] = vi.mocked(apiRequest).mock.calls[0]
    expect(path).toMatch(/backup-jobs$/)
    expect(options).toMatchObject({ method: 'POST', json: { enabled: false, schedule, retention: input.retention } })
  })
})
