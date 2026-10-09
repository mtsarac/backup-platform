import { beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest, ApiError } from '@/lib/api/client'
import { errorMessage } from '@/lib/api/failure-messages'
import type * as ClientModule from '@/lib/api/client'
import { closeSession, getSupportArtifact, getSupportArtifacts, getSupportDownloads, getSupportRestoreTargets, openSession, precheckSupportRestore, revokeSession, startSupportRestore } from './api'

vi.mock('@/lib/api/client', async importOriginal => ({ ...await importOriginal<typeof ClientModule>(), apiRequest: vi.fn() }))
beforeEach(() => vi.mocked(apiRequest).mockReset())
describe('support HTTP contract', () => {
  it('opens one tenant with explicit scoped permissions and required Idempotency-Key header', async () => {
    const input = { tenantId: 'tenant-a', reason: 'Customer request', permissions: ['ViewBackupMetadata' as const], durationMinutes: 60 }
    await openSession(input, 'request-key')
    expect(apiRequest).toHaveBeenCalledWith('platform/support-sessions', { method: 'POST', json: input, headers: { 'Idempotency-Key': 'request-key' } })
  })
  it('uses only support-scoped artifact/data/download routes, not tenant hooks or hosts', async () => {
    const signal = new AbortController().signal
    await getSupportArtifacts('support/a', { jobId: 'job-a', page: 2, pageSize: 25 }, signal)
    await getSupportArtifact('support/a', 'artifact/a', signal)
    await getSupportDownloads('support/a', { page: 3, pageSize: 25 }, signal)
    await getSupportRestoreTargets('support/a', 'artifact/a', signal)
    expect(apiRequest).toHaveBeenNthCalledWith(1, 'platform/support-sessions/support%2Fa/artifacts?jobId=job-a&page=2&pageSize=25', { signal })
    expect(apiRequest).toHaveBeenNthCalledWith(2, 'platform/support-sessions/support%2Fa/artifacts/artifact%2Fa', { signal })
    expect(apiRequest).toHaveBeenNthCalledWith(3, 'platform/support-sessions/support%2Fa/downloads?page=3&pageSize=25', { signal })
    expect(apiRequest).toHaveBeenNthCalledWith(4, 'platform/support-sessions/support%2Fa/artifacts/artifact%2Fa/restore-targets', { signal })
  })
  it('precheck/start submit targetJobId only, omit mode/credentials/hosts, and Start includes precheckId', async () => {
    const input = { targetJobId: 'job-a', databaseName: 'NewDatabase', options: { verifyChecksum: true, verifyBackup: true } }
    await precheckSupportRestore('support-a', 'artifact-a', input, 'precheck-key')
    await startSupportRestore('support-a', 'artifact-a', { ...input, precheckId: 'precheck-a' }, 'restore-key')
    expect(apiRequest).toHaveBeenNthCalledWith(1, 'platform/support-sessions/support-a/artifacts/artifact-a/restore-precheck', { method: 'POST', json: input, headers: { 'Idempotency-Key': 'precheck-key' } })
    expect(apiRequest).toHaveBeenNthCalledWith(2, 'platform/support-sessions/support-a/artifacts/artifact-a/restore', { method: 'POST', json: { ...input, precheckId: 'precheck-a' }, headers: { 'Idempotency-Key': 'restore-key' } })
  })
  it('close POST has no reason body while revoke POST includes its reason', async () => {
    await closeSession('support-a')
    await revokeSession('support-a', 'Customer requested')
    expect(apiRequest).toHaveBeenNthCalledWith(1, 'platform/support-sessions/support-a/close', { method: 'POST' })
    expect(apiRequest).toHaveBeenNthCalledWith(2, 'platform/support-sessions/support-a/revoke', { method: 'POST', json: { reason: 'Customer requested' } })
  })
  it.each([
    ['session_expired', 'süresi doldu'], ['session_closed', 'kapatıldı'], ['session_revoked', 'geri alındı'],
    ['permission_missing', 'gereken izin yok'], ['not_session_owner', 'oturumunu açan PlatformAdmin'], ['replace_forbidden', 'mevcut Database üzerine yazılamaz'],
  ])('maps 403 %s to specific Turkish text without backend exception details', (reason, expected) => {
    const error = new ApiError(403, { error: 'support_access_denied', reason })
    expect(errorMessage(error)).toContain(expected)
  })
  it('preserves active_session_exists sessionId but never renders arbitrary server exception text', () => {
    const error = new ApiError(409, { error: 'active_session_exists', sessionId: 'support-existing' })
    expect(error.sessionId).toBe('support-existing')
    expect(errorMessage(error)).toContain('zaten etkin bir destek oturumunuz')
    expect(errorMessage(new ApiError(503, { error: 'support_unavailable' }))).toContain('audit kaydı')
    expect(errorMessage(new ApiError(403, { error: 'support_access_denied', reason: 'System.Exception: secret' }))).toBe('İşlem başarısız oldu.')
  })
})
