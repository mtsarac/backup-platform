import { tx } from '@/lib/i18n'
import type { ChannelKind, EventType, Severity, WebhookAuthentication } from './types'

/** The mandatory account security category: always delivered, with no preference to switch it off. */
export type SecurityEventType = 'security_password_changed' | 'security_temporary_password' | 'security_password_reset' | 'security_account_disabled' | 'security_account_enabled' | 'security_factor_added' | 'security_factor_removed' | 'security_recovery_regenerated' | 'security_recovery_used' | 'security_email_changed' | 'security_suspicious_login'

export const eventTypeLabels: Record<EventType | 'test' | 'recipient_verification' | 'backup_share_created' | 'backup_share_extended' | 'backup_share_revoked' | SecurityEventType, string> = {
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
  get security_password_changed() { return tx("Şifreniz değiştirildi") },
  get security_temporary_password() { return tx("Hesabınıza geçici şifre atandı") },
  get security_password_reset() { return tx("Şifreniz e-posta ile sıfırlandı") },
  get security_account_disabled() { return tx("Hesabınız devre dışı bırakıldı") },
  get security_account_enabled() { return tx("Hesabınız yeniden etkinleştirildi") },
  get security_factor_added() { return tx("İkinci faktör veya geçiş anahtarı eklendi") },
  get security_factor_removed() { return tx("İkinci faktör veya geçiş anahtarı kaldırıldı") },
  get security_recovery_regenerated() { return tx("Kurtarma kodları yeniden oluşturuldu") },
  get security_recovery_used() { return tx("Bir kurtarma kodu kullanıldı") },
  get security_email_changed() { return tx("E-posta adresiniz değiştirildi") },
  get security_suspicious_login() { return tx("Şüpheli giriş etkinliği") },
}
export const severityLabels: Record<Severity, string> = { get info() { return tx("Bilgi") }, get warning() { return tx("Uyarı") }, get critical() { return tx("Kritik") } }
export const kindLabels: Record<ChannelKind, string> = { get email() { return tx("E-posta") }, get webhook() { return tx("Webhook") }, sms: 'SMS' }
export const authenticationLabels: Record<WebhookAuthentication, string> = { get none() { return tx("Kimlik doğrulama yok") }, get bearer() { return tx("Bearer token") }, get hmac() { return tx("HMAC imzası") } }
export const phaseLabels = { get raised() { return tx("Oluştu") }, get recovered() { return tx("Düzeldi") }, test: 'Test' } as const
