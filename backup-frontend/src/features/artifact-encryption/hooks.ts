import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import * as api from './api'

export const encryptionKeys = {
  wrapKeys: (tenantId: string) => ['platform', 'encryption', 'wrap-keys', tenantId] as const,
  tenant: (tenantId: string) => ['tenant', tenantId, 'encryption'] as const,
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

