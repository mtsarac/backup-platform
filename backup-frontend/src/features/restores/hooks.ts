import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useVisibleTab } from '@/hooks/use-visible-tab'
import { useTenant } from '@/features/tenancy/use-tenant'
import { cancelRestore, getRestore, getRestores, getRestoreTargets, removeRollbackCopy, retryRestoreCleanup, startRestore, startRestoreDiscovery, startRestorePrecheck, startRestoreVerification } from './api'
import { isActiveRestore } from './presentation'
import type { RestoreFilters, RestoreView, StartRestoreDiscoveryVariables, StartRestorePrecheckVariables, StartRestoreVariables, StartRestoreVerificationVariables } from './types'

export const restoreQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'restores'] as const,
  list: (tenantId: string, filters: RestoreFilters) => ['tenant', tenantId, 'restores', 'list', filters] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'restores', 'detail', id] as const,
  targets: (tenantId: string, artifactId: string) => ['tenant', tenantId, 'backup-artifacts', artifactId, 'restore-targets'] as const,
}
export function useRestores(filters: RestoreFilters = {}) {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  return useQuery({
    queryKey: restoreQueryKeys.list(tenantId, filters),
    queryFn: ({ signal }) => getRestores(tenantId, filters, signal),
    refetchInterval: (query) => visible && !query.state.error && query.state.data?.items.some((restore) => isActiveRestore(restore.status)) ? 5000 : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
  })
}
export function useRestore(id: string) {
  const { tenantId } = useTenant()
  const visible = useVisibleTab()
  return useQuery({
    queryKey: restoreQueryKeys.detail(tenantId, id),
    queryFn: ({ signal }) => getRestore(tenantId, id, signal),
    enabled: id.length > 0,
    refetchInterval: (query) => visible && !query.state.error && query.state.data && isActiveRestore(query.state.data.status) ? 5000 : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
  })
}
export function useRestoreTargets(artifactId: string) {
  const { tenantId } = useTenant()
  return useQuery({
    queryKey: restoreQueryKeys.targets(tenantId, artifactId),
    queryFn: ({ signal }) => getRestoreTargets(tenantId, artifactId, signal),
    enabled: artifactId.length > 0,
    staleTime: 10000,
  })
}
export function useStartRestoreDiscovery() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ artifactId, request, idempotencyKey }: StartRestoreDiscoveryVariables) => startRestoreDiscovery(tenantId, artifactId, request, idempotencyKey),
    retry: false,
    onSuccess: (restore) => {
      if (restore?.id) client.setQueryData(restoreQueryKeys.detail(tenantId, restore.id), restore)
    },
  })
}
export function useStartRestore() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ artifactId, request, idempotencyKey }: StartRestoreVariables) => startRestore(tenantId, artifactId, request, idempotencyKey),
    retry: false,
    onSuccess: async (restore) => {
      if (restore?.id) client.setQueryData(restoreQueryKeys.detail(tenantId, restore.id), restore)
      await Promise.all([
        client.invalidateQueries({ queryKey: restoreQueryKeys.all(tenantId) }),
        client.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts'] }),
      ])
    },
  })
}
export function useStartRestorePrecheck() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ artifactId, request, idempotencyKey }: StartRestorePrecheckVariables) => startRestorePrecheck(tenantId, artifactId, request, idempotencyKey),
    retry: false,
    onSuccess: (restore) => {
      if (restore?.id) client.setQueryData(restoreQueryKeys.detail(tenantId, restore.id), restore)
    },
  })
}
export function useCancelRestore(id: string) {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: () => cancelRestore(tenantId, id),
    retry: false,
    onSuccess: async (restore: RestoreView) => {
      // The returned operation stays Running until the Agent acknowledges cancellation.
      client.setQueryData(restoreQueryKeys.detail(tenantId, id), restore)
      await Promise.all([
        client.invalidateQueries({ queryKey: restoreQueryKeys.all(tenantId) }),
        client.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts'] }),
      ])
    },
  })
}
export function useStartRestoreVerification() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ artifactId, request, idempotencyKey }: StartRestoreVerificationVariables) => startRestoreVerification(tenantId, artifactId, request, idempotencyKey),
    retry: false,
    onSuccess: async (restore) => {
      if (restore?.id) client.setQueryData(restoreQueryKeys.detail(tenantId, restore.id), restore)
      await client.invalidateQueries({ queryKey: restoreQueryKeys.all(tenantId) })
    },
  })
}
function useCleanupMutation(id: string, call: (tenantId: string, variables: string) => Promise<RestoreView>) {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: (variables: string) => call(tenantId, variables),
    retry: false,
    onSuccess: async (restore: RestoreView) => {
      client.setQueryData(restoreQueryKeys.detail(tenantId, id), restore)
      await client.invalidateQueries({ queryKey: restoreQueryKeys.all(tenantId) })
    },
  })
}
export function useRetryRestoreCleanup(id: string) {
  return useCleanupMutation(id, (tenantId) => retryRestoreCleanup(tenantId, id))
}
export function useRemoveRollbackCopy(id: string) {
  return useCleanupMutation(id, (tenantId, confirmDatabaseName) => removeRollbackCopy(tenantId, id, confirmDatabaseName))
}
