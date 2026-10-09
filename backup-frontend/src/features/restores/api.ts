import { apiRequest, tenantPath } from '@/lib/api/client'
import type { RestoreFilters, RestorePage, RestoreTargetsResponse, RestoreView, StartRestoreDiscoveryRequest, StartRestorePrecheckRequest, StartRestoreRequest } from './types'

export function getRestores(tenantId: string, filters: RestoreFilters = {}, signal?: AbortSignal) {
  const search = new URLSearchParams({ page: String(filters.page ?? 1), pageSize: String(filters.pageSize ?? 25) })
  if (filters.status) search.set('status', filters.status)
  if (filters.artifactId) search.set('artifactId', filters.artifactId)
  if (filters.agentId) search.set('agentId', filters.agentId)
  return apiRequest<RestorePage>(`${tenantPath(tenantId, 'restores')}?${search}`, { signal })
}
export function getRestore(tenantId: string, id: string, signal?: AbortSignal) {
  return apiRequest<RestoreView>(tenantPath(tenantId, `restores/${encodeURIComponent(id)}`), { signal })
}
export function startRestore(tenantId: string, artifactId: string, request: StartRestoreRequest, idempotencyKey: string) {
  return apiRequest<RestoreView>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(artifactId)}/restore`), {
    method: 'POST', json: request, headers: { 'Idempotency-Key': idempotencyKey },
  })
}
export function startRestorePrecheck(tenantId: string, artifactId: string, request: StartRestorePrecheckRequest, idempotencyKey: string) {
  return apiRequest<RestoreView>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(artifactId)}/restore-precheck`), {
    method: 'POST', json: request, headers: { 'Idempotency-Key': idempotencyKey },
  })
}
export function getRestoreTargets(tenantId: string, artifactId: string, signal?: AbortSignal) {
  return apiRequest<RestoreTargetsResponse>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(artifactId)}/restore-targets`), { signal })
}
export function startRestoreDiscovery(tenantId: string, artifactId: string, request: StartRestoreDiscoveryRequest, idempotencyKey: string) {
  return apiRequest<RestoreView>(tenantPath(tenantId, `backup-artifacts/${encodeURIComponent(artifactId)}/restore-discovery`), {
    method: 'POST', json: request, headers: { 'Idempotency-Key': idempotencyKey },
  })
}
export function cancelRestore(tenantId: string, id: string) {
  return apiRequest<RestoreView>(tenantPath(tenantId, `restores/${encodeURIComponent(id)}/cancel`), { method: 'POST' })
}
