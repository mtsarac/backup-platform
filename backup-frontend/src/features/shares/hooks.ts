import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createShare, getShare, listShares } from './api'
import type { CreateShareRequest } from './types'

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
