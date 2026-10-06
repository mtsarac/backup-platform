import { useQuery } from '@tanstack/react-query'
import { getAgent, getAgents, getEnrollmentPolicy, getEnrollmentTokens } from './api'

export const agentQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'agents'] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'agents', id] as const,
  policy: (tenantId: string) => ['tenant', tenantId, 'agent-enrollment'] as const,
  tokens: (tenantId: string) => ['tenant', tenantId, 'agent-enrollment-tokens'] as const,
}

export function useAgents(tenantId: string) {
  return useQuery({ queryKey: agentQueryKeys.all(tenantId), queryFn: ({ signal }) => getAgents(tenantId, signal) })
}

export function useAgent(tenantId: string, id: string) {
  return useQuery({ queryKey: agentQueryKeys.detail(tenantId, id), queryFn: ({ signal }) => getAgent(tenantId, id, signal), enabled: Boolean(id) })
}

export function useEnrollmentPolicy(tenantId: string, enabled: boolean) {
  return useQuery({ queryKey: agentQueryKeys.policy(tenantId), queryFn: ({ signal }) => getEnrollmentPolicy(tenantId, signal), enabled })
}

export function useEnrollmentTokens(tenantId: string, enabled: boolean) {
  return useQuery({ queryKey: agentQueryKeys.tokens(tenantId), queryFn: ({ signal }) => getEnrollmentTokens(tenantId, signal), enabled })
}
