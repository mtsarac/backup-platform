import { useQuery } from '@tanstack/react-query'
import type { Agent } from './types'
import { getAgent, getAgentDistribution, getAgents, getEnrollmentPolicy, getEnrollmentTokens } from './api'

export const agentQueryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'agents'] as const,
  detail: (tenantId: string, id: string) => ['tenant', tenantId, 'agents', id] as const,
  policy: (tenantId: string) => ['tenant', tenantId, 'agent-enrollment'] as const,
  tokens: (tenantId: string) => ['tenant', tenantId, 'agent-enrollment-tokens'] as const,
}

/** Revoked agents are hidden unless asked for; screens that resolve the agent of a past run or restore need `includeRevoked`. */
export function useAgents(tenantId: string, includeRevoked = false, pollEveryMs?: (agents: Agent[] | undefined) => number | false) {
  return useQuery({
    queryKey: includeRevoked ? [...agentQueryKeys.all(tenantId), { includeRevoked }] : agentQueryKeys.all(tenantId),
    queryFn: ({ signal }) => getAgents(tenantId, signal, includeRevoked),
    refetchInterval: query => pollEveryMs?.(query.state.data) ?? false,
  })
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

export function useAgentDistribution() {
  return useQuery({ queryKey: ['agent-distribution'], queryFn: ({ signal }) => getAgentDistribution(signal) })
}
