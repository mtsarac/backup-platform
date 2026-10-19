import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { PageParams } from '@/features/platform/types'
import * as api from './api'
import type { BreakGlassFilters, CreateBreakGlassInput } from './types'

export const encryptionKeys = {
  wrapKeys: (tenantId: string) => ['platform', 'encryption', 'wrap-keys', tenantId] as const,
  breakGlassAll: ['platform', 'encryption', 'break-glass'] as const,
  breakGlassList: (filters: BreakGlassFilters) => ['platform', 'encryption', 'break-glass', 'list', filters] as const,
  breakGlass: (id: string) => ['platform', 'encryption', 'break-glass', 'detail', id] as const,
  tenant: (tenantId: string) => ['tenant', tenantId, 'encryption'] as const,
  tenantBreakGlass: (tenantId: string, params: PageParams) => ['tenant', tenantId, 'encryption', 'break-glass', params] as const,
}

export function useWrapKeys(tenantId: string) {
  return useQuery({ queryKey: encryptionKeys.wrapKeys(tenantId), queryFn: ({ signal }) => api.getWrapKeys(tenantId, signal), enabled: Boolean(tenantId) })
}

export function useWrapKeyActions(tenantId: string) {
  const client = useQueryClient()
  const refresh = () => client.invalidateQueries({ queryKey: encryptionKeys.wrapKeys(tenantId) })
  const rotate = useMutation({ mutationFn: () => api.rotateWrapKey(tenantId), onSuccess: refresh, retry: false })
  const retire = useMutation({ mutationFn: (version: number) => api.retireWrapKey(tenantId, version), onSuccess: refresh, retry: false })
  return { rotate, retire }
}

export function useTenantEncryption(tenantId: string) {
  return useQuery({ queryKey: encryptionKeys.tenant(tenantId), queryFn: ({ signal }) => api.getTenantEncryption(tenantId, signal), enabled: Boolean(tenantId) })
}

/** The PEM is public key material, but it is still cleared from the mutation cache once settled so it never lingers. */
export function useTenantRecoveryKeyActions(tenantId: string) {
  const client = useQueryClient()
  const refresh = () => client.invalidateQueries({ queryKey: encryptionKeys.tenant(tenantId) })
  const set = useMutation({ mutationFn: (publicKeyPem: string) => api.setTenantRecoveryKey(tenantId, publicKeyPem), onSuccess: view => client.setQueryData(encryptionKeys.tenant(tenantId), view), retry: false, gcTime: 0 })
  const remove = useMutation({ mutationFn: () => api.removeTenantRecoveryKey(tenantId), onSuccess: refresh, retry: false })
  return { set, remove }
}

export function useTenantBreakGlass(tenantId: string, params: PageParams) {
  return useQuery({ queryKey: encryptionKeys.tenantBreakGlass(tenantId, params), queryFn: ({ signal }) => api.getTenantBreakGlass(tenantId, params, signal), placeholderData: keepPreviousData })
}

export function useBreakGlassRequests(filters: BreakGlassFilters) {
  return useQuery({ queryKey: encryptionKeys.breakGlassList(filters), queryFn: ({ signal }) => api.getBreakGlassRequests(filters, signal), placeholderData: keepPreviousData })
}

export function useBreakGlassRequest(id: string) {
  return useQuery({
    queryKey: encryptionKeys.breakGlass(id), queryFn: ({ signal }) => api.getBreakGlassRequest(id, signal), enabled: Boolean(id),
    // A pending request is waiting for another admin and an approved one is racing its TTL: both must show the other side's action quickly.
    refetchInterval: query => ['Pending', 'Approved'].includes(query.state.data?.status ?? '') ? 10_000 : false,
  })
}

export function useCreateBreakGlass() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateBreakGlassInput) => api.createBreakGlassRequest(input), retry: false,
    onSuccess: request => { client.setQueryData(encryptionKeys.breakGlass(request.id), request); void client.invalidateQueries({ queryKey: encryptionKeys.breakGlassAll }) },
  })
}

export function useBreakGlassDecision(id: string) {
  const client = useQueryClient()
  const updated = (request: Awaited<ReturnType<typeof api.approveBreakGlass>>) => { client.setQueryData(encryptionKeys.breakGlass(id), request); void client.invalidateQueries({ queryKey: encryptionKeys.breakGlassAll }) }
  // A conflict (already decided, expired) means the cached view is stale: reload it so the buttons match the server.
  const stale = () => { void client.invalidateQueries({ queryKey: encryptionKeys.breakGlass(id) }) }
  const approve = useMutation({ mutationFn: () => api.approveBreakGlass(id), onSuccess: updated, onError: stale, retry: false })
  const deny = useMutation({ mutationFn: (reason: string) => api.denyBreakGlass(id, reason), onSuccess: updated, onError: stale, retry: false })
  return { approve, deny }
}
