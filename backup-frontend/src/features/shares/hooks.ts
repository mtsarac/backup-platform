import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  createPlatformShare,
  createShare,
  extendShare,
  getAuditRetention,
  getPlatformShare,
  getShare,
  listDownloadAttempts,
  listPlatformShares,
  listShares,
  revokeShare,
  updateAuditRetention,
} from './api'
import type {
  CreatePlatformShareRequest,
  CreateShareRequest,
  DownloadAttemptQuery,
  DownloadAttemptScope,
  ExtendShareRequest,
  RevokeShareRequest,
} from './types'

/** Every download-history query: a download leaves a new attempt behind, so the history is refreshed after one. */
export const downloadAttemptsKey = ['download-attempts'] as const

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

export function useDownloadAttempts(scope: DownloadAttemptScope, list: DownloadAttemptQuery) {
  return useQuery({
    queryKey: [...downloadAttemptsKey, scope, list],
    queryFn: ({ signal }) => listDownloadAttempts(scope, list, signal),
    enabled: scope.route === 'platform' || Boolean(scope.tenantId),
    placeholderData: keepPreviousData,
  })
}

export function useAuditRetention() {
  return useQuery({
    queryKey: ['audit-retention'],
    queryFn: ({ signal }) => getAuditRetention(signal),
  })
}

export function useUpdateAuditRetention() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (retentionDays: number) => updateAuditRetention(retentionDays),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['audit-retention'] })
    },
  })
}
