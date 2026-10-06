import { apiRequest, tenantPath } from '@/lib/api/client'
import type { ArtifactFilters, ArtifactList, BackupArtifact } from './types'

export function listArtifacts(tenantId: string, filters: ArtifactFilters, signal: AbortSignal) {
  const search = new URLSearchParams({ page: String(filters.page), pageSize: String(filters.pageSize) })
  if (filters.state) search.set('state', filters.state)
  if (filters.from) search.set('from', new Date(`${filters.from}T00:00:00`).toISOString())
  if (filters.to) search.set('to', new Date(`${filters.to}T23:59:59.999`).toISOString())
  return apiRequest<ArtifactList>(`${tenantPath(tenantId, 'backup-artifacts')}?${search}`, { signal })
}

export function getArtifact(tenantId: string, id: string, signal: AbortSignal) {
  return apiRequest<BackupArtifact>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}`), { signal })
}

export function deleteArtifact(tenantId: string, id: string) {
  return apiRequest<BackupArtifact>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}`), { method: 'DELETE' })
}

export function cancelArtifactDeletion(tenantId: string, id: string) {
  return apiRequest<BackupArtifact>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/cancel-delete`), { method: 'POST' })
}
