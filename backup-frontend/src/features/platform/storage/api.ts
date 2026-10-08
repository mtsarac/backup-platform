import { apiRequest } from '@/lib/api/client'
import type { PagedResult } from '../types'
import { query } from '../api'
import type {
  CreateQuotaPolicyInput, PlatformTenantQuota, QuotaPolicy, QuotaPolicyInput, QuotaPolicyUpdateResult, StorageUsageFilters,
  StorageUsageRow, UsageReconciliation,
} from '@/features/storage/types'

const tenantUrl = (tenantId: string) => `platform/tenants/${encodeURIComponent(tenantId)}`

/** One paginated read for every tenant: never one request per tenant. */
export function getStorageUsage(filters: StorageUsageFilters, signal?: AbortSignal) {
  return apiRequest<PagedResult<StorageUsageRow>>(`platform/storage-usage${query(filters)}`, { signal })
}

export function getPlatformTenantQuota(tenantId: string, signal?: AbortSignal) {
  return apiRequest<PlatformTenantQuota>(`${tenantUrl(tenantId)}/quota`, { signal })
}

export function reconcileUsage(tenantId: string) {
  return apiRequest<UsageReconciliation>(`${tenantUrl(tenantId)}/storage-usage/reconcile`, { method: 'POST' })
}

export function rebuildUsage(tenantId: string) {
  return apiRequest<UsageReconciliation>(`${tenantUrl(tenantId)}/storage-usage/rebuild`, { method: 'POST' })
}

export function getQuotaPolicies(signal?: AbortSignal) {
  return apiRequest<{ items: QuotaPolicy[] }>('platform/quota-policies', { signal })
}

export function createQuotaPolicy(input: CreateQuotaPolicyInput) {
  return apiRequest<QuotaPolicy>('platform/quota-policies', { method: 'POST', json: input })
}

export function updateQuotaPolicy(policyId: string, input: QuotaPolicyInput) {
  return apiRequest<QuotaPolicyUpdateResult>(`platform/quota-policies/${encodeURIComponent(policyId)}`, { method: 'PUT', json: input })
}
