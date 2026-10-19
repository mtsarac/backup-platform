import { beforeEach, expect, it, vi } from 'vitest'
import type * as ClientModule from '@/lib/api/client'
import { apiRequest } from '@/lib/api/client'
import { setOwnerSecurityRecipient } from './api'

vi.mock('@/lib/api/client', async importOriginal => ({ ...await importOriginal<typeof ClientModule>(), apiRequest: vi.fn() }))
beforeEach(() => vi.clearAllMocks())

it('designates and removes the authenticated Owner security recipient through the tenant-scoped channel route', async () => {
  await setOwnerSecurityRecipient('tenant-a', 'channel/a', true)
  expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/notifications/channels/channel%2Fa/owner-security-recipient', { method: 'POST', json: { enabled: true } })
  await setOwnerSecurityRecipient('tenant-a', 'channel/a', false)
  expect(apiRequest).toHaveBeenLastCalledWith('tenants/tenant-a/notifications/channels/channel%2Fa/owner-security-recipient', { method: 'POST', json: { enabled: false } })
})
