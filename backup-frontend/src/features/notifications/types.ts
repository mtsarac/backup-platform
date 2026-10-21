import type { PagedResult } from '@/features/platform/types'

export const eventTypes = ['backup_failed', 'backup_stale', 'agent_offline', 'quota_critical', 'replica_degraded', 'storage_capacity_critical', 'backup_size_anomaly'] as const
export const severities = ['info', 'warning', 'critical'] as const
export const channelKinds = ['email', 'webhook', 'sms'] as const
export const webhookAuthentications = ['none', 'bearer', 'hmac'] as const
export const deliveryStatuses = ['pending', 'in_progress', 'sent', 'failed', 'suppressed', 'cancelled'] as const

export type EventType = (typeof eventTypes)[number]
export type Severity = (typeof severities)[number]
export type ChannelKind = (typeof channelKinds)[number]
export type WebhookAuthentication = (typeof webhookAuthentications)[number]
export type DeliveryStatus = (typeof deliveryStatuses)[number]

export interface PolicyInput {
  enabled: boolean
  eventTypes: EventType[]
  minimumSeverity: Severity
  channelKinds: ChannelKind[]
  recoveryNotifications: boolean
  staleAfterHours: number
  agentOfflineAfterMinutes: number
  reminderIntervalHours: number
  maxNotificationsPerHour: number
}

export interface NotificationPolicy extends PolicyInput {
  updatedAt: string | null
  transports: { email: { available: boolean }; sms: { available: boolean } }
}

export interface ChannelLastDelivery { status: DeliveryStatus; at: string; errorCode: string | null }

export interface NotificationChannel {
  id: string
  kind: ChannelKind
  name: string
  targetDisplay: string
  enabled: boolean
  /** E-posta kanallarında adres sahibinin onayı; diğer türlerde null. Onaylanmamış adrese bildirim gitmez. */
  recipientVerified: boolean | null
  securityOwnerUserId: string | null
  authentication: WebhookAuthentication
  hasSecret: boolean
  secretUpdatedAt: string | null
  createdAt: string
  updatedAt: string
  lastDelivery: ChannelLastDelivery | null
}

export interface CreateChannelInput {
  kind: ChannelKind
  name: string
  target: string
  authentication?: WebhookAuthentication
  secret?: string
}

export interface UpdateChannelInput { name?: string; target?: string; authentication?: WebhookAuthentication }

/** Always-on Owner security notice, independent of ordinary notification policy and event selection. */
export type MandatoryEventType = 'break_glass_access'

export interface NotificationDelivery {
  id: string
  eventId: string
  eventType: EventType | 'test' | 'recipient_verification' | MandatoryEventType
  phase: 'raised' | 'recovered' | 'test'
  severity: Severity
  subjectKind: string
  subjectName: string
  channelKind: ChannelKind
  channelName: string
  targetDisplay: string
  status: DeliveryStatus
  attempts: number
  errorCode: string | null
  nextAttemptAt: string | null
  sentAt: string | null
  createdAt: string
}

export interface DeliveryFilters { page: number; pageSize: number; status?: DeliveryStatus; kind?: ChannelKind; eventType?: EventType | 'test' | MandatoryEventType }
export type DeliveryPage = PagedResult<NotificationDelivery>
