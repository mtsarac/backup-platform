import type { ComponentProps } from 'react'
import type { Badge } from '@/components/ui/badge'
import type { ChannelKind, DeliveryStatus, EventType, Severity, WebhookAuthentication } from './types'

type BadgeTone = NonNullable<ComponentProps<typeof Badge>['tone']>

export const eventTypeLabels: Record<EventType | 'test', string> = {
  backup_failed: 'Backup başarısız',
  backup_stale: 'Backup güncel değil',
  agent_offline: 'Agent çevrimdışı',
  quota_critical: 'Kota kritik',
  replica_degraded: 'Replica bozuldu',
  test: 'Test bildirimi',
}
export const severityLabels: Record<Severity, string> = { info: 'Bilgi', warning: 'Uyarı', critical: 'Kritik' }
export const kindLabels: Record<ChannelKind, string> = { email: 'E-posta', webhook: 'Webhook', sms: 'SMS' }
export const authenticationLabels: Record<WebhookAuthentication, string> = { none: 'Kimlik doğrulama yok', bearer: 'Bearer token', hmac: 'HMAC imzası' }
export const phaseLabels = { raised: 'Oluştu', recovered: 'Düzeldi', test: 'Test' } as const
export const deliveryStatusLabels: Record<DeliveryStatus, { label: string; tone: BadgeTone }> = {
  pending: { label: 'Sırada', tone: 'neutral' },
  in_progress: { label: 'Gönderiliyor', tone: 'info' },
  sent: { label: 'Gönderildi', tone: 'success' },
  failed: { label: 'Başarısız', tone: 'error' },
  suppressed: { label: 'Bastırıldı', tone: 'warning' },
  cancelled: { label: 'İptal edildi', tone: 'neutral' },
}
