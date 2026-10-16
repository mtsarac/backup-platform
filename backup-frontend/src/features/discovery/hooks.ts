import { useEffect, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getDiscoveryAgents, getDiscoveryInstance, getDiscoveryInstances, rescanDiscoveryAgent, saveDiscoveryCredential, saveDiscoverySettings, verifyDiscoveryInstance } from './api'
import { isScanActive, SCAN_POLL_MS } from './status'
import type { DiscoveryCredentialInput, DiscoveryInstanceFilters, DiscoverySettingsInput } from './types'

export const discoveryKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'discovery'] as const,
  agents: (tenantId: string) => ['tenant', tenantId, 'discovery', 'agents'] as const,
  instances: (tenantId: string, filters: DiscoveryInstanceFilters) => ['tenant', tenantId, 'discovery', 'instances', filters] as const,
  instance: (tenantId: string, id: string) => ['tenant', tenantId, 'discovery', 'instance', id] as const,
}
const denied = () => { throw new Error('Bu işlem için yetkiniz yok.') }

/** Polls while any agent scan is queued or running, and refreshes the inventory once when it finishes. */
export function useDiscoveryAgents(enabled = true) {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  const query = useQuery({
    queryKey: discoveryKeys.agents(tenantId),
    queryFn: ({ signal }) => getDiscoveryAgents(tenantId, signal),
    enabled,
    refetchInterval: current => isScanActive(current.state.data?.items) ? SCAN_POLL_MS : false,
  })
  const scanning = isScanActive(query.data?.items)
  const wasScanning = useRef(false)
  useEffect(() => {
    if (wasScanning.current && !scanning) void client.invalidateQueries({ queryKey: [...discoveryKeys.all(tenantId), 'instances'] }).then(() => client.invalidateQueries({ queryKey: [...discoveryKeys.all(tenantId), 'instance'] }))
    wasScanning.current = scanning
  }, [client, scanning, tenantId])
  return { ...query, scanning }
}
export function useDiscoveryInstances(filters: DiscoveryInstanceFilters = {}, enabled = true) {
  const { tenantId } = useTenant()
  const { scanning } = useDiscoveryAgents(enabled)
  return useQuery({ queryKey: discoveryKeys.instances(tenantId, filters), queryFn: ({ signal }) => getDiscoveryInstances(tenantId, filters, signal), enabled, refetchInterval: scanning ? SCAN_POLL_MS : false })
}
export function useDiscoveryInstance(id: string) {
  const { tenantId } = useTenant()
  const { scanning } = useDiscoveryAgents(Boolean(id))
  return useQuery({ queryKey: discoveryKeys.instance(tenantId, id), queryFn: ({ signal }) => getDiscoveryInstance(tenantId, id, signal), enabled: Boolean(id), refetchInterval: scanning ? SCAN_POLL_MS : false })
}
function useInvalidateDiscovery() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return () => client.invalidateQueries({ queryKey: discoveryKeys.all(tenantId) })
}
export function useRescanAgent() {
  const { tenantId, canOperate } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (agentId: string) => canOperate ? rescanDiscoveryAgent(tenantId, agentId) : denied(), onSuccess: invalidate, onError: invalidate })
}
export function useVerifyInstance() {
  const { tenantId, canOperate } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (id: string) => canOperate ? verifyDiscoveryInstance(tenantId, id) : denied(), onSuccess: invalidate, onError: invalidate })
}
export function useSaveCredential(id: string) {
  const { tenantId, canOperate } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (input: DiscoveryCredentialInput) => canOperate ? saveDiscoveryCredential(tenantId, id, input) : denied(), onSuccess: invalidate })
}
export function useSaveSettings(agentId: string) {
  const { tenantId, canAdmin } = useTenant()
  const invalidate = useInvalidateDiscovery()
  return useMutation({ mutationFn: (input: DiscoverySettingsInput) => canAdmin ? saveDiscoverySettings(tenantId, agentId, input) : denied(), onSuccess: invalidate })
}
