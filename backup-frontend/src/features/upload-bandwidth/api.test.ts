import { beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest } from '@/lib/api/client'
import { getUploadBandwidth, setUploadBandwidth } from './api'

vi.mock('@/lib/api/client', async importOriginal => ({ ...(await importOriginal()), apiRequest: vi.fn().mockResolvedValue({}) }))
beforeEach(() => vi.mocked(apiRequest).mockClear())

describe('upload bandwidth api', () => {
  it('reads the tenant-scoped document', async () => {
    await getUploadBandwidth('tenant-a')
    expect(vi.mocked(apiRequest).mock.calls[0][0]).toBe('tenants/tenant-a/upload-bandwidth')
  })
  it.each([
    [{ scope: 'tenant' } as const, 'tenants/tenant-a/upload-bandwidth/tenant'],
    [{ scope: 'agent', id: 'agent/1' } as const, 'tenants/tenant-a/upload-bandwidth/agents/agent%2F1'],
    [{ scope: 'job', id: 'job-1' } as const, 'tenants/tenant-a/upload-bandwidth/jobs/job-1'],
  ])('PUTs %j and sends null for Unlimited', async (target, path) => {
    await setUploadBandwidth('tenant-a', target, null)
    expect(vi.mocked(apiRequest).mock.calls[0]).toEqual([path, { method: 'PUT', json: { bytesPerSecond: null } }])
  })
})
