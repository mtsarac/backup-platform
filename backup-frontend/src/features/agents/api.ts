import { apiRequest, tenantPath } from '@/lib/api/client'
import type { Agent, AgentDetail, CreatedEnrollmentToken, EnrollmentPolicy, EnrollmentToken } from './types'
export type { Agent } from './types'

export function getAgents(tenantId: string, signal?: AbortSignal) {
  return apiRequest<Agent[]>(tenantPath(tenantId, 'agents'), { signal })
}

export function getAgent(tenantId: string, agentId: string, signal?: AbortSignal) {
  return apiRequest<AgentDetail>(tenantPath(tenantId, `agents/${encodeURIComponent(agentId)}`), { signal })
}

export function revokeAgent(tenantId: string, agentId: string) {
  return apiRequest<void>(tenantPath(tenantId, `agents/${encodeURIComponent(agentId)}/revoke`), { method: 'POST' })
}

export function getEnrollmentPolicy(tenantId: string, signal?: AbortSignal) {
  return apiRequest<EnrollmentPolicy>(tenantPath(tenantId, 'agent-enrollment'), { signal })
}

export function setEnrollmentPolicy(tenantId: string, enabled: boolean) {
  return apiRequest<EnrollmentPolicy>(tenantPath(tenantId, 'agent-enrollment'), { method: 'PUT', json: { enabled } })
}

export function getEnrollmentTokens(tenantId: string, signal?: AbortSignal) {
  return apiRequest<EnrollmentToken[]>(tenantPath(tenantId, 'agent-enrollment-tokens'), { signal })
}

// Call directly from transient UI state; never place the plaintext result in Query's mutation cache.
export function createEnrollmentToken(tenantId: string, lifetimeMinutes: number, signal: AbortSignal) {
  return apiRequest<CreatedEnrollmentToken>(tenantPath(tenantId, 'agent-enrollment-tokens'), { method: 'POST', json: { lifetimeMinutes }, signal })
}

export function revokeEnrollmentToken(tenantId: string, tokenId: string) {
  return apiRequest<void>(tenantPath(tenantId, `agent-enrollment-tokens/${encodeURIComponent(tokenId)}`), { method: 'DELETE' })
}
