import { apiRequest, tenantPath } from '@/lib/api/client'
import type { RotateWrapKeyResult, TenantEncryption, WrapKey, WrapKeyList } from './types'

const wrapKeysPath = (tenantId: string) => `platform/tenants/${encodeURIComponent(tenantId)}/wrap-keys`
export const getWrapKeys = (tenantId: string, signal?: AbortSignal) => apiRequest<WrapKeyList>(wrapKeysPath(tenantId), { signal })
export const rotateWrapKey = (tenantId: string) => apiRequest<RotateWrapKeyResult>(`${wrapKeysPath(tenantId)}/rotate`, { method: 'POST' })
export const retireWrapKey = (tenantId: string, version: number) => apiRequest<{ key: WrapKey }>(`${wrapKeysPath(tenantId)}/${version}/retire`, { method: 'POST' })

const encryptionPath = (tenantId: string) => tenantPath(tenantId, 'encryption')
export const getTenantEncryption = (tenantId: string, signal?: AbortSignal) => apiRequest<TenantEncryption>(encryptionPath(tenantId), { signal })
export const setTenantRecoveryKey = (tenantId: string, publicKeyPem: string) => apiRequest<TenantEncryption>(`${encryptionPath(tenantId)}/recovery-key`, { method: 'PUT', json: { publicKeyPem } })
export const removeTenantRecoveryKey = (tenantId: string) => apiRequest<void>(`${encryptionPath(tenantId)}/recovery-key`, { method: 'DELETE' })
