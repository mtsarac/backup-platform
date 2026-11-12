import { tx } from '@/lib/i18n'
import type { ChannelKind, EventType, Severity, WebhookAuthentication } from './types'

export const eventTypeLabels: Record<EventType | 'test' | 'recipient_verification' | 'backup_share_created' | 'backup_share_extended' | 'backup_share_revoked', string> = {
  get backup_failed() { return tx("Backup başarısız") },
  get backup_stale() { return tx("Backup güncel değil") },
  get agent_offline() { return tx("Agent çevrimdışı") },
  get quota_critical() { return tx("Kota kritik") },
  get replica_degraded() { return tx("Replica bozuldu") },
  get storage_capacity_critical() { return tx("Depolama kapasitesi kritik") },
  get backup_size_anomaly() { return tx("Boyut Anomalisi") },
  get test() { return tx("Test bildirimi") },
  get recipient_verification() { return tx("E-posta onay isteği") },
  get backup_share_created() { return tx("Paylaşım oluşturuldu") },
  get backup_share_extended() { return tx("Paylaşım süresi uzatıldı") },
  get backup_share_revoked() { return tx("Paylaşım iptal edildi") },
}
export const severityLabels: Record<Severity, string> = { get info() { return tx("Bilgi") }, get warning() { return tx("Uyarı") }, get critical() { return tx("Kritik") } }
export const kindLabels: Record<ChannelKind, string> = { get email() { return tx("E-posta") }, get webhook() { return tx("Webhook") }, sms: 'SMS' }
export const authenticationLabels: Record<WebhookAuthentication, string> = { get none() { return tx("Kimlik doğrulama yok") }, get bearer() { return tx("Bearer token") }, get hmac() { return tx("HMAC imzası") } }
export const phaseLabels = { get raised() { return tx("Oluştu") }, get recovered() { return tx("Düzeldi") }, test: 'Test' } as const
