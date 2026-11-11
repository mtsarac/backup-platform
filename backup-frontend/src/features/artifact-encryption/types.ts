export const encryptionPolicies = ['Off', 'Optional', 'Required'] as const
export type EncryptionPolicy = (typeof encryptionPolicies)[number]

export type ArtifactFormat = 'v1-plain' | 'v2-bpe'
export type RecipientType = 'tenant-wrap' | 'platform-recovery' | 'tenant-recovery'

/** Descriptive only: no wrapped key, DEK or private material is ever part of this view. */
export interface ArtifactEncryption {
  format: 'v2-bpe'
  keyId: string
  keyVersion: number
  recipients: { type: RecipientType; keyId: string; keyVersion: number }[]
}

export type WrapKeyState = 'Active' | 'DecryptOnly' | 'Retired'
export interface WrapKey { version: number; keyId: string; state: WrapKeyState; createdAt: string; artifactCount: number }
export interface WrapKeyList { items: WrapKey[] }
export interface RotateWrapKeyResult { key: WrapKey; previousKey: WrapKey | null }

export interface TenantEncryption {
  platformRecovery: { configured: boolean; keyId: string | null; keyVersion: number | null }
  tenantRecovery: { keyId: string; keyVersion: number; createdAt: string } | null
  wrapKey: { version: number; keyId: string; state: WrapKeyState } | null
}
