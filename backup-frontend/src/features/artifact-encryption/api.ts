import { apiRequest, tenantPath } from '@/lib/api/client'
import { query } from '@/features/platform/api'
import type { PageParams, PagedResult } from '@/features/platform/types'
import type { DownloadStatus, PreparedDownload } from '@/features/artifacts/api'
import type { BreakGlassFilters, BreakGlassRequest, CreateBreakGlassInput, RotateWrapKeyResult, TenantBreakGlassEvent, TenantEncryption, WrapKey, WrapKeyList } from './types'

const wrapKeysPath = (tenantId: string) => `platform/tenants/${encodeURIComponent(tenantId)}/wrap-keys`
export const getWrapKeys = (tenantId: string, signal?: AbortSignal) => apiRequest<WrapKeyList>(wrapKeysPath(tenantId), { signal })
export const rotateWrapKey = (tenantId: string) => apiRequest<RotateWrapKeyResult>(`${wrapKeysPath(tenantId)}/rotate`, { method: 'POST' })
export const retireWrapKey = (tenantId: string, version: number) => apiRequest<{ key: WrapKey }>(`${wrapKeysPath(tenantId)}/${version}/retire`, { method: 'POST' })

const encryptionPath = (tenantId: string) => tenantPath(tenantId, 'encryption')
export const getTenantEncryption = (tenantId: string, signal?: AbortSignal) => apiRequest<TenantEncryption>(encryptionPath(tenantId), { signal })
export const setTenantRecoveryKey = (tenantId: string, publicKeyPem: string) => apiRequest<TenantEncryption>(`${encryptionPath(tenantId)}/recovery-key`, { method: 'PUT', json: { publicKeyPem } })
export const removeTenantRecoveryKey = (tenantId: string) => apiRequest<void>(`${encryptionPath(tenantId)}/recovery-key`, { method: 'DELETE' })
export const getTenantBreakGlass = (tenantId: string, params: PageParams, signal?: AbortSignal) => apiRequest<PagedResult<TenantBreakGlassEvent>>(`${tenantPath(tenantId, 'artifact-break-glass')}${query(params)}`, { signal })

const breakGlassRoot = 'platform/artifact-break-glass'
const requestPath = (id: string) => `${breakGlassRoot}/${encodeURIComponent(id)}`
export const getBreakGlassRequests = (filters: BreakGlassFilters, signal?: AbortSignal) => apiRequest<PagedResult<BreakGlassRequest>>(`${breakGlassRoot}${query(filters)}`, { signal })
export const getBreakGlassRequest = (id: string, signal?: AbortSignal) => apiRequest<BreakGlassRequest>(requestPath(id), { signal })
export const createBreakGlassRequest = (input: CreateBreakGlassInput) => apiRequest<BreakGlassRequest>(breakGlassRoot, { method: 'POST', json: input })
export const approveBreakGlass = (id: string) => apiRequest<BreakGlassRequest>(`${requestPath(id)}/approve`, { method: 'POST' })
export const denyBreakGlass = (id: string, reason: string) => apiRequest<BreakGlassRequest>(`${requestPath(id)}/deny`, { method: 'POST', json: { reason } })
export const downloadBreakGlass = (id: string, replicaId?: string) => apiRequest<PreparedDownload>(`${requestPath(id)}/download`, { method: 'POST', json: replicaId ? { replicaId } : {} })
export const getBreakGlassDownloadStatus = (id: string, sessionId: string, signal?: AbortSignal) => apiRequest<DownloadStatus>(`${requestPath(id)}/downloads/${encodeURIComponent(sessionId)}`, { signal })
