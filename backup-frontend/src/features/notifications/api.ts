import { apiRequest, tenantPath } from '@/lib/api/client'
import { query } from '@/features/platform/api'
import type { CreateChannelInput, DeliveryFilters, DeliveryPage, NotificationChannel, NotificationPolicy, PolicyInput, UpdateChannelInput } from './types'

const notifications = (tenantId: string, suffix: string) => tenantPath(tenantId, `notifications/${suffix}`)
const channel = (tenantId: string, id: string, suffix = '') => notifications(tenantId, `channels/${encodeURIComponent(id)}${suffix}`)

export function getPolicy(tenantId: string, signal?: AbortSignal) {
  return apiRequest<NotificationPolicy>(notifications(tenantId, 'policy'), { signal })
}

export function updatePolicy(tenantId: string, input: PolicyInput) {
  return apiRequest<NotificationPolicy>(notifications(tenantId, 'policy'), { method: 'PUT', json: input })
}

export function getChannels(tenantId: string, signal?: AbortSignal) {
  return apiRequest<{ items: NotificationChannel[] }>(notifications(tenantId, 'channels'), { signal })
}

export function createChannel(tenantId: string, input: CreateChannelInput) {
  return apiRequest<NotificationChannel>(notifications(tenantId, 'channels'), { method: 'POST', json: input })
}

export function updateChannel(tenantId: string, id: string, input: UpdateChannelInput) {
  return apiRequest<NotificationChannel>(channel(tenantId, id), { method: 'PUT', json: input })
}

export function setChannelSecret(tenantId: string, id: string, value: string) {
  return apiRequest<NotificationChannel>(channel(tenantId, id, '/secret'), { method: 'PUT', json: { value } })
}

export function clearChannelSecret(tenantId: string, id: string) {
  return apiRequest<NotificationChannel>(channel(tenantId, id, '/secret'), { method: 'DELETE' })
}

export function setChannelEnabled(tenantId: string, id: string, enabled: boolean) {
  return apiRequest<NotificationChannel>(channel(tenantId, id, enabled ? '/enable' : '/disable'), { method: 'POST' })
}

export function deleteChannel(tenantId: string, id: string) {
  return apiRequest<void>(channel(tenantId, id), { method: 'DELETE' })
}

export function testChannel(tenantId: string, id: string) {
  return apiRequest<{ deliveryId: string }>(channel(tenantId, id, '/test'), { method: 'POST' })
}

export function getDeliveries(tenantId: string, filters: DeliveryFilters, signal?: AbortSignal) {
  return apiRequest<DeliveryPage>(`${notifications(tenantId, 'deliveries')}${query(filters)}`, { signal })
}
