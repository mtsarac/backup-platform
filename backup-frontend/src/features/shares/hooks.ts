import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createPlatformShare, createShare, extendShare, getPlatformShare, getShare, listPlatformShares, listShares, revokeShare } from './api'
import type { CreatePlatformShareRequest, CreateShareRequest, ExtendShareRequest, RevokeShareRequest } from './types'

export function useShares(tenantId: string, artifactId?: string) {
  return useQuery({
    queryKey: ['shares', tenantId, artifactId ?? 'all'],
    queryFn: ({ signal }) => listShares(tenantId, artifactId, signal),
    enabled: Boolean(tenantId),
  })
}

export function useShare(tenantId: string, shareId: string) {
  return useQuery({
    queryKey: ['share', tenantId, shareId],
    queryFn: ({ signal }) => getShare(tenantId, shareId, signal),
    enabled: Boolean(tenantId && shareId),
  })
}

export function useCreateShare(tenantId: string, artifactId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: CreateShareRequest) => createShare(tenantId, artifactId, request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['shares', tenantId] })
    },
  })
}

export function usePlatformShares(params?: { tenantId?: string; status?: string }) {
  return useQuery({
    queryKey: ['platform-shares', params?.tenantId ?? '', params?.status ?? ''],
    queryFn: ({ signal }) => listPlatformShares(params, signal),
  })
}

export function usePlatformShare(shareId: string) {
  return useQuery({
    queryKey: ['platform-share', shareId],
    queryFn: ({ signal }) => getPlatformShare(shareId, signal),
    enabled: Boolean(shareId),
  })
}

export function useCreatePlatformShare() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: CreatePlatformShareRequest) => createPlatformShare(request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['platform-shares'] })
      void queryClient.invalidateQueries({ queryKey: ['shares'] })
    },
  })
}

export function useExtendShare(tenantId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ shareId, request }: { shareId: string; request: ExtendShareRequest }) =>
      extendShare(tenantId, shareId, request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['shares', tenantId] })
      void queryClient.invalidateQueries({ queryKey: ['share', tenantId] })
      void queryClient.invalidateQueries({ queryKey: ['platform-shares'] })
    },
  })
}

export function useRevokeShare(tenantId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ shareId, request }: { shareId: string; request?: RevokeShareRequest }) =>
      revokeShare(tenantId, shareId, request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['shares', tenantId] })
      void queryClient.invalidateQueries({ queryKey: ['share', tenantId] })
      void queryClient.invalidateQueries({ queryKey: ['platform-shares'] })
    },
  })
}
