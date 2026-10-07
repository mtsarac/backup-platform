import { apiRequest } from '@/lib/api/client'
import type {
  AuditFilters, CreateUserInput, MembershipRole, PageParams, PagedResult, PlatformAgent, PlatformAgentDetail,
  PlatformAgentFilters, PlatformAuditEntry, PlatformMembership, PlatformSummary, PlatformTenant, PlatformTenantDetail,
  PlatformUser, SearchParams, TenantEnrollmentResult,
} from './types'

function query(params: object) {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

const tenantUrl = (tenantId: string) => `platform/tenants/${encodeURIComponent(tenantId)}`

export function getSummary(signal?: AbortSignal) {
  return apiRequest<PlatformSummary>('platform/summary', { signal })
}

export function getTenants(params: SearchParams, signal?: AbortSignal) {
  return apiRequest<PagedResult<PlatformTenant>>(`platform/tenants${query(params)}`, { signal })
}

export function createTenant(name: string) {
  return apiRequest<PlatformTenant>('platform/tenants', { method: 'POST', json: { name } })
}

export function getTenant(tenantId: string, signal?: AbortSignal) {
  return apiRequest<PlatformTenantDetail>(tenantUrl(tenantId), { signal })
}

export function renameTenant(tenantId: string, name: string) {
  return apiRequest<PlatformTenant>(tenantUrl(tenantId), { method: 'PUT', json: { name } })
}

export function setTenantEnrollment(tenantId: string, enabled: boolean) {
  return apiRequest<TenantEnrollmentResult>(`${tenantUrl(tenantId)}/agent-enrollment`, { method: 'PUT', json: { enabled } })
}

export function getMemberships(tenantId: string, params: PageParams, signal?: AbortSignal) {
  return apiRequest<PagedResult<PlatformMembership>>(`${tenantUrl(tenantId)}/memberships${query(params)}`, { signal })
}

export function addMembership(tenantId: string, userId: string, role: MembershipRole) {
  return apiRequest<PlatformMembership>(`${tenantUrl(tenantId)}/memberships`, { method: 'POST', json: { userId, role } })
}

export function changeMembershipRole(tenantId: string, userId: string, role: MembershipRole) {
  return apiRequest<PlatformMembership>(`${tenantUrl(tenantId)}/memberships/${encodeURIComponent(userId)}`, { method: 'PUT', json: { role } })
}

export function removeMembership(tenantId: string, userId: string) {
  return apiRequest<void>(`${tenantUrl(tenantId)}/memberships/${encodeURIComponent(userId)}`, { method: 'DELETE' })
}

export function getUsers(params: SearchParams, signal?: AbortSignal) {
  return apiRequest<PagedResult<PlatformUser>>(`platform/users${query(params)}`, { signal })
}

export function createUser(input: CreateUserInput) {
  return apiRequest<PlatformUser>('platform/users', { method: 'POST', json: input })
}

export function getPlatformAgents(filters: PlatformAgentFilters, signal?: AbortSignal) {
  return apiRequest<PagedResult<PlatformAgent>>(`platform/agents${query(filters)}`, { signal })
}

export function getPlatformAgent(tenantId: string, agentId: string, signal?: AbortSignal) {
  return apiRequest<PlatformAgentDetail>(`${tenantUrl(tenantId)}/agents/${encodeURIComponent(agentId)}`, { signal })
}

export function getAudit(filters: AuditFilters, signal?: AbortSignal) {
  return apiRequest<PagedResult<PlatformAuditEntry>>(`platform/audit${query(filters)}`, { signal })
}
