import { apiRequest, tenantPath } from '@/lib/api/client'
import type { TenantQuota } from './types'

export function getTenantStorageQuota(tenantId: string, signal?: AbortSignal) {
  return apiRequest<TenantQuota>(tenantPath(tenantId, 'storage-quota'), { signal })
}
