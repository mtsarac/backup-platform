import { tx } from '@/lib/i18n'
import type { ArtifactFormat, EncryptionPolicy, RecipientType } from './types'

export const policyLabels: Record<EncryptionPolicy, string> = {
  get Off() { return tx("Kapalı · şifrelenmez") },
  get Optional() { return tx("İsteğe bağlı") },
  get Required() { return tx("Gerekli") },
}
export const policyDescriptions: Record<EncryptionPolicy, string> = {
  get Off() { return tx("Yedek dosyası düz (v1-plain) olarak yazılır. Mevcut davranış korunur.") },
  get Optional() { return tx("Yedek şifrelenir.") },
  get Required() { return tx("Yedek şifrelenir; şifrelenmemiş sonuç başarısız sayılır.") },
}

export const formatLabels: Record<ArtifactFormat, string> = {
  get 'v1-plain'() { return tx("v1-plain · şifresiz") }, get 'v2-bpe'() { return tx("v2-bpe · şifreli (AES-256-GCM)") },
}
export const recipientLabels: Record<RecipientType, string> = {
  get 'tenant-wrap'() { return tx("Tenant anahtarı (backend)") }, get 'platform-recovery'() { return tx("Platform kurtarma anahtarı") }, get 'tenant-recovery'() { return tx("Tenant kurtarma anahtarı") },
}
