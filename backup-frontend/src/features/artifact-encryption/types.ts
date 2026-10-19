import type { PageParams } from '@/features/platform/types'

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

export type BreakGlassStatus = 'Pending' | 'Approved' | 'Denied' | 'Consumed' | 'Expired'
export const breakGlassStatuses: readonly BreakGlassStatus[] = ['Pending', 'Approved', 'Denied', 'Consumed', 'Expired']
export interface BreakGlassRequest {
  id: string; tenantId: string; tenantName: string | null; artifactId: string
  status: BreakGlassStatus
  reason: string; createdAt: string; expiresAt: string; serverNow: string
  requestedBy: string; requestedByName: string | null
  decidedBy: string | null; decidedByName: string | null; decidedAt: string | null; decisionReason: string | null
  consumedAt: string | null; downloadId: string | null
  isRequester: boolean; canApprove: boolean; canDownload: boolean
}
export interface BreakGlassFilters extends PageParams { tenantId?: string; status?: BreakGlassStatus | ''; mine?: boolean }
export interface CreateBreakGlassInput { tenantId: string; artifactId: string; reason: string; ttlMinutes?: number }

export interface TenantBreakGlassEvent {
  id: string; artifactId: string; status: BreakGlassStatus; reason: string; createdAt: string; expiresAt: string
  requestedByName: string | null; decidedByName: string | null; decidedAt: string | null; consumedAt: string | null
}
