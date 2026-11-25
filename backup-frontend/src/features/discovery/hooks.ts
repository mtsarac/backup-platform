import { useEffect, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getAgents } from '@/features/agents/api'
import { agentQueryKeys } from '@/features/agents/hooks'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getDiscoveryAgents, getDiscoveryInstance, getDiscoveryInstances, rescanDiscoveryAgent, saveDiscoveryCredential, verifyDiscoveryInstance } from './api'
import { isRescanCoolingDown, isScanActive, SCAN_POLL_MS } from './status'
import type { DiscoveryAgentStatus, DiscoveryCredentialInput, DiscoveryInstanceFilters } from './types'

export const discoveryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'discovery'] as const,
  agents: (tenantId: string) => ['tenant', tenantId, 'discovery', 'agents'] as const,
  instances: (tenantId: string, filters: DiscoveryInstanceFilters) => ['tenant', tenantId, 'discovery', 'instances', filters] as const,
  instance: (tenantId: string, id: string) => ['tenant', tenantId, 'discovery', 'instance', id] as const,
}

/**
 * Polls while a scan of an active, online computer is queued or running (optionally only `agentId`), and refreshes the inventory once when it finishes.
 * Revoked computers (absent from the agents list) and offline ones keep a 'Requested' scan forever and must not count.
 */
export function useDiscoveryAgents(enabled = true, agentId?: string) {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  const computers = useQuery({
    queryKey: agentQueryKeys.all(tenantId),
    queryFn: ({ signal }) => getAgents(tenantId, signal, false),
    enabled,
    refetchOnMount: false,
  }).data
  const counts = (status: DiscoveryAgentStatus) => (!agentId || status.agentId === agentId) && Boolean(computers?.some(item => item.agentId === status.agentId && item.connectivity === 'online'))
  const query = useQuery({
    queryKey: discoveryKeys.agents(tenantId),
    queryFn: ({ signal }) => getDiscoveryAgents(tenantId, signal),
    enabled,
    refetchInterval: current => isScanActive(current.state.data?.items.filter(counts)) ? SCAN_POLL_MS : false,
  })
  const scanning = isScanActive(query.data?.items.filter(counts))
  const wasScanning = useRef(false)
  useEffect(() => {
    if (wasScanning.current && !scanning) void client.invalidateQueries({ queryKey: [...discoveryKeys.all(tenantId), 'instances'] }).then(() => client.invalidateQueries({ queryKey: [...discoveryKeys.all(tenantId), 'instance'] }))
    wasScanning.current = scanning
  }, [client, scanning, tenantId])
  return { ...query, scanning, scanningFor: (id: string) => isScanActive(query.data?.items.filter(item => item.agentId === id && counts(item))) }
}
export function useDiscoveryInstances(filters: DiscoveryInstanceFilters = {}, enabled = true) {
  const { tenantId } = useTenant()
  const { scanning } = useDiscoveryAgents(enabled, filters.agentId)
  return useQuery({ queryKey: discoveryKeys.instances(tenantId, filters), queryFn: ({ signal }) => getDiscoveryInstances(tenantId, filters, signal), enabled, refetchInterval: scanning ? SCAN_POLL_MS : false })
}
export function useDiscoveryInstance(id: string) {
  const { tenantId } = useTenant()
  const statuses = useDiscoveryAgents(Boolean(id))
  return useQuery({
    queryKey: discoveryKeys.instance(tenantId, id),
    queryFn: ({ signal }) => getDiscoveryInstance(tenantId, id, signal),
    enabled: Boolean(id),
    refetchInterval: current => {
      const agentId = current.state.data?.agentId
      return agentId && statuses.scanningFor(agentId) ? SCAN_POLL_MS : false
    },
  })
}
function useInvalidateDiscovery() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return () => client.invalidateQueries({ queryKey: discoveryKeys.all(tenantId) })
}
export function useRescanAgent() {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (agentId: string) => rescanDiscoveryAgent(tenantId, agentId), onSuccess: invalidate, onError: invalidate })
}
/** One automatic scan per computer when a screen opens, once its scan status is known and only if no scan is queued or just done; `deciding` stays true until then. */
export function useAutoRescan(agentId: string, enabled: boolean, statuses: DiscoveryAgentStatus[] | undefined) {
  const rescan = useRescanAgent()
  const started = useRef('')
  const skip = isRescanCoolingDown(statuses?.find(status => status.agentId === agentId))
  useEffect(() => {
    if (!enabled || !agentId || !statuses || started.current === agentId) return
    started.current = agentId
    if (!skip) rescan.mutate(agentId)
  }, [enabled, agentId, statuses, skip]) // oxlint-disable-line react-hooks/exhaustive-deps -- rescan is a fresh object per render
  return { rescan, deciding: enabled && agentId !== '' && (!statuses || rescan.isPending || (rescan.isIdle && !skip)) }
}
export function useVerifyInstance() {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (id: string) => verifyDiscoveryInstance(tenantId, id), onSuccess: invalidate, onError: invalidate })
}
export function useSaveCredential(id: string) {
  const { tenantId } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (input: DiscoveryCredentialInput) => saveDiscoveryCredential(tenantId, id, input), onSuccess: invalidate })
}
