import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { STORAGE_POLL_MS } from '@/features/storage/hooks'
import type { CreateQuotaPolicyInput, QuotaPolicyInput, StorageUsageFilters } from '@/features/storage/types'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { platformKeys } from '../hooks'
import {
  createQuotaPolicy, getPlatformTenantQuota, getQuotaPolicies, getStorageUsage, rebuildUsage, reconcileUsage, updateQuotaPolicy,
} from './api'

const USAGE_LIST_POLL_MS = 30_000

type Client = ReturnType<typeof useQueryClient>

/** Tenants' own caches (a PlatformAdmin may also be a member): the quota of one tenant, or of all of them. */
function tenantQuotaPredicate(tenantId?: string) {
  return (query: { queryKey: readonly unknown[] }) => query.queryKey[0] === 'tenant' && query.queryKey[2] === 'storage-quota' && (tenantId === undefined || query.queryKey[1] === tenantId)
}

/** Every quota-shaped platform view (list, per-tenant standing, policies) plus the tenants' own copies. `tenantId` omitted = all tenants. */
export function invalidateStorageViews(client: Client, tenantId?: string) {
  return Promise.all([
    client.invalidateQueries({ queryKey: platformKeys.storage }),
    client.invalidateQueries({ predicate: tenantQuotaPredicate(tenantId) }),
  ])
}

export function useStorageUsage(filters: StorageUsageFilters) {
  const visible = useVisibleTab()
  return useQuery({
    queryKey: platformKeys.storageUsageList(filters),
    queryFn: ({ signal }) => getStorageUsage(filters, signal),
    placeholderData: keepPreviousData,
    staleTime: 0,
    refetchOnMount: 'always',
    refetchInterval: (query) => visible && !query.state.error ? USAGE_LIST_POLL_MS : false,
    refetchIntervalInBackground: false,
  })
}

export function usePlatformTenantQuota(tenantId: string) {
  const visible = useVisibleTab()
  return useQuery({
    queryKey: platformKeys.tenantQuota(tenantId),
    queryFn: ({ signal }) => getPlatformTenantQuota(tenantId, signal),
    enabled: Boolean(tenantId),
    staleTime: 0,
    refetchOnMount: 'always',
    refetchInterval: (query) => visible && !query.state.error ? STORAGE_POLL_MS : false,
    refetchIntervalInBackground: false,
  })
}

export function useQuotaPolicies() {
  return useQuery({ queryKey: platformKeys.quotaPolicies, queryFn: ({ signal }) => getQuotaPolicies(signal), staleTime: 0, refetchOnMount: 'always' })
}

/** Reconcile and rebuild rewrite the projection and are audited: refresh after success and failure alike. */
function useUsageOperation(tenantId: string, run: (tenantId: string) => ReturnType<typeof reconcileUsage>) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: () => run(tenantId),
    onSettled: () => Promise.all([invalidateStorageViews(client, tenantId), client.invalidateQueries({ queryKey: platformKeys.audit })]),
  })
}

export function useReconcileUsage(tenantId: string) {
  return useUsageOperation(tenantId, reconcileUsage)
}

export function useRebuildUsage(tenantId: string) {
  return useUsageOperation(tenantId, rebuildUsage)
}

/** A policy change can change how every tenant that uses it is counted, so all quota views and plans refresh. */
function refreshAfterPolicyChange(client: Client) {
  return Promise.all([
    invalidateStorageViews(client),
    client.invalidateQueries({ queryKey: platformKeys.plans }),
    client.invalidateQueries({ queryKey: platformKeys.audit }),
  ])
}

export function useCreateQuotaPolicy() {
  const client = useQueryClient()
  return useMutation({ mutationFn: (input: CreateQuotaPolicyInput) => createQuotaPolicy(input), onSettled: () => refreshAfterPolicyChange(client) })
}

export function useUpdateQuotaPolicy(policyId: string) {
  const client = useQueryClient()
  return useMutation({ mutationFn: (input: QuotaPolicyInput) => updateQuotaPolicy(policyId, input), onSettled: () => refreshAfterPolicyChange(client) })
}
