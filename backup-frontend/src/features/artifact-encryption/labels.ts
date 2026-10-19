import { tx } from '@/lib/i18n'
import type { ArtifactFormat, BreakGlassStatus, EncryptionPolicy, RecipientType, WrapKeyState } from './types'

export const policyLabels: Record<EncryptionPolicy, string> = {
  get Off() { return tx("Kapalı · şifrelenmez") },
  get Optional() { return tx("İsteğe bağlı · mümkünse şifrele") },
  get Required() { return tx("Zorunlu · şifrelenemezse başarısız") },
}
export const policyShortLabels: Record<EncryptionPolicy, string> = {
  get Off() { return tx("Kapalı") }, get Optional() { return tx("İsteğe bağlı") }, get Required() { return tx("Zorunlu") },
}
export const policyDescriptions: Record<EncryptionPolicy, string> = {
  get Off() { return tx("Yedek dosyası düz (v1-plain) olarak yazılır. Mevcut davranış korunur.") },
  get Optional() { return tx("Yedek, Agent'ta şifrelenir (v2-bpe). Agent şifreleyemezse düz dosya (v1-plain) olarak kabul edilir; yedek reddedilmez.") },
  get Required() { return tx("Yedek yalnızca şifreli (v2-bpe) gelirse kabul edilir. Düz dosya gelirse çalıştırma başarısız olur ve dosya yayımlanmaz. Üçüncü taraf depolama için önerilir.") },
}

export const formatLabels: Record<ArtifactFormat, string> = {
  get 'v1-plain'() { return tx("v1-plain · şifresiz") }, get 'v2-bpe'() { return tx("v2-bpe · şifreli (AES-256-GCM)") },
}
export const recipientLabels: Record<RecipientType, string> = {
  get 'tenant-wrap'() { return tx("Tenant anahtarı (backend)") }, get 'platform-recovery'() { return tx("Platform kurtarma anahtarı") }, get 'tenant-recovery'() { return tx("Tenant kurtarma anahtarı") },
}
export const wrapKeyStateLabels: Record<WrapKeyState, { label: string; tone: 'success' | 'info' | 'neutral' }> = {
  Active: { get label() { return tx("Etkin") }, tone: 'success' },
  DecryptOnly: { get label() { return tx("Yalnızca çözme") }, tone: 'info' },
  Retired: { get label() { return tx("Emekli") }, tone: 'neutral' },
}
export const breakGlassStatusLabels: Record<BreakGlassStatus, { label: string; tone: 'success' | 'warning' | 'error' | 'neutral' | 'info' }> = {
  Pending: { get label() { return tx("Onay bekliyor") }, tone: 'warning' },
  Approved: { get label() { return tx("Onaylandı") }, tone: 'info' },
  Denied: { get label() { return tx("Reddedildi") }, tone: 'error' },
  Consumed: { get label() { return tx("Kullanıldı") }, tone: 'success' },
  Expired: { get label() { return tx("Süresi doldu") }, tone: 'neutral' },
}
