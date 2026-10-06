import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { cancelArtifactDeletion, deleteArtifact, getArtifact, listArtifacts } from './api'
import { deletionInProgress } from './format'
import type { ArtifactFilters, BackupArtifact } from './types'

export function useArtifacts(tenantId: string, filters: ArtifactFilters) {
  return useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'list', filters],
    queryFn: ({ signal }) => listArtifacts(tenantId, filters, signal),
    enabled: !filters.from || !filters.to || filters.from <= filters.to,
    refetchInterval: (query) => query.state.data?.items.some(deletionInProgress) ? 5000 : false,
  })
}

export function useArtifact(tenantId: string, id: string) {
  return useQuery({
    queryKey: ['tenant', tenantId, 'backup-artifacts', 'detail', id],
    queryFn: ({ signal }) => getArtifact(tenantId, id, signal),
    enabled: Boolean(id),
    refetchInterval: (query) => query.state.data && deletionInProgress(query.state.data) ? 5000 : false,
  })
}

export function useArtifactActions(tenantId: string, id: string) {
  const queryClient = useQueryClient()
  async function updated(artifact: BackupArtifact) {
    queryClient.setQueryData(['tenant', tenantId, 'backup-artifacts', 'detail', id], artifact)
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-artifacts'] }),
      queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'dashboard'] }),
      ...(artifact.runId ? [queryClient.invalidateQueries({ queryKey: ['tenant', tenantId, 'backup-runs'] })] : []),
    ])
  }
  const requestDelete = useMutation({ mutationFn: () => deleteArtifact(tenantId, id), onSuccess: updated })
  const cancelDelete = useMutation({ mutationFn: () => cancelArtifactDeletion(tenantId, id), onSuccess: updated })
  return { requestDelete, cancelDelete }
}
