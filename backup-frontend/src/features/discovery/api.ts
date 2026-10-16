import { apiRequest, tenantPath } from '@/lib/api/client'
import type { DiscoveryAgentStatus, DiscoveryCredentialInput, DiscoveryInstanceDetail, DiscoveryInstanceFilters, DiscoveryInstanceSummary, DiscoverySettingsInput } from './types'

const path = (tenantId: string, resource: string) => tenantPath(tenantId, `discovery/${resource}`)
const segment = encodeURIComponent

export const getDiscoveryAgents = (tenantId: string, signal?: AbortSignal) => apiRequest<{ items: DiscoveryAgentStatus[] }>(path(tenantId, 'agents'), { signal })
export function getDiscoveryInstances(tenantId: string, filters: DiscoveryInstanceFilters = {}, signal?: AbortSignal) {
  const query = new URLSearchParams()
  if (filters.agentId) query.set('agentId', filters.agentId)
  if (filters.engine) query.set('engine', filters.engine)
  if (filters.includeMissing) query.set('includeMissing', 'true')
  const text = query.toString()
  return apiRequest<{ items: DiscoveryInstanceSummary[] }>(path(tenantId, `instances${text ? `?${text}` : ''}`), { signal })
}
export const getDiscoveryInstance = (tenantId: string, id: string, signal?: AbortSignal) => apiRequest<DiscoveryInstanceDetail>(path(tenantId, `instances/${segment(id)}`), { signal })
export const rescanDiscoveryAgent = (tenantId: string, agentId: string) => apiRequest<DiscoveryAgentStatus>(path(tenantId, `agents/${segment(agentId)}/rescan`), { method: 'POST' })
export const verifyDiscoveryInstance = (tenantId: string, id: string) => apiRequest<DiscoveryAgentStatus>(path(tenantId, `instances/${segment(id)}/verify`), { method: 'POST' })
export const saveDiscoveryCredential = (tenantId: string, id: string, input: DiscoveryCredentialInput) => apiRequest<DiscoveryInstanceSummary>(path(tenantId, `instances/${segment(id)}/credential`), { method: 'PUT', json: input })
export const saveDiscoverySettings = (tenantId: string, agentId: string, input: DiscoverySettingsInput) => apiRequest<DiscoveryAgentStatus>(path(tenantId, `agents/${segment(agentId)}/settings`), { method: 'PUT', json: input })
