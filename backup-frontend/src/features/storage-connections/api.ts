import { apiRequest, tenantPath } from '@/lib/api/client'
import { query } from '@/features/platform/api'
import type { PagedResult } from '@/features/platform/types'
import type {
  ConnectionTestResponse, ConnectionView, PlatformConnectionFilters, PlatformCreateConnectionInput, PlatformUpdateConnectionInput,
  ProviderDescriptor, SecretValues, TenantCreateConnectionInput, TenantUpdateConnectionInput,
} from './types'

const connection = (tenantId: string, id: string, suffix = '') => tenantPath(tenantId, `storage-connections/${encodeURIComponent(id)}${suffix}`)

export function getTenantProviders(tenantId: string, signal?: AbortSignal) {
  return apiRequest<{ items: ProviderDescriptor[] }>(tenantPath(tenantId, 'storage-providers'), { signal })
}

export function getTenantConnections(tenantId: string, signal?: AbortSignal) {
  return apiRequest<{ items: ConnectionView[] }>(tenantPath(tenantId, 'storage-connections'), { signal })
}

export function createTenantConnection(tenantId: string, input: TenantCreateConnectionInput) {
  return apiRequest<ConnectionView>(tenantPath(tenantId, 'storage-connections'), { method: 'POST', json: input })
}

export function updateTenantConnection(tenantId: string, id: string, input: TenantUpdateConnectionInput) {
  return apiRequest<ConnectionView>(connection(tenantId, id), { method: 'PUT', json: input })
}

export function setTenantConnectionSecret(tenantId: string, id: string, values: SecretValues) {
  return apiRequest<ConnectionView>(connection(tenantId, id, '/secret'), { method: 'PUT', json: { values } })
}

export function clearTenantConnectionSecret(tenantId: string, id: string) {
  return apiRequest<ConnectionView>(connection(tenantId, id, '/secret'), { method: 'DELETE' })
}

export function testTenantConnection(tenantId: string, id: string) {
  return apiRequest<ConnectionTestResponse>(connection(tenantId, id, '/test'), { method: 'POST' })
}

export function setTenantConnectionEnabled(tenantId: string, id: string, enabled: boolean) {
  return apiRequest<ConnectionView>(connection(tenantId, id, enabled ? '/enable' : '/disable'), { method: 'POST' })
}

export function deleteTenantConnection(tenantId: string, id: string) {
  return apiRequest<void>(connection(tenantId, id), { method: 'DELETE' })
}

export function getGoogleDriveAuthorizeUrl(tenantId: string, id: string) {
  return apiRequest<{ authorizationUrl: string }>(connection(tenantId, id, '/google-drive/authorize-url'), { method: 'POST' })
}

export function disconnectGoogleDrive(tenantId: string, id: string) {
  return apiRequest<{ disconnected: boolean }>(connection(tenantId, id, '/google-drive/disconnect'), { method: 'POST' })
}

const platformConnection = (id: string, suffix = '') => `platform/storage-connections/${encodeURIComponent(id)}${suffix}`

export function getPlatformProviders(signal?: AbortSignal) {
  return apiRequest<{ items: ProviderDescriptor[] }>('platform/storage-providers', { signal })
}

export function getPlatformConnections(filters: PlatformConnectionFilters, signal?: AbortSignal) {
  return apiRequest<PagedResult<ConnectionView>>(`platform/storage-connections${query(filters)}`, { signal })
}

export function getPlatformTenantConnections(tenantId: string, signal?: AbortSignal) {
  return apiRequest<{ items: ConnectionView[] }>(`platform/tenants/${encodeURIComponent(tenantId)}/storage-connections`, { signal })
}

export function createPlatformConnection(tenantId: string, input: PlatformCreateConnectionInput) {
  return apiRequest<ConnectionView>(`platform/tenants/${encodeURIComponent(tenantId)}/storage-connections`, { method: 'POST', json: input })
}

export function updatePlatformConnection(id: string, input: PlatformUpdateConnectionInput) {
  return apiRequest<ConnectionView>(platformConnection(id), { method: 'PUT', json: input })
}

export function setPlatformConnectionSecret(id: string, values: SecretValues) {
  return apiRequest<ConnectionView>(platformConnection(id, '/secret'), { method: 'PUT', json: { values } })
}

export function clearPlatformConnectionSecret(id: string) {
  return apiRequest<ConnectionView>(platformConnection(id, '/secret'), { method: 'DELETE' })
}

export function testPlatformConnection(id: string) {
  return apiRequest<ConnectionTestResponse>(platformConnection(id, '/test'), { method: 'POST' })
}

/** The platform enable/disable routes clear/set the administrative lock, not the owner's enabled preference. */
export function setPlatformConnectionEnabled(id: string, enabled: boolean) {
  return apiRequest<ConnectionView>(platformConnection(id, enabled ? '/enable' : '/disable'), { method: 'POST' })
}

export function deletePlatformConnection(id: string) {
  return apiRequest<void>(platformConnection(id), { method: 'DELETE' })
}
