import { beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest } from '@/lib/api/client'
import { assignBandwidthProfile, deleteBandwidthProfile, getBandwidthProfiles, getUploadBandwidth, saveBandwidthProfile, setUploadBandwidth } from './api'

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

  it('calls getBandwidthProfiles', async () => {
    await getBandwidthProfiles('tenant-a')
    expect(vi.mocked(apiRequest).mock.calls[0][0]).toBe('tenants/tenant-a/bandwidth-profiles')
  })

  it('calls saveBandwidthProfile for create and update', async () => {
    await saveBandwidthProfile('tenant-a', { name: 'P1', timeZoneId: 'UTC', windows: [] })
    expect(vi.mocked(apiRequest).mock.calls[0]).toEqual(['tenants/tenant-a/bandwidth-profiles', { method: 'POST', json: { name: 'P1', timeZoneId: 'UTC', windows: [] } }])

    await saveBandwidthProfile('tenant-a', { id: 'prof-1', name: 'P1', timeZoneId: 'UTC', windows: [] })
    expect(vi.mocked(apiRequest).mock.calls[1]).toEqual(['tenants/tenant-a/bandwidth-profiles/prof-1', { method: 'PUT', json: { name: 'P1', timeZoneId: 'UTC', windows: [] } }])
  })

  it('calls deleteBandwidthProfile', async () => {
    await deleteBandwidthProfile('tenant-a', 'prof-1')
    expect(vi.mocked(apiRequest).mock.calls[0]).toEqual(['tenants/tenant-a/bandwidth-profiles/prof-1', { method: 'DELETE' }])
  })

  it('calls assignBandwidthProfile', async () => {
    await assignBandwidthProfile('tenant-a', { scope: 'job', id: 'job-1' }, 'prof-1')
    expect(vi.mocked(apiRequest).mock.calls[0]).toEqual(['tenants/tenant-a/upload-bandwidth/jobs/job-1/profile', { method: 'PUT', json: { profileId: 'prof-1' } }])
  })
})
