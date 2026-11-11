import { beforeEach, describe, expect, it, vi } from 'vitest'
import type * as ClientModule from '@/lib/api/client'
import { apiRequest } from '@/lib/api/client'
import { prepareDownload } from '@/features/artifacts/api'
import * as api from './api'

vi.mock('@/lib/api/client', async (importOriginal) => {
  const original = await importOriginal<typeof ClientModule>()
  return { ...original, apiRequest: vi.fn() }
})

beforeEach(() => vi.clearAllMocks())

describe('wrap key API (platform)', () => {
  it('lists, rotates and retires a tenant key under the encoded tenant route', async () => {
    const signal = new AbortController().signal
    await api.getWrapKeys('tenant/a', signal)
    expect(apiRequest).toHaveBeenLastCalledWith('platform/tenants/tenant%2Fa/wrap-keys', { signal })
    await api.rotateWrapKey('tenant-a')
    expect(apiRequest).toHaveBeenLastCalledWith('platform/tenants/tenant-a/wrap-keys/rotate', { method: 'POST' })
    await api.retireWrapKey('tenant-a', 2)
    expect(apiRequest).toHaveBeenLastCalledWith('platform/tenants/tenant-a/wrap-keys/2/retire', { method: 'POST' })
  })
})

describe('tenant encryption API', () => {
  it('reads the overview and replaces or removes the recovery key', async () => {
    const signal = new AbortController().signal
    await api.getTenantEncryption('tenant-a', signal)
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/encryption', { signal })
    await api.setTenantRecoveryKey('tenant-a', '-----BEGIN PUBLIC KEY-----')
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/encryption/recovery-key', { method: 'PUT', json: { publicKeyPem: '-----BEGIN PUBLIC KEY-----' } })
    await api.removeTenantRecoveryKey('tenant-a')
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/encryption/recovery-key', { method: 'DELETE' })
  })
})

describe('artifact download request', () => {
  it('only sends decrypt when it is chosen, so the default stays the stored file', async () => {
    await prepareDownload('tenant-a', 'artifact-a')
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/backup-artifacts/artifact-a/downloads', { method: 'POST', json: {} })
    await prepareDownload('tenant-a', 'artifact-a', 'replica-a', false)
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/backup-artifacts/artifact-a/downloads', { method: 'POST', json: { replicaId: 'replica-a' } })
    await prepareDownload('tenant-a', 'artifact-a', undefined, true)
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/backup-artifacts/artifact-a/downloads', { method: 'POST', json: { decrypt: true } })
  })
})
