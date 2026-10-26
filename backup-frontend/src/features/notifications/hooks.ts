import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTenant } from '@/features/tenancy/use-tenant'
import {
  clearChannelSecret, createChannel, deleteChannel, getChannels, getDeliveries, getPolicy, setChannelEnabled, setChannelSecret, testChannel,
  updateChannel, updatePolicy, setOwnerSecurityRecipient,
} from './api'
import type { CreateChannelInput, DeliveryFilters, PolicyInput, UpdateChannelInput } from './types'

/** Secret VALUES are request bodies only: they are never part of a key. */
export const notificationKeys = {
  all: (tenantId: string) => ['tenant', tenantId, 'notifications'] as const,
  policy: (tenantId: string) => ['tenant', tenantId, 'notifications', 'policy'] as const,
  channels: (tenantId: string) => ['tenant', tenantId, 'notifications', 'channels'] as const,
  deliveries: (tenantId: string) => ['tenant', tenantId, 'notifications', 'deliveries'] as const,
  deliveryList: (tenantId: string, filters: DeliveryFilters) => ['tenant', tenantId, 'notifications', 'deliveries', filters] as const,
}

export function useNotificationPolicy() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: notificationKeys.policy(tenantId), queryFn: ({ signal }) => getPolicy(tenantId, signal) })
}

export function useNotificationChannels() {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: notificationKeys.channels(tenantId), queryFn: ({ signal }) => getChannels(tenantId, signal) })
}

export function useNotificationDeliveries(filters: DeliveryFilters) {
  const { tenantId } = useTenant()
  return useQuery({ queryKey: notificationKeys.deliveryList(tenantId, filters), queryFn: ({ signal }) => getDeliveries(tenantId, filters, signal), placeholderData: keepPreviousData })
}

export function useUpdateNotificationPolicy() {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: PolicyInput) => updatePolicy(tenantId, input),
    onSuccess: (policy) => { client.setQueryData(notificationKeys.policy(tenantId), policy) },
  })
}

/** Every channel mutation refreshes the list; a test also queues a delivery, so history refreshes too. Secret-carrying mutations are not kept in the mutation cache. */
function useChannelMutation<V>(fn: (tenantId: string, variables: V) => Promise<unknown>, { secret = false, deliveries = false } = {}) {
  const { tenantId } = useTenant()
  const client = useQueryClient()
  return useMutation({
    mutationFn: (variables: V) => fn(tenantId, variables),
    gcTime: secret ? 0 : undefined,
    onSettled: () => Promise.all([
      client.invalidateQueries({ queryKey: notificationKeys.channels(tenantId) }),
      deliveries ? client.invalidateQueries({ queryKey: notificationKeys.deliveries(tenantId) }) : undefined,
    ]),
  })
}

export const useCreateChannel = () => useChannelMutation((tenantId, input: CreateChannelInput) => createChannel(tenantId, input), { secret: true })
export const useUpdateChannel = () => useChannelMutation((tenantId, v: { id: string; input: UpdateChannelInput }) => updateChannel(tenantId, v.id, v.input))
export const useSetChannelSecret = () => useChannelMutation((tenantId, v: { id: string; value: string }) => setChannelSecret(tenantId, v.id, v.value), { secret: true })
export const useClearChannelSecret = () => useChannelMutation((tenantId, id: string) => clearChannelSecret(tenantId, id))
export const useSetChannelEnabled = () => useChannelMutation((tenantId, v: { id: string; enabled: boolean }) => setChannelEnabled(tenantId, v.id, v.enabled))
export const useSetOwnerSecurityRecipient = () => useChannelMutation((tenantId, v: { id: string; enabled: boolean }) => setOwnerSecurityRecipient(tenantId, v.id, v.enabled))
export const useDeleteChannel = () => useChannelMutation((tenantId, id: string) => deleteChannel(tenantId, id), { deliveries: true })
export const useTestChannel = () => useChannelMutation((tenantId, id: string) => testChannel(tenantId, id), { deliveries: true })
