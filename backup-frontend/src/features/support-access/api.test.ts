import { beforeEach, describe, expect, it, vi } from 'vitest'
import type * as ClientModule from '@/lib/api/client'
import { apiRequest } from '@/lib/api/client'
import { getSupportAccessActivity, getSupportAccessSession, getSupportAccessSessions } from './api'

vi.mock('@/lib/api/client', async (importOriginal) => {
  const original = await importOriginal<typeof ClientModule>()
  return { ...original, apiRequest: vi.fn() }
})

beforeEach(() => vi.clearAllMocks())

describe('tenant support access API', () => {
  it('uses the tenant-scoped paged list endpoint and forwards cancellation', async () => {
    const signal = new AbortController().signal
    await getSupportAccessSessions('tenant-a', { page: 2, pageSize: 25 }, signal)
    expect(apiRequest).toHaveBeenCalledWith('tenants/tenant-a/support-access/sessions?page=2&pageSize=25', { signal })
  })

  it('encodes session identifiers for detail and paged activity endpoints', async () => {
    const signal = new AbortController().signal
    await getSupportAccessSession('tenant-b', 'session/a', signal)
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-b/support-access/sessions/session%2Fa', { signal })
    await getSupportAccessActivity('tenant-b', 'session/a', { page: 3, pageSize: 25 }, signal)
    expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-b/support-access/sessions/session%2Fa/activity?page=3&pageSize=25', { signal })
  })
})
