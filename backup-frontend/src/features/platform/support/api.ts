import { apiRequest } from '@/lib/api/client'
import { query } from '@/features/platform/api'
import type { PageParams, PagedResult } from '@/features/platform/types'
import type { DownloadStatus, PreparedDownload } from '@/features/artifacts/api'
import type { RestoreView } from '@/features/restores/types'
import type { OpenSupportSessionInput, SupportArtifact, SupportAuditEntry, SupportDataFilters, SupportDownload, SupportDownloadAvailability, SupportJob, SupportPrecheckInput, SupportRestoreInput, SupportRestoreTargets, SupportRun, SupportSession, SupportSessionFilters } from './types'

const root = 'platform/support-sessions'
const sessionPath = (id: string) => `${root}/${encodeURIComponent(id)}`
const artifactPath = (id: string, artifactId: string) => `${sessionPath(id)}/artifacts/${encodeURIComponent(artifactId)}`
export const getSessions = (filters: SupportSessionFilters, signal?: AbortSignal) => apiRequest<PagedResult<SupportSession>>(`${root}${query(filters)}`, { signal })
export const getSession = (id: string, signal?: AbortSignal) => apiRequest<SupportSession>(sessionPath(id), { signal })
export const openSession = (input: OpenSupportSessionInput, idempotencyKey: string) => apiRequest<SupportSession>(root, { method: 'POST', json: input, headers: { 'Idempotency-Key': idempotencyKey } })
export const closeSession = (id: string) => apiRequest<SupportSession>(`${sessionPath(id)}/close`, { method: 'POST' })
export const revokeSession = (id: string, reason: string) => apiRequest<SupportSession>(`${sessionPath(id)}/revoke`, { method: 'POST', json: { reason } })
export const getSessionAudit = (id: string, params: PageParams, signal?: AbortSignal) => apiRequest<PagedResult<SupportAuditEntry>>(`${sessionPath(id)}/audit${query(params)}`, { signal })
export const getSupportJobs = (id: string, signal?: AbortSignal) => apiRequest<SupportJob[]>(`${sessionPath(id)}/backup-jobs`, { signal })
export const getSupportRuns = (id: string, filters: SupportDataFilters, signal?: AbortSignal) => apiRequest<PagedResult<SupportRun>>(`${sessionPath(id)}/backup-runs${query(filters)}`, { signal })
export const getSupportRun = (id: string, runId: string, signal?: AbortSignal) => apiRequest<SupportRun>(`${sessionPath(id)}/backup-runs/${encodeURIComponent(runId)}`, { signal })
export const getSupportArtifacts = (id: string, filters: SupportDataFilters, signal?: AbortSignal) => apiRequest<PagedResult<SupportArtifact>>(`${sessionPath(id)}/artifacts${query(filters)}`, { signal })
export const getSupportArtifact = (id: string, artifactId: string, signal?: AbortSignal) => apiRequest<SupportArtifact>(artifactPath(id, artifactId), { signal })
export const getSupportDownloadAvailability = (id: string, artifactId: string, signal?: AbortSignal) => apiRequest<SupportDownloadAvailability>(`${artifactPath(id, artifactId)}/download-availability`, { signal })
export const prepareSupportDownload = (id: string, artifactId: string, replicaId?: string) => apiRequest<PreparedDownload>(`${artifactPath(id, artifactId)}/downloads`, { method: 'POST', json: replicaId ? { replicaId } : {} })
export const getSupportDownloadStatus = (id: string, artifactId: string, downloadId: string, signal?: AbortSignal) => apiRequest<DownloadStatus>(`${artifactPath(id, artifactId)}/downloads/${encodeURIComponent(downloadId)}`, { signal })
export const getSupportDownloads = (id: string, params: PageParams, signal?: AbortSignal) => apiRequest<PagedResult<SupportDownload>>(`${sessionPath(id)}/downloads${query(params)}`, { signal })
export const getSupportRestoreTargets = (id: string, artifactId: string, signal?: AbortSignal) => apiRequest<SupportRestoreTargets>(`${artifactPath(id, artifactId)}/restore-targets`, { signal })
export const precheckSupportRestore = (id: string, artifactId: string, input: SupportPrecheckInput, idempotencyKey: string) => apiRequest<RestoreView>(`${artifactPath(id, artifactId)}/restore-precheck`, { method: 'POST', json: input, headers: { 'Idempotency-Key': idempotencyKey } })
export const startSupportRestore = (id: string, artifactId: string, input: SupportRestoreInput, idempotencyKey: string) => apiRequest<RestoreView>(`${artifactPath(id, artifactId)}/restore`, { method: 'POST', json: input, headers: { 'Idempotency-Key': idempotencyKey } })
export const getSupportRestores = (id: string, params: SupportDataFilters, signal?: AbortSignal) => apiRequest<PagedResult<RestoreView>>(`${sessionPath(id)}/restores${query(params)}`, { signal })
export const getSupportRestore = (id: string, restoreId: string, signal?: AbortSignal) => apiRequest<RestoreView>(`${sessionPath(id)}/restores/${encodeURIComponent(restoreId)}`, { signal })
export const cancelSupportRestore = (id: string, restoreId: string) => apiRequest<RestoreView>(`${sessionPath(id)}/restores/${encodeURIComponent(restoreId)}/cancel`, { method: 'POST' })
