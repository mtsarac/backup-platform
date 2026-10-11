import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { cancelArtifactDeletion, deleteArtifact, getArtifact, listArtifacts, startReplicaRepair, getReplicaRepair, cancelReplicaRepair } from './api'
import { storageQueryKeys, useRefreshQuotaWhenFinished } from '@/features/storage/hooks'
import { deletionInProgress } from './format'
import type { ArtifactFilters, BackupArtifact, StartReplicaRepairRequest } from './types'

export function useArtifacts(tenantId: string, filters: ArtifactFilters) {
  const query = useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'list', filters],
    queryFn: ({ signal }) => listArtifacts(tenantId, filters, signal),
    enabled: !filters.from || !filters.to || filters.from <= filters.to,
    refetchInterval: (query) => query.state.data?.items.some(deletionInProgress) ? 5000 : false,
  })
  // Replica bytes leave the quota when the physical deletion is verified, not when it is requested.
  useRefreshQuotaWhenFinished(tenantId, Boolean(query.data?.items.some(deletionInProgress)))
  return query
}

export function useArtifact(tenantId: string, id: string) {
  const query = useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'detail', id],
    queryFn: ({ signal }) => getArtifact(tenantId, id, signal),
    enabled: Boolean(id),
    refetchInterval: (query) => query.state.data && deletionInProgress(query.state.data) ? 5000 : false,
  })
  useRefreshQuotaWhenFinished(tenantId, Boolean(query.data && deletionInProgress(query.data)))
  return query
}

export function useArtifactActions(tenantId: string, id: string) {
  const queryClient = useQueryClient()
  async function updated(artifact: BackupArtifact) {
    queryClient.setQueryData(['tenant', tenantId, 'backup-artifacts', 'detail', id], artifact)
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts'] }),
      queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
      queryClient.invalidateQueries({ queryKey: storageQueryKeys.quota(tenantId) }),
      ...(artifact.runId ? [queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-runs'] })] : []),
    ])
  }
  const requestDelete = useMutation({ mutationFn: () => deleteArtifact(tenantId, id), onSuccess: updated })
  const cancelDelete = useMutation({ mutationFn: () => cancelArtifactDeletion(tenantId, id), onSuccess: updated })
  return { requestDelete, cancelDelete }
}

export function useReplicaRepair(tenantId: string, id: string) {
  const queryClient = useQueryClient()
  const startRepair = useMutation({
    mutationFn: (request: StartReplicaRepairRequest) => startReplicaRepair(tenantId, id, request),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts', 'detail', id] }),
        queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts', 'repairs', id] }),
      ])
    },
  })
  const cancelRepair = useMutation({
    mutationFn: (repairId: string) => cancelReplicaRepair(tenantId, id, repairId),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts', 'detail', id] }),
        queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts', 'repairs', id] }),
      ])
    },
  })
  return { startRepair, cancelRepair }
}

export function useRepairStatus(tenantId: string, artifactId: string, repairId: string | null) {
  return useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'repairs', artifactId, repairId],
    queryFn: ({ signal }) => getReplicaRepair(tenantId, artifactId, repairId!, signal),
    enabled: Boolean(repairId),
    refetchInterval: (query) => {
      const status = query.state.data?.status
      return status === 'Pending' || status === 'InProgress' ? 2000 : false
    },
  })
}
