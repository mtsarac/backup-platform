import { apiRequest, tenantPath } from '@/lib/api/client'
import type { ArtifactFormat } from '@/features/artifact-encryption/types'
import type { ArtifactFilters, ArtifactList, BackupArtifact, ReplicaRepairOperation, StartReplicaRepairRequest } from './types'

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

export function startReplicaRepair(tenantId: string, id: string, request: StartReplicaRepairRequest) {
  return apiRequest<ReplicaRepairOperation>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/repairs`), { method: 'POST', json: request })
}

export function getReplicaRepair(tenantId: string, id: string, repairId: string, signal?: AbortSignal) {
  return apiRequest<ReplicaRepairOperation>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/repairs/${encodeURIComponent(repairId)}`), { signal })
}

export function cancelReplicaRepair(tenantId: string, id: string, repairId: string) {
  return apiRequest<ReplicaRepairOperation>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/repairs/${encodeURIComponent(repairId)}/cancel`), { method: 'POST' })
}

export function listReplicaRepairs(tenantId: string, id: string, signal?: AbortSignal) {
  return apiRequest<ReplicaRepairOperation[]>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/repairs`), { signal })
}

export interface DownloadAvailability {
  available: boolean
  reason: string | null
  replicas: { replicaId: string; storageProvider: string; storageTarget: string; available: boolean; reason: string | null }[]
  /** For `v2-bpe`, `checksum` and `sizeBytes` describe the stored ciphertext. */
  checksum: string
  sizeBytes: number
  format: ArtifactFormat
}
/** `decrypted` downloads carry the plaintext `sourceChecksum` and have no Content-Length; `sizeBytes` stays the stored ciphertext size. */
export interface PreparedDownload { sessionId: string; downloadUrl: string; filename: string; checksum: string; sizeBytes: number; decrypted: boolean }
export interface DownloadStatus {
  sessionId: string
  state: 'Preparing' | 'Ready' | 'Streaming' | 'Completed' | 'Failed' | 'Cancelled'
  failureCode?: string | null
  transferredBytes: number
}
export function getDownloadAvailability(tenantId: string, id: string, signal: AbortSignal) {
  return apiRequest<DownloadAvailability>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/download-availability`), { signal })
}
/** `decrypt` is only meaningful (and only sent) for encrypted artifacts: the default download is the stored file, unchanged. */
export function prepareDownload(tenantId: string, id: string, replicaId?: string, decrypt?: boolean) {
  return apiRequest<PreparedDownload>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/downloads`), { method: 'POST', json: { ...(replicaId ? { replicaId } : {}), ...(decrypt ? { decrypt: true } : {}) } })
}
export function getDownloadStatus(tenantId: string, id: string, sessionId: string, signal: AbortSignal) {
  return apiRequest<DownloadStatus>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(id)}/downloads/${encodeURIComponent(sessionId)}`), { signal })
}
