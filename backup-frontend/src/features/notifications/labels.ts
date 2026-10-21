import { tx } from '@/lib/i18n'
import type { ComponentProps } from 'react'
import type { Badge } from '@/components/ui/badge'
import type { ChannelKind, DeliveryStatus, EventType, MandatoryEventType, Severity, WebhookAuthentication } from './types'

type BadgeTone = NonNullable<ComponentProps<typeof Badge>['tone']>

export const eventTypeLabels: Record<EventType | MandatoryEventType | 'test' | 'recipient_verification', string> = {
  get backup_failed() { return tx("Backup başarısız") },
  get backup_stale() { return tx("Backup güncel değil") },
  get agent_offline() { return tx("Agent çevrimdışı") },
  get quota_critical() { return tx("Kota kritik") },
  get replica_degraded() { return tx("Replica bozuldu") },
  get storage_capacity_critical() { return tx("Depolama kapasitesi kritik") },
  get backup_size_anomaly() { return tx("Boyut Anomalisi") },
  get test() { return tx("Test bildirimi") },
  get recipient_verification() { return tx("E-posta onay isteği") },
  get break_glass_access() { return tx("Break-glass erişimi") },
}
export const severityLabels: Record<Severity, string> = { get info() { return tx("Bilgi") }, get warning() { return tx("Uyarı") }, get critical() { return tx("Kritik") } }
export const kindLabels: Record<ChannelKind, string> = { get email() { return tx("E-posta") }, get webhook() { return tx("Webhook") }, sms: 'SMS' }
export const authenticationLabels: Record<WebhookAuthentication, string> = { get none() { return tx("Kimlik doğrulama yok") }, get bearer() { return tx("Bearer token") }, get hmac() { return tx("HMAC imzası") } }
export const phaseLabels = { get raised() { return tx("Oluştu") }, get recovered() { return tx("Düzeldi") }, test: 'Test' } as const
export const deliveryStatusLabels: Record<DeliveryStatus, { label: string; tone: BadgeTone }> = {
  pending: { get label() { return tx("Sırada") }, tone: 'neutral' },
  in_progress: { get label() { return tx("Gönderiliyor") }, tone: 'info' },
  sent: { get label() { return tx("Gönderildi") }, tone: 'success' },
  failed: { get label() { return tx("Başarısız") }, tone: 'error' },
  suppressed: { get label() { return tx("Bastırıldı") }, tone: 'warning' },
  cancelled: { get label() { return tx("İptal edildi") }, tone: 'neutral' },
}
