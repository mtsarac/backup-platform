import { tx } from '@/lib/i18n'
import type { ArtifactFormat, EncryptionPolicy, RecipientType } from './types'

/** "Açık" covers both Required and Optional; only Off is "Kapalı". */
export const encryptionOn = (policy: EncryptionPolicy) => policy !== 'Off'
export const encryptionStateLabel = (policy: EncryptionPolicy) => encryptionOn(policy) ? tx('Açık') : tx('Kapalı')

export const formatLabels: Record<ArtifactFormat, string> = {
  get 'v1-plain'() { return tx("v1-plain · şifresiz") }, get 'v2-bpe'() { return tx("v2-bpe · şifreli (AES-256-GCM)") },
}
export const recipientLabels: Record<RecipientType, string> = {
  get 'tenant-wrap'() { return tx("Tenant anahtarı (backend)") }, get 'platform-recovery'() { return tx("Platform kurtarma anahtarı") }, get 'tenant-recovery'() { return tx("Tenant kurtarma anahtarı") },
}
